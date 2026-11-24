using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility.Raii;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui.V2;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

[Module]
internal sealed class FreeCompaniesUI : ModuleUI
{
    private const uint Gil = 1;
    private const uint CreditsIcon = 65011;
    private const uint CharacterIcon = 1;
    private const uint GrandCompanyIcon = 60870;
    private const long FocusMs = 3000;

    private long rowsKey = -1;
    private ulong focusId;
    private long focusUntil;
    private bool scrollToFocus;
    private Row[] rows = [];
    private bool sortDirty;

    public override string Name => "FreeCompanies";
    public override Enum Category => MogVaultCategory.FreeCompany;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Flag;
    public override Action? Help => () => ImGui.TextUnformatted(T("Help"));

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvFreeCompaniesTable", 11, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("FreeCompany"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Fixed(T("World"), "Excalibur");
        Columns.Icon(T("Rank"), FontAwesomeIcon.Star, "30", compact: true);
        Columns.Icon(T("GrandCompany"), FontAwesomeIcon.ShieldAlt,
            ImGui.GetTextLineHeight() + ImGui.GetStyle().ItemSpacing.X + GameText.Standings.Max(s => ImGui.CalcTextSize(s).X));
        Columns.Icon(T("Members"), FontAwesomeIcon.Users, "512", compact: true);
        Columns.Icon(T("ChestGil"), GameText.ItemIcon(1), "9,999,999,999");
        Columns.Icon(T("Credits"), CreditsIcon, "99,999,999");
        Columns.Fixed(T("Founded"), "12/31/2026");
        Columns.Icon(T("Characters"), CharacterIcon, "99", compact: true);
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.Delete();
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => r.World,
            2 => r.Rank,
            3 => r.Standing is { } s ? s.Level * 1000 + s.Percent : -1,
            4 => r.Members,
            5 => r.ChestGil,
            6 => r.Credits,
            7 => r.Status?.FoundedTicks is > 0 and var founded ? founded : long.MaxValue,
            8 => r.Characters.Length,
            9 => r.Updated,
            _ => r.Name,
        });

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            if (row.Id == focusId)
                HighlightFocus();
            Cell(row.Name);
            Cell(row.World);
            Cell(row.Rank?.ToString() ?? "-");
            if (row is { Rank: { } rank, Status: { RankPointsNeeded: > 0 } progress } && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(T("RankProgress"), progress.RankPoints, progress.RankPointsNeeded, rank + 1));
            GrandCompanyCell(row);
            Cell(row.Members?.ToString() ?? "-");
            if (row.MemberNames.Length > 0 && ImGui.IsItemHovered())
                MembersTooltip(row.MemberNames);
            Cell(row.ChestGil?.ToString("N0") ?? "-");
            Cell(row.Credits?.ToString("N0") ?? "-");
            Cell(row.Status?.FoundedTicks is > 0 and var ticks ? new DateTime(ticks).ToShortDateString() : "-");
            if (row.Status?.FoundedTicks is > 0 and var foundedTicks && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Format(T("DaysAgo"), (DateTime.Today - new DateTime(foundedTicks)).Days));
            Cell(row.Characters.Length.ToString());
            if (row.Characters.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join('\n', row.Characters));
            Cell(GameText.Age(row.Updated));
            if (Columns.DeleteCell(row.Mine ? T("DeleteCurrent") : null, T("Delete")))
                Plugin.P!.DeleteOwner(row.Id);
        }
    }

    internal void Focus(ulong id)
    {
        focusId = id;
        focusUntil = Environment.TickCount64 + FocusMs;
        scrollToFocus = true;
    }

    private void HighlightFocus()
    {
        if (scrollToFocus)
        {
            ImGui.SetScrollHereY(0.5f);
            scrollToFocus = false;
        }

        var left = focusUntil - Environment.TickCount64;
        if (left > 0)
            ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, ImGui.GetColorU32(V2Palette.Purple with { W = 0.45f * left / FocusMs }));
    }

    private void GrandCompanyCell(Row row)
    {
        ImGui.TableNextColumn();
        ImGui.BeginGroup();
        if (row.GrandCompanyId > 0)
        {
            var icon = UnderlingsSvc.Texture.GetFromGameIcon(new GameIconLookup(GrandCompanyIcon + row.GrandCompanyId)).GetWrapOrEmpty();
            ImGui.Image(icon.Handle, new System.Numerics.Vector2(ImGui.GetTextLineHeight()));
            ImGui.SameLine();
        }

        ImGui.TextUnformatted(row.Standing is { Level: > 0 } standing ? GameText.Standing(standing.Level) : "-");
        ImGui.EndGroup();
        if (!ImGui.IsItemHovered())
            return;
        var lines = row.Status is { } status
            ? new byte[] { 1, 2, 3 }.Where(gc => status.For(gc).Level > 0).Select(gc => StandingLine(gc, status.For(gc))).ToArray()
            : [];
        ImGui.SetTooltip(lines.Length > 0 ? string.Join('\n', lines) : row.GrandCompany);
    }

    private static void MembersTooltip(string[] names)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 35f);
        ImGui.TextUnformatted(string.Join(", ", names));
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    private string StandingLine(byte grandCompany, Standing standing) =>
        string.Format(T("StandingLine"), GameText.GrandCompany(grandCompany), GameText.Standing(standing.Level), standing.Percent);

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
        var myFreeCompany = snapshot.Find(Plugin.P!.CurrentCharacterId)?.Profile?.FreeCompanyId ?? 0;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.FreeCompany)
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(fc =>
            {
                var profile = fc.FreeCompany;
                var members = snapshot.Characters
                    .Where(c => c.Profile?.FreeCompanyId == fc.Id)
                    .Select(c => Names.Owner(snapshot, c))
                    .Order(StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
                long? chestGil = fc.Containers.Length == 0 ? null : query.GetItemCount(Gil, null, fc.Id, includeRetainers: false);
                return new Row(
                    fc.Id, Names.Owner(snapshot, fc), GameText.World(fc.WorldId),
                    profile?.Rank,
                    profile is { } p ? GameText.GrandCompany(p.GrandCompany) : "-", profile?.GrandCompany ?? 0,
                    profile is { TotalMembers: > 0 } m ? m.TotalMembers : null, fc.Members?.Names ?? [],
                    chestGil, fc.Credits?.Amount, members, fc.LastUpdatedTicks, fc.Id == myFreeCompany,
                    fc.Status, profile is { } own && fc.Status is { } status ? status.For(own.GrandCompany) : null);
            })
            .ToArray();
        sortDirty = true;
    }

    private sealed record Row(
        ulong Id, string Name, string World, byte? Rank, string GrandCompany, byte GrandCompanyId, ushort? Members, string[] MemberNames, long? ChestGil, uint? Credits, string[] Characters, long Updated, bool Mine,
        FreeCompanyStatus? Status, Standing? Standing);
}

