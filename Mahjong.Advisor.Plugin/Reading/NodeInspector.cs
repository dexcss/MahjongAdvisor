using Mahjong.Advisor;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongAdvisor.Reading;

public readonly record struct ContainerTile(int Tile, bool Rotated, int Raw, float X, float Y);

public sealed record NodeDump(
    uint NodeId,
    uint ParentId,
    int TileCount,
    float X,
    float Y,
    IReadOnlyList<int> SampleTextureIds);

/// <summary>A pond mapping worked out from tile positions rather than hand-entered ids.</summary>
public sealed record PondMapping(int[] PondNodeIds, string Diagnostic)
{
    public bool Ok => PondNodeIds.Length == 4 && PondNodeIds.All(id => id > 0);
}

/// <summary>Why a scan found nothing, so the setup window can say something useful.</summary>
public sealed record ScanDiagnostics(
    int NodesVisited,
    int ImageNodes,
    int TilesDecoded,
    IReadOnlyList<int> UnrecognisedTextureIds);

/// <summary>
/// Reads tiles out of the addon node tree.
///
/// The addon's flat <c>UldManager.NodeList</c> only holds its own nodes. Anything
/// inside a component node (Type >= 1000) lives in that component's separate
/// <c>Component->UldManager.NodeList</c>, so the scan has to descend into those
/// too or it misses every tile drawn inside a component.
/// </summary>
public sealed unsafe class NodeInspector
{
    private const float RowTolerance = 20f;
    private const int MaxDepth = 12;

    /// <summary>A tile image, with every node id above it.</summary>
    private readonly struct TileHit(int texture, int tile, bool rotated, float x, float y, uint[] ancestry)
    {
        public int Texture { get; } = texture;
        public int Tile { get; } = tile;
        public bool Rotated { get; } = rotated;
        public float X { get; } = x;
        public float Y { get; } = y;
        public uint[] Ancestry { get; } = ancestry;
    }

    public ScanDiagnostics LastScan { get; private set; } = new(0, 0, 0, []);

    /// <summary>Texture ids seen inside pond containers that are not decodable tiles.</summary>
    public HashSet<int> UnknownPondTextures { get; } = [];

    // A single read asks the same questions many times: the board census, pond
    // detection, four containers, the riichi markers and the newly-visible diff.
    // Without this cache that is seven full tree walks per read (about six reads
    // a second). Now the tile scan and the visibility scan are each done once,
    // because the tree cannot change inside one framework tick.
    private AtkUnitBase* scanUnit;
    private int scanTextureBase;
    private List<TileHit>? scanHits;
    private HashSet<uint>? scanVisibleNodes;
    private bool scanCached;

    private AtkUnitBase* visibleUnit;
    private bool visibleCached;
    private readonly HashSet<uint> cachedVisibleIds = [];
    private readonly Dictionary<uint, (float X, float Y, int Texture)> cachedVisiblePositions = [];

    /// <summary>
    /// Starts a new read and drops the cached tree walks. Call once per
    /// <see cref="EmjBoardReader.Read"/> before asking any questions.
    /// </summary>
    public void BeginRead(AtkUnitBase* unit, int textureBase)
    {
        scanUnit = unit;
        scanTextureBase = textureBase;
        scanHits = null;
        scanVisibleNodes = null;
        scanCached = false;

        visibleUnit = null;
        visibleCached = false;
        cachedVisibleIds.Clear();
        cachedVisiblePositions.Clear();
        UnknownPondTextures.Clear();
    }

    public List<ContainerTile>? ReadTileContainer(AtkUnitBase* unit, int nodeId, int textureBase) =>
        ReadTileContainers(unit, [nodeId], textureBase);

    /// <summary>
    /// The hand in the client's display-slot order. Slot 0-12 are the closed
    /// hand, 13 is the drawn tile. This is the only order the discard callback
    /// understands; the memory hand array is not guaranteed to match it.
    ///
    /// Uses the cached tile scan, so this costs no extra tree walk inside a read.
    /// </summary>
    public List<HandSlot> ReadHandSlots(AtkUnitBase* unit, LayoutProfile profile, int textureBase)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var slots = new List<HandSlot>(14);
        if (unit is null || !profile.HasHandSlots)
            return slots;

