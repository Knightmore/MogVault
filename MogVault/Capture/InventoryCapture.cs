using System.Buffers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using MogVault.Contract;
using MogVault.Game;
using MogVault.Query;
using MogVault.Store;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture : IDisposable
{
    private static readonly InventoryType[] PlayerTypes =
    [
        InventoryType.Inventory1, InventoryType.Inventory2, InventoryType.Inventory3, InventoryType.Inventory4,
        InventoryType.EquippedItems, InventoryType.Currency, InventoryType.Crystals,
        InventoryType.ArmoryOffHand, InventoryType.ArmoryHead, InventoryType.ArmoryBody, InventoryType.ArmoryHands,
        InventoryType.ArmoryWaist, InventoryType.ArmoryLegs, InventoryType.ArmoryFeets, InventoryType.ArmoryEar,
        InventoryType.ArmoryNeck, InventoryType.ArmoryWrist, InventoryType.ArmoryRings, InventoryType.ArmorySoulCrystal,
        InventoryType.ArmoryMainHand,
        InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2,
    ];

    private static readonly InventoryType[] SaddlebagTypes =
        [InventoryType.SaddleBag1, InventoryType.SaddleBag2, InventoryType.PremiumSaddleBag1, InventoryType.PremiumSaddleBag2];

    private const int SaddlebagStateOffset = 0x1E04;
    private const int SaddlebagStateLoaded = 2;
    private const int MaxRetries = 20;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private enum Group
    {
        Player,
        Retainer,
        RetainerList,
    }

    private readonly IGameInventory inventory;
    private readonly IFramework framework;
    private readonly IClientState clientState;
    private readonly IPlayerState playerState;
    private readonly IAddonLifecycle addonLifecycle;
    private readonly IGameGui gameGui;
    private readonly IUnlockState unlocks;
    private readonly CollectionCatalog collections;
    private readonly IPluginLog log;
    private readonly VaultWriter writer;
    private readonly VaultQuery query;
    private readonly CancellationTokenSource cts = new();
    private readonly List<InventoryType> dirtyPlayer = new(8);
    private ulong currentCharacterId;
    private bool suspended;
    private long loginAt = Environment.TickCount64;
    private bool saddlebagsLoaded;

    public InventoryCapture(
        IGameInventory inventory, IFramework framework, IClientState clientState, IPlayerState playerState,
        IAddonLifecycle addonLifecycle, IGameGui gameGui, IUnlockState unlocks, CollectionCatalog collections, IPluginLog log, VaultWriter writer, VaultQuery query,
        IGameInteropProvider interop)
    {
        this.inventory = inventory;
        this.framework = framework;
        this.clientState = clientState;
        this.playerState = playerState;
        this.addonLifecycle = addonLifecycle;
        this.gameGui = gameGui;
        this.unlocks = unlocks;
        this.collections = collections;
        this.log = log;
        this.writer = writer;
        this.query = query;

        unlocks.Unlock += OnUnlock;
        inventory.InventoryChangedRaw += OnInventoryChanged;
        clientState.Login += OnLogin;
        clientState.Logout += OnLogout;
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerAddons, OnRetainerOpened);
        addonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerListAddon, OnRetainerListChanged);
        addonLifecycle.RegisterListener(AddonEvent.PostRefresh, RetainerListAddon, OnRetainerListChanged);
        addonLifecycle.RegisterListener(AddonEvent.PreFinalize, FreeCompanyWindow, OnFreeCompanyWindowClosing);
        sheetsLoaded = Task.Run(() => (QuestIds.Value, CabinetItems.Value, OutfitCatalog.All.Count));
        HookFreeCompanyPackets(interop);
        framework.Update += OnUpdate;

        Schedule(Group.Player);
    }

    public ulong CurrentCharacterId => Volatile.Read(ref currentCharacterId);

    public void Recapture() => _ = framework.RunOnFrameworkThread(() =>
    {
        lastProfileOwner = 0;
        cacheImportedFor = 0;
        saddlebagsLoaded = false;
        chestSaved = 0;
        lastIndoor = 0;
        Schedule(Group.Player);
    });

    public void Dispose()
    {
        cts.Cancel();
        framework.Update -= OnUpdate;
        UnhookFreeCompanyPackets();
        unlocks.Unlock -= OnUnlock;
        inventory.InventoryChangedRaw -= OnInventoryChanged;
        clientState.Login -= OnLogin;
        clientState.Logout -= OnLogout;
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerAddons, OnRetainerOpened);
        addonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerListAddon, OnRetainerListChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PostRefresh, RetainerListAddon, OnRetainerListChanged);
        addonLifecycle.UnregisterListener(AddonEvent.PreFinalize, FreeCompanyWindow, OnFreeCompanyWindowClosing);
        cts.Dispose();
    }

    private void OnLogin()
    {
        suspended = false;
        loginAt = Environment.TickCount64;
        collectionsDirty = true;
        Schedule(Group.Player);
    }

    private void OnLogout(int type, int code)
    {
        suspended = true;
        summonedRetainer = 0;
        lastIndoor = 0;
        Volatile.Write(ref currentCharacterId, 0);
    }

    private void OnUpdate(IFramework _)
    {
        if (suspended)
            return;

        var now = Environment.TickCount64;
        TrackSummonedRetainer(now);

        var saddlebags = SaddlebagsLoaded();
        if (saddlebags && !saddlebagsLoaded && Player() is { } player)
            foreach (var type in SaddlebagTypes)
                CaptureOne(player, null, type);
        saddlebagsLoaded = saddlebags;

        if (now - loginAt >= HouseGraceMs)
            TrackHouseEntry();

        if (now >= nextChestPoll)
        {
            nextChestPoll = now + ChestPollMs;
            CaptureSettledChestTabs();
        }

        if (now < nextProfilePoll)
            return;
        nextProfilePoll = now + ProfilePollMs;

        try
        {
            CaptureProfiles(now);
        }
        catch (Exception ex)
        {
            log.Error(ex, "MogVault profile capture failed.");
        }
    }

    private void OnInventoryChanged(IReadOnlyCollection<InventoryEventArgs> events)
    {
        if (suspended)
            return;

        try
        {
            dirtyPlayer.Clear();
            dirtyFreeCompany.Clear();
            var retainer = false;
            foreach (var e in events)
            {
                var type = (InventoryType)e.Item.ContainerType;
                var kind = ContainerKinds.Categorize((ushort)type);
                if (kind == ContainerType.Unknown)
                    continue;
                if (ContainerKinds.IsRetainer(kind))
                    retainer = true;
                else if (ContainerKinds.IsFreeCompany(kind))
                    AddOnce(dirtyFreeCompany, type);
                else
                    AddOnce(dirtyPlayer, type);
            }

            if (dirtyPlayer.Count > 0 && Player() is { } player)
                foreach (var type in dirtyPlayer)
                    CaptureOne(player, null, type);
            if (retainer)
                CaptureGroup(Group.Retainer);
            if (dirtyFreeCompany.Count > 0)
                CaptureFreeCompany(dirtyFreeCompany);
        }
        catch (Exception ex)
        {
            log.Error(ex, "MogVault capture failed.");
        }
    }

    private void Schedule(Group group, int attempt = 0)
    {
        if (cts.IsCancellationRequested)
            return;

        _ = framework.RunOnTick(
            () =>
            {
                if (suspended)
                    return;
                try
                {
                    if (!CaptureGroup(group) && attempt < MaxRetries)
                        Schedule(group, attempt + 1);
                }
                catch (Exception ex)
                {
                    log.Error(ex, $"MogVault {group} capture failed.");
                }
            },
            attempt == 0 ? TimeSpan.Zero : RetryDelay,
            cancellationToken: cts.Token);
    }

    private bool CaptureGroup(Group group)
    {
        OwnerInfo? owner;
        RetainerInfo? retainer = null;
        InventoryType[] types;
        switch (group)
        {
            case Group.Player:
                owner = Player();
                types = PlayerTypes;
                break;
            case Group.Retainer:
                if (!AnyVisible(RetainerAddons))
                    return false;
                owner = Player();
                retainer = ActiveRetainer();
                if (retainer is null)
                    return false;
                types = RetainerTypes;
                break;
            default:
                return CaptureRetainerList();
        }

        if (owner is not { } o)
            return false;

        var any = false;
        foreach (var type in types)
            any |= CaptureOne(o, retainer, type);
        return any;
    }

    private static void AddOnce(List<InventoryType> list, InventoryType type)
    {
        if (!list.Contains(type))
            list.Add(type);
    }

    private bool AnyVisible(string[] addons)
    {
        foreach (var name in addons)
            if (gameGui.GetAddonByName(name).IsVisible)
                return true;
        return false;
    }

    private bool CaptureOne(OwnerInfo owner, RetainerInfo? retainer, InventoryType type)
    {
        if (Array.IndexOf(SaddlebagTypes, type) >= 0 && !SaddlebagsLoaded())
            return false;

        var buffer = CopyContainer(type, out var count, out var extras, out var extraCount);
        if (buffer is null)
            return false;
        writer.Post(new ContainerCapture(owner, retainer, (ushort)type, DateTime.UtcNow.Ticks, buffer, count, extras, extraCount));
        return true;
    }

    private OwnerInfo? Player()
    {
        if (!playerState.IsLoaded || playerState.ContentId == 0 || playerState.HomeWorld.RowId == 0)
            return null;
        var id = playerState.ContentId;
        Volatile.Write(ref currentCharacterId, id);
        return new OwnerInfo(id, OwnerKind.Character, playerState.CharacterName, (ushort)playerState.HomeWorld.RowId);
    }

    private static unsafe Stack[]? CopyContainer(InventoryType type, out int count, out StackExtra[]? extras, out int extraCount)
    {
        count = 0;
        extras = null;
        extraCount = 0;
        var manager = InventoryManager.Instance();
        if (manager == null)
            return null;
        var container = manager->GetInventoryContainer(type);
        if (container == null || !container->IsLoaded || container->Items == null || container->Size <= 0)
            return null;

        var size = Math.Min(container->Size, byte.MaxValue + 1);
        var buffer = ArrayPool<Stack>.Shared.Rent(size);
        for (var i = 0; i < size; i++)
        {
            ref var item = ref container->Items[i];
            if (item.IsSymbolic || item.ItemId == 0 || item.ItemId > Stack.ItemIdMask || item.Quantity <= 0)
                continue;
            var qty = (uint)item.Quantity;
            var hq = (item.Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var flags = (item.Flags & InventoryItem.ItemFlags.Collectable) != 0 ? StackFlags.Collectable : StackFlags.None;
            if (NeedsExtra(ref item, qty))
            {
                extras ??= ArrayPool<StackExtra>.Shared.Rent(size);
                extras[extraCount++] = new StackExtra((byte)i, qty, item.SpiritbondOrCollectability, item.Stains, item.Materia, item.MateriaGrades);
                flags |= StackFlags.HasExtra;
            }

            buffer[count++] = new Stack(item.ItemId, hq, (ushort)Math.Min(qty, ushort.MaxValue), (byte)i, flags);
        }

        return buffer;
    }

    private static bool NeedsExtra(ref InventoryItem item, uint qty)
    {
        if (qty >= ushort.MaxValue || item.SpiritbondOrCollectability != 0)
            return true;
        foreach (var stain in item.Stains)
            if (stain != 0)
                return true;
        foreach (var materia in item.Materia)
            if (materia != 0)
                return true;
        return false;
    }

    private static unsafe bool SaddlebagsLoaded()
    {
        var manager = InventoryManager.Instance();
        return manager != null && *(int*)((byte*)manager + SaddlebagStateOffset) == SaddlebagStateLoaded;
    }
}
