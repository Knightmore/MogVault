using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui;
using Underlings.Ui.V2;
using HouseId = FFXIVClientStructs.FFXIV.Client.Game.HouseId;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

[Module]
internal sealed class HousesUI : ModuleUI
{
    private const int WarningDays = 30;
    private const int DangerDays = 35;
    private const int SubdivisionOffset = 100;
    private const int NeverEntered = -1;
    private static readonly int[] DayFilters = [0, NeverEntered, 7, 14, 21, 30, 35, 40];

    private long rowsKey = -1;
    private Row[] rows = [];
    private bool sortDirty;
    private string ownerFilter = string.Empty;
    private readonly HashSet<string> typeFilter = [];
    private readonly HashSet<string> worldFilter = [];
    private readonly HashSet<string> districtFilter = [];
    private int dayFilter;

    public override string Name => "Houses";
    public override Enum Category => MogVaultCategory.Houses;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Home;
    public override System.Action? Help => () => ImGui.TextUnformatted(T("Help"));
    public override string? Summary =>
        rows.Count(r => Demolishable(r.Type)) is var estates and > 0
            ? string.Format(T("Summary"), estates, rows.Count(r => Demolishable(r.Type) && Since(r.Entered, WarningDays)))
            : null;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvHousesTable", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Owner"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("Type"), Widest(Enum.GetValues<HouseType>().Select(t => T(t.ToString()))));
        Columns.Fixed(T("World"), Widest(rows.Select(r => r.World)));
        ImGui.TableSetupColumn(T("District"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("Address"), Widest(rows.Select(r => r.Address)));
        var dayLabels = DayFilters.Select(DayLabel).ToArray();
        ImGui.TableSetupColumn(T("LastEntered"), ImGuiTableColumnFlags.WidthFixed, Fit.Combo(0, dayLabels.Append(T("LastEntered"))));
        Columns.HeadersRow();
        DrawFilters(dayLabels);
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => T(r.Type.ToString()),
            2 => r.World,
            3 => r.District,
            4 => r.Position,
            5 => r.Entered,
            _ => r.Owner,
        });

        foreach (var row in rows)
        {
            if (!Matches(row))
                continue;
            ImGui.TableNextRow();
            OwnerCell(row);
            Cell(T(row.Type.ToString()));
            Cell(row.World);
            Cell(row.District);
            Cell(row.Address);
            ImGui.TableNextColumn();
            if (row.Entered == 0)
                Widgets.TextMuted(T("Never"));
            else if (Demolishable(row.Type) && Since(row.Entered, DangerDays))
                Widgets.TextColored(V2Palette.Danger, GameText.Age(row.Entered));
            else if (Demolishable(row.Type) && Since(row.Entered, WarningDays))
                Widgets.TextColored(V2Palette.Warning, GameText.Age(row.Entered));
            else
                ImGui.TextUnformatted(GameText.Age(row.Entered));
        }
    }

    private void DrawFilters(string[] dayLabels)
    {
        if (ImGui.TableSetColumnIndex(0))
            TableFilter.Text("##mvHouseOwner", T("Owner"), ref ownerFilter, Avail);
        if (ImGui.TableSetColumnIndex(1))
            TableFilter.MultiSelect("##mvHouseType", T("Type"), typeFilter, rows.Select(r => T(r.Type.ToString())), Avail);
        if (ImGui.TableSetColumnIndex(2))
            TableFilter.MultiSelect("##mvHouseWorld", T("World"), worldFilter, rows.Select(r => r.World), Avail);
        if (ImGui.TableSetColumnIndex(3))
            TableFilter.MultiSelect("##mvHouseDistrict", T("District"), districtFilter, rows.Select(r => r.District), Avail);
        if (ImGui.TableSetColumnIndex(5))
            TableFilter.Choice("##mvHouseDays", T("LastEntered"), ref dayFilter, dayLabels, Avail);
    }

    private static float Avail => ImGui.GetContentRegionAvail().X;

    private string DayLabel(int days) => days switch
    {
        0 => T("AnyTime"),
        NeverEntered => T("NeverEntered"),
        _ => string.Format(T("DaysOrMore"), days),
    };

    private bool Matches(Row row) =>
        row.Owner.Contains(ownerFilter, StringComparison.CurrentCultureIgnoreCase) &&
        (typeFilter.Count == 0 || typeFilter.Contains(T(row.Type.ToString()))) &&
        (worldFilter.Count == 0 || worldFilter.Contains(row.World)) &&
        (districtFilter.Count == 0 || districtFilter.Contains(row.District)) &&
        DayFilters[dayFilter] switch
        {
            0 => true,
            NeverEntered => row.Entered == 0,
            var days => Since(row.Entered, days),
        };

    private void OwnerCell(Row row)
    {
        ImGui.TableNextColumn();
        if (row.FreeCompanyId == 0)
        {
            ImGui.TextUnformatted(row.Owner);
            return;
        }

        if (ImGui.Selectable($"{row.Owner}##mvFc{row.FreeCompanyId}-{row.Position}"))
            Plugin.P!.OpenFreeCompany(row.FreeCompanyId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("OpenFreeCompany"));
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static string Widest(IEnumerable<string> texts) => texts.MaxBy(t => ImGui.CalcTextSize(t).X) ?? string.Empty;

    private static bool Demolishable(HouseType type) => type is HouseType.PersonalEstate or HouseType.FreeCompanyEstate;

    private static bool Since(long ticks, int days) => ticks > 0 && DateTime.UtcNow.Ticks - ticks >= TimeSpan.TicksPerDay * days;

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var me = Plugin.P!.CurrentCharacterId;
        var myFreeCompany = snapshot.Find(me)?.Profile?.FreeCompanyId ?? 0;
        rows = snapshot.Characters
            .Where(c => c.Houses is not null)
            .SelectMany(c => c.Houses!.Houses.Select(h => ToRow(Names.Owner(snapshot, c), c.Kind == OwnerKind.FreeCompany ? c.Id : 0, h, c.Id == me || c.Id == myFreeCompany)))
            .ToArray();
        sortDirty = true;
    }

    private Row ToRow(string owner, ulong freeCompanyId, House house, bool mine)
    {
        HouseId id = house.Id;
        var ward = id.WardIndex + 1;
        var plot = id.PlotIndex + 1;
        var district = UnderlingsSvc.Data.GetExcelSheet<TerritoryType>().GetRowOrDefault(id.TerritoryTypeId)?.PlaceName.ValueNullable?.Name.ExtractText();
        var (address, position) = house.Type switch
        {
            HouseType.Apartment => (string.Format(T(id.ApartmentDivision == 0 ? "ApartmentAddress" : "SubdivisionApartmentAddress"), ward, id.RoomNumber),
                                    (ward, SubdivisionOffset + id.ApartmentDivision, (int)id.RoomNumber)),
            HouseType.PrivateChambers => (string.Format(T("ChambersAddress"), ward, plot, id.RoomNumber), (ward, plot, (int)id.RoomNumber)),
            _ => (string.Format(T("PlotAddress"), ward, plot), (ward, plot, 0)),
        };
        return new Row(owner, freeCompanyId, house.Type, GameText.World(id.WorldId), district is { Length: > 0 } ? district : $"#{id.TerritoryTypeId}", address, position, house.EnteredTicks, mine);
    }

    private sealed record Row(string Owner, ulong FreeCompanyId, HouseType Type, string World, string District, string Address, (int Ward, int Plot, int Room) Position, long Entered, bool Mine);
}