        var hits = ScanTiles(unit, textureBase);
        for (int slot = 0; slot < 13; slot++)
            AddHandSlot(slots, hits, slot, profile.HandSlotNodeIds[slot], textureBase);

        AddHandSlot(slots, hits, 13, profile.DrawnTileNodeId, textureBase);
        return slots;
    }

    private static void AddHandSlot(
        List<HandSlot> slots,
        List<TileHit> hits,
        int slot,
        int nodeId,
        int textureBase)
    {
        if (nodeId <= 0)
            return;

        uint wanted = (uint)nodeId;
        foreach (var hit in hits)
        {
            if (Array.IndexOf(hit.Ancestry, wanted) < 0)
                continue;

            var decoded = TileDecoder.Decode(hit.Texture, textureBase);
            if (!decoded.IsValid)
                continue;

            slots.Add(new HandSlot(slot, decoded.Tile, hit.Texture, decoded.IsRed));
            return;
        }
    }

    /// <summary>
    /// Every tile under any of <paramref name="nodeIds"/>, ordered by screen
    /// position. Null when none of the containers is on screen, so the caller can
    /// mark the field unobserved instead of reading "not found" as "empty".
    ///
    /// A meld strip is several nodes (a parent plus one slot per called set), so
    /// this takes the union. Each tile is counted once even when several ancestor
    /// ids match.
    /// </summary>
    public List<ContainerTile>? ReadTileContainers(
        AtkUnitBase* unit, IReadOnlyList<int> nodeIds, int textureBase)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        if (unit is null || nodeIds.Count == 0)
            return null;

        var wanted = new HashSet<uint>();
        foreach (int id in nodeIds)
        {
            if (id > 0)
                wanted.Add((uint)id);
        }

        if (wanted.Count == 0)
            return null;

        var hits = ScanTiles(unit, textureBase);
        var found = new List<ContainerTile>(24);
        foreach (var hit in hits)
        {
            bool belongs = false;
            foreach (uint id in hit.Ancestry)
            {
                if (!wanted.Contains(id))
                    continue;
                belongs = true;
                break;
            }

            if (belongs)
                found.Add(new ContainerTile(hit.Tile, hit.Rotated, hit.Texture, hit.X, hit.Y));
        }

        // An empty container is valid only when the node itself is on screen: an
        // empty pond at the start of a hand is observed, a wrong id is not. This
        // distinction is what keeps "cannot see melds" from becoming "no melds".
        if (found.Count == 0
            && (scanVisibleNodes is null || !wanted.Any(scanVisibleNodes.Contains)))
        {
            return null;
        }

        // Row-major screen order; node order is render order, not layout order.
        found.Sort(static (a, b) =>
        {
            if (Math.Abs(a.Y - b.Y) > RowTolerance)
                return a.Y.CompareTo(b.Y);
            return a.X.CompareTo(b.X);
        });

        return found;
    }

    /// <summary>
    /// Works out the four discard piles from tile positions, so nobody has to
    /// look up node ids by hand.
    ///
    /// The trick is that we already know our own hand from memory, so the
    /// container whose tiles match it exactly can be identified and removed.
    /// What is left is ponds, melds and the dora indicator; ponds are the inner
    /// clusters in each of the four directions.
    /// </summary>
    /// <param name="handCounts">Our concealed hand, used to spot and exclude it.</param>
    /// <summary>
    /// Finds the piles by matching each candidate container's tile count against
    /// the discard count the client publishes for that seat. An exact count is a
    /// far better test than any judgement about shape or position.
    /// </summary>
    public PondMapping MapPondsByCount(AtkUnitBase* unit, int textureBase, int[] expectedCounts)
    {
        ArgumentNullException.ThrowIfNull(expectedCounts);

        if (unit is null || expectedCounts.Length != 4)
            return new PondMapping([], "No addon.");

        if (expectedCounts.Sum() < 8)
            return new PondMapping([], $"Waiting for discards ({string.Join("/", expectedCounts)}).");

        var hits = ScanTiles(unit, textureBase);
        if (hits.Count == 0)
            return new PondMapping([], "No tiles decoded.");

        var byContainer = new Dictionary<uint, int>();
        foreach (var hit in hits)
        {
            foreach (uint id in hit.Ancestry)
            {
                if (id == 0)
                    continue;
                byContainer[id] = byContainer.GetValueOrDefault(id) + 1;
            }
        }

        var chosen = new int[4];
        for (int seat = 0; seat < 4; seat++)
        {
            int want = expectedCounts[seat];

            // The tightest container holding exactly that many tiles.
            var matches = byContainer
                .Where(kv => kv.Value == want && !chosen.Contains((int)kv.Key))
                .Select(kv => (int)kv.Key)
                .OrderBy(id => id)
                .ToList();

            if (matches.Count == 0)
                return new PondMapping([], $"No container holds {want} tiles for seat {seat}.");

            chosen[seat] = matches[0];
        }

        if (chosen.Distinct().Count() != 4)
            return new PondMapping([], "Candidate piles overlap.");

        return new PondMapping(chosen, $"Matched counts {string.Join("/", expectedCounts)}");
    }

    public PondMapping AutoMapPonds(AtkUnitBase* unit, int textureBase, int[] handCounts)
    {
        ArgumentNullException.ThrowIfNull(handCounts);

        if (unit is null)
            return new PondMapping([], "No addon.");

        var hits = ScanTiles(unit, textureBase);
        if (hits.Count == 0)
            return new PondMapping([], "No tiles decoded yet.");

        // Group every tile by each container above it.
        var byContainer = new Dictionary<uint, List<int>>();
        for (int i = 0; i < hits.Count; i++)
        {
            foreach (uint id in hits[i].Ancestry)
            {
                if (id == 0)
                    continue;
                if (!byContainer.TryGetValue(id, out var list))
                    byContainer[id] = list = [];
                list.Add(i);
            }
        }

        // Our hand: the tightest container whose tiles are exactly our hand.
        int handTotal = handCounts.Sum();
        uint handContainer = 0;
        int handContainerSize = int.MaxValue;

        foreach (var (id, indices) in byContainer)
        {
            if (indices.Count != handTotal || indices.Count >= handContainerSize)
                continue;

            var counts = new int[34];
            foreach (int i in indices)
                counts[hits[i].Tile]++;

            if (counts.SequenceEqual(handCounts))
            {
                handContainer = id;
                handContainerSize = indices.Count;
            }
        }

        if (handContainer == 0)
            return new PondMapping([], "Could not identify our own hand among the tiles yet.");

        var handTiles = byContainer[handContainer].ToHashSet();
        var board = Enumerable.Range(0, hits.Count).Where(i => !handTiles.Contains(i)).ToList();

        // Too early to tell the piles apart.
        if (board.Count < 14)
            return new PondMapping([], $"Only {board.Count} tiles on the table so far - need a few more discards.");

        float centreX = board.Average(i => hits[i].X);
        float centreY = board.Average(i => hits[i].Y);

        // A container is usable only if every tile under it sits in one direction.
        var best = new (uint Id, int Count, float Distance)[4];

        foreach (var (id, indices) in byContainer)
        {
            if (id == handContainer)
                continue;

            var tiles = indices.Where(i => !handTiles.Contains(i)).ToList();
            if (tiles.Count == 0 || tiles.Count != indices.Count)
                continue; // straddles our hand, so it is an ancestor of everything

            // Classify by the container's centre, then require most of its tiles
            // to agree. Demanding unanimity threw away real ponds, because the
            // corner tiles of a wide pile fall on the wrong side of the diagonal.
            float cx = tiles.Average(i => hits[i].X);
            float cy = tiles.Average(i => hits[i].Y);
            int direction = DirectionOf(cx - centreX, cy - centreY);

            int agree = tiles.Count(i => DirectionOf(hits[i].X - centreX, hits[i].Y - centreY) == direction);
            if (agree < tiles.Count * 0.7f)
                continue;
            float distance = MathF.Sqrt(((cx - centreX) * (cx - centreX)) + ((cy - centreY) * (cy - centreY)));

            // Prefer the container holding the MOST tiles in this direction. A
            // single tile node is also "pure" and sits at almost the same spot as
            // its pond, so picking by distance silently selects one loose tile.
            // Ponds outgrow melds, so size is the reliable discriminator.
            ref var slot = ref best[direction];
            bool better = slot.Id == 0
                || tiles.Count > slot.Count
                || (tiles.Count == slot.Count && distance < slot.Distance);

            if (better)
                slot = (id, tiles.Count, distance);
        }

        if (best.Any(b => b.Id == 0))
        {
            string missing = string.Join(", ", Enumerable.Range(0, 4)
                .Where(d => best[d].Id == 0)
                .Select(DirectionName));
            return new PondMapping([], $"No pile found for: {missing}.");
        }

        // A pile holding one tile while others hold many means we latched onto a
        // stray node instead of the pond. Accepting that reports false safe tiles,
        // which is worse than having no danger scoring at all.
        int biggest = best.Max(b => b.Count);
        int smallest = best.Min(b => b.Count);
        if (smallest < 2 || (biggest >= 6 && smallest * 3 < biggest))
        {
            string detail = string.Join(" ", Enumerable.Range(0, 4)
                .Select(d => $"{DirectionName(d)}={best[d].Count}"));
            return new PondMapping([], $"Unconvincing spread ({detail}) - still looking.");
        }

        // Sanity: ponds plus our hand cannot contain more than four of any tile.
        var tally = (int[])handCounts.Clone();
        foreach (var slot in best)
        {
            foreach (int i in byContainer[slot.Id])
                tally[hits[i].Tile]++;
        }

        for (int t = 0; t < 34; t++)
        {
            if (tally[t] > 4)
                return new PondMapping([], $"Rejected: mapping implies {tally[t]} copies of {Tiles.Name(t)}.");
        }

        // Seat order is self, shimocha (right), toimen (across), kamicha (left).
        int[] ids =
        [
            (int)best[(int)Direction.Bottom].Id,
            (int)best[(int)Direction.Right].Id,
            (int)best[(int)Direction.Top].Id,
            (int)best[(int)Direction.Left].Id,
        ];

        string summary = string.Join("  ", Enumerable.Range(0, 4)
            .Select(d => $"{DirectionName(d)}={best[d].Id}({best[d].Count})"));

        return new PondMapping(ids, $"Detected {summary}");
    }

    private enum Direction
    {
        Bottom = 0,
        Right = 1,
        Top = 2,
        Left = 3,
    }

    private static int DirectionOf(float dx, float dy) =>
        Math.Abs(dy) >= Math.Abs(dx)
            ? (dy > 0 ? (int)Direction.Bottom : (int)Direction.Top)
            : (dx > 0 ? (int)Direction.Right : (int)Direction.Left);

    private static string DirectionName(int d) => ((Direction)d) switch
    {
        Direction.Bottom => "self",
        Direction.Right => "shimocha",
        Direction.Top => "toimen",
        _ => "kamicha",
    };

    /// <summary>
    /// Every tile visible anywhere on the board, as a 34-count array. Needs no
    /// container mapping, so it works from the first turn of a hand.
    /// </summary>
    public int[] CountAllTiles(AtkUnitBase* unit, int textureBase)
    {
        var counts = new int[Tiles.Kinds];
        if (unit is null)
            return counts;

        foreach (var hit in ScanTiles(unit, textureBase))
            counts[hit.Tile]++;

        return counts;
    }

    /// <summary>
    /// Raw geometry of every tile on the board: what it is, where it is, and every
    /// container above it. Written out on request so the clustering rules can be
    /// designed against a real table instead of guessed at.
    /// </summary>
    public List<string> DumpGeometry(AtkUnitBase* unit, int textureBase)
    {
        var lines = new List<string>();
        if (unit is null)
            return lines;

        foreach (var hit in ScanTiles(unit, textureBase))
        {
            lines.Add(string.Join("\t",
                Tiles.Name(hit.Tile),
                hit.X.ToString("0.0"),
                hit.Y.ToString("0.0"),
                hit.Rotated ? "rot" : "-",
                string.Join(">", hit.Ancestry)));
        }

        return lines;
    }

    /// <summary>Every node id that has tiles beneath it, most tiles last.</summary>
    public List<NodeDump> Dump(AtkUnitBase* unit, int textureBase, int minTiles = 1)
    {
        var results = new List<NodeDump>();
        if (unit is null)
            return results;

        var hits = ScanTiles(unit, textureBase);

        var byContainer = new Dictionary<uint, (int Count, List<int> Samples, float SumX, float SumY)>();
        foreach (var hit in hits)
        {
            foreach (uint id in hit.Ancestry)
            {
                if (id == 0)
                    continue;

                byContainer.TryGetValue(id, out var entry);
                entry.Count++;
                entry.SumX += hit.X;
                entry.SumY += hit.Y;
                entry.Samples ??= [];
                if (entry.Samples.Count < 6)
                    entry.Samples.Add(hit.Texture);
                byContainer[id] = entry;
            }
        }

        foreach (var (id, entry) in byContainer)
        {
            if (entry.Count < minTiles)
                continue;
            results.Add(new NodeDump(
                NodeId: id,
                ParentId: 0,
                TileCount: entry.Count,
                X: entry.SumX / entry.Count,
                Y: entry.SumY / entry.Count,
                SampleTextureIds: entry.Samples ?? []));
        }

        // Smallest counts first: the tightest node holding exactly one pond is
        // the one to record, not the whole-board ancestors above it.
        results.Sort(static (a, b) =>
        {
            int byCount = a.TileCount.CompareTo(b.TileCount);
            return byCount != 0 ? byCount : a.NodeId.CompareTo(b.NodeId);
        });

        return results;
    }

    /// <summary>
    /// Whether a node exists and is currently drawn. The riichi marker beside a
    /// seat is hidden until that player declares, so this is the declaration.
    /// </summary>
    public bool IsNodeVisible(AtkUnitBase* unit, int nodeId)
    {
        if (unit is null || nodeId <= 0)
            return false;

        // GetNodeById only searches the addon's own node list, so anything living
        // inside a component is invisible to it. The riichi markers are exactly
        // that: the snapshot walk found node 117 while this lookup never did.
        var ids = new HashSet<uint>();
        var positions = new Dictionary<uint, (float X, float Y, int Texture)>();
        CollectVisible(unit, ids, positions);
        return ids.Contains((uint)nodeId);
    }

    /// <summary>Which of the given nodes are currently drawn, in one walk.</summary>
    public bool[] AreNodesVisible(AtkUnitBase* unit, IReadOnlyList<int> nodeIds)
    {
        ArgumentNullException.ThrowIfNull(nodeIds);
        var result = new bool[nodeIds.Count];
        if (unit is null)
            return result;

        var ids = new HashSet<uint>();
        var positions = new Dictionary<uint, (float X, float Y, int Texture)>();
        CollectVisible(unit, ids, positions);

        for (int i = 0; i < nodeIds.Count; i++)
            result[i] = nodeIds[i] > 0 && ids.Contains((uint)nodeIds[i]);

        return result;
    }

    /// <summary>A node that just appeared on screen.</summary>
    public readonly record struct ShownNode(uint NodeId, float X, float Y, int TextureId);

    private readonly HashSet<uint> visibleLastTick = [];
    private bool visibilityPrimed;

    /// <summary>
    /// Nodes that switched from hidden to visible since the last call.
    ///
    /// The riichi stick appears beside the declaring player, so the declaration
    /// shows up here as a node becoming visible at that seat's position. With no
    /// published node ids, watching for the change is how to find it.
    /// </summary>
    public List<ShownNode> TakeNewlyVisible(AtkUnitBase* unit)
    {
        var shown = new List<ShownNode>();
        if (unit is null)
            return shown;

        var nowVisible = new HashSet<uint>();
        var positions = new Dictionary<uint, (float X, float Y, int Texture)>();
        CollectVisible(unit, nowVisible, positions);

        if (visibilityPrimed)
        {
            foreach (uint id in nowVisible)
            {
                if (visibleLastTick.Contains(id))
                    continue;
                var info = positions.TryGetValue(id, out var p) ? p : default;
                shown.Add(new ShownNode(id, info.X, info.Y, info.Texture));
            }
        }

        visibleLastTick.Clear();
        foreach (uint id in nowVisible)
            visibleLastTick.Add(id);
        visibilityPrimed = true;

        return shown;
    }

    public void ResetVisibility()
    {
        visibleLastTick.Clear();
        visibilityPrimed = false;
    }

    /// <summary>Every currently visible node, for a one-shot dump.</summary>
    public List<ShownNode> SnapshotVisible(AtkUnitBase* unit)
    {
        var ids = new HashSet<uint>();
        var positions = new Dictionary<uint, (float X, float Y, int Texture)>();
        if (unit is not null)
            CollectVisible(unit, ids, positions);

        return [.. positions.Select(kv => new ShownNode(kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Texture))];
    }

    private void CollectVisible(
        AtkUnitBase* unit,
        HashSet<uint> ids,
        Dictionary<uint, (float X, float Y, int Texture)> positions)
    {
        if (unit is null)
            return;

        if (!visibleCached || visibleUnit != unit)
        {
            cachedVisibleIds.Clear();
            cachedVisiblePositions.Clear();

            var uld = unit->UldManager;
            if (uld.NodeList is not null)
            {
                for (int i = 0; i < uld.NodeListCount; i++)
                {
                    var node = uld.NodeList[i];
                    CollectVisibleFrom(node, cachedVisibleIds, cachedVisiblePositions, 0);
                }
            }

            visibleUnit = unit;
            visibleCached = true;
        }

        foreach (uint id in cachedVisibleIds)
            ids.Add(id);
        foreach (var entry in cachedVisiblePositions)
            positions[entry.Key] = entry.Value;
    }

    private void CollectVisibleFrom(
        AtkResNode* node,
        HashSet<uint> ids,
        Dictionary<uint, (float X, float Y, int Texture)> positions,
        int depth)
    {
        if (node is null || depth > MaxDepth || ids.Count > 600)
            return;

        if (!node->IsVisible())
            return;

        uint id = node->NodeId;
        if (id != 0 && ids.Add(id))
        {
            AbsolutePosition(node, out float x, out float y);
            positions[id] = (x, y, TryGetTextureId(node));
        }

        if ((ushort)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null && component->UldManager.NodeList is not null)
            {
                for (int i = 0; i < component->UldManager.NodeListCount; i++)
                    CollectVisibleFrom(component->UldManager.NodeList[i], ids, positions, depth + 1);
            }

            return;
        }

        for (var child = node->ChildNode; child is not null; child = child->PrevSiblingNode)
            CollectVisibleFrom(child, ids, positions, depth + 1);
    }

    private List<TileHit> ScanTiles(AtkUnitBase* unit, int textureBase)
    {
        if (scanCached && scanUnit == unit && scanTextureBase == textureBase && scanHits is not null)
            return scanHits;

        var hits = new List<TileHit>(128);
        var visible = new HashSet<uint>();
        var unrecognised = new List<int>();
        int visited = 0, imageNodes = 0;

        if (unit is not null)
        {
            var uld = unit->UldManager;
            if (uld.NodeList is not null)
            {
                var ancestry = new List<uint>(MaxDepth);
                for (int i = 0; i < uld.NodeListCount; i++)
                {
                    Descend(uld.NodeList[i], ancestry, visible, textureBase, hits, unrecognised,
                        ref visited, ref imageNodes, 0);
                }
            }
        }

        scanUnit = unit;
        scanTextureBase = textureBase;
        scanHits = hits;
        scanVisibleNodes = visible;
        scanCached = true;
        LastScan = new ScanDiagnostics(visited, imageNodes, hits.Count, unrecognised);
        return hits;
    }

    private void Descend(
        AtkResNode* node,
        List<uint> ancestry,
        HashSet<uint> visible,
        int textureBase,
        List<TileHit> sink,
        List<int> unrecognised,
        ref int visited,
        ref int imageNodes,
        int depth)
    {
        if (node is null || depth > MaxDepth)
            return;
        if (!node->IsVisible())
            return;

        visited++;
        ancestry.Add(node->NodeId);
        if (node->NodeId != 0)
            visible.Add(node->NodeId);

        int textureId = TryGetTextureId(node);
        if (textureId > 0)
        {
            imageNodes++;
            var decoded = TileDecoder.Decode(textureId, textureBase);
            if (decoded.IsValid)
            {
                AbsolutePosition(node, out float x, out float y);

                // A riichi declaration tile lies on its side: either an explicit
                // rotation, or a node wider than it is tall.
                bool rotated = Math.Abs(node->Rotation) > 0.3f || node->Width > node->Height;

                // Ancestry from the descent path, plus the parent chain, since a
                // component's inner nodes may or may not link back across.
                var ids = new HashSet<uint>(ancestry);
                for (var cur = node; cur is not null; cur = cur->ParentNode)
                    ids.Add(cur->NodeId);

                sink.Add(new TileHit(textureId, decoded.Tile, rotated, x, y, [.. ids]));
            }
            else if (textureId is > 70000 and < 90000)
            {
                // In tile-id territory but not a tile we recognise. The sideways
                // riichi declaration is the prime suspect for a separate sprite,
                // and it would otherwise be dropped without trace.
                if (UnknownPondTextures.Count < 32)
                    UnknownPondTextures.Add(textureId);
            }
            else if (unrecognised.Count < 12 && !unrecognised.Contains(textureId))
            {
                unrecognised.Add(textureId);
            }
        }

        // Component nodes keep their children in a separate node list.
        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null)
            {
                var sub = component->UldManager;
                if (sub.NodeList is not null)
                {
                    for (int i = 0; i < sub.NodeListCount; i++)
                    {
                        Descend(sub.NodeList[i], ancestry, visible, textureBase, sink, unrecognised,
                            ref visited, ref imageNodes, depth + 1);
                    }
                }
            }
        }

        ancestry.RemoveAt(ancestry.Count - 1);
    }

    private static void AbsolutePosition(AtkResNode* node, out float x, out float y)
    {
        x = 0;
        y = 0;
        for (var cur = node; cur is not null; cur = cur->ParentNode)
        {
            x = cur->X + (x * cur->ScaleX);
            y = cur->Y + (y * cur->ScaleY);
        }
    }

    /// <summary>
    /// Finds a node by id. The addon's own lookup is O(1) and covers the hand
    /// tile nodes; the recursive fallback reaches component-internal nodes when
    /// that lookup cannot see them.
    /// </summary>
    public static AtkResNode* FindNode(AtkUnitBase* unit, uint nodeId)
    {
        if (unit is null || nodeId == 0)
            return null;

        var direct = unit->GetNodeById(nodeId);
        if (direct is not null)
            return direct;

        var uld = unit->UldManager;
        if (uld.NodeList is null)
            return null;

        for (int i = 0; i < uld.NodeListCount; i++)
        {
            var found = FindNodeFrom(uld.NodeList[i], nodeId, 0);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static AtkResNode* FindNodeFrom(AtkResNode* node, uint nodeId, int depth)
    {
        if (node is null || depth > MaxDepth)
            return null;
        if (node->NodeId == nodeId)
            return node;

        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null)
            {
                var sub = component->UldManager;
                if (sub.NodeList is not null)
                {
                    for (int i = 0; i < sub.NodeListCount; i++)
                    {
                        var found = FindNodeFrom(sub.NodeList[i], nodeId, depth + 1);
                        if (found is not null)
                            return found;
                    }
                }
            }

            return null;
        }

        for (var child = node->ChildNode; child is not null; child = child->PrevSiblingNode)
        {
            var found = FindNodeFrom(child, nodeId, depth + 1);
            if (found is not null)
                return found;
        }

        return null;
    }

    internal static int TryGetTextureId(AtkResNode* node)
    {
        if (node is null || node->Type != NodeType.Image)
            return 0;

        var image = (AtkImageNode*)node;
        var parts = image->PartsList;
        if (!IsHeapPointer((nint)parts) || !IsHeapPointer((nint)parts->Parts))
            return 0;

        uint partId = image->PartId;
        if (partId >= parts->PartCount)
            return 0;

        var asset = parts->Parts[partId].UldAsset;
        if (!IsHeapPointer((nint)asset))
            return 0;

        var resource = asset->AtkTexture.Resource;
        if (!IsHeapPointer((nint)resource))
            return 0;

        return (int)resource->IconId;
    }

    /// <summary>
    /// Null checks are not enough: a stale node can hold a non-null garbage
    /// pointer, and dereferencing one crashes the framework thread mid-game.
    /// </summary>
    private static bool IsHeapPointer(nint p) =>
        p != nint.Zero
        && (ulong)p >= 0x10000UL
        && (ulong)p <= 0x0000_7FFF_FFFF_FFFFUL
        && ((ulong)p & 0x7) == 0;
}
