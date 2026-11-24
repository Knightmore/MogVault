using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class VoyagesUI : ModuleUI
{
    private long rowsKey = -1;
    private Row[] rows = [];
    private bool sortDirty;

    public override string Name => "Voyages";
    public override Enum Category => MogVaultCategory.FreeCompany;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Ship;
    public override Action? Help => () => ImGui.TextUnformatted(T("Help"));
    public override string? Summary => rows.Length == 0 ? null : string.Format(T("Summary"), rows.Length, rows.Count(r => IsBack(r.Vessel)));

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvVoyagesTable", 6, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("FreeCompany"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("Type"), "Submersible");
        ImGui.TableSetupColumn(T("Vessel"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Icon(T("Rank"), FontAwesomeIcon.Star, "125");
        Columns.Icon(T("Return"), FontAwesomeIcon.HourglassHalf, "23h 59m");
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => T(r.Vessel.Kind.ToString()),
            2 => r.VesselName,
            3 => r.Vessel.Rank,
            4 => r.Vessel.ReturnTime,
            5 => r.Updated,
            _ => r.FreeCompany,
        });

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var (fcId, fc, vessel, vesselName, updated, _) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (ImGui.Selectable($"{fc}##mvFc{fcId}-{(int)vessel.Kind}-{vessel.Slot}"))
                Plugin.P!.OpenFreeCompany(fcId);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(T("OpenFreeCompany"));
            Cell(T(vessel.Kind.ToString()));
            Cell(vesselName);
            Cell(vessel.Rank.ToString());
            ImGui.TableNextColumn();
            if (vessel.ReturnTime == 0)
                Widgets.TextMuted(T("Docked"));
            else if (vessel.ReturnTime <= now)
                Widgets.TextColored(V2Palette.Success, T("Back"));
            else
                ImGui.TextUnformatted(GameText.Duration(TimeSpan.FromSeconds(vessel.ReturnTime - now)));
            Cell(GameText.Age(updated));
        }
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private static bool IsBack(Vessel v) => v.ReturnTime != 0 && v.ReturnTime <= DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var myFreeCompany = snapshot.Find(Plugin.P!.CurrentCharacterId)?.Profile?.FreeCompanyId ?? 0;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.FreeCompany && c.Workshop is not null)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(c => c.Workshop!.Vessels
                .OrderBy(v => v.Kind)
                .ThenBy(v => v.Slot)
                .Select(v => new Row(c.Id, Names.Owner(snapshot, c), v, Names.Vessel(snapshot, c, v), c.Workshop.UpdatedTicks, c.Id == myFreeCompany)))
            .ToArray();
        sortDirty = true;
    }

    private sealed record Row(ulong FreeCompanyId, string FreeCompany, Vessel Vessel, string VesselName, long Updated, bool Mine);
}

