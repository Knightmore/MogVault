using System.Text.Json;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using MogVault.Contract;
using MogVault.Game;
using MogVault.Query;
using ClassJob = Lumina.Excel.Sheets.ClassJob;
using MogVault.Store;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ipc;

internal sealed class IpcService : IDisposable
{
    private const int MaxQueryResults = 5000;
    private const int BagSlots = 35;
    private const int RetainerBagSlots = 25;
    private const uint GilItemId = 1;

    private readonly IDalamudPluginInterface pi;
    private readonly VaultQuery query;
    private readonly CollectionCatalog collections;
    private readonly Func<ulong> currentCharacterId;
    private readonly List<Action> unregister = [];
    private readonly ICallGateProvider<object> ready;
    private readonly ICallGateProvider<ulong, ulong, int, object> changed;
    private readonly ICallGateProvider<ulong, object> ownerChanged;
    private volatile bool disposed;

    public IpcService(IDalamudPluginInterface pi, VaultQuery query, CollectionCatalog collections, Func<ulong> currentCharacterId, Func<bool> loaded)
    {
        this.pi = pi;
        this.query = query;
        this.collections = collections;
        this.currentCharacterId = currentCharacterId;

        ready = pi.GetIpcProvider<object>(MogVaultIpc.Ready);
        changed = pi.GetIpcProvider<ulong, ulong, int, object>(MogVaultIpc.Changed);
        ownerChanged = pi.GetIpcProvider<ulong, object>(MogVaultIpc.OwnerChanged);

        Register(MogVaultIpc.Version, () => MogVaultIpc.ContractVersion);
        Register(MogVaultIpc.IsReady, loaded);
        Register<uint, bool, long>(MogVaultIpc.GetItemCount, GetItemCount);
        Register<ulong, uint, bool, long>(MogVaultIpc.GetItemCountFor, Count);
        Register<uint, bool, long>(MogVaultIpc.GetItemCountAll, CountAll);
        Register<string, string>(MogVaultIpc.Query, Query);
        Register(MogVaultIpc.GetCharacters, GetCharacters);
        Register<ulong, string>(MogVaultIpc.GetCollections, GetCollections);
        Register<ulong, int, uint, bool>(MogVaultIpc.IsUnlocked, IsUnlocked);
        Register<ulong, uint, bool>(MogVaultIpc.IsItemUnlocked, IsItemUnlocked);
        Register<int, uint, string>(MogVaultIpc.GetCollectionOwners, GetCollectionOwners);
        Register(MogVaultIpc.GetWallets, GetWallets);
        Register(MogVaultIpc.GetFreeSlots, GetFreeSlots);
        Register<ulong, uint, int>(MogVaultIpc.GetJobLevel, GetJobLevel);
        Register<uint, int, ulong[]>(MogVaultIpc.GetCharactersWithJob, GetCharactersWithJob);
        Register(MogVaultIpc.GetTimers, GetTimers);
        Register<ulong, uint, bool>(MogVaultIpc.IsQuestComplete, IsQuestComplete);
        Register<ulong, string>(MogVaultIpc.GetCompletedQuests, GetCompletedQuests);
        Register<ulong, string>(MogVaultIpc.GetOutfits, GetOutfits);
    }

    public void RaiseChanged(ContainerUpdate[] updates, ulong[] owners)
    {
        if (disposed)
            return;
        foreach (var u in updates)
            changed.SendMessage(u.Owner.Id, u.RetainerId, u.InventoryType);
        foreach (var owner in owners)
            ownerChanged.SendMessage(owner);
    }

