using System.Globalization;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using MogVault.Contract;
using MogVault.Store;
using Dalamud.Interface.Textures;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Game;

internal sealed class CollectionCatalog
{
    private const uint CompanionAction = 853;
    private const uint BuddyEquipAction = 1013;
    private const uint MountAction = 1322;
    private const uint UnlockLinkAction = 2633;
    private const uint TripleTriadCardAction = 3357;
    private const uint OrnamentAction = 20086;
    private const uint OrchestrionAction = 25183;
    private const uint GlassesAction = 37312;
    private const uint OrchestrionRollIcon = 60173;
    private const uint OrnamentIconFolder = 8000;

    private readonly IDataManager data;
    private readonly Lazy<ItemLinks> links;

    public CollectionCatalog(IDataManager data, IUnlockState unlocks)
    {
        this.data = data;
        links = new Lazy<ItemLinks>(LoadItemLinks);
        Sources =
        [
            Source(CollectionKind.Mounts, data.GetExcelSheet<Mount>(), r => !r.Singular.IsEmpty && r.Icon != 0, unlocks.IsMountUnlocked,
                   MountAction, r => r.RowId, r => Title(r.Singular.ExtractText()), r => r.Icon),
            Source(CollectionKind.Minions, data.GetExcelSheet<Companion>(), r => !r.Singular.IsEmpty && r.Icon != 0, unlocks.IsCompanionUnlocked,
                   CompanionAction, r => r.RowId, r => Title(r.Singular.ExtractText()), r => r.Icon),
            Source(CollectionKind.Orchestrion, data.GetExcelSheet<Orchestrion>(), r => !r.Name.IsEmpty, unlocks.IsOrchestrionUnlocked,
                   OrchestrionAction, r => r.RowId, r => r.Name.ExtractText(), _ => 0),
            Source(CollectionKind.Emotes, data.GetExcelSheet<Emote>(), r => !r.Name.IsEmpty && r.Icon != 0 && r.UnlockLink != 0, unlocks.IsEmoteUnlocked,
                   UnlockLinkAction, r => r.UnlockLink, r => r.Name.ExtractText(), r => r.Icon),
            Source(CollectionKind.FashionAccessories, data.GetExcelSheet<Ornament>(), r => !r.Singular.IsEmpty && r.Icon >= OrnamentIconFolder, unlocks.IsOrnamentUnlocked,
                   OrnamentAction, r => r.RowId, r => Title(r.Singular.ExtractText()), r => r.Icon),
            Source(CollectionKind.Facewear, data.GetExcelSheet<Glasses>(), r => !r.Singular.IsEmpty, unlocks.IsGlassesUnlocked,
                   GlassesAction, r => r.RowId, r => Title(r.Singular.ExtractText()), r => (uint)r.Icon),
            Source(CollectionKind.TripleTriadCards, data.GetExcelSheet<TripleTriadCard>(), r => r.RowId != 0 && !r.Name.IsEmpty, unlocks.IsTripleTriadCardUnlocked,
                   TripleTriadCardAction, r => r.RowId, r => r.Name.ExtractText(), _ => 0),
            Source(CollectionKind.Bardings, data.GetExcelSheet<BuddyEquip>(), r => !r.Name.IsEmpty, unlocks.IsBuddyEquipUnlocked,
                   BuddyEquipAction, r => r.RowId, r => r.Name.ExtractText(), r => r.IconBody != 0 ? r.IconBody : r.IconHead != 0 ? r.IconHead : r.IconLegs),
        ];
    }

    public CollectionSource[] Sources { get; }

    public CollectionSource? Find(CollectionKind kind) => Array.Find(Sources, s => s.Kind == kind);

    public CollectionsData Read() => new(DateTime.UtcNow.Ticks, [.. Sources.Select(s => new CollectionSet(s.Kind, s.ReadUnlocked()))]);

    public bool TryFindItem(uint itemId, out CollectionKind kind, out uint rowId)
    {
        var found = links.Value.ByItem.TryGetValue(itemId, out var link);
        (kind, rowId) = link;
        return found;
    }

