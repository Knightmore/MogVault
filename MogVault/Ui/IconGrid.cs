using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using Underlings.Ui.V2;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

internal readonly record struct IconCell(uint IconId, bool Faded = false, string? Badge = null);

internal static class IconGrid
{
    public static void Draw(string id, int count, Func<int, IconCell> cell, Action<int> hovered)
    {
        using var child = ImRaii.Child(id, new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!child || count == 0)
            return;

        var size = Widgets.Px(40);
        var gap = Widgets.Px(6);
        var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + gap) / (size + gap)));
        var lines = (count + columns - 1) / columns;
        var drawList = ImGui.GetWindowDrawList();
        var faded = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.25f));
        var full = ImGui.GetColorU32(Vector4.One);

        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(gap, gap));
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(lines, size + gap);
        while (clipper.Step())
        {
            for (var line = clipper.DisplayStart; line < clipper.DisplayEnd; line++)
            {
                for (var column = 0; column < columns; column++)
                {
                    var index = line * columns + column;
                    if (index >= count)
                        break;
                    if (column > 0)
                        ImGui.SameLine();

                    var pos = ImGui.GetCursorScreenPos();
                    ImGui.InvisibleButton($"##mvIcon{index}", new Vector2(size));
                    DrawCell(drawList, pos, size, cell(index), faded, full);
                    if (ImGui.IsItemHovered())
                        hovered(index);
                }
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    public static void Flow(string id, int count, float width, Func<int, IconCell> cell, Action<int> hovered)
    {
        if (count == 0)
            return;

        var size = Widgets.Px(40);
        var gap = Widgets.Px(6);
        var columns = Math.Max(1, (int)((width + gap) / (size + gap)));
        var lines = (count + columns - 1) / columns;
        var drawList = ImGui.GetWindowDrawList();
        var faded = ImGui.GetColorU32(new Vector4(1, 1, 1, 0.25f));
        var full = ImGui.GetColorU32(Vector4.One);
        var origin = ImGui.GetCursorScreenPos();

        using var scope = ImRaii.PushId(id);
        for (var i = 0; i < count; i++)
        {
            var pos = origin + new Vector2(i % columns * (size + gap), i / columns * (size + gap));
            ImGui.SetCursorScreenPos(pos);
            ImGui.InvisibleButton($"##mvIcon{i}", new Vector2(size));
            DrawCell(drawList, pos, size, cell(i), faded, full);
            if (ImGui.IsItemHovered())
                hovered(i);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(Math.Min(count, columns) * (size + gap) - gap, lines * (size + gap) - gap));
    }

    private static void DrawCell(ImDrawListPtr drawList, Vector2 pos, float size, IconCell cell, uint faded, uint full)
    {
        var texture = UnderlingsSvc.Texture.GetFromGameIcon(new GameIconLookup(cell.IconId)).GetWrapOrEmpty();
        drawList.AddImage(texture.Handle, pos, pos + new Vector2(size), Vector2.Zero, Vector2.One, cell.Faded ? faded : full);
        if (cell.Badge is { } badge)
            DrawBadge(drawList, pos, size, badge);
    }

    private static void DrawBadge(ImDrawListPtr drawList, Vector2 pos, float size, string text)
    {
        var textSize = ImGui.CalcTextSize(text);
        var at = pos + new Vector2(size - textSize.X - Widgets.Px(2), size - textSize.Y);
        drawList.AddRectFilled(at - new Vector2(Widgets.Px(2), 0), at + textSize + new Vector2(Widgets.Px(2), 0), ImGui.GetColorU32(new Vector4(0, 0, 0, 0.7f)), Widgets.Px(3));
        drawList.AddText(at, ImGui.GetColorU32(Vector4.One), text);
    }
}
