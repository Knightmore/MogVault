using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MogVault.Game;
using MogVault.Store;

namespace MogVault.Capture;

internal static unsafe class GameCache
{
    private const uint CollectibleOffset = 500000;
    private const uint HqOffset = 1000000;
    private const uint EventItemStart = 2000000;
    private const int RetainerPageSize = 25;
    private const uint FirstCrystal = 2;

    public static bool Available => ItemFinderModule.Instance() != null;

    public static List<(ulong RetainerId, ushort InventoryType, Stack[] Stacks)> Retainers()
    {
        var result = new List<(ulong, ushort, Stack[])>();
        var finder = ItemFinderModule.Instance();
        if (finder == null)
            return result;

        foreach (var (retainerId, pointer) in finder->RetainerInventories)
        {
            var inventory = pointer.Value;
            if (retainerId == 0 || inventory == null)
                continue;

            var items = inventory->ItemIds;
            var counts = inventory->ItemCount;
            for (var page = 0; page < items.Length / RetainerPageSize; page++)
            {
                var stacks = new List<Stack>();
                for (var slot = 0; slot < RetainerPageSize; slot++)
                {
                    var index = page * RetainerPageSize + slot;
                    if (ToStack(items[index], counts[index], slot) is { } stack)
                        stacks.Add(stack);
                }

                result.Add((retainerId, (ushort)((int)InventoryType.RetainerPage1 + page), [.. stacks]));
            }

            var equipped = new List<Stack>();
            var equippedIds = inventory->EquippedItemIds;
            for (var slot = 0; slot < equippedIds.Length; slot++)
                if (ToStack(equippedIds[slot], 1, slot) is { } stack)
                    equipped.Add(stack);
            result.Add((retainerId, (ushort)InventoryType.RetainerEquippedItems, [.. equipped]));

            var crystals = new List<Stack>();
            var quantities = inventory->CrystalQuantities;
            for (var slot = 0; slot < quantities.Length; slot++)
                if (quantities[slot] > 0)
                    crystals.Add(new Stack(FirstCrystal + (uint)slot, false, quantities[slot], (byte)slot));
            result.Add((retainerId, (ushort)InventoryType.RetainerCrystals, [.. crystals]));
        }

        return result;
    }

    public static Stack[]? Dresser()
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return null;
        return [.. finder->GlamourDresserItemIds.ToArray()
            .Where(id => id != 0 && id % HqOffset <= Stack.ItemIdMask)
            .GroupBy(id => id)
            .Select(g => new Stack(g.Key % HqOffset, g.Key >= HqOffset, (ushort)g.Count(), 0))
            .OrderBy(s => s.ItemIdHq)];
    }

    public static OutfitData? Outfits()
    {
        var finder = ItemFinderModule.Instance();
        if (finder == null || !finder->IsGlamourDresserCached)
            return null;

        var ids = finder->GlamourDresserItemIds;
        var missingBits = finder->GlamourDresserItemSetUnlockBits;
        var sets = new SortedDictionary<uint, ushort>();
        for (var i = 0; i < ids.Length; i++)
        {
            if (ids[i] == 0 || OutfitCatalog.Find(ids[i]) is not { } outfit)
                continue;
            var collected = (ushort)(~missingBits[i] & outfit.Slots);
            sets[outfit.SetId] = (ushort)(sets.GetValueOrDefault(outfit.SetId) | collected);
        }

        return new OutfitData(0, [.. sets.Select(p => new OutfitPieces(p.Key, p.Value))]);
    }

    private static Stack? ToStack(uint rawId, ushort count, int slot)
    {
        if (rawId == 0 || rawId >= EventItemStart)
            return null;
        var hq = rawId >= HqOffset;
        var collectible = !hq && rawId >= CollectibleOffset;
        var itemId = hq ? rawId - HqOffset : collectible ? rawId - CollectibleOffset : rawId;
        if (itemId == 0 || itemId > Stack.ItemIdMask)
            return null;
        return new Stack(itemId, hq, Math.Max((ushort)1, count), (byte)slot, collectible ? StackFlags.Collectable : StackFlags.None);
    }
}