    public uint[] ItemsFor(CollectionKind kind, uint rowId) => links.Value.ByRow.TryGetValue((kind, rowId), out var items) ? items : [];

    public HashSet<uint> UnlockItems() => [.. links.Value.ByItem.Keys];

    private CollectionSource Source<T>(
        CollectionKind kind, ExcelSheet<T> sheet, Func<T, bool> include, Func<T, bool> unlocked,
        uint unlockAction, Func<T, uint> unlockKey, Func<T, string> name, Func<T, uint> icon)
        where T : struct, IExcelRow<T>
    {
        var rows = sheet.Where(include).ToArray();
        var ids = rows.Select(r => r.RowId).ToArray();
        var size = ids.Length == 0 ? 0 : (int)(ids.Max() >> 3) + 1;
        var rowsByKey = rows.GroupBy(unlockKey).ToDictionary(g => g.Key, g => g.Select(r => r.RowId).ToArray());
        return new CollectionSource(
            kind, ids, unlockAction, rowsByKey,
            () =>
            {
                var bits = new byte[size];
                foreach (var row in rows)
                    if (unlocked(row))
                        bits[row.RowId >> 3] |= (byte)(1 << (int)(row.RowId & 7));
                return bits;
            },
            new Lazy<CollectionEntry[]>(() => rows.Select(r =>
            {
                var item = links.Value.Display.TryGetValue((kind, r.RowId), out var found) ? found : (Item?)null;
                var iconId = icon(r) is var own and not 0 && IconExists(own) ? own : item?.Icon ?? 0u;
                if (iconId == 0 && kind == CollectionKind.Orchestrion)
                    iconId = OrchestrionRollIcon;
                return new CollectionEntry(r.RowId, name(r), iconId, item?.RowId ?? 0);
            }).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray()));
    }

    private ItemLinks LoadItemLinks()
    {
        var sources = Sources.ToDictionary(s => s.UnlockAction);
        var display = new Dictionary<(CollectionKind, uint), Item>();
        var byItem = new Dictionary<uint, (CollectionKind, uint)>();
        var byRow = new Dictionary<(CollectionKind, uint), List<uint>>();
        foreach (var item in data.GetExcelSheet<Item>())
        {
            if (item.ItemAction.ValueNullable is not { } action || !sources.TryGetValue(action.Action.RowId, out var source))
                continue;
            var key = source.UnlockAction is OrchestrionAction or GlassesAction or TripleTriadCardAction ? item.AdditionalData.RowId : action.Data[0];
            if (key == 0 || !source.RowsByKey.TryGetValue(key, out var rowIds))
                continue;
            byItem[item.RowId] = (source.Kind, rowIds[0]);
            foreach (var rowId in rowIds)
            {
                display.TryAdd((source.Kind, rowId), item);
                (CollectionsMarshal.GetValueRefOrAddDefault(byRow, (source.Kind, rowId), out _) ??= []).Add(item.RowId);
            }
        }

        return new ItemLinks(display, byItem, byRow.ToDictionary(p => p.Key, p => p.Value.ToArray()));
    }

    private static bool IconExists(uint iconId) =>
        iconId != 0 && UnderlingsSvc.Texture.TryGetIconPath(new GameIconLookup(iconId), out _);

    private static string Title(string name) => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(name);

    private sealed record ItemLinks(
        Dictionary<(CollectionKind Kind, uint RowId), Item> Display,
        Dictionary<uint, (CollectionKind Kind, uint RowId)> ByItem,
        Dictionary<(CollectionKind Kind, uint RowId), uint[]> ByRow);
}

internal sealed record CollectionSource(
    CollectionKind Kind, uint[] RowIds, uint UnlockAction, Dictionary<uint, uint[]> RowsByKey, Func<byte[]> ReadUnlocked, Lazy<CollectionEntry[]> Entries);

internal sealed record CollectionEntry(uint RowId, string Name, uint IconId, uint ItemId);
