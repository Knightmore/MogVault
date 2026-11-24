using Dalamud.Configuration;

namespace MogVault.Config;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public int StaleDays { get; set; } = 7;

    public int ForgetFreeCompanyDays { get; set; }

    public bool Backups { get; set; } = true;

    public int BackupDays { get; set; } = 7;

    public bool HideNames { get; set; }

    public bool ItemTooltips { get; set; }

    public ulong NameSeed { get; set; }
}

