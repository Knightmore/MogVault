using MogVault.Contract;
using MogVault.Game;
using MogVault.Store;

namespace MogVault.Query;

public readonly record struct QueryFilter(bool? Hq = null, ulong? OwnerId = null, ContainerType? Type = null);

public readonly record struct ItemLocation(CharacterData Owner, ContainerData Container, Stack Stack, uint OutfitId = 0)
{
    public long Quantity => Container.QuantityOf(Stack);
}

public sealed class VaultQuery(VaultStore store)
{
    public Snapshot Current => store.Current;

    public bool Find(ReadOnlySpan<uint> sortedItemIds, QueryFilter filter, List<ItemLocation> results, int max = int.MaxValue) =>
        sortedItemIds.IsEmpty || Scan(sortedItemIds, anyItem: false, filter, results, max);

    public bool FindAll(QueryFilter filter, List<ItemLocation> results, int max = int.MaxValue) =>
        Scan([], anyItem: true, filter, results, max);

    private bool Scan(ReadOnlySpan<uint> sortedItemIds, bool anyItem, QueryFilter filter, List<ItemLocation> results, int max)
    {
        foreach (var owner in store.Current.Characters)
        {
            if (filter.OwnerId is { } oid && owner.Id != oid)
                continue;

            foreach (var container in owner.Containers)
            {
                if (filter.Type is { } t && container.Type != t)
                    continue;

                var outfits = container.Type == ContainerType.GlamourDresser ? owner.Outfits : null;
                foreach (var stack in container.Stacks)
                {
                    if (Matches(sortedItemIds, anyItem, filter, stack))
                    {
                        if (results.Count >= max)
                            return false;
                        results.Add(new ItemLocation(owner, container, stack));
                    }

                    if (outfits is null || OutfitCatalog.Find(stack.ItemId) is not { } outfit || (!anyItem && !ContainsAny(sortedItemIds, outfit.Pieces)))
                        continue;
                    foreach (var piece in outfit.PiecesIn(outfits.SlotsOf(outfit.SetId)))
                    {
                        var pieceStack = new Stack(piece, false, 1, stack.Slot);
                        if (!Matches(sortedItemIds, anyItem, filter, pieceStack))
                            continue;
                        if (results.Count >= max)
                            return false;
                        results.Add(new ItemLocation(owner, container, pieceStack, outfit.SetId));
                    }
                }
            }
        }

        return true;
    }

    private static bool Matches(ReadOnlySpan<uint> sortedItemIds, bool anyItem, QueryFilter filter, in Stack stack) =>
        (filter.Hq is not { } hq || stack.IsHq == hq) && (anyItem || sortedItemIds.BinarySearch(stack.ItemId) >= 0);

    private static bool ContainsAny(ReadOnlySpan<uint> sortedItemIds, uint[] itemIds)
    {
        foreach (var id in itemIds)
            if (id != 0 && sortedItemIds.BinarySearch(id) >= 0)
                return true;
        return false;
    }

    public List<ItemLocation> FindItem(uint itemId, bool? hq = null, ulong? ownerId = null, ContainerType? type = null)
    {
        var results = new List<ItemLocation>();
        Find([itemId], new QueryFilter(hq, ownerId, type), results);
        return results;
    }

    public long GetItemCount(uint itemId, bool? hq, ulong ownerId, bool includeRetainers)
    {
        var owner = store.Current.Find(ownerId);
        if (owner is null)
            return 0;

        var total = 0L;
        foreach (var container in owner.Containers)
        {
            if (!includeRetainers && container.RetainerId != 0)
                continue;
            var outfits = hq != true && container.Type == ContainerType.GlamourDresser ? owner.Outfits : null;
            foreach (var stack in container.Stacks)
            {
                if (stack.ItemId == itemId && (hq is null || stack.IsHq == hq))
                    total += container.QuantityOf(stack);
                if (outfits is not null && OutfitCatalog.Find(stack.ItemId) is { } outfit && Array.IndexOf(outfit.Pieces, itemId) >= 0)
                    total += outfit.PiecesIn(outfits.SlotsOf(outfit.SetId)).Count(p => p == itemId);
            }
        }

        return total;
    }
}

