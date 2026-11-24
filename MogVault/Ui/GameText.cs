using Lumina.Excel.Sheets;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Ui;

internal static class GameText
{
    private const uint QuestSheetOffset = 0x10000;

    public static string World(ushort id) =>
        UnderlingsSvc.Data.GetExcelSheet<World>().GetRowOrDefault(id)?.Name.ExtractText() is { Length: > 0 } name ? name : id.ToString();

    public static string Quest(ushort id) =>
        id == 0 ? "-" : UnderlingsSvc.Data.GetExcelSheet<Quest>().GetRowOrDefault(QuestSheetOffset + id)?.Name.ExtractText() ?? $"#{id}";

    public static string ClassJob(byte id) =>
        UnderlingsSvc.Data.GetExcelSheet<ClassJob>().GetRowOrDefault(id)?.Abbreviation.ExtractText() ?? "?";

    public static string Item(uint id) =>
        UnderlingsSvc.Data.GetExcelSheet<Item>().GetRowOrDefault(id)?.Name.ExtractText() ?? $"#{id}";

    public static uint ItemIcon(uint id) => UnderlingsSvc.Data.GetExcelSheet<Item>().GetRowOrDefault(id)?.Icon ?? 0;

    public static string GrandCompany(byte id) =>
        id == 0 ? "-" : UnderlingsSvc.Data.GetExcelSheet<GrandCompany>().GetRowOrDefault(id)?.Name.ExtractText() ?? "?";

    public static string Standing(byte level) =>
        UnderlingsSvc.Data.GetExcelSheet<FCReputation>().GetRowOrDefault(level)?.Name.ExtractText() is { Length: > 0 } name ? name : "-";

    public static string[] Standings => StandingNames.Value;

    private static readonly Lazy<string[]> StandingNames =
        new(() => [.. UnderlingsSvc.Data.GetExcelSheet<FCReputation>().Select(r => r.Name.ExtractText()).Where(n => n.Length > 0)]);

    public static string Age(long ticks)
    {
        if (ticks <= 0)
            return "-";
        var age = TimeSpan.FromTicks(Math.Max(0, DateTime.UtcNow.Ticks - ticks));
        return age.TotalMinutes < 1 ? "now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours}h ago"
            : $"{(int)age.TotalDays}d ago";
    }

    public static string Duration(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays}d {span.Hours}h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
        : $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}m";
}