    public void RaiseReady()
    {
        if (!disposed)
            ready.SendMessage();
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var action in unregister)
            action();
    }

    private void Register<TRet>(string name, Func<TRet> func)
    {
        var provider = pi.GetIpcProvider<TRet>(name);
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }

    private void Register<T1, TRet>(string name, Func<T1, TRet> func)
    {
        var provider = pi.GetIpcProvider<T1, TRet>(name);
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }

    private void Register<T1, T2, TRet>(string name, Func<T1, T2, TRet> func)
    {
        var provider = pi.GetIpcProvider<T1, T2, TRet>(name);
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }

    private void Register<T1, T2, T3, TRet>(string name, Func<T1, T2, T3, TRet> func)
    {
        var provider = pi.GetIpcProvider<T1, T2, T3, TRet>(name);
        provider.RegisterFunc(func);
        unregister.Add(provider.UnregisterFunc);
    }

    private string GetWallets()
    {
        var owners = query.Current.Characters;
        var characters = owners.Where(o => o.Kind == OwnerKind.Character)
            .Select(c => new CharacterWalletDto(c.Id, Gil(c, false), Gil(c, true), Currencies(c)))
            .ToArray();
        var freeCompanies = owners.Where(o => o.Kind == OwnerKind.FreeCompany)
            .Select(f => new FreeCompanyWalletDto(f.Id, Gil(f, false), f.Credits?.Amount))
            .ToArray();
        var total = characters.Sum(c => c.Gil + c.RetainerGil) + freeCompanies.Sum(f => f.Gil);
        return JsonSerializer.Serialize(new WalletsResponse(MogVaultIpc.ContractVersion, total, characters, freeCompanies), MogVaultJsonContext.Default.WalletsResponse);
    }

    private static long Gil(CharacterData owner, bool retainers) =>
        owner.Containers.Where(c => (c.RetainerId != 0) == retainers)
            .Sum(c => c.Stacks.Where(s => s.ItemId == GilItemId).Sum(s => c.QuantityOf(s)));

    private static CurrencyDto[] Currencies(CharacterData owner) =>
        owner.Container(0, ContainerKinds.CurrencyType) is { } wallet
            ? [.. wallet.Stacks.Where(s => s.ItemId != GilItemId).GroupBy(s => s.ItemId).Select(g => new CurrencyDto(g.Key, g.Sum(s => wallet.QuantityOf(s))))]
            : [];

    private string GetFreeSlots()
    {
        var characters = query.Current.Characters.Where(o => o.Kind == OwnerKind.Character)
            .Select(c => new CharacterSlotsDto(
                c.Id,
                Free(c, 0, ContainerType.Bag, BagSlots),
                Free(c, 0, ContainerType.Saddlebag, BagSlots),
                Free(c, 0, ContainerType.PremiumSaddlebag, BagSlots),
                [.. c.Retainers.Select(r => new RetainerSlotsDto(r.Id, r.Name, Free(c, r.Id, ContainerType.RetainerBag, RetainerBagSlots)))]))
            .ToArray();
        return JsonSerializer.Serialize(new FreeSlotsResponse(MogVaultIpc.ContractVersion, characters), MogVaultJsonContext.Default.FreeSlotsResponse);
    }

    private static int? Free(CharacterData owner, ulong retainerId, ContainerType type, int slotsPerContainer)
    {
        var found = owner.Containers.Where(c => c.RetainerId == retainerId && c.Type == type).ToArray();
        return found.Length == 0 ? null : found.Sum(c => Math.Max(0, slotsPerContainer - c.Stacks.Length));
    }

    private int GetJobLevel(ulong characterId, uint classJobId) =>
        Character(characterId) is { } c ? JobLevel(c, classJobId) : -1;

    private ulong[] GetCharactersWithJob(uint classJobId, int minLevel) =>
        [.. query.Current.Characters.Where(c => c.Kind == OwnerKind.Character && JobLevel(c, classJobId) >= Math.Max(1, minLevel)).Select(c => c.Id)];

    private static int JobLevel(CharacterData character, uint classJobId) =>
        character.Profile is { } p &&
        UnderlingsSvc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(classJobId) is { ExpArrayIndex: >= 0 } job &&
        job.ExpArrayIndex < p.JobLevels.Length
            ? p.JobLevels[job.ExpArrayIndex]
            : -1;

    private string GetTimers()
    {
        var now = DateTime.UtcNow;
        var owners = query.Current.Characters;
        var ventures = owners.Where(o => o.Kind == OwnerKind.Character)
            .SelectMany(c => c.Retainers.Where(r => r.Details is { VentureComplete: > 0 })
                .Select(r => (Character: c, Retainer: r, Complete: Unix(r.Details!.Value.VentureComplete))))
            .Select(v => new VentureDto(v.Character.Id, v.Retainer.Id, v.Retainer.Name, v.Retainer.Details!.Value.VentureId, v.Complete, v.Complete <= now))
            .ToArray();
        var voyages = owners.Where(o => o.Kind == OwnerKind.FreeCompany && o.Workshop is not null)
            .SelectMany(f => f.Workshop!.Vessels.Where(v => v.ReturnTime > 0)
                .Select(v => new VoyageDto(f.Id, v.Kind, v.Slot, v.Name, Unix(v.ReturnTime), Unix(v.ReturnTime) <= now)))
            .ToArray();
        return JsonSerializer.Serialize(new TimersResponse(MogVaultIpc.ContractVersion, ventures, voyages), MogVaultJsonContext.Default.TimersResponse);
    }

    private string GetOutfits(ulong characterId)
    {
        var response = Character(characterId) is not { } c
            ? new OutfitsResponse(MogVaultIpc.ContractVersion, "Unknown character.", null, [])
            : c.Outfits is not { } data
                ? new OutfitsResponse(MogVaultIpc.ContractVersion, null, null, [])
                : new OutfitsResponse(MogVaultIpc.ContractVersion, null, Utc(data.UpdatedTicks),
                    [.. data.Sets.Select(s => (Set: s, Outfit: OutfitCatalog.Find(s.SetId))).Where(x => x.Outfit is not null).Select(x => ToDto(x.Outfit!, x.Set.Slots))]);
        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.OutfitsResponse);
    }

    private static OutfitDto ToDto(Outfit outfit, ushort slots)
    {
        var owned = new List<uint>();
        var missing = new List<uint>();
        for (var slot = 0; slot < outfit.Pieces.Length; slot++)
            if (outfit.Pieces[slot] != 0)
                ((slots & (1 << slot)) != 0 ? owned : missing).Add(outfit.Pieces[slot]);
        return new OutfitDto(outfit.SetId, missing.Count == 0, [.. owned], [.. missing]);
    }

    private bool IsQuestComplete(ulong characterId, uint questId) => Character(characterId)?.Quests?.Has(questId) == true;

    private string GetCompletedQuests(ulong characterId)
    {
        var response = Character(characterId) is not { } c
            ? new QuestsResponse(MogVaultIpc.ContractVersion, "Unknown character.", null, [])
            : c.Quests is not { } quests
                ? new QuestsResponse(MogVaultIpc.ContractVersion, null, null, [])
                : new QuestsResponse(MogVaultIpc.ContractVersion, null, Utc(quests.UpdatedTicks), quests.Completed());
        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.QuestsResponse);
    }

    private string GetCharacters()
    {
        var owners = query.Current.Characters;
        var response = new CharactersResponse(
            MogVaultIpc.ContractVersion,
            [.. owners.Where(o => o.Kind == OwnerKind.Character).Select(ToCharacterDto)],
            [.. owners.Where(o => o.Kind == OwnerKind.FreeCompany).Select(ToFreeCompanyDto)]);
        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.CharactersResponse);
    }

    private string GetCollections(ulong characterId)
    {
        var response = Character(characterId) is not { } c
            ? new CollectionsResponse(MogVaultIpc.ContractVersion, "Unknown character.", null, [])
            : c.Collections is not { } data
                ? new CollectionsResponse(MogVaultIpc.ContractVersion, null, null, [])
                : new CollectionsResponse(MogVaultIpc.ContractVersion, null, Utc(data.UpdatedTicks),
                    [.. collections.Sources.Where(s => data.Get(s.Kind) is not null).Select(s => ToDto(s, data.Get(s.Kind)!))]);
        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.CollectionsResponse);
    }

    private CollectionDto ToDto(CollectionSource source, CollectionSet set)
    {
        var unlocked = source.RowIds.Where(set.Has).ToArray();
        var locked = source.RowIds.Where(id => !set.Has(id)).ToArray();
        return new CollectionDto(source.Kind, unlocked, locked, Items(source.Kind, unlocked), Items(source.Kind, locked));
    }

    private uint[] Items(CollectionKind kind, uint[] rowIds) => [.. rowIds.SelectMany(id => collections.ItemsFor(kind, id)).Distinct()];

    private bool IsUnlocked(ulong characterId, int kind, uint id) =>
        Character(characterId)?.Collections?.Get((CollectionKind)kind)?.Has(id) == true;

    private bool IsItemUnlocked(ulong characterId, uint itemId) =>
        collections.TryFindItem(itemId, out var kind, out var rowId) && Character(characterId)?.Collections?.Get(kind)?.Has(rowId) == true;

    private string GetCollectionOwners(int kind, uint id)
    {
        CollectionOwnersResponse response;
        if (collections.Find((CollectionKind)kind) is not { } source)
        {
            response = new CollectionOwnersResponse(MogVaultIpc.ContractVersion, "Unknown collection kind.", [], [], []);
        }
        else if (Array.BinarySearch(source.RowIds, id) < 0)
        {
            response = new CollectionOwnersResponse(MogVaultIpc.ContractVersion, "Unknown collection entry.", [], [], []);
        }
        else
        {
            var characters = query.Current.Characters.Where(c => c.Kind == OwnerKind.Character).ToArray();
            var sets = characters.Select(c => c.Collections?.Get(source.Kind)).ToArray();
            response = new CollectionOwnersResponse(
                MogVaultIpc.ContractVersion, null,
                [.. characters.Where((_, i) => sets[i]?.Has(id) == true).Select(c => c.Id)],
                [.. characters.Where((_, i) => sets[i] is { } set && !set.Has(id)).Select(c => c.Id)],
                [.. characters.Where((_, i) => sets[i] is null).Select(c => c.Id)]);
        }

        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.CollectionOwnersResponse);
    }

    private CharacterData? Character(ulong characterId) =>
        query.Current.Find(characterId == 0 ? currentCharacterId() : characterId) is { Kind: OwnerKind.Character } c ? c : null;

    private static CharacterDto ToCharacterDto(CharacterData c) => new(
        c.Id, c.Name, c.WorldId, Utc(c.LastUpdatedTicks),
        c.Profile is { } p
            ? new ProfileDto(Utc(p.UpdatedTicks), p.ClassJob, p.FreeCompanyId, p.MsqQuestId == 0 ? null : QuestRow(p.MsqQuestId),
                p.JobLevels, [.. p.ActiveQuests.Select(QuestRow)])
            : null,
        [.. c.Retainers.Select(r => new RetainerDto(r.Id, r.Name, r.Details is { } d
            ? new RetainerDetailsDto(Utc(d.UpdatedTicks), d.ClassJob, d.Level, d.Town, d.ItemCount, d.VentureId,
                d.VentureComplete == 0 ? null : Unix(d.VentureComplete))
            : null, r.Unavailable))]);

    private static FreeCompanyDto ToFreeCompanyDto(CharacterData fc) => new(
        fc.Id, fc.Name, fc.WorldId, Utc(fc.LastUpdatedTicks),
        fc.FreeCompany is { } p ? new FreeCompanyProfileDto(Utc(p.UpdatedTicks), p.Rank, p.GrandCompany, p.TotalMembers) : null,
        fc.Credits is { } credits ? new CreditsDto(Utc(credits.UpdatedTicks), credits.Amount) : null,
        fc.Workshop is { } w
            ? new WorkshopDto(Utc(w.UpdatedTicks), [.. w.Vessels.Select(v =>
                new VesselDto(v.Kind, v.Slot, v.Name, v.Rank, Unix(v.RegisterTime), v.ReturnTime == 0 ? null : Unix(v.ReturnTime)))])
            : null);

    private static uint QuestRow(ushort questId) => 0x10000u + questId;

    private static DateTime Utc(long ticks) => new(ticks, DateTimeKind.Utc);

    private static DateTime Unix(uint seconds) => DateTime.UnixEpoch.AddSeconds(seconds);

    private long GetItemCount(uint itemId, bool includeRetainers)
    {
        var owner = currentCharacterId();
        return owner == 0 ? 0 : Count(owner, itemId, includeRetainers);
    }

    private long Count(ulong ownerId, uint itemId, bool includeRetainers)
    {
        var (id, hq) = SplitHq(itemId);
        return query.GetItemCount(id, hq, ownerId, includeRetainers);
    }

    private long CountAll(uint itemId, bool includeRetainers)
    {
        var (id, hq) = SplitHq(itemId);
        return query.Current.Characters.Sum(o => query.GetItemCount(id, hq, o.Id, includeRetainers));
    }

    private static (uint ItemId, bool? Hq) SplitHq(uint itemId) =>
        itemId is >= MogVaultIpc.HqOffset and < 2 * MogVaultIpc.HqOffset ? (itemId - MogVaultIpc.HqOffset, true) : (itemId, null);

    private string Query(string requestJson)
    {
        QueryResponse response;
        try
        {
            var request = JsonSerializer.Deserialize(requestJson, MogVaultJsonContext.Default.QueryRequest);
            response = request is { ItemIds.Length: > 0 } or { CharacterId: not null }
                ? Run(request)
                : Error("itemIds or characterId is required.");
        }
        catch (JsonException ex)
        {
            response = Error("Invalid request JSON: " + ex.Message);
        }

        return JsonSerializer.Serialize(response, MogVaultJsonContext.Default.QueryResponse);
    }

    private QueryResponse Run(QueryRequest request)
    {
        var ids = request.ItemIds is { } given ? (uint[])given.Clone() : [];
        Array.Sort(ids);
        var max = Math.Clamp(request.MaxResults, 1, MaxQueryResults);
        var hits = new List<ItemLocation>();
        var filter = new QueryFilter(request.Hq, request.CharacterId, request.ContainerType);
        var complete = ids.Length == 0 ? query.FindAll(filter, hits, max) : query.Find(ids, filter, hits, max);

        var dtos = new ItemLocationDto[hits.Count];
        for (var i = 0; i < hits.Count; i++)
        {
            var (owner, container, stack, outfitId) = hits[i];
            dtos[i] = new ItemLocationDto(
                stack.ItemId, stack.IsHq, hits[i].Quantity,
                owner.Id, owner.Name, owner.WorldId, owner.Kind == OwnerKind.FreeCompany,
                container.RetainerId, container.RetainerId == 0 ? null : owner.RetainerName(container.RetainerId),
                container.Type, container.InventoryType, stack.Slot,
                new DateTime(container.LastUpdatedTicks, DateTimeKind.Utc),
                container.TryGetExtra(stack, out var extra) ? ToDto(extra) : null,
                outfitId);
        }

        return new QueryResponse(MogVaultIpc.ContractVersion, null, dtos, !complete);
    }

    private static StackExtrasDto ToDto(in StackExtra e)
    {
        var materia = new List<MateriaDto>(StackExtra.MateriaSlots);
        for (var i = 0; i < StackExtra.MateriaSlots; i++)
            if (e.Materia(i) != 0)
                materia.Add(new MateriaDto(e.Materia(i), e.Grade(i)));
        return new StackExtrasDto(e.SpiritbondOrCollectability, [e.Stain(0), e.Stain(1)], [.. materia]);
    }

    private static QueryResponse Error(string message) => new(MogVaultIpc.ContractVersion, message, [], false);
}

