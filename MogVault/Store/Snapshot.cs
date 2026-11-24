using MogVault.Contract;

namespace MogVault.Store;

public enum OwnerKind : byte
{
    Character = 0,
    FreeCompany = 1,
}

public readonly record struct OwnerInfo(ulong Id, OwnerKind Kind, string Name, ushort WorldId);

public sealed record RetainerInfo(ulong Id, string Name, RetainerDetails? Details = null, bool Unavailable = false);

public sealed record ContainerData(ulong RetainerId, ushort InventoryType, long LastUpdatedTicks, Stack[] Stacks, StackExtra[] Extras)
{
    public ContainerType Type => ContainerKinds.Categorize(InventoryType);

    public bool TryGetExtra(in Stack stack, out StackExtra extra)
    {
        if ((stack.Flags & StackFlags.HasExtra) != 0)
        {
            foreach (var e in Extras)
            {
                if (e.Slot == stack.Slot)
                {
                    extra = e;
                    return true;
                }
            }
        }

        extra = default;
        return false;
    }

    public long QuantityOf(in Stack stack) =>
        stack.Qty == ushort.MaxValue && TryGetExtra(stack, out var extra) ? extra.Quantity : stack.Qty;
}

public sealed record CharacterData(
    ulong Id, OwnerKind Kind, string Name, ushort WorldId, RetainerInfo[] Retainers, ContainerData[] Containers,
    CharacterProfile? Profile = null, FreeCompanyProfile? FreeCompany = null, CollectionsData? Collections = null, WorkshopData? Workshop = null,
    FreeCompanyCredits? Credits = null, QuestsData? Quests = null, OutfitData? Outfits = null, HousesData? Houses = null,
    FreeCompanyStatus? Status = null, MembersData? Members = null)
{
    public OwnerInfo Owner => new(Id, Kind, Name, WorldId);

    public long LastUpdatedTicks
    {
        get
        {
            long[] parts = [Profile?.UpdatedTicks ?? 0, FreeCompany?.UpdatedTicks ?? 0, Collections?.UpdatedTicks ?? 0, Workshop?.UpdatedTicks ?? 0, Credits?.UpdatedTicks ?? 0, Quests?.UpdatedTicks ?? 0, Outfits?.UpdatedTicks ?? 0, Houses?.UpdatedTicks ?? 0, Status?.UpdatedTicks ?? 0, Members?.UpdatedTicks ?? 0];
            var ticks = parts.Max();
            foreach (var c in Containers)
                ticks = Math.Max(ticks, c.LastUpdatedTicks);
            return ticks;
        }
    }

    public ContainerData? Container(ulong retainerId, ushort inventoryType)
    {
        foreach (var c in Containers)
            if (c.RetainerId == retainerId && c.InventoryType == inventoryType)
                return c;
        return null;
    }

    public string? RetainerName(ulong retainerId)
    {
        foreach (var r in Retainers)
            if (r.Id == retainerId)
                return r.Name;
        return null;
    }
}

public sealed record Snapshot(long Version, CharacterData[] Characters)
{
    public static readonly Snapshot Empty = new(0, []);

    public CharacterData? Find(ulong ownerId)
    {
        foreach (var c in Characters)
            if (c.Id == ownerId)
                return c;
        return null;
    }
}

