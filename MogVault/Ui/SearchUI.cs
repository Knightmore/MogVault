using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using MogVault.Contract;
using MogVault.Query;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui;
using Underlings.Ui.V2;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

[Module]
internal sealed class SearchUI : ModuleUI
{
    private const int DebounceMs = 150;
    private const int IconRequestsPerFrame = 8;
    private const uint HqOffset = 1000000;
    private static readonly Vector4 StaleColor = new(1f, 0.6f, 0.2f, 1f);

    private readonly ItemSearch search = Plugin.P!.Search;
    private readonly VaultQuery query = Plugin.P!.Query;
    private readonly List<(int Group, int Line)> rows = [];
    private readonly HashSet<uint> expanded = [];
    private string text = string.Empty;
    private ulong ownerFilter;
    private ContainerType? typeFilter;
    private bool hqOnly;
    private long dueAt;
    private Task? running;
    private ResultView? view;
    private ResultView? shown;
    private ResultGroup[] groups = [];
    private bool rowsDirty;
    private bool sortDirty;

    public override string Name => "Search";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Search;

    public void Reset()
    {
        search.ReleaseNames();
        Volatile.Write(ref view, null);
        shown = null;
        groups = [];
        rows.Clear();
        expanded.Clear();
    }

    public override void Draw()
    {
        var now = Environment.TickCount64;

        if (Widgets.SearchField("##mvSearch", ref text, T("SearchHint"), Widgets.Avail))
            dueAt = now + DebounceMs;

        DrawFilters(now);

        var current = Volatile.Read(ref view);
        if (dueAt == 0 && running is not { IsCompleted: false } && (current is null || current.Key != Names.Key(query.Current)))
            dueAt = now;
        if (dueAt != 0 && now >= dueAt && running is not { IsCompleted: false })
            StartSearch();

        if (!ReferenceEquals(current, shown))
        {
            shown = current;
            groups = current?.Groups ?? [];
            rowsDirty = true;
            sortDirty = true;
        }

        if (rowsDirty)
            RebuildRows();

        Widgets.TextMuted(shown?.Status ?? T("Searching"));

        if (groups.Length > 0)
            DrawResults();
    }

    private void DrawFilters(long now)
    {
        var allCharacters = T("AllCharacters");
        var snapshot = query.Current;
        ImGui.SetNextItemWidth(Fit.Combo(200, [allCharacters, snapshot.Find(ownerFilter) is { } shown ? OwnerLabel(snapshot, shown) : allCharacters]));
        using (var combo = ImRaii.Combo("##mvOwner", snapshot.Find(ownerFilter) is { } selected ? OwnerLabel(snapshot, selected) : allCharacters))
        {
            if (combo)
            {
                if (ImGui.Selectable(allCharacters, ownerFilter == 0))
                    SetOwner(0, now);
                foreach (var c in snapshot.Characters)
                {
                    var label = OwnerLabel(snapshot, c);
                    if (ImGui.Selectable($"{label}##{c.Id}", ownerFilter == c.Id))
                        SetOwner(c.Id, now);
                }
            }
        }

        ImGui.SameLine();
        var allContainers = T("AllContainers");
        ImGui.SetNextItemWidth(Fit.Combo(170, Enum.GetValues<ContainerType>().Select(ItemSearch.TypeName).Append(allContainers)));
        using (var combo = ImRaii.Combo("##mvType", typeFilter is { } t ? ItemSearch.TypeName(t) : allContainers))
        {
            if (combo)
            {
                if (ImGui.Selectable(allContainers, typeFilter is null))
                {
                    typeFilter = null;
                    dueAt = now;
                }

                foreach (var type in Enum.GetValues<ContainerType>())
                {
                    if (type is ContainerType.Unknown or ContainerType.RetainerMarket)
                        continue;
                    if (ImGui.Selectable(ItemSearch.TypeName(type), typeFilter == type))
                    {
                        typeFilter = type;
                        dueAt = now;
                    }
                }
            }
        }

        ImGui.SameLine();
        if (Widgets.Checkbox(T("HqOnly") + "##mvHq", ref hqOnly))
            dueAt = now;
    }

    private void SetOwner(ulong id, long now)
    {
        ownerFilter = id;
        dueAt = now;
    }

