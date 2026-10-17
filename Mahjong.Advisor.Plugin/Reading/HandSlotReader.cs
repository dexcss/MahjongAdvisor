using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MahjongAdvisor.Reading;

/// <summary>One visible hand tile in display-slot order (0-12 hand, 13 drawn).</summary>
public readonly record struct HandSlot(int Slot, int Tile, int IconId, bool IsRed)
{
    public bool IsValid => Tile is >= 0 and < 34;
}

/// <summary>
/// Reads the hand as the client displays it: the discard callback addresses a
/// tile by display slot, not by the memory hand array's slot order. The hand
/// array is still the source of truth for counts and dora; this reader exists
/// only to turn a recommended tile into the slot + icon pair the callback needs.
/// </summary>
public sealed unsafe class HandSlotReader
{
    public List<HandSlot> Read(AtkUnitBase* unit, LayoutProfile profile, int textureBase)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var slots = new List<HandSlot>(14);
        if (unit is null)
            return slots;

        int closed = Math.Min(13, profile.HandSlotNodeIds.Length);
        for (int slot = 0; slot < closed; slot++)
        {
            var node = NodeInspector.FindNode(unit, (uint)profile.HandSlotNodeIds[slot]);
            if (node is null || !node->IsVisible())
                continue;

            if (TryReadTile(node, profile, textureBase, out int tile, out int icon, out bool red))
                slots.Add(new HandSlot(slot, tile, icon, red));
        }

        var drawn = NodeInspector.FindNode(unit, (uint)profile.DrawnTileNodeId);
        if (drawn is not null && drawn->IsVisible()
            && TryReadTile(drawn, profile, textureBase, out int drawnTile, out int drawnIcon, out bool drawnRed))
        {
            slots.Add(new HandSlot(13, drawnTile, drawnIcon, drawnRed));
        }

        return slots;
    }

    /// <summary>
    /// Picks the slot for a tile, preferring a plain five over a red five so the
    /// aka dora stays in the hand when both are present.
    /// </summary>
    public static HandSlot? FindForTile(IReadOnlyList<HandSlot> slots, int tile)
    {
        ArgumentNullException.ThrowIfNull(slots);

        HandSlot? red = null;
        foreach (var slot in slots)
        {
            if (slot.Tile != tile)
                continue;
            if (!slot.IsRed)
                return slot;
            red ??= slot;
        }

        return red;
    }

    private static bool TryReadTile(
        AtkResNode* node,
        LayoutProfile profile,
        int textureBase,
        out int tile,
        out int icon,
        out bool red)
    {
        tile = -1;
        icon = 0;
        red = false;

        if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component is null || component->UldManager.NodeList is null)
                return false;

            var list = component->UldManager;
            if (list.NodeListCount > 0)
            {
                int index = Math.Clamp(profile.HandButtonNodeListIndex, 0, list.NodeListCount - 1);
                icon = ReadImageIcon(list.NodeList[index], profile.HandImageNodeListIndex, textureBase);
            }

            if (icon <= 0)
                icon = FindTileIcon(list, textureBase);
        }
        else
        {
            icon = NodeInspector.TryGetTextureId(node);
        }

        if (icon <= 0)
            return false;

        var decoded = TileDecoder.Decode(icon, textureBase);
        if (!decoded.IsValid)
            return false;

        tile = decoded.Tile;
        red = decoded.IsRed;
        return true;
    }

    private static int ReadImageIcon(AtkResNode* button, int preferredIndex, int textureBase)
    {
        if (button is null)
            return 0;

        if ((int)button->Type >= 1000)
        {
            var component = ((AtkComponentNode*)button)->Component;
            if (component is null)
                return 0;

            var list = component->UldManager;
            if (list.NodeList is null || list.NodeListCount == 0)
                return 0;

            int index = Math.Clamp(preferredIndex, 0, list.NodeListCount - 1);
            int preferred = NodeInspector.TryGetTextureId(list.NodeList[index]);
            if (preferred > 0 && TileDecoder.Decode(preferred, textureBase).IsValid)
                return preferred;

            // Patch drift: the image can move within the button's children.
            return FindTileIcon(list, textureBase);
        }

        int direct = NodeInspector.TryGetTextureId(button);
        return direct > 0 && TileDecoder.Decode(direct, textureBase).IsValid ? direct : 0;
    }

    private static int FindTileIcon(AtkUldManager list, int textureBase)
    {
        if (list.NodeList is null)
            return 0;

        for (int i = 0; i < list.NodeListCount; i++)
        {
            int icon = NodeInspector.TryGetTextureId(list.NodeList[i]);
            if (icon > 0 && TileDecoder.Decode(icon, textureBase).IsValid)
                return icon;
        }

        return 0;
    }
}
