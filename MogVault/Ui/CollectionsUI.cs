using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Contract;
using MogVault.Game;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class CollectionsUI : ModuleUI
{
    private const int MaxOwnersInTooltip = 12;

    private long rowsKey = -1;
    private (string Name, ulong Id, int[] Owned)[] rows = [];
    private bool sortDirty;

    private bool selectDetails;
    private bool selectKind;
    private ulong detailOwner;
    private int detailKind;
    private int filter;
    private string search = string.Empty;
    private object? detailState;
    private (CollectionEntry Entry, int Owners)[] shown = [];
    private CharacterData[] holders = [];
    private int ownedCount;
    private int totalCount;

    public override string Name => "Collections";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Star;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var bar = ImRaii.TabBar("##mvCollectionTabs");
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
        var sources = Plugin.P!.Collections.Sources;
        using var table = ImRaii.Table("##mvCollectionsTable", sources.Length + 1,
                                       ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn(T("Character"));
        foreach (var source in sources)
            Columns.Icon(T(source.Kind.ToString()), KindIcon(source.Kind), "9999/9999", ImGuiTableColumnFlags.PreferSortDescending);
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Id == Plugin.P!.CurrentCharacterId, (r, column) => column == 0 ? r.Name : r.Owned[column - 1]);

        foreach (var (name, id, owned) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            for (var i = 0; i < sources.Length; i++)
            {
                ImGui.TableNextColumn();
                var total = sources[i].RowIds.Length;
                var text = $"{owned[i]}/{total}";
                if (owned[i] >= total)
                    Widgets.TextColored(V2Palette.Success, text);
                else
                    ImGui.TextUnformatted(text);
                if (!ImGui.IsItemHovered())
                    continue;
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if (total > 0)
                    ImGui.SetTooltip(string.Format(T("Tooltip"), 100 * owned[i] / total, total - owned[i]));
                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    detailOwner = id;
                    detailKind = i;
                    selectDetails = true;
                    selectKind = true;
                }
            }
        }
    }

    private static uint KindIcon(CollectionKind kind) => kind switch
    {
        CollectionKind.Mounts => 58,
        CollectionKind.Minions => 59,
        CollectionKind.Orchestrion => 67,
        CollectionKind.Emotes => 9,
        CollectionKind.FashionAccessories => 86,
        CollectionKind.Facewear => 92,
        CollectionKind.TripleTriadCards => 62,
        CollectionKind.Bardings => 49,
        _ => 0,
    };

    private void DrawDetails()
    {
        var snapshot = Plugin.P!.Query.Current;
        var sources = Plugin.P!.Collections.Sources;
        detailKind = Math.Clamp(detailKind, 0, sources.Length - 1);

        var allCharacters = T("AllCharacters");
        ImGui.SetNextItemWidth(Fit.Combo(220, rows.Select(r => r.Name).Append(allCharacters)));
        using (var combo = ImRaii.Combo("##mvCollectionOwner", snapshot.Find(detailOwner) is { } selected ? Names.Owner(snapshot, selected) : allCharacters))
        {
            if (combo)
            {
                if (ImGui.Selectable(allCharacters, detailOwner == 0))
                    detailOwner = 0;
                foreach (var row in rows.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase))
                    if (ImGui.Selectable($"{row.Name}##{row.Id}", detailOwner == row.Id))
                        detailOwner = row.Id;
            }
        }

        ImGui.SameLine();
        string[] filters = [T("ShowAll"), T("ShowOwned"), T("ShowMissing")];
        ImGui.SetNextItemWidth(Fit.Combo(130, filters));
        using (var combo = ImRaii.Combo("##mvCollectionFilter", filters[filter]))
        {
            if (combo)
                for (var i = 0; i < filters.Length; i++)
                    if (ImGui.Selectable(filters[i], filter == i))
                        filter = i;
        }

        ImGui.SameLine();
        Widgets.SearchField("##mvCollectionSearch", ref search, T("SearchHint"), Widgets.Px(220));

        using (var kinds = ImRaii.TabBar("##mvCollectionKinds"))
        {
            if (kinds)
                for (var i = 0; i < sources.Length; i++)
                {
                    var flags = selectKind && i == detailKind ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
                    if (!ImGui.BeginTabItem($"{T(sources[i].Kind.ToString())}##mvKind{i}", flags))
                        continue;
                    if (!selectKind)
                        detailKind = i;
                    ImGui.EndTabItem();
                }
        }

        selectKind = false;

        RefreshDetails(snapshot, sources[detailKind]);
        Widgets.TextMuted(string.Format(T(detailOwner == 0 ? "OwnedByAny" : "OwnedCount"), ownedCount, totalCount));
        DrawGrid(snapshot);
    }

    private void RefreshDetails(Snapshot snapshot, CollectionSource source)
    {
        var state = (Names.Key(snapshot), detailOwner, source.Kind, filter, search);
        if (state.Equals(detailState))
            return;
        detailState = state;

        holders = [.. snapshot.Characters.Where(c => c.Kind == OwnerKind.Character && c.Collections is not null && (detailOwner == 0 || c.Id == detailOwner))];
        var sets = holders.Select(c => c.Collections!.Get(source.Kind)).ToArray();
        var entries = source.Entries.Value;
        var list = new List<(CollectionEntry, int)>(entries.Length);
        ownedCount = 0;
        totalCount = entries.Length;
        foreach (var entry in entries)
        {
            var owners = sets.Count(s => s is not null && s.Has(entry.RowId));
            if (owners > 0)
                ownedCount++;
            if (filter == 1 && owners == 0 || filter == 2 && owners == holders.Length)
                continue;
            if (search.Length > 0 && !entry.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase))
                continue;
            list.Add((entry, owners));
        }

        shown = [.. list];
    }

    private void DrawGrid(Snapshot snapshot) =>
        IconGrid.Draw("##mvCollectionGrid", shown.Length,
            i => new IconCell(shown[i].Entry.IconId, shown[i].Owners == 0, detailOwner == 0 && shown[i].Owners > 0 ? shown[i].Owners.ToString() : null),
            i => DrawTooltip(snapshot, shown[i].Entry, shown[i].Owners));

    private void DrawTooltip(Snapshot snapshot, CollectionEntry entry, int owners)
    {
        void Details()
        {
            ImGui.TextUnformatted(entry.Name);
            if (detailOwner != 0)
            {
                Widgets.TextColored(owners > 0 ? V2Palette.Success : V2Palette.TextMuted, T(owners > 0 ? "Owned" : "Missing"));
                return;
            }

            Widgets.TextMuted(string.Format(T("OwnedBy"), owners, holders.Length));
            var kind = Plugin.P!.Collections.Sources[detailKind].Kind;
            var names = holders.Where(c => c.Collections!.Get(kind)?.Has(entry.RowId) == true)
                               .Select(c => Names.Owner(snapshot, c))
                               .Order(StringComparer.CurrentCultureIgnoreCase)
                               .ToArray();
            foreach (var name in names.Take(MaxOwnersInTooltip))
                ImGui.TextUnformatted(name);
            if (names.Length > MaxOwnersInTooltip)
                Widgets.TextMuted(string.Format(T("More"), names.Length - MaxOwnersInTooltip));
        }

        if (entry.ItemId != 0)
        {
            GameItemTooltip.ShowOnHover(entry.ItemId, Details);
            return;
        }

        ImGui.BeginTooltip();
        Details();
        ImGui.EndTooltip();
    }

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var sources = Plugin.P!.Collections.Sources;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character && c.Collections is not null)
            .Select(c => (Name: Names.Owner(snapshot, c), c.Id, sources.Select(s => c.Collections!.Get(s.Kind) is { } set ? s.RowIds.Count(set.Has) : 0).ToArray()))
            .ToArray();
        sortDirty = true;
    }
}
