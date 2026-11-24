using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Underlings.Ui.V2;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

internal static class Columns
{
    private static readonly Dictionary<string, (uint GameIcon, FontAwesomeIcon Glyph, string Tooltip)> Icons = [];

    public static void Fixed(string label, string widest, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, bool measureLabel = true)
    {
        var header = measureLabel ? ImGui.CalcTextSize(label, true).X + ImGui.GetFontSize() : 0f;
        Setup(label, header, ImGui.CalcTextSize(widest).X, flags);
    }

    public static void Icon(string label, uint gameIcon, string widest, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, string? tooltip = null, bool compact = false)
    {
        Icons[label] = (gameIcon, FontAwesomeIcon.None, tooltip ?? label);
        Setup(label, IconHeader(compact), ImGui.CalcTextSize(widest).X, flags);
    }

    public static void Icon(string label, FontAwesomeIcon glyph, string widest, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, string? tooltip = null, bool compact = false) =>
        Icon(label, glyph, ImGui.CalcTextSize(widest).X, flags, tooltip, compact);

    public static void Icon(string label, FontAwesomeIcon glyph, float contentWidth, ImGuiTableColumnFlags flags = ImGuiTableColumnFlags.None, string? tooltip = null, bool compact = false)
    {
        Icons[label] = (0, glyph, tooltip ?? label);
        Setup(label, IconHeader(compact), contentWidth, flags);
    }

    private static float IconHeader(bool compact) => ImGui.GetTextLineHeight() + (compact ? 0f : ImGui.GetFontSize());

    public static float CompactWidth(string widest) =>
        Math.Max(ImGui.GetTextLineHeight(), ImGui.CalcTextSize(widest).X) + ImGui.GetStyle().CellPadding.X * 2;

    public static void CompactIcon(string label, uint gameIcon, bool stretch, string widest, ImGuiTableColumnFlags flags, string tooltip)
    {
        Icons[label] = (gameIcon, FontAwesomeIcon.None, tooltip);
        ImGui.TableSetupColumn(label, (stretch ? ImGuiTableColumnFlags.WidthStretch : ImGuiTableColumnFlags.WidthFixed) | flags, stretch ? 1f : CompactWidth(widest));
    }

    public static void Delete() =>
        ImGui.TableSetupColumn("##mvDelete", ImGuiTableColumnFlags.WidthFixed | ImGuiTableColumnFlags.NoSort, ImGui.GetTextLineHeight());

    public static bool DeleteCell(string? blocked, string hint)
    {
        ImGui.TableNextColumn();
        var io = ImGui.GetIO();
        var armed = blocked is null && io.KeyCtrl && io.KeyShift;
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(armed ? V2Palette.Danger : V2Palette.TextDim with { W = 0.45f }, FontAwesomeIcon.Trash.ToIconString());
        if (!ImGui.IsItemHovered())
            return false;
        ImGui.SetTooltip(blocked ?? hint);
        if (armed)
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        return armed && ImGui.IsItemClicked();
    }

    public static void HeadersRow()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        var size = ImGui.GetTextLineHeight();
        var count = ImGui.TableGetColumnCount();
        for (var i = 0; i < count; i++)
        {
            if (!ImGui.TableSetColumnIndex(i))
                continue;
            var label = ImGui.TableGetColumnName(i).ToString();
            if (!Icons.TryGetValue(label, out var icon))
            {
                ImGui.TableHeader(label);
                continue;
            }

            var pos = ImGui.GetCursorScreenPos();
            ImGui.TableHeader($"##{label}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(icon.Tooltip);
            var drawList = ImGui.GetWindowDrawList();
            if (icon.GameIcon != 0)
            {
                var texture = UnderlingsSvc.Texture.GetFromGameIcon(new GameIconLookup(icon.GameIcon)).GetWrapOrEmpty();
                drawList.AddImage(texture.Handle, pos, pos + new Vector2(size));
            }
            else
            {
                drawList.AddText(UiBuilder.IconFont, ImGui.GetFontSize() * 0.9f, pos + new Vector2(0, size * 0.05f), ImGui.GetColorU32(ImGuiCol.Text), icon.Glyph.ToIconString());
            }
        }

        Icons.Clear();
    }

    private static void Setup(string label, float header, float content, ImGuiTableColumnFlags flags)
    {
        var width = Math.Max(header, content) + ImGui.GetStyle().CellPadding.X * 2;
        ImGui.TableSetupColumn(label, ImGuiTableColumnFlags.WidthFixed | flags, width);
    }
}

internal static class Fit
{
    public static float Combo(float minWidth, IEnumerable<string> options)
    {
        var widest = 0f;
        foreach (var option in options)
            widest = Math.Max(widest, ImGui.CalcTextSize(option, true).X);
        return Math.Max(Underlings.Ui.V2.Widgets.Px(minWidth), widest + ImGui.GetStyle().FramePadding.X * 2 + ImGui.GetFrameHeight());
    }
}
