using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;
using MogVault.Contract;
using MogVault.Query;
using MogVault.Ui;
using Underlings.Localization;

namespace MogVault.Game;

internal sealed unsafe class StorageTooltip : IDisposable
{
    private const string AddonName = "ItemDetail";
    private const int StringArrayIndex = 27;
    private const int NumberArrayIndex = 30;
    private const int UpdateField = 3;
    private const int UpdateTooltipFlag = 1;
    private const int VisibilityField = 5;
    private const int DescriptionFlag = 2;
    private const int DescriptionField = 13;
    private const int MaxLines = 8;
    private const ushort LineColor = 3;
    private const ulong ItemIdModulo = 500000;
    private const ulong EventItemStart = 2000000;

    private readonly IAddonLifecycle lifecycle;
    private readonly IGameGui gameGui;
    private readonly IDataManager data;
    private readonly VaultQuery query;
    private readonly Func<bool> enabled;
    private readonly Func<ulong> currentCharacterId;
    private byte[] lastBlock = [];

    public StorageTooltip(IAddonLifecycle lifecycle, IGameGui gameGui, IDataManager data, VaultQuery query, Func<bool> enabled, Func<ulong> currentCharacterId)
    {
        this.lifecycle = lifecycle;
        this.gameGui = gameGui;
        this.data = data;
        this.query = query;
        this.enabled = enabled;
        this.currentCharacterId = currentCharacterId;
        lifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnRequestedUpdate);
    }

    public void Dispose() => lifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, AddonName, OnRequestedUpdate);

    private void OnRequestedUpdate(AddonEvent type, AddonArgs args)
    {
        if (!enabled() || args is not AddonRequestedUpdateArgs update || gameGui.HoveredItem >= EventItemStart)
            return;

        var strings = ((StringArrayData**)update.StringArrayData)[StringArrayIndex];
        var numbers = ((NumberArrayData**)update.NumberArrayData)[NumberArrayIndex];
        if (strings == null || numbers == null || strings->Size <= DescriptionField || numbers->Size <= VisibilityField)
            return;
        if ((numbers->IntArray[UpdateField] & UpdateTooltipFlag) == 0)
            return;

        byte[] current = (numbers->IntArray[VisibilityField] & DescriptionFlag) != 0 && strings->StringArray[DescriptionField].HasValue
            ? strings->StringArray[DescriptionField].AsSpan().ToArray()
            : [];
        var at = lastBlock.Length > 0 ? current.AsSpan().IndexOf(lastBlock) : -1;
        var before = at >= 0 ? current[..at] : current;
        var after = at >= 0 ? current[(at + lastBlock.Length)..] : [];

        lastBlock = Block((uint)(gameGui.HoveredItem % ItemIdModulo), before.Length > 0);
        if (lastBlock.Length == 0)
        {
            if (at >= 0)
                Write(strings, [.. before, .. after]);
            return;
        }

        Write(strings, [.. before, .. lastBlock, .. after]);
        numbers->IntArray[VisibilityField] |= DescriptionFlag;
    }

    private static void Write(StringArrayData* strings, byte[] text)
    {
        var buffer = new byte[text.Length + 1];
        text.CopyTo(buffer, 0);
        fixed (byte* p = buffer)
            strings->SetValue(DescriptionField, p, false, true, true);
    }

    private byte[] Block(uint itemId, bool separate)
    {
        if (itemId == 0)
            return [];

        var snapshot = query.Current;
        var current = currentCharacterId();
        var lines = query.FindItem(itemId)
            .Where(h => h.Container.Type != ContainerType.RetainerMarket)
            .GroupBy(h => (h.Owner, h.Container.RetainerId, Type: h.Container.RetainerId == 0 ? h.Container.Type : ContainerType.RetainerBag, h.Stack.IsHq, h.OutfitId))
            .Select(g => (g.Key, Qty: g.Sum(h => h.Quantity)))
            .OrderBy(l => l.Key.Owner.Id != current ? 2 : l.Key.RetainerId == 0 ? 0 : 1)
            .ThenByDescending(l => l.Qty)
            .ToArray();
        if (lines.Length == 0)
            return [];

        var worlds = data.GetExcelSheet<World>();
        var builder = new SeStringBuilder();
        if (separate)
            builder.Add(NewLinePayload.Payload).Add(NewLinePayload.Payload);
        builder.AddText(string.Format(Loc.G("Tooltip.Stored"), lines.Sum(l => l.Qty).ToString("N0")));
        builder.AddUiForeground(LineColor);
        foreach (var ((owner, retainerId, containerType, hq, outfitId), qty) in lines.Take(MaxLines))
        {
            var quality = hq ? " " + SeIconChar.HighQuality.ToIconString() : string.Empty;
            builder.Add(NewLinePayload.Payload).AddText($"{ItemSearch.Location(snapshot, owner, retainerId, containerType, worlds, true, outfitId)}: {qty:N0}{quality}");
        }

        if (lines.Length > MaxLines)
            builder.Add(NewLinePayload.Payload).AddText(string.Format(Loc.G("Tooltip.More"), lines.Length - MaxLines));
        builder.AddUiForegroundOff();
        return builder.Build().Encode();
    }
}
