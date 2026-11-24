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
internal sealed class CurrenciesUI : ModuleUI
{
    private const ushort CurrencyContainer = 2000;

    private long rowsKey = -1;
    private Currency[] currencies = [];
    private (string Name, long[] Amounts, bool Mine)[] rows = [];
    private bool sortDirty;

    public override string Name => "Currencies";
    public override Enum Category => MogVaultCategory.Characters;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Coins;

    public override void Draw()
    {
        Refresh();
        if (rows.Length == 0)
        {
            Widgets.TextMuted(T("Empty"), true);
            return;
        }

        using var table = ImRaii.Table("##mvCurrenciesTable", currencies.Length + 1,
                                       ImGuiTableFlags.ScrollX | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.Sortable | ImGuiTableFlags.SortTristate,
                                       new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y));
        if (!table)
            return;

        ImGui.TableSetupScrollFreeze(1, 1);
        ImGui.TableSetupColumn(T("Character"));
        foreach (var currency in currencies)
            Columns.Fixed(currency.Name, currency.ItemId == 1 ? "999,999,999" : "9,999,999", ImGuiTableColumnFlags.PreferSortDescending, measureLabel: false);

        var iconSize = new System.Numerics.Vector2(ImGui.GetTextLineHeight());
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        ImGui.TableHeader(T("Character"));
        for (var i = 0; i < currencies.Length; i++)
        {
            ImGui.TableSetColumnIndex(i + 1);
            var start = ImGui.GetCursorPos();
            ImGui.TableHeader($"##mvCurrency{i}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(currencies[i].Name);
            ImGui.SetCursorPos(start);
            if (UnderlingsSvc.Texture.GetFromGameIcon(new GameIconLookup(currencies[i].IconId)).GetWrapOrEmpty() is { } icon)
                ImGui.Image(icon.Handle, iconSize);
        }

        TableSort.Apply(ref rows, ref sortDirty, r => r.Mine, (r, column) => column == 0 ? r.Name : r.Amounts[column - 1]);

        foreach (var (name, amounts, _) in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(name);
            foreach (var amount in amounts)
            {
                ImGui.TableNextColumn();
                if (amount > 0)
                    ImGui.TextUnformatted(amount.ToString("N0"));
            }
        }
    }

    private void Refresh()
    {
        var snapshot = Plugin.P!.Query.Current;
        if (Names.Key(snapshot) == rowsKey)
            return;
        rowsKey = Names.Key(snapshot);
        var me = Plugin.P!.CurrentCharacterId;

        var holders = snapshot.Characters
            .Where(c => c.Kind == OwnerKind.Character)
            .Select(c => (Name: Names.Owner(snapshot, c), Container: c.Container(0, CurrencyContainer), Mine: c.Id == me))
            .Where(x => x.Container is not null)
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var ids = holders.SelectMany(h => h.Container!.Stacks.Select(s => s.ItemId)).Distinct().Order().ToArray();
        currencies = [.. ids.Select(id => new Currency(id, GameText.Item(id), GameText.ItemIcon(id)))];
        rows = holders.Select(h =>
        {
            var amounts = new long[ids.Length];
            foreach (var stack in h.Container!.Stacks)
                amounts[Array.BinarySearch(ids, stack.ItemId)] += h.Container.QuantityOf(stack);
            return (h.Name, amounts, h.Mine);
        }).ToArray();
        sortDirty = true;
    }

    private sealed record Currency(uint ItemId, string Name, uint IconId);
}

