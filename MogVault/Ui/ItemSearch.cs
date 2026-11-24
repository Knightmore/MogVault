using Dalamud.Game.Text;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using MogVault.Contract;
using MogVault.Game;
using MogVault.Query;
using MogVault.Store;
using Underlings.Localization;

namespace MogVault.Ui;

internal sealed record ResultLine(string Location, string Qty, string Value, string Updated, bool Stale);

internal sealed class ResultGroup(uint itemId, bool hq, string name, uint iconId, long total, long value, long oldestUpdate, ResultLine[] lines)
{
    public uint ItemId { get; } = itemId;
    public bool Hq { get; } = hq;
    public string Name { get; } = name;
    public uint IconId { get; } = iconId;
    public long TotalQty { get; } = total;
    public string Total { get; } = total.ToString("N0");
    public long TotalValue { get; } = value;
    public string Value { get; } = ItemSearch.Gil(value);
    public long OldestUpdate { get; } = oldestUpdate;
    public ResultLine[] Lines { get; } = lines;
    public uint Key => ItemId | (Hq ? Stack.HqBit : 0);

    public ISharedImmediateTexture? Icon { get; set; }
}

internal sealed record ResultView(long Key, ResultGroup[] Groups, string Status);

internal sealed class ItemSearch(IDataManager data, VaultQuery query)
{
    public static string TypeName(ContainerType type) => Loc.G($"Container.{type}");

    public static string Gil(long value) => value > 0 ? value.ToString("N0") : "-";

    private static long VendorValue(in ItemLocation hit, uint price)
    {
        long unit = hit.Stack.IsHq ? (price * 11L + 9) / 10 : price;
        if (hit.Container.TryGetExtra(hit.Stack, out var extra))
        {
            var materia = 0;
            for (var i = 0; i < StackExtra.MateriaSlots; i++)
                if (extra.Materia(i) != 0)
                    materia++;
            if (materia > 0)
                unit = (unit * (materia + 10) + 9) / 10;
        }

        return unit * hit.Quantity;
    }

    private (uint Id, string Name)[]? names;
    private Match? lastMatch;

    public void ReleaseNames()
    {
        Volatile.Write(ref names, null);
        Volatile.Write(ref lastMatch, null);
    }

    public ResultView Run(string text, QueryFilter filter, int staleDays)
    {
        var snapshot = query.Current;
        var hits = new List<ItemLocation>();
        var ids = Array.Empty<uint>();
        if (text.Length == 0)
        {
            query.FindAll(filter, hits);
        }
        else
        {
            ids = MatchItems(text);
            if (ids.Length == 0)
                return new ResultView(Names.Key(snapshot), [], "No item matches that name.");
            query.Find(ids, filter, hits);
        }

        hits.RemoveAll(h => h.Container.Type == ContainerType.RetainerMarket);

        var items = data.GetExcelSheet<Item>();
        var worlds = data.GetExcelSheet<World>();
        var now = DateTime.UtcNow.Ticks;
        var staleTicks = TimeSpan.FromDays(Math.Max(1, staleDays)).Ticks;

        var groups = hits
            .GroupBy(h => h.Stack.ItemIdHq)
            .Select(g =>
            {
                var first = g.First().Stack;
                var row = items.GetRowOrDefault(first.ItemId);
                var name = row?.Name.ExtractText() ?? $"#{first.ItemId}";
                var price = row?.PriceLow ?? 0;
                if (first.IsHq)
                    name += " " + SeIconChar.HighQuality.ToIconString();

                var lines = g
                    .GroupBy(h => (h.Owner, h.Container.RetainerId, h.Container.Type, h.OutfitId))
                    .Select(l =>
                    {
                        var (owner, retainerId, type, outfitId) = l.Key;
                        var qty = l.Sum(h => h.Quantity);
                        var value = l.Sum(h => VendorValue(h, price));
                        var updated = l.Min(h => h.Container.LastUpdatedTicks);
                        return (Qty: qty, Value: value, Line: new ResultLine(
                            Location(snapshot, owner, retainerId, type, worlds, outfitId: outfitId),
                            qty.ToString("N0"),
                            Gil(value),
                            GameText.Age(updated),
                            now - updated > staleTicks));
                    })
                    .OrderByDescending(l => l.Qty)
                    .ToArray();

                var total = lines.Sum(l => l.Qty);
                var oldest = g.Min(h => h.Container.LastUpdatedTicks);
                return new ResultGroup(first.ItemId, first.IsHq, name, row?.Icon ?? 0, total, lines.Sum(l => l.Value), oldest, [.. lines.Select(l => l.Line)]);
            })
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var status = groups.Length > 0 ? $"{groups.Length} item(s) found."
            : text.Length == 0 ? "Nothing stored for this filter."
            : $"{ids.Length} matching item(s), none stored.";
        return new ResultView(Names.Key(snapshot), groups, status);
    }

    private uint[] MatchItems(string text)
    {
        if (Volatile.Read(ref lastMatch) is { } m && m.Text == text)
            return m.Ids;

        var all = Volatile.Read(ref names) ?? LoadNames();
        var result = new List<uint>();
        foreach (var (id, name) in all)
            if (name.Contains(text, StringComparison.OrdinalIgnoreCase))
                result.Add(id);

        var ids = result.ToArray();
        Array.Sort(ids);
        Volatile.Write(ref lastMatch, new Match(text, ids));
        return ids;
    }

    private sealed record Match(string Text, uint[] Ids);

    private (uint, string)[] LoadNames()
    {
        var list = new List<(uint, string)>(50000);
        foreach (var row in data.GetExcelSheet<Item>())
        {
            var name = row.Name.ExtractText();
            if (name.Length > 0 && row.RowId <= Stack.ItemIdMask)
                list.Add((row.RowId, name));
        }

        var array = list.ToArray();
        Volatile.Write(ref names, array);
        return array;
    }

    internal static string Location(Snapshot snapshot, CharacterData owner, ulong retainerId, ContainerType type, Lumina.Excel.ExcelSheet<World> worlds, bool tooltip = false, uint outfitId = 0)
    {
        var world = worlds.GetRowOrDefault(owner.WorldId)?.Name.ExtractText() ?? owner.WorldId.ToString();
        var name = tooltip ? owner.Name : Names.Owner(snapshot, owner);
        var who = owner.Kind == OwnerKind.FreeCompany ? $"FC {name} ({world})" : $"{name} ({world})";
        var where = outfitId != 0 && OutfitCatalog.Find(outfitId) is { } outfit ? $"{TypeName(type)} ({outfit.Name})" : TypeName(type);
        if (retainerId == 0)
            return $"{who} > {where}";
        var retainer = owner.RetainerName(retainerId) ?? "Retainer";
        if (tooltip)
            return $"{who} > {retainer}";
        return $"{who} > {Names.Retainer(snapshot, retainerId, retainer)} > {where}";
    }
}

