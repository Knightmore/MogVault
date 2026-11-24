using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class CharactersUI : ModuleUI
{
    private const uint Gil = 1;

    private long rowsKey = -1;
    private Row[] rows = [];

    public override string Name => "Characters";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Users;
    public override string? Summary => rows.Length == 0 ? null : string.Format(T("Summary"), rows.Length, rows.Sum(r => r.TotalGil).ToString("N0"));

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvCharactersTable", 10, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn(T("Character"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("World"), "Excalibur");
        Columns.Fixed(T("FreeCompany"), rows.Select(r => r.FreeCompany).MaxBy(t => ImGui.CalcTextSize(t).X) ?? "-");
        Columns.Fixed(T("Job"), "DNC 100");
        ImGui.TableSetupColumn(T("Msq"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Icon(T("Gil"), GameText.ItemIcon(1), "999,999,999");
        Columns.Icon(T("RetainerGil"), FontAwesomeIcon.UserTie, "9,999,999,999");
        Columns.Icon(T("Total"), FontAwesomeIcon.Coins, "99,999,999,999");
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.Delete();
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => r.World,
            2 => r.FreeCompany,
            3 => r.Job,
            4 => r.Msq,
            5 => r.Gil,
            6 => r.RetainerGil,
            7 => r.TotalGil,
            8 => r.Updated,
            _ => r.Name,
        });

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            Cell(row.Name);
            Cell(row.World);
            FreeCompanyCell(row);
            Cell(row.Job);
            Cell(row.Msq);
            Cell(row.Gil.ToString("N0"));
            Cell(row.RetainerGil.ToString("N0"));
            Cell(row.TotalGil.ToString("N0"));
            Cell(GameText.Age(row.Updated));
            if (Columns.DeleteCell(row.Mine ? T("DeleteCurrent") : null, T("Delete")))
                Plugin.P!.DeleteOwner(row.Id);
        }
    }

    private void FreeCompanyCell(Row row)
    {
        ImGui.TableNextColumn();
        if (row.FreeCompanyId == 0)
        {
            ImGui.TextUnformatted(row.FreeCompany);
            return;
        }

        if (ImGui.Selectable($"{row.FreeCompany}##mvFc{row.Id}"))
            Plugin.P!.OpenFreeCompany(row.FreeCompanyId);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(T("OpenFreeCompany"));
    }

    private static void Cell(string text)
    {
        ImGui.TableNextColumn();
        ImGui.TextUnformatted(text);
    }

    private void Refresh()
    {
        var query = Plugin.P!.Query;
        var snapshot = query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var me = Plugin.P!.CurrentCharacterId;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character)
            .Select(c =>
            {
                var own = query.GetItemCount(Gil, null, c.Id, includeRetainers: false);
                var all = query.GetItemCount(Gil, null, c.Id, includeRetainers: true);
                var job = c.Profile is { } p && p.ClassJob != 0
                    ? $"{GameText.ClassJob(p.ClassJob)} {Level(p, p.ClassJob)}"
                    : "-";
                var fc = c.Profile is { FreeCompanyId: > 0 } profile ? snapshot.Find(profile.FreeCompanyId) : null;
                return new Row(c.Id, Names.Owner(snapshot, c), GameText.World(c.WorldId), fc?.Id ?? 0, fc is null ? "-" : Names.Owner(snapshot, fc), job, GameText.Quest(c.Profile?.MsqQuestId ?? 0), own, all - own, c.LastUpdatedTicks, c.Id == me);
            })
            .ToArray();
        sortDirty = true;
    }

    internal static string Level(CharacterProfile profile, byte classJob)
    {
        var row = Underlings.Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>().GetRowOrDefault(classJob);
        return row is { ExpArrayIndex: >= 0 } r && r.ExpArrayIndex < profile.JobLevels.Length ? profile.JobLevels[r.ExpArrayIndex].ToString() : "?";
    }

    private bool sortDirty;

    private sealed record Row(ulong Id, string Name, string World, ulong FreeCompanyId, string FreeCompany, string Job, string Msq, long Gil, long RetainerGil, long Updated, bool Mine)
    {
        public long TotalGil => Gil + RetainerGil;
    }
}

