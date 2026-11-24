using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Ipc.Exceptions;

namespace MogVault.Contract;

public static class MogVaultIpc
{
    public const int ContractVersion = 1;
    public const uint HqOffset = 1000000;
    public const string Version = "MogVault.Version";
    public const string GetItemCount = "MogVault.GetItemCount";
    public const string GetItemCountFor = "MogVault.GetItemCountFor";
    public const string IsReady = "MogVault.IsReady";
    public const string Ready = "MogVault.Ready";
    public const string Query = "MogVault.Query";
    public const string Changed = "MogVault.Changed";
    public const string GetCharacters = "MogVault.GetCharacters";
    public const string GetCollections = "MogVault.GetCollections";
    public const string IsUnlocked = "MogVault.IsUnlocked";
    public const string IsItemUnlocked = "MogVault.IsItemUnlocked";
    public const string GetCollectionOwners = "MogVault.GetCollectionOwners";
    public const string GetItemCountAll = "MogVault.GetItemCountAll";
    public const string GetWallets = "MogVault.GetWallets";
    public const string GetFreeSlots = "MogVault.GetFreeSlots";
    public const string GetJobLevel = "MogVault.GetJobLevel";
    public const string GetCharactersWithJob = "MogVault.GetCharactersWithJob";
    public const string GetTimers = "MogVault.GetTimers";
    public const string IsQuestComplete = "MogVault.IsQuestComplete";
    public const string GetCompletedQuests = "MogVault.GetCompletedQuests";
    public const string GetOutfits = "MogVault.GetOutfits";
    public const string OwnerChanged = "MogVault.OwnerChanged";
}

