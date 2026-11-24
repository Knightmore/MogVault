using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;
using Lumina.Excel.Sheets;
using MogVault.Store;

namespace MogVault.Capture;

internal sealed partial class InventoryCapture
{
    private const string DialogPacketSignature =
        "48 89 5C 24 18 48 89 74 24 20 57 48 81 EC B0 0F 00 00 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 84 24 A0 0F 00 00 0F B6 42 31 48 8B FA 88 41 6B";

    private const string StatusPacketSignature = "48 89 5C 24 08 57 48 83 EC 50 0F B6 42 38 48 8B D9 88 41 6B 48 8B FA 48 8B 49 08";

    private const ulong NoFreeCompanyMask = ~4UL;
    private const int NameOffset = 50;
    private const int NameLength = 22;
    private const byte MaxStandingLevel = 8;
    private const uint MaxRankDefine = 10;
    private const int MaxQueuedPackets = 8;
    private static readonly TimeSpan PacketMaxAge = TimeSpan.FromMinutes(2);

    private delegate void FreeCompanyPacketDelegate(nint proxy, nint packet);

    private sealed record FreeCompanyPacket(ulong Id, string Name, FreeCompanyCredits Credits, FreeCompanyStatus Status);

    private readonly List<FreeCompanyPacket> packets = [];
    private Hook<FreeCompanyPacketDelegate>? dialogHook;
    private Hook<FreeCompanyPacketDelegate>? statusHook;

    private bool PacketsHooked => dialogHook is not null && statusHook is not null;

    private void HookFreeCompanyPackets(IGameInteropProvider interop)
    {
        try
        {
            dialogHook = interop.HookFromSignature<FreeCompanyPacketDelegate>(DialogPacketSignature, OnDialogPacket);
            statusHook = interop.HookFromSignature<FreeCompanyPacketDelegate>(StatusPacketSignature, OnStatusPacket);
            dialogHook.Enable();
            statusHook.Enable();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MogVault free company packets not found; credits and status are read while the free company window is open.");
            UnhookFreeCompanyPackets();
        }
    }

    private void UnhookFreeCompanyPackets()
    {
        dialogHook?.Dispose();
        statusHook?.Dispose();
        dialogHook = null;
        statusHook = null;
    }

    private void OnDialogPacket(nint proxy, nint packet)
    {
        dialogHook!.Original(proxy, packet);
        Queue(proxy, packet, ReadDialogPacket);
    }

    private void OnStatusPacket(nint proxy, nint packet)
    {
        statusHook!.Original(proxy, packet);
        Queue(proxy, packet, ReadStatusPacket);
    }

    private void Queue(nint proxy, nint packet, Func<nint, nint, FreeCompanyPacket?> read)
    {
        try
        {
            if (proxy == 0 || packet == 0 || read(proxy, packet) is not { } parsed)
                return;
            lock (packets)
            {
                if (packets.Count == MaxQueuedPackets)
                    packets.RemoveAt(0);
                packets.Add(parsed);
            }
        }
        catch (Exception ex)
        {
            log.Error(ex, "MogVault free company packet read failed.");
        }
    }

    private void CapturePackets(OwnerInfo freeCompany)
    {
        FreeCompanyPacket[] received;
        lock (packets)
        {
            if (packets.Count == 0)
                return;
            received = [.. packets.Where(p => p.Id == freeCompany.Id)];
            var expired = DateTime.UtcNow.Ticks - PacketMaxAge.Ticks;
            packets.RemoveAll(p => p.Id == freeCompany.Id || p.Credits.UpdatedTicks < expired);
        }

        foreach (var packet in received)
        {
            var owner = freeCompany.Name.Length == 0 && packet.Name.Length > 0 ? freeCompany with { Name = packet.Name } : freeCompany;
            PostCredits(owner, packet.Credits);
            PostStatus(owner, packet.Status);
        }
    }

    private static unsafe FreeCompanyPacket? ReadDialogPacket(nint proxy, nint address)
    {
        var p = (byte*)address;
        var id = *(ulong*)p;
        if ((id & NoFreeCompanyMask) == 0)
            return null;
        var (points, needed) = RankProgress(p[49], *(ulong*)(p + 16), *(uint*)(p + 40), *(uint*)(p + 36));
        var now = DateTime.UtcNow.Ticks;
        return new FreeCompanyPacket(
            id, ReadName(p + NameOffset), new FreeCompanyCredits(now, Clamp(*(ulong*)(p + 24))),
            FreeCompanyStatus.Partial(now, points, needed, p[48], StandingOf(*(uint*)(p + 32))));
    }

    private static unsafe FreeCompanyPacket? ReadStatusPacket(nint proxy, nint address)
    {
        var p = (byte*)address;
        var id = ((InfoProxyFreeCompany*)proxy)->Id;
        if (id == 0)
            return null;
        var (points, needed) = RankProgress(p[56], *(ulong*)(p + 8), *(uint*)(p + 40), *(uint*)(p + 36));
        var founded = *(uint*)(p + 32);
        var reputation = (uint*)(p + 44);
        var now = DateTime.UtcNow.Ticks;
        return new FreeCompanyPacket(
            id, string.Empty, new FreeCompanyCredits(now, Clamp(*(ulong*)(p + 16))),
            new FreeCompanyStatus(
                now, founded == 0 ? 0 : DateTimeOffset.FromUnixTimeSeconds(founded).LocalDateTime.Date.Ticks, points, needed,
                StandingOf(reputation[0]), StandingOf(reputation[1]), StandingOf(reputation[2])));
    }

    private static unsafe string ReadName(byte* name)
    {
        var bytes = new ReadOnlySpan<byte>(name, NameLength);
        var end = bytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? bytes : bytes[..end]);
    }

    private static uint Clamp(ulong value) => (uint)Math.Min(value, MaxedPoints);

    private static (uint Points, uint Needed) RankProgress(byte rank, ulong total, uint current, uint next)
    {
        var needed = next - current;
        var maxRank = Underlings.Svc.Data.GetExcelSheet<FCDefine>().GetRowOrDefault(MaxRankDefine)?.Unknown0;
        return rank >= maxRank || needed > MaxedPoints ? (0, 0) : (Clamp(total - current), needed);
    }

    private static Standing StandingOf(uint reputation)
    {
        var level = (byte)Math.Min(reputation >> 24, MaxStandingLevel);
        if (level == MaxStandingLevel)
            return new Standing(level, 100);
        var sheet = Underlings.Svc.Data.GetExcelSheet<FCReputation>();
        if (sheet.GetRowOrDefault(level) is not { } row || sheet.GetRowOrDefault(level == 0 ? 0u : level - 1u) is not { } previous)
            return new Standing(level, 0);
        return new Standing(level, Percent((int)(reputation & 0xFFFFFF) - (int)previous.PointsToNext, (int)(row.PointsToNext - previous.PointsToNext)));
    }

    private static byte Percent(int value, int total)
    {
        if (value <= 0)
            return 0;
        if (value == total)
            return 100;
        return (byte)Math.Clamp((int)MathF.Round(value * 100f / total, MidpointRounding.AwayFromZero), 1, 99);
    }
}
