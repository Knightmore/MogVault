using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Contract;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class RetainersUI : ModuleUI
{
    private const uint Gil = 1;
    private const ushort RetainerGilContainer = 12000;
    private const int BagSlots = 175;
    private const uint InventoryIcon = 2;
    private const uint VentureItem = 21072;
    private const uint HqItemOffset = 1000000;
    private static readonly (ContainerType Type, string Title)[] SectionTypes =
    [
        (ContainerType.RetainerBag, "SectionBags"),
        (ContainerType.RetainerEquipped, "SectionEquipped"),
        (ContainerType.RetainerCrystals, "SectionCrystals"),
        (ContainerType.RetainerMarket, "SectionMarket"),
    ];

    private long rowsKey = -1;
    private Row[] rows = [];
    private bool sortDirty;
    private readonly HashSet<ulong> expanded = [];
    private readonly Dictionary<ulong, Section[]> contents = [];

    public override string Name => "Retainers";
    public override Enum Category => MogVaultCategory.Retainers;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.UserTie;
    public override Action? Help => () => ImGui.TextUnformatted(T("Help"));
    public override string? Summary => rows.Length == 0 ? null : string.Format(T("Summary"), rows.Length, rows.Count(r => !r.Unavailable && VentureDone(r.Details)));

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvRetainersTable", 7, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Character"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("Retainer"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("Job"), "BTN 100");
        Columns.Icon(T("Gil"), GameText.ItemIcon(1), "999,999,999");
        Columns.Icon(T("Items"), InventoryIcon, "175/175");
        Columns.Icon(T("Venture"), GameText.ItemIcon(VentureItem), "23h 59m");
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => r.Retainer,
            2 => r.Job,
            3 => r.Gil,
            4 => r.Details?.ItemCount,
            5 => r.Unavailable || r.Details is not { VentureId: > 0 } d ? null : d.VentureComplete,
            6 => r.Updated,
            _ => r.Character,
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (expanded.Count > 0)
        {
            foreach (var row in rows)
                DrawRow(row, now);
            return;
        }

        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Length, ImGui.GetTextLineHeightWithSpacing());
        while (clipper.Step())
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                DrawRow(rows[i], now);

        clipper.End();
        clipper.Destroy();
    }

    private void DrawRow(Row row, long now)
    {
        var d = row.Details;
        ImGui.TableNextRow();
        var open = expanded.Contains(row.RetainerId);
        using (ImRaii.PushColor(ImGuiCol.Text, V2Palette.TextFaint, row.Unavailable))
        {
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"##mvRetainer{row.RetainerId}", open, ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowItemOverlap))
                open = !open;
            ImGui.SameLine(0, 0);
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextUnformatted((open ? FontAwesomeIcon.ChevronDown : FontAwesomeIcon.ChevronRight).ToIconString());
            ImGui.SameLine();
            ImGui.TextUnformatted(row.Character);
            Cell(row.Retainer);
            if (row.Unavailable && ImGui.IsItemHovered())
                ImGui.SetTooltip(T("UnavailableHint"));
            Cell(row.Job);
            Cell(row.Gil.ToString("N0"));
            Cell(d is { } items ? $"{items.ItemCount}/{BagSlots}" : "-");

            ImGui.TableNextColumn();
            if (row.Unavailable)
                ImGui.TextUnformatted(T("Unavailable"));
            else if (d is not { VentureId: > 0 } venture)
                ImGui.TextUnformatted("-");
            else if (venture.VentureComplete <= now)
                Widgets.TextColored(V2Palette.Success, T("VentureDone"));
            else
                ImGui.TextUnformatted(GameText.Duration(TimeSpan.FromSeconds(venture.VentureComplete - now)));

            Cell(GameText.Age(row.Updated));
        }

        if (!open)
        {
            expanded.Remove(row.RetainerId);
            return;
        }

        expanded.Add(row.RetainerId);
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        DrawContents(row);
    }

    private void DrawContents(Row row)
    {
        var sections = Contents(row);
        var clipMin = ImGui.GetWindowDrawList().GetClipRectMin();
        var clipMax = ImGui.GetWindowDrawList().GetClipRectMax();
        var start = ImGui.GetCursorScreenPos();
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X;
        ImGui.PushClipRect(new Vector2(clipMin.X, clipMin.Y), new Vector2(right, clipMax.Y), false);
        if (sections.Length == 0)
            Widgets.TextMuted(T("NoItems"));
        foreach (var section in sections)
        {
            Widgets.TextMuted(string.Format(T(section.Title), section.Items.Length));
            IconGrid.Flow($"##mvRetainerItems{row.RetainerId}{section.Title}", section.Items.Length, right - start.X,
                i => new IconCell(section.Items[i].IconId, Badge: section.Items[i].Qty > 1 ? section.Items[i].Qty.ToString("N0") : null),
                i => GameItemTooltip.ShowOnHover(section.Items[i].Hq ? section.Items[i].ItemId + HqItemOffset : section.Items[i].ItemId));
        }

        ImGui.PopClipRect();
    }

    private Section[] Contents(Row row)
    {
        if (contents.TryGetValue(row.RetainerId, out var cached))
            return cached;
        var owner = Plugin.P!.Query.Current.Find(row.OwnerId);
        var sections = SectionTypes
            .Select(t => new Section(t.Title, [.. (owner?.Containers ?? [])
                .Where(c => c.RetainerId == row.RetainerId && c.Type == t.Type)
                .SelectMany(c => c.Stacks.Select(s => (Stack: s, Qty: c.QuantityOf(s))))
                .GroupBy(x => x.Stack.ItemIdHq)
                .Select(g => new Entry(g.First().Stack.ItemId, g.First().Stack.IsHq, g.Sum(x => x.Qty), GameText.Item(g.First().Stack.ItemId), GameText.ItemIcon(g.First().Stack.ItemId)))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)]))
            .Where(s => s.Items.Length > 0)
            .ToArray();
        contents[row.RetainerId] = sections;
        return sections;
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static bool VentureDone(RetainerDetails? details) =>
        details is { VentureId: > 0 } d && d.VentureComplete <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        contents.Clear();
        var me = Plugin.P!.CurrentCharacterId;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(c => c.Retainers.Select(r =>
            {
                var gil = c.Container(r.Id, RetainerGilContainer) is { } container
                    ? container.Stacks.Where(s => s.ItemId == Gil).Sum(s => container.QuantityOf(s))
                    : 0;
                var updated = Math.Max(r.Details?.UpdatedTicks ?? 0, c.Containers.Where(x => x.RetainerId == r.Id).Select(x => x.LastUpdatedTicks).DefaultIfEmpty().Max());
                var job = r.Details is { ClassJob: > 0 } d ? $"{GameText.ClassJob(d.ClassJob)} {d.Level}" : "-";
                return new Row(Names.Owner(snapshot, c), Names.Retainer(snapshot, r.Id, r.Name), job, gil, r.Details, updated, r.Unavailable, c.Id == me, c.Id, r.Id);
            }))
            .ToArray();
        sortDirty = true;
    }

    private sealed record Row(string Character, string Retainer, string Job, long Gil, RetainerDetails? Details, long Updated, bool Unavailable, bool Mine, ulong OwnerId, ulong RetainerId);

    private sealed record Section(string Title, Entry[] Items);

    private sealed record Entry(uint ItemId, bool Hq, long Qty, string Name, uint IconId);
}