    private static string OwnerLabel(Snapshot snapshot, CharacterData owner) =>
        owner.Kind == OwnerKind.FreeCompany ? $"FC {Names.Owner(snapshot, owner)}" : Names.Owner(snapshot, owner);

    private void StartSearch()
    {
        dueAt = 0;
        var term = text.Trim();
        var filter = new QueryFilter(hqOnly ? true : null, ownerFilter == 0 ? null : ownerFilter, typeFilter);
        var staleDays = Plugin.P!.Config.StaleDays;
        var failed = T("SearchFailed");
        running = Task.Run(() =>
        {
            try
            {
                Volatile.Write(ref view, search.Run(term, filter, staleDays));
            }
            catch (Exception ex)
            {
                UnderlingsSvc.Log.Error(ex, "MogVault search failed.");
                Volatile.Write(ref view, new ResultView(Names.Key(query.Current), [], failed));
            }
        });
    }

    private void RebuildRows()
    {
        rowsDirty = false;
        rows.Clear();
        for (var g = 0; g < groups.Length; g++)
        {
            rows.Add((g, -1));
            if (expanded.Contains(groups[g].Key))
                for (var l = 0; l < groups[g].Lines.Length; l++)
                    rows.Add((g, l));
        }
    }

    private void DrawResults()
    {
        using var table = ImRaii.Table("##mvResults", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable,
                                       new Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("ColumnItem"), ImGuiTableColumnFlags.WidthStretch | ImGuiTableColumnFlags.DefaultSort);
        Columns.Icon(T("ColumnQty"), FontAwesomeIcon.Hashtag, "99,999,999,999", ImGuiTableColumnFlags.PreferSortDescending);
        Columns.Icon(T("ColumnVendor"), GameText.ItemIcon(1), "99,999,999,999", ImGuiTableColumnFlags.PreferSortDescending, T("ColumnVendorTooltip"));
        Columns.Icon(T("ColumnUpdated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.HeadersRow();
        if (TableSort.Apply(ref groups, ref sortDirty, (g, column) => column switch
            {
                1 => g.TotalQty,
                2 => g.TotalValue,
                3 => g.OldestUpdate,
                _ => g.Name,
            }))
            RebuildRows();

        var lineHeight = ImGui.GetTextLineHeight();
        var iconBudget = IconRequestsPerFrame;
        var stale = T("Stale");
        var clipper = ImGui.ImGuiListClipper();
        clipper.Begin(rows.Count, ImGui.GetTextLineHeightWithSpacing());
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var (g, l) = rows[i];
                var group = groups[g];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (l < 0)
                    DrawGroup(group, lineHeight, ref iconBudget);
                else
                    DrawLine(group.Lines[l], stale);
            }
        }

        clipper.End();
        clipper.Destroy();
    }

    private void DrawGroup(ResultGroup group, float lineHeight, ref int iconBudget)
    {
        if (group.Icon is null && group.IconId != 0 && iconBudget-- > 0)
            group.Icon = UnderlingsSvc.Texture.GetFromGameIcon(new GameIconLookup(group.IconId, group.Hq));
        var tooltipId = group.Hq ? group.ItemId + HqOffset : group.ItemId;
        if (group.Icon?.GetWrapOrEmpty() is { } wrap)
            ImGui.Image(wrap.Handle, new Vector2(lineHeight));
        else
            ImGui.Dummy(new Vector2(lineHeight));
        GameItemTooltip.ShowOnHover(tooltipId);
        ImGui.SameLine();

        var open = expanded.Contains(group.Key);
        ImGui.SetNextItemOpen(open);
        if (ImGui.TreeNodeEx($"{group.Name}##{group.Key}", ImGuiTreeNodeFlags.NoTreePushOnOpen | ImGuiTreeNodeFlags.SpanAvailWidth) != open)
        {
            if (open)
                expanded.Remove(group.Key);
            else
                expanded.Add(group.Key);
            rowsDirty = true;
        }

        GameItemTooltip.ShowOnHover(tooltipId);

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(group.Total);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(group.Value);
    }

    private static void DrawLine(ResultLine line, string stale)
    {
        ImGui.Indent();
        ImGui.TextUnformatted(line.Location);
        ImGui.Unindent();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.Qty);
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(line.Value);
        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Text, StaleColor, line.Stale))
            ImGui.TextUnformatted(line.Stale ? $"{line.Updated} ({stale})" : line.Updated);
    }
}

