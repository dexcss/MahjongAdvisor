using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace MahjongAdvisor.UI;

/// <summary>Shared colours and small layout helpers, so both windows read as one UI.</summary>
internal static class UiTheme
{
    public static readonly Vector4 Good = new(0.45f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Warn = new(0.95f, 0.75f, 0.30f, 1f);
    public static readonly Vector4 Bad = new(0.92f, 0.42f, 0.42f, 1f);
    public static readonly Vector4 Muted = new(0.62f, 0.64f, 0.68f, 1f);
    public static readonly Vector4 Accent = new(0.55f, 0.78f, 1f, 1f);
    public static readonly Vector4 Red = new(0.95f, 0.48f, 0.48f, 1f);
    public static readonly Vector4 Text = new(0.92f, 0.93f, 0.95f, 1f);

    public static void Section(string title)
    {
        ImGui.Spacing();
        ImGui.TextColored(Accent, title);
        ImGui.Separator();
    }

    public static void MutedText(string text) => ImGui.TextColored(Muted, text);

    public static void Wrapped(string text)
    {
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    public static void WrappedColored(string text, Vector4 colour)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void KeyValue(string key, string value)
    {
        ImGui.TextColored(Muted, key);
        ImGui.SameLine(132);
        ImGui.TextUnformatted(value);
    }

    public static string ShapeLabel(int shanten) => shanten switch
    {
        < 0 => "win",
        0 => "tenpai",
        1 => "1-shanten",
        _ => $"{shanten}-shanten",
    };
}
