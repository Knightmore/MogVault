using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Lumina.Excel.Sheets;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui.V2;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

[Module]
internal sealed class JobsUI : ModuleUI
{
    private const uint JobIconBase = 62100;
    private const string LevelSample = "100";
    private const uint BlueMage = 36;
    private const uint Beastmaster = 43;

    private long rowsKey = -1;
    private Job[]? jobs;
    private (string Name, short[] Levels, bool Mine)[] rows = [];
    private bool sortDirty;
    private short maxLevel;

    public override string Name => "Jobs";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.ChartBar;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        var columns = jobs!;
        var nameWidth = rows.Max(r => ImGui.CalcTextSize(r.Name).X) + ImGui.GetStyle().CellPadding.X * 2;
        var fits = nameWidth + Columns.CompactWidth(LevelSample) * columns.Length <= ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize;
        var sizing = fits ? ImGuiTableFlags.SizingStretchSame : ImGuiTableFlags.ScrollX | ImGuiTableFlags.SizingFixedFit;
        using var table = ImRaii.Table("##mvJobsTable", columns.Length + 1,
                                       ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate | sizing,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn(T("Character"), ImGuiTableColumnFlags.WidthFixed, nameWidth);
        foreach (var job in columns)
            Columns.CompactIcon(job.Abbreviation, JobIconBase + job.RowId, fits, LevelSample, ImGuiTableColumnFlags.PreferSortDescending, job.Name);
        Columns.HeadersRow();

        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) =>
            column == 0 ? r.Name : columns[column - 1].ExpIndex < r.Levels.Length ? r.Levels[columns[column - 1].ExpIndex] : (short)0);

        foreach (var (name, levels, _) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            foreach (var job in columns)
            {
                ImGui.TableNextColumn();
                var level = job.ExpIndex < levels.Length ? levels[job.ExpIndex] : (short)0;
                if (level <= 0)
                    continue;
                if (level >= Math.Min(maxLevel, job.MaxLevel))
                    Widgets.TextColored(V2Palette.Success, level.ToString());
                else
                    ImGui.TextUnformatted(level.ToString());
            }
        }
    }

    private void Refresh()
    {
        jobs ??= LoadJobs();
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var me = Plugin.P!.CurrentCharacterId;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character && c.Profile is not null)
            .Select(c => (Names.Owner(snapshot, c), c.Profile!.JobLevels, c.Id == me))
            .ToArray();
        sortDirty = true;
        maxLevel = rows.Length == 0 ? (short)0 : rows.Max(r => r.Levels.DefaultIfEmpty().Max());
    }

    private static Job[] LoadJobs() =>
        UnderlingsSvc.Data.GetExcelSheet<ClassJob>()
            .Where(j => j.RowId != 0 && j.ExpArrayIndex >= 0)
            .GroupBy(j => j.ExpArrayIndex)
            .Select(g => g.OrderByDescending(j => j.JobIndex).First())
            .OrderBy(j => j.UIPriority)
            .Select(j => new Job(j.RowId, j.Abbreviation.ExtractText(), j.Name.ExtractText(), j.ExpArrayIndex, MaxLevelOf(j.RowId)))
            .ToArray();

    private static short MaxLevelOf(uint classJob) => classJob switch
    {
        BlueMage => 80,
        Beastmaster => 50,
        _ => short.MaxValue,
    };

    private sealed record Job(uint RowId, string Abbreviation, string Name, int ExpIndex, short MaxLevel);
}

