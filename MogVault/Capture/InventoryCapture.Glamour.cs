using System.Buffers;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using MogVault.Game;
using MogVault.Store;
using CabinetRow = Lumina.Excel.Sheets.Cabinet;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private const int CacheImportAttempts = 6;
    private const uint HqItemOffset = 1000000;

    private static readonly Lazy<(uint CabinetId, uint ItemId)[]> CabinetItems = new(() =>
        [.. Underlings.Svc.Data.GetExcelSheet<CabinetRow>().Where(c => c.Item.RowId is > 0 and <= Stack.ItemIdMask).Select(c => (c.RowId, c.Item.RowId))]);

    private ulong cacheImportedFor;
    private int cacheImportAttempts;
    private Stack[]? lastDresser;
    private Stack[]? lastArmoire;
    private OutfitData? lastOutfits;
    private readonly Task sheetsLoaded;

    private void CaptureSheetBacked(OwnerInfo owner, bool quests)
    {
        if (cacheImportedFor != owner.Id)
            ImportGameCache(owner);

        if (quests && ReadCompletedQuests() is var completed && !completed.SameAs(lastQuests))
        {
            writer.Post(new OwnerCapture(new OwnerUpdate(owner, Quests: completed)));
            lastQuests = completed;
        }

        CaptureGlamour(owner, ContainerKinds.GlamourDresser, ReadDresser(), ref lastDresser);
        CaptureGlamour(owner, ContainerKinds.Armoire, ReadArmoire(), ref lastArmoire);
        if (ReadOutfits() is { } outfits && !outfits.SameAs(lastOutfits))
        {
            writer.Post(new OwnerCapture(new OwnerUpdate(owner, Outfits: outfits)));
            lastOutfits = outfits;
        }
    }

    private void ImportGameCache(OwnerInfo owner)
    {
        if (!GameCache.Available)
            return;
        var cachedRetainers = GameCache.Retainers();
        var dresser = GameCache.Dresser();
        if (cachedRetainers.Count == 0 && dresser is null && ++cacheImportAttempts < CacheImportAttempts)
            return;
        cacheImportedFor = owner.Id;
        cacheImportAttempts = 0;

        var saved = query.Current.Find(owner.Id);
        foreach (var (retainerId, type, stacks) in cachedRetainers)
        {
            if (saved is null || Array.Find(saved.Retainers, r => r.Id == retainerId) is not { } retainer)
                continue;
            var kind = ContainerKinds.Categorize(type);
            if (!Array.Exists(saved.Containers, c => c.RetainerId == retainerId && c.Type == kind))
                PostCached(owner, retainer, type, stacks);
        }

        if (dresser is not null && saved?.Container(0, ContainerKinds.GlamourDresser) is null)
            PostCached(owner, null, ContainerKinds.GlamourDresser, dresser);
        if (saved?.Outfits is null && GameCache.Outfits() is { } outfits)
            writer.Post(new OwnerCapture(new OwnerUpdate(owner, Outfits: outfits)));
    }

    private void PostCached(OwnerInfo owner, RetainerInfo? retainer, ushort type, Stack[] stacks)
    {
        var buffer = ArrayPool<Stack>.Shared.Rent(Math.Max(1, stacks.Length));
        stacks.CopyTo(buffer, 0);
        writer.Post(new ContainerCapture(owner, retainer, type, 0, buffer, stacks.Length, null, 0));
    }

    private void CaptureGlamour(OwnerInfo owner, ushort type, Stack[]? stacks, ref Stack[]? last)
    {
        if (stacks is null || last is not null && MemoryMarshal.AsBytes(stacks.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(last.AsSpan())))
            return;
        var buffer = ArrayPool<Stack>.Shared.Rent(Math.Max(1, stacks.Length));
        stacks.CopyTo(buffer, 0);
        writer.Post(new ContainerCapture(owner, null, type, DateTime.UtcNow.Ticks, buffer, stacks.Length, null, 0));
        last = stacks;
    }

    private static unsafe Stack[]? ReadDresser()
    {
        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            return null;
        return [.. mirage->PrismBoxItemIds.ToArray()
            .Where(id => id != 0 && id % HqItemOffset <= Stack.ItemIdMask)
            .GroupBy(id => id)
            .Select(g => new Stack(g.Key % HqItemOffset, g.Key >= HqItemOffset, (ushort)g.Count(), 0))
            .OrderBy(s => s.ItemIdHq)];
    }

    private static unsafe OutfitData? ReadOutfits()
    {
        var mirage = MirageManager.Instance();
        if (mirage == null || !mirage->PrismBoxLoaded)
            return null;
        var sets = new SortedDictionary<uint, ushort>();
        var ids = mirage->PrismBoxItemIds;
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0 || OutfitCatalog.Find(ids[i]) is not { } outfit)
                continue;
            var slots = (ushort)0;
            for (var slot = 0; slot < OutfitCatalog.SlotCount; slot++)
                if ((outfit.Slots & (1 << slot)) != 0 && mirage->IsSetSlotUnlocked((uint)i, slot))
                    slots |= (ushort)(1 << slot);
            sets[outfit.SetId] = (ushort)(sets.GetValueOrDefault(outfit.SetId) | slots);
        }

        return new OutfitData(DateTime.UtcNow.Ticks, [.. sets.Select(p => new OutfitPieces(p.Key, p.Value))]);
    }

    private static unsafe Stack[]? ReadArmoire()
    {
        var ui = UIState.Instance();
        if (ui == null || !ui->Cabinet.IsCabinetLoaded())
            return null;
        var stacks = new List<Stack>();
        foreach (var (cabinetId, itemId) in CabinetItems.Value)
            if (ui->Cabinet.IsItemInCabinet(cabinetId))
                stacks.Add(new Stack(itemId, false, 1, 0));
        return [.. stacks.OrderBy(s => s.ItemIdHq)];
    }
}
