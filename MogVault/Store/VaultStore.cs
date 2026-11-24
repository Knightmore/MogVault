namespace MogVault.Store;

public sealed record ContainerUpdate(OwnerInfo Owner, RetainerInfo? Retainer, ushort InventoryType, long Ticks, Stack[] Stacks, StackExtra[] Extras)
{
    public ulong RetainerId => Retainer?.Id ?? 0;
}

public sealed record OwnerUpdate(
    OwnerInfo Owner, CharacterProfile? Profile = null, FreeCompanyProfile? FreeCompany = null, RetainerInfo[]? Retainers = null,
    CollectionsData? Collections = null, WorkshopData? Workshop = null, FreeCompanyCredits? Credits = null, bool AllRetainers = false,
    QuestsData? Quests = null, OutfitData? Outfits = null, HousesData? Houses = null, FreeCompanyStatus? Status = null,
    MembersData? Members = null);

public sealed class VaultStore
{
    private Snapshot current = Snapshot.Empty;

    public Snapshot Current => Volatile.Read(ref current);

    public void Load(CharacterData[] characters) => Volatile.Write(ref current, new Snapshot(current.Version + 1, characters));

    public void Remove(ulong[] ids)
    {
        var snap = current;
        Volatile.Write(ref current, new Snapshot(snap.Version + 1, Array.FindAll(snap.Characters, c => !ids.Contains(c.Id))));
    }

    public void Apply(IReadOnlyList<ContainerUpdate> updates, IReadOnlyList<OwnerUpdate>? owners = null)
    {
        owners ??= [];
        if (updates.Count == 0 && owners.Count == 0)
            return;

        var snap = current;
        var chars = new List<CharacterData>(snap.Characters);
        foreach (var u in updates)
            Upsert(chars, u.Owner, c => WithContainer(c, u));
        foreach (var o in owners)
            Upsert(chars, o.Owner, c => WithOwner(c, o));

        Volatile.Write(ref current, new Snapshot(snap.Version + 1, chars.ToArray()));
    }

    private static void Upsert(List<CharacterData> chars, OwnerInfo owner, Func<CharacterData, CharacterData> change)
    {
        var idx = chars.FindIndex(c => c.Id == owner.Id);
        var updated = change(idx >= 0 ? chars[idx] : new CharacterData(owner.Id, owner.Kind, owner.Name, owner.WorldId, [], []));
        if (idx >= 0)
            chars[idx] = updated;
        else
            chars.Add(updated);
    }

    internal static CharacterData WithOwner(CharacterData c, OwnerUpdate o)
    {
        var retainers = c.Retainers;
        var containers = c.Containers;
        if (o is { AllRetainers: true, Retainers: { } current })
        {
            retainers = Array.FindAll(retainers, r => Array.Exists(current, x => x.Id == r.Id));
            containers = Array.FindAll(containers, x => x.RetainerId == 0 || Array.Exists(current, r => r.Id == x.RetainerId));
        }

        foreach (var r in o.Retainers ?? [])
            retainers = WithRetainer(retainers, r);
        return c with
        {
            Name = NameOrKnown(o.Owner.Name, c.Name),
            WorldId = WorldOrKnown(o.Owner.WorldId, c.WorldId),
            Retainers = retainers,
            Containers = containers,
            Profile = o.Profile ?? c.Profile,
            FreeCompany = o.FreeCompany is { TotalMembers: 0 } partial && c.FreeCompany is { } known
                ? partial with { TotalMembers = known.TotalMembers }
                : o.FreeCompany ?? c.FreeCompany,
            Collections = o.Collections ?? c.Collections,
            Workshop = o.Workshop ?? c.Workshop,
            Credits = o.Credits ?? c.Credits,
            Quests = o.Quests ?? c.Quests,
            Outfits = o.Outfits ?? c.Outfits,
            Houses = o.Houses?.MergedWith(c.Houses) ?? c.Houses,
            Status = o.Status is { } status && c.Status is { } old ? status.Over(old) : o.Status ?? c.Status,
            Members = o.Members ?? c.Members,
        };
    }

    internal static RetainerInfo[] WithRetainer(RetainerInfo[] retainers, RetainerInfo r)
    {
        var i = Array.FindIndex(retainers, x => x.Id == r.Id);
        if (i < 0)
            return [.. retainers, r];
        var copy = (RetainerInfo[])retainers.Clone();
        copy[i] = r.Details is null
            ? r with { Name = NameOrKnown(r.Name, retainers[i].Name), Details = retainers[i].Details, Unavailable = retainers[i].Unavailable }
            : r with { Name = NameOrKnown(r.Name, retainers[i].Name) };
        return copy;
    }

    public static CharacterData[] LeftFreeCompanies(IReadOnlyList<CharacterData> owners, long cutoffTicks) =>
        owners.Where(fc => fc.Kind == OwnerKind.FreeCompany && fc.LastUpdatedTicks < cutoffTicks && !owners.Any(c =>
                c.Kind == OwnerKind.Character &&
                (c.Profile is { } p ? p.FreeCompanyId == fc.Id : c.WorldId == fc.WorldId || c.WorldId == 0)))
            .ToArray();

    private static string NameOrKnown(string captured, string known) => captured.Length > 0 ? captured : known;

    private static ushort WorldOrKnown(ushort captured, ushort known) => captured != 0 ? captured : known;

    internal static CharacterData WithContainer(CharacterData c, ContainerUpdate u)
    {
        var retainerId = u.RetainerId;
        var containers = c.Containers;
        var i = Array.FindIndex(containers, x => x.RetainerId == retainerId && x.InventoryType == u.InventoryType);
        var newContainers = new ContainerData[i >= 0 ? containers.Length : containers.Length + 1];
        containers.CopyTo(newContainers, 0);
        newContainers[i >= 0 ? i : containers.Length] = new ContainerData(retainerId, u.InventoryType, u.Ticks, u.Stacks, u.Extras);

        var retainers = c.Retainers;
        if (u.Retainer is { } r && c.RetainerName(r.Id) != r.Name)
            retainers = WithRetainer(retainers, r);

        return c with { Name = NameOrKnown(u.Owner.Name, c.Name), WorldId = WorldOrKnown(u.Owner.WorldId, c.WorldId), Retainers = retainers, Containers = newContainers };
    }
}

