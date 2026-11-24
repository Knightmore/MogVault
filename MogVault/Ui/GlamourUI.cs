using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Text;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Game;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class GlamourUI : ModuleUI
{
    private const int DresserSlots = 800;
    private const int DresserTab = 0;
    private const int OutfitsTab = 1;
    private const int ArmoireTab = 2;
    private static readonly string[] TabNames = ["GlamourDresser", "Outfits", "Armoire"];
    private static readonly Lazy<HashSet<uint>> ArmoireItems = new(() => [.. Underlings.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Cabinet>().Select(c => c.Item.RowId).Where(id => id != 0)]);

    private long rowsKey = -1;
    private Row[] rows = [];
    private bool sortDirty;

    private bool selectDetails;
    private bool selectTab;
    private ulong detailOwner;
    private int detailTab;
    private int filter;
    private bool armoireOnly;
    private string search = string.Empty;
    private object? detailState;
    private Entry[] items = [];
    private (Outfit Outfit, int Owned)[] outfits = [];
    private CharacterData? detailCharacter;
    private (int Complete, int Partial) outfitCounts;

    public override string Name => "Glamour";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Tshirt;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var bar = ImRaii.TabBar("##mvGlamourTabs");
        if (!bar)
            return;

        if (ImGui.BeginTabItem(T("Summary")))
        {
            DrawSummary();
            ImGui.EndTabItem();
        }

        var select = selectDetails ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        selectDetails = false;
        if (ImGui.BeginTabItem(T("Details"), select))
        {
            DrawDetails();
            ImGui.EndTabItem();
        }
    }

    private void DrawSummary()
    {
        using var table = ImRaii.Table("##mvGlamourTable", 5,
                                       ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Character"));
        Columns.Icon(T("GlamourDresser"), FontAwesomeIcon.Tshirt, T("NotOpened"), ImGuiTableColumnFlags.PreferSortDescending);
        Columns.Icon(T("Outfits"), FontAwesomeIcon.LayerGroup, $"{OutfitCatalog.All.Count}/{OutfitCatalog.All.Count}", ImGuiTableColumnFlags.PreferSortDescending);
        Columns.Icon(T("Armoire"), FontAwesomeIcon.Archive, T("NotOpened"), ImGuiTableColumnFlags.PreferSortDescending);
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago", ImGuiTableColumnFlags.PreferSortDescending);
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Id == Plugin.P!.CurrentCharacterId, (r, column) => column switch
        {
            0 => r.Name,
            1 => r.Dresser ?? -1,
            2 => r.Complete ?? -1,
            3 => r.Armoire ?? -1,
            _ => r.Updated,
        });

        var total = OutfitCatalog.All.Count;
        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            Cell(row, DresserTab, row.Dresser is { } d ? $"{d}/{DresserSlots}" : null);
            Cell(row, OutfitsTab, row.Complete is { } c ? $"{c}/{total}" : null, string.Format(T("OutfitsHint"), row.Complete, row.Partial));
            Cell(row, ArmoireTab, row.Armoire?.ToString("N0"));
            ImGui.TableNextColumn();
            if (row.Updated > 0)
                Widgets.TextMuted(GameText.Age(row.Updated));
        }
    }

    private void Cell(Row row, int tab, string? text, string? tooltip = null)
    {
        ImGui.TableNextColumn();
        if (text is null)
        {
            Widgets.TextMuted(T("NotOpened"));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(T("NotOpenedHint"));
            return;
        }

        ImGui.TextUnformatted(text);
        if (!ImGui.IsItemHovered())
            return;
        ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
        if (tooltip is not null)
            ImGui.SetTooltip(tooltip);
        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
        {
            detailOwner = row.Id;
            detailTab = tab;
            selectDetails = true;
            selectTab = true;
        }
    }

    private void DrawDetails()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Array.FindIndex(rows, r => r.Id == detailOwner) < 0)
            detailOwner = rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).First().Id;

        ImGui.SetNextItemWidth(Fit.Combo(220, rows.Select(r => r.Name)));
        using (var combo = ImRaii.Combo("##mvGlamourOwner", Array.Find(rows, r => r.Id == detailOwner)?.Name ?? string.Empty))
        {
            if (combo)
                foreach (var row in rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
                    if (ImGui.Selectable($"{row.Name}##{row.Id}", detailOwner == row.Id))
                        detailOwner = row.Id;
        }

        if (detailTab == OutfitsTab)
        {
            ImGui.SameLine();
            string[] filters = [T("ShowAll"), T("ShowComplete"), T("ShowPartial"), T("ShowMissing")];
            ImGui.SetNextItemWidth(Fit.Combo(130, filters));
            using var combo = ImRaii.Combo("##mvOutfitFilter", filters[filter]);
            if (combo)
                for (var i = 0; i < filters.Length; i++)
                    if (ImGui.Selectable(filters[i], filter == i))
                        filter = i;
        }

        if (detailTab == DresserTab)
        {
            ImGui.SameLine();
            Widgets.Checkbox(T("ArmoireOnly") + "##mvArmoireOnly", ref armoireOnly);
        }

        ImGui.SameLine();
        Widgets.SearchField("##mvGlamourSearch", ref search, T("SearchHint"), Widgets.Px(220));

        using (var tabs = ImRaii.TabBar("##mvGlamourStorages"))
        {
            if (tabs)
                for (var i = 0; i < TabNames.Length; i++)
                {
                    var flags = selectTab && i == detailTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    if (!ImGui.BeginTabItem($"{T(TabNames[i])}##mvGlamourTab{i}", flags))
                        continue;
                    if (!selectTab)
                        detailTab = i;
                    ImGui.EndTabItem();
                }
        }

        selectTab = false;
        RefreshDetails(snapshot);
        if (detailTab == OutfitsTab)
            DrawOutfits();
        else
            DrawItems();
    }

    private void DrawItems()
    {
        var container = detailCharacter?.Container(0, detailTab == DresserTab ? ContainerKinds.GlamourDresser : ContainerKinds.Armoire);
        if (container is null)
        {
            Widgets.TextMuted(T("NotOpenedHint"), true);
            return;
        }

        Widgets.TextMuted(string.Format(T("Count"), items.Length, GameText.Age(container.LastUpdatedTicks)));
        IconGrid.Draw("##mvGlamourGrid", items.Length, i => new IconCell(items[i].IconId, Badge: items[i].Qty > 1 ? items[i].Qty.ToString() : null), i =>
        {
            var entry = items[i];
            GameItemTooltip.ShowOnHover(entry.ItemId, () =>
            {
                ImGui.TextUnformatted(entry.Hq ? $"{entry.Name} {SeIconChar.HighQuality.ToIconString()}" : entry.Name);
                if (entry.Qty > 1)
                    Widgets.TextMuted(string.Format(T("Copies"), entry.Qty));
                if (entry.Outfit is { } outfitName)
                    Widgets.TextMuted(string.Format(T("InOutfit"), outfitName));
            });
        });
    }

    private void DrawOutfits()
    {
        if (detailCharacter?.Outfits is not { } data)
        {
            Widgets.TextMuted(T("NotOpenedHint"), true);
            return;
        }

        Widgets.TextMuted(string.Format(T("OutfitCount"), outfitCounts.Complete, outfitCounts.Partial, OutfitCatalog.All.Count, GameText.Age(data.UpdatedTicks)));
        IconGrid.Draw("##mvOutfitGrid", outfits.Length, i =>
        {
            var (outfit, owned) = outfits[i];
            return new IconCell(outfit.IconId, owned == 0, owned > 0 && owned < outfit.PieceCount ? $"{owned}/{outfit.PieceCount}" : null);
        }, i =>
        {
            var (outfit, _) = outfits[i];
            var slots = data.SlotsOf(outfit.SetId);
            GameItemTooltip.ShowOnHover(outfit.SetId, () =>
            {
                ImGui.TextUnformatted(outfit.Name);
                for (var slot = 0; slot < outfit.Pieces.Length; slot++)
                {
                    if (outfit.Pieces[slot] == 0)
                        continue;
                    var name = GameText.Item(outfit.Pieces[slot]);
                    if ((slots & (1 << slot)) != 0)
                        Widgets.TextColored(V2Palette.Success, name);
                    else
                        Widgets.TextMuted(name);
                }
            });
        });
    }

    private void RefreshDetails(Snapshot snapshot)
    {
        var state = (snapshot.Version, detailOwner, detailTab, filter, search, armoireOnly);
        if (state.Equals(detailState))
            return;
        detailState = state;
        detailCharacter = snapshot.Find(detailOwner);

        var container = detailTab == OutfitsTab ? null : detailCharacter?.Container(0, detailTab == DresserTab ? ContainerKinds.GlamourDresser : ContainerKinds.Armoire);
        var outfitPieces = detailTab == DresserTab ? detailCharacter?.Outfits : null;
        items = container is null
            ? []
            : [.. container.Stacks
                .SelectMany(s => Expand(s, outfitPieces))
                .Where(e => Matches(e.Name) && (!armoireOnly || detailTab != DresserTab || ArmoireItems.Value.Contains(e.ItemId)))
                .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];

        var collected = detailCharacter?.Outfits?.Sets.ToDictionary(s => s.SetId, s => s.Slots);
        outfitCounts = detailCharacter?.Outfits is { } counted ? Count(counted) : default;
        outfits = detailTab != OutfitsTab || collected is null
            ? []
            : [.. OutfitCatalog.All
                .Select(o => (Outfit: o, Owned: o.Owned(collected.GetValueOrDefault(o.SetId))))
                .Where(x => filter switch
                {
                    1 => x.Owned == x.Outfit.PieceCount,
                    2 => x.Owned > 0 && x.Owned < x.Outfit.PieceCount,
                    3 => x.Owned == 0,
                    _ => true,
                })
                .Where(x => Matches(x.Outfit.Name))
                .OrderBy(x => x.Outfit.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    private bool Matches(string name) => search.Length == 0 || name.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        rows = [.. snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character)
            .Select(c =>
            {
                var dresser = c.Container(0, ContainerKinds.GlamourDresser);
                var armoire = c.Container(0, ContainerKinds.Armoire);
                var counts = c.Outfits is { } data ? Count(data) : ((int Complete, int Partial)?)null;
                return new Row(
                    c.Id, Names.Owner(snapshot, c),
                    dresser?.Stacks.Sum(s => s.Qty),
                    counts?.Complete,
                    counts?.Partial ?? 0,
                    armoire?.Stacks.Length,
                    Math.Max(Math.Max(dresser?.LastUpdatedTicks ?? 0, armoire?.LastUpdatedTicks ?? 0), c.Outfits?.UpdatedTicks ?? 0));
            })];
        sortDirty = true;
    }

    private static (int Complete, int Partial) Count(OutfitData data)
    {
        var complete = 0;
        var partial = 0;
        foreach (var set in data.Sets)
        {
            if (OutfitCatalog.Find(set.SetId) is not { } outfit || outfit.Owned(set.Slots) is not (var owned and > 0))
                continue;
            if (owned == outfit.PieceCount)
                complete++;
            else
                partial++;
        }

        return (complete, partial);
    }

    private sealed record Row(ulong Id, string Name, int? Dresser, int? Complete, int Partial, int? Armoire, long Updated);

    private static IEnumerable<Entry> Expand(Stack stack, OutfitData? collected)
    {
        if (collected is not null && OutfitCatalog.Find(stack.ItemId) is { } outfit && outfit.PiecesIn(collected.SlotsOf(outfit.SetId)).ToArray() is { Length: > 0 } pieces)
            return pieces.Select(p => new Entry(p, false, 1, GameText.Item(p), GameText.ItemIcon(p), outfit.Name));
        return [new Entry(stack.ItemId, stack.IsHq, stack.Qty, GameText.Item(stack.ItemId), GameText.ItemIcon(stack.ItemId))];
    }

    private sealed record Entry(uint ItemId, bool Hq, int Qty, string Name, uint IconId, string? Outfit = null);
}