public sealed class MogVaultClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ICallGateSubscriber<int> version;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<object> ready;
    private readonly ICallGateSubscriber<uint, bool, long> getItemCount;
    private readonly ICallGateSubscriber<ulong, uint, bool, long> getItemCountFor;
    private readonly ICallGateSubscriber<string, string> query;
    private readonly ICallGateSubscriber<ulong, ulong, int, object> changed;
    private readonly ICallGateSubscriber<string> getCharacters;
    private readonly ICallGateSubscriber<ulong, string> getCollections;
    private readonly ICallGateSubscriber<ulong, int, uint, bool> isUnlocked;
    private readonly ICallGateSubscriber<ulong, uint, bool> isItemUnlocked;
    private readonly ICallGateSubscriber<int, uint, string> getCollectionOwners;
    private readonly ICallGateSubscriber<uint, bool, long> getItemCountAll;
    private readonly ICallGateSubscriber<string> getWallets;
    private readonly ICallGateSubscriber<string> getFreeSlots;
    private readonly ICallGateSubscriber<ulong, uint, int> getJobLevel;
    private readonly ICallGateSubscriber<uint, int, ulong[]> getCharactersWithJob;
    private readonly ICallGateSubscriber<string> getTimers;
    private readonly ICallGateSubscriber<ulong, uint, bool> isQuestComplete;
    private readonly ICallGateSubscriber<ulong, string> getCompletedQuests;
    private readonly ICallGateSubscriber<ulong, string> getOutfits;
    private readonly ICallGateSubscriber<ulong, object> ownerChanged;

    public MogVaultClient(IDalamudPluginInterface pi)
    {
        version = pi.GetIpcSubscriber<int>(MogVaultIpc.Version);
        isReady = pi.GetIpcSubscriber<bool>(MogVaultIpc.IsReady);
        ready = pi.GetIpcSubscriber<object>(MogVaultIpc.Ready);
        getItemCount = pi.GetIpcSubscriber<uint, bool, long>(MogVaultIpc.GetItemCount);
        getItemCountFor = pi.GetIpcSubscriber<ulong, uint, bool, long>(MogVaultIpc.GetItemCountFor);
        query = pi.GetIpcSubscriber<string, string>(MogVaultIpc.Query);
        changed = pi.GetIpcSubscriber<ulong, ulong, int, object>(MogVaultIpc.Changed);
        getCharacters = pi.GetIpcSubscriber<string>(MogVaultIpc.GetCharacters);
        getCollections = pi.GetIpcSubscriber<ulong, string>(MogVaultIpc.GetCollections);
        isUnlocked = pi.GetIpcSubscriber<ulong, int, uint, bool>(MogVaultIpc.IsUnlocked);
        isItemUnlocked = pi.GetIpcSubscriber<ulong, uint, bool>(MogVaultIpc.IsItemUnlocked);
        getCollectionOwners = pi.GetIpcSubscriber<int, uint, string>(MogVaultIpc.GetCollectionOwners);
        getItemCountAll = pi.GetIpcSubscriber<uint, bool, long>(MogVaultIpc.GetItemCountAll);
        getWallets = pi.GetIpcSubscriber<string>(MogVaultIpc.GetWallets);
        getFreeSlots = pi.GetIpcSubscriber<string>(MogVaultIpc.GetFreeSlots);
        getJobLevel = pi.GetIpcSubscriber<ulong, uint, int>(MogVaultIpc.GetJobLevel);
        getCharactersWithJob = pi.GetIpcSubscriber<uint, int, ulong[]>(MogVaultIpc.GetCharactersWithJob);
        getTimers = pi.GetIpcSubscriber<string>(MogVaultIpc.GetTimers);
        isQuestComplete = pi.GetIpcSubscriber<ulong, uint, bool>(MogVaultIpc.IsQuestComplete);
        getCompletedQuests = pi.GetIpcSubscriber<ulong, string>(MogVaultIpc.GetCompletedQuests);
        getOutfits = pi.GetIpcSubscriber<ulong, string>(MogVaultIpc.GetOutfits);
        ownerChanged = pi.GetIpcSubscriber<ulong, object>(MogVaultIpc.OwnerChanged);

        ready.Subscribe(OnReady);
        changed.Subscribe(OnChanged);
        ownerChanged.Subscribe(OnOwnerChanged);
    }

    public event Action? Ready;

    public event Action<ulong, ulong, int>? Changed;

    public event Action<ulong>? OwnerChanged;

    public bool IsAvailable => Call(version.InvokeFunc, 0) == MogVaultIpc.ContractVersion;

    public bool IsReady => Call(isReady.InvokeFunc, false);

    public long GetItemCount(uint itemId, bool includeRetainers = true) =>
        Call(() => getItemCount.InvokeFunc(itemId, includeRetainers), 0L);

    public long GetItemCountFor(ulong characterId, uint itemId, bool includeRetainers = true) =>
        Call(() => getItemCountFor.InvokeFunc(characterId, itemId, includeRetainers), 0L);

    public QueryResponse? Query(QueryRequest request) =>
        Read<QueryResponse>(() => query.InvokeFunc(JsonSerializer.Serialize(request, Json)));

    public CharactersResponse? GetCharacters() => Read<CharactersResponse>(getCharacters.InvokeFunc);

    public CollectionsResponse? GetCollections(ulong characterId = 0) =>
        Read<CollectionsResponse>(() => getCollections.InvokeFunc(characterId));

    public bool IsUnlocked(CollectionKind kind, uint id, ulong characterId = 0) =>
        Call(() => isUnlocked.InvokeFunc(characterId, (int)kind, id), false);

    public bool IsItemUnlocked(uint itemId, ulong characterId = 0) =>
        Call(() => isItemUnlocked.InvokeFunc(characterId, itemId), false);

    public CollectionOwnersResponse? GetCollectionOwners(CollectionKind kind, uint id) =>
        Read<CollectionOwnersResponse>(() => getCollectionOwners.InvokeFunc((int)kind, id));

    public long GetItemCountAll(uint itemId, bool includeRetainers = true) =>
        Call(() => getItemCountAll.InvokeFunc(itemId, includeRetainers), 0L);

    public WalletsResponse? GetWallets() => Read<WalletsResponse>(getWallets.InvokeFunc);

    public FreeSlotsResponse? GetFreeSlots() => Read<FreeSlotsResponse>(getFreeSlots.InvokeFunc);

    public int GetJobLevel(uint classJobId, ulong characterId = 0) =>
        Call(() => getJobLevel.InvokeFunc(characterId, classJobId), -1);

    public ulong[] GetCharactersWithJob(uint classJobId, int minLevel = 1) =>
        Call(() => getCharactersWithJob.InvokeFunc(classJobId, minLevel), []);

    public TimersResponse? GetTimers() => Read<TimersResponse>(getTimers.InvokeFunc);

    public bool IsQuestComplete(uint questId, ulong characterId = 0) =>
        Call(() => isQuestComplete.InvokeFunc(characterId, questId), false);

    public QuestsResponse? GetCompletedQuests(ulong characterId = 0) =>
        Read<QuestsResponse>(() => getCompletedQuests.InvokeFunc(characterId));

    public OutfitsResponse? GetOutfits(ulong characterId = 0) =>
        Read<OutfitsResponse>(() => getOutfits.InvokeFunc(characterId));

    public void Dispose()
    {
        ready.Unsubscribe(OnReady);
        changed.Unsubscribe(OnChanged);
        ownerChanged.Unsubscribe(OnOwnerChanged);
    }

    private void OnReady() => Ready?.Invoke();

    private void OnChanged(ulong ownerId, ulong retainerId, int inventoryType) => Changed?.Invoke(ownerId, retainerId, inventoryType);

    private void OnOwnerChanged(ulong ownerId) => OwnerChanged?.Invoke(ownerId);

    private static T Call<T>(Func<T> call, T fallback)
    {
        try
        {
            return call();
        }
        catch (IpcError)
        {
            return fallback;
        }
    }

    private static T? Read<T>(Func<string> call) where T : class =>
        Call(() => JsonSerializer.Deserialize<T>(call(), Json), null);
}

public enum ContainerType : byte
{
    Unknown,
    Bag,
    Equipped,
    Currency,
    Crystals,
    Armoury,
    Saddlebag,
    PremiumSaddlebag,
    RetainerBag,
    RetainerEquipped,
    RetainerGil,
    RetainerCrystals,
    RetainerMarket,
    FreeCompanyChest,
    FreeCompanyGil,
    FreeCompanyCrystals,
    GlamourDresser,
    Armoire,
}

