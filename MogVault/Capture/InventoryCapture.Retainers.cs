using System.Buffers;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using MogVault.Store;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private static readonly InventoryType[] RetainerTypes =
    [
        InventoryType.RetainerPage1, InventoryType.RetainerPage2, InventoryType.RetainerPage3, InventoryType.RetainerPage4,
        InventoryType.RetainerPage5, InventoryType.RetainerPage6, InventoryType.RetainerPage7,
        InventoryType.RetainerEquippedItems, InventoryType.RetainerGil, InventoryType.RetainerCrystals, InventoryType.RetainerMarket,
    ];

    private static readonly string[] RetainerAddons = ["InventoryRetainer", "InventoryRetainerLarge", "RetainerSellList"];
    private const long MinSummonMs = 1000;
    private const string RetainerListAddon = "RetainerList";
    private ulong summonedRetainer;
    private ulong summonedBy;
    private long summonedAt;

    private void OnRetainerOpened(AddonEvent type, AddonArgs args) => Schedule(Group.Retainer);

    private void OnRetainerListChanged(AddonEvent type, AddonArgs args) => Schedule(Group.RetainerList);

    private void TrackSummonedRetainer(long now)
    {
        var active = SummonedRetainerId();
        if (active == summonedRetainer)
            return;

        if (active == 0 && summonedRetainer != 0 && now - summonedAt >= MinSummonMs && Player() is { } owner && owner.Id == summonedBy)
        {
            var retainer = RetainerById(summonedRetainer);
            foreach (var type in RetainerTypes)
                CaptureOne(owner, retainer, type);
        }

        summonedRetainer = active;
        summonedBy = playerState.ContentId;
        summonedAt = now;
    }

    private unsafe bool CaptureRetainerList()
    {
        if (!gameGui.GetAddonByName(RetainerListAddon).IsVisible || Player() is not { } owner)
            return false;
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return false;

        var ticks = DateTime.UtcNow.Ticks;
        var retainers = new List<RetainerInfo>(10);
        foreach (ref var r in manager->Retainers)
        {
            if (r.RetainerId == 0)
                continue;

            var details = new RetainerDetails(ticks, r.ClassJob, r.Level, (byte)r.Town, r.ItemCount, r.MarketItemCount, r.VentureId, r.VentureComplete, r.MarketExpire);
            var retainer = new RetainerInfo(r.RetainerId, r.NameString, details, Unavailable: !r.Available);
            retainers.Add(retainer);

            var buffer = ArrayPool<Stack>.Shared.Rent(1);
            StackExtra[]? extras = null;
            var count = 0;
            if (r.Gil > 0)
            {
                var flags = StackFlags.None;
                if (r.Gil >= ushort.MaxValue)
                {
                    extras = ArrayPool<StackExtra>.Shared.Rent(1);
                    extras[0] = new StackExtra(0, r.Gil, 0, [], [], []);
                    flags = StackFlags.HasExtra;
                }

                buffer[count++] = new Stack(GilItemId, false, (ushort)Math.Min(r.Gil, ushort.MaxValue), 0, flags);
            }

            writer.Post(new ContainerCapture(owner, retainer, (ushort)InventoryType.RetainerGil, ticks, buffer, count, extras, extras is null ? 0 : 1));
        }

        if (retainers.Count == 0)
            return false;
        writer.Post(new OwnerCapture(new OwnerUpdate(owner, Retainers: [.. retainers], AllRetainers: true)));
        return true;
    }

    private static unsafe RetainerInfo? ActiveRetainer()
    {
        var manager = RetainerManager.Instance();
        if (manager == null || !manager->IsReady)
            return null;
        var retainer = manager->GetActiveRetainer();
        return retainer == null || retainer->RetainerId == 0 ? null : new RetainerInfo(retainer->RetainerId, retainer->NameString);
    }

    private static unsafe ulong SummonedRetainerId()
    {
        var module = ItemOrderModule.Instance();
        return module == null ? 0 : module->ActiveRetainerId;
    }

    private static unsafe RetainerInfo RetainerById(ulong id)
    {
        var manager = RetainerManager.Instance();
        if (manager != null)
            foreach (ref var r in manager->Retainers)
                if (r.RetainerId == id)
                    return new RetainerInfo(id, r.NameString);
        return new RetainerInfo(id, string.Empty);
    }
}
