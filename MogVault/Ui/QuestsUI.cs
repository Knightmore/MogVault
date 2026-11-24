using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using MogVault.Store;
using Underlings.Modules;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class QuestsUI : ModuleUI
{
    private const uint JournalIcon = 5;

    private long rowsKey = -1;
    private Row[] rows = [];
    private bool sortDirty;

    public override string Name => "Quests";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Book;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvQuestsTable", 4, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn(T("Character"), ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn(T("Msq"), ImGuiTableColumnFlags.WidthStretch);
        Columns.Icon(T("Active"), JournalIcon, "30");
        Columns.Icon(T("Updated"), FontAwesomeIcon.Clock, "30d ago");
        Columns.HeadersRow();
        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column switch
        {
            1 => r.Msq,
            2 => r.Active.Length,
            3 => r.Updated,
            _ => r.Name,
        });

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Msq);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(row.Active.Length.ToString());
            if (row.Active.Length > 0 && ImGui.IsItemHovered())
                ImGui.SetTooltip(string.Join('\n', row.Active));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(GameText.Age(row.Updated));
        }
    }

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var me = Plugin.P!.CurrentCharacterId;
        rows = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character && c.Profile is not null)
            .Select(c => new Row(
                Names.Owner(snapshot, c),
                GameText.Quest(c.Profile!.MsqQuestId),
                [.. c.Profile.ActiveQuests.Select(GameText.Quest).Order(StringComparer.CurrentCultureIgnoreCase)],
                c.Profile.UpdatedTicks,
                c.Id == me))
            .ToArray();
        sortDirty = true;
    }

    private sealed record Row(string Name, string Msq, string[] Active, long Updated, bool Mine);
}