public enum CollectionKind : byte
{
    Mounts = 1,
    Minions = 2,
    Orchestrion = 3,
    Emotes = 4,
    FashionAccessories = 5,
    Facewear = 6,
    TripleTriadCards = 7,
    Bardings = 8,
}

public enum VesselKind : byte
{
    Submersible = 0,
    Airship = 1,
}

public sealed record QueryRequest(
    uint[]? ItemIds,
    bool? Hq = null,
    ulong? CharacterId = null,
    ContainerType? ContainerType = null,
    int MaxResults = 1000);

public sealed record QueryResponse(int ContractVersion, string? Error, ItemLocationDto[] Locations, bool Truncated);

public sealed record ItemLocationDto(
    uint ItemId,
    bool Hq,
    long Quantity,
    ulong CharacterId,
    string CharacterName,
    ushort WorldId,
    bool IsFreeCompany,
    ulong RetainerId,
    string? RetainerName,
    ContainerType ContainerType,
    int InventoryType,
    int Slot,
    DateTime LastUpdatedUtc,
    StackExtrasDto? Extras,
    uint OutfitId = 0);

public sealed record StackExtrasDto(int SpiritbondOrCollectability, int[] Stains, MateriaDto[] Materia);

public sealed record MateriaDto(int Id, int Grade);

public sealed record CharactersResponse(int ContractVersion, CharacterDto[] Characters, FreeCompanyDto[] FreeCompanies);

public sealed record CharacterDto(ulong Id, string Name, ushort WorldId, DateTime LastUpdatedUtc, ProfileDto? Profile, RetainerDto[] Retainers);

public sealed record ProfileDto(
    DateTime UpdatedUtc, byte ClassJob, ulong FreeCompanyId, uint? MainScenarioQuest, short[] JobLevels, uint[] ActiveQuests);

public sealed record RetainerDto(ulong Id, string Name, RetainerDetailsDto? Details, bool Unavailable);

public sealed record RetainerDetailsDto(
    DateTime UpdatedUtc, byte ClassJob, byte Level, byte Town, byte ItemCount, ushort VentureId, DateTime? VentureCompleteUtc);

public sealed record FreeCompanyDto(
    ulong Id, string Name, ushort WorldId, DateTime LastUpdatedUtc, FreeCompanyProfileDto? Profile, CreditsDto? Credits, WorkshopDto? Workshop);

public sealed record FreeCompanyProfileDto(DateTime UpdatedUtc, byte Rank, byte GrandCompany, ushort TotalMembers);

public sealed record CreditsDto(DateTime UpdatedUtc, uint Amount);

public sealed record WorkshopDto(DateTime UpdatedUtc, VesselDto[] Vessels);

public sealed record VesselDto(VesselKind Kind, byte Slot, string Name, byte Rank, DateTime RegisteredUtc, DateTime? ReturnUtc);

public sealed record CollectionsResponse(int ContractVersion, string? Error, DateTime? UpdatedUtc, CollectionDto[] Sets);

public sealed record CollectionDto(CollectionKind Kind, uint[] Unlocked, uint[] Locked, uint[] UnlockedItems, uint[] LockedItems);

public sealed record CollectionOwnersResponse(int ContractVersion, string? Error, ulong[] Unlocked, ulong[] Locked, ulong[] Unknown);

public sealed record WalletsResponse(int ContractVersion, long TotalGil, CharacterWalletDto[] Characters, FreeCompanyWalletDto[] FreeCompanies);

public sealed record CharacterWalletDto(ulong CharacterId, long Gil, long RetainerGil, CurrencyDto[] Currencies);

public sealed record CurrencyDto(uint ItemId, long Amount);

public sealed record FreeCompanyWalletDto(ulong FreeCompanyId, long Gil, uint? Credits);

public sealed record FreeSlotsResponse(int ContractVersion, CharacterSlotsDto[] Characters);

public sealed record CharacterSlotsDto(ulong CharacterId, int? Bags, int? Saddlebag, int? PremiumSaddlebag, RetainerSlotsDto[] Retainers);

public sealed record RetainerSlotsDto(ulong RetainerId, string Name, int? Bags);

public sealed record TimersResponse(int ContractVersion, VentureDto[] Ventures, VoyageDto[] Voyages);

public sealed record VentureDto(ulong CharacterId, ulong RetainerId, string RetainerName, ushort VentureId, DateTime CompleteUtc, bool Done);

public sealed record VoyageDto(ulong FreeCompanyId, VesselKind Kind, byte Slot, string Name, DateTime ReturnUtc, bool Done);

public sealed record QuestsResponse(int ContractVersion, string? Error, DateTime? UpdatedUtc, uint[] Completed);

public sealed record OutfitsResponse(int ContractVersion, string? Error, DateTime? UpdatedUtc, OutfitDto[] Outfits);

public sealed record OutfitDto(uint SetItemId, bool Complete, uint[] OwnedPieces, uint[] MissingPieces);
