using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Underlings.Modules;
using Underlings.Ui.V2;

namespace MogVault.Ui;

[Module]
internal sealed class SettingsUI : ModuleUI
{
    public override string Name => "Settings";
    public override Enum Category => MogVaultCategory.Settings;
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Cog;

    public override void Draw()
    {
        var plugin = Plugin.P!;
        Widgets.Card(T("Display"), () =>
        {
            Widgets.FieldRow(T("StaleDays"), () =>
            {
                var days = plugin.Config.StaleDays;
                if (Widgets.Stepper("##mvStaleDays", ref days, 1, 365))
                {
                    plugin.Config.StaleDays = days;
                    plugin.SaveConfig();
                }
            }, 140f, T("StaleDaysHint"));

            Widgets.FieldRow(T("ItemTooltips"), () =>
            {
                var on = plugin.Config.ItemTooltips;
                if (Widgets.Switch("##mvItemTooltips", ref on))
                {
                    plugin.Config.ItemTooltips = on;
                    plugin.SaveConfig();
                }
            }, 140f, T("ItemTooltipsHint"));

            Widgets.FieldRow(T("HideNames"), () =>
            {
                var hide = plugin.Config.HideNames;
                if (Widgets.Switch("##mvHideNames", ref hide))
                {
                    plugin.Config.HideNames = hide;
                    plugin.SaveConfig();
                }
            }, 140f, T("HideNamesHint"));

            if (!plugin.Config.HideNames)
                return;
            Widgets.FieldRow(T("NewNames"), () =>
            {
                if (Widgets.Button(T("NewNames") + "##mvNewNames"))
                {
                    plugin.Config.NameSeed = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
                    plugin.SaveConfig();
                }
            }, 140f, T("NewNamesHint"));
        });

        Widgets.Card(T("Cleanup"), () =>
        {
            Widgets.FieldRow(T("ForgetFreeCompanyDays"), () =>
            {
                var days = plugin.Config.ForgetFreeCompanyDays;
                if (Widgets.Stepper("##mvForgetFcDays", ref days, 0, 3650))
                {
                    plugin.Config.ForgetFreeCompanyDays = days;
                    plugin.SaveConfig();
                }
            }, 140f, T("ForgetFreeCompanyDaysHint"));

            Widgets.FieldRow(T("ResetData"), () =>
            {
                var io = ImGui.GetIO();
                if (Widgets.Button(T("ResetDataButton") + "##mvResetData", ButtonKind.Danger, FontAwesomeIcon.Trash, enabled: io.KeyCtrl && io.KeyShift,
                        tooltip: T("ResetDataTooltip")))
                    plugin.ResetData();
            }, 140f, T("ResetDataHint"));
        });

        Widgets.Card(T("Backup"), () =>
        {
            Widgets.FieldRow(T("Backups"), () =>
            {
                var on = plugin.Config.Backups;
                if (Widgets.Switch("##mvBackups", ref on))
                {
                    plugin.Config.Backups = on;
                    plugin.SaveConfig();
                }
            }, 140f, T("BackupsHint"));

            if (!plugin.Config.Backups)
                return;
            Widgets.FieldRow(T("BackupDays"), () =>
            {
                var days = plugin.Config.BackupDays;
                if (Widgets.Stepper("##mvBackupDays", ref days, 1, 30))
                {
                    plugin.Config.BackupDays = days;
                    plugin.SaveConfig();
                }
            }, 140f, T("BackupDaysHint"));
        });
    }
}

