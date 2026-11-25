using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MogVault.Capture;
using MogVault.Config;
using MogVault.Game;
using MogVault.Ipc;
using MogVault.Query;
using MogVault.Store;
using MogVault.Ui;
using Underlings.Localization;
using Underlings.Logging;
using Underlings.Modules;
using Underlings.Ui;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault;

public sealed class Plugin : IDalamudPlugin
{
    internal const string Name = "MogVault";
    private const string Command = "/mogvault";
    private const int ChatGroups = 5;
    private const int ChatLinesPerGroup = 5;

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly ICommandManager commands;
    private readonly IChatGui chat;
    private readonly IPluginLog log;
    private readonly VaultWriter writer;
    private readonly InventoryCapture capture;
    private readonly IpcService ipc;
    private readonly StorageTooltip tooltip;
    private readonly WindowSystem windows = new(Name);
    private readonly MogVaultWindow window;

    public Plugin(
        IDalamudPluginInterface pi, IGameInventory inventory, IFramework framework, IClientState clientState,
        IPlayerState playerState, IAddonLifecycle addonLifecycle, IGameGui gameGui, ICommandManager commands, IDataManager data,
        IUnlockState unlocks, IChatGui chat, IPluginLog log, IGameInteropProvider interop)
    {
        P = this;
        this.pi = pi;
        this.framework = framework;
        this.commands = commands;
        this.chat = chat;
        this.log = log;
        UnderlingsSvc.Init(pi);
        Chat.Init(Name);
        Loc.Load("en");
        Config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        if (Config.NameSeed == 0)
        {
            Config.NameSeed = (ulong)Random.Shared.NextInt64(1, long.MaxValue);
            SaveConfig();
        }

        var store = new VaultStore();
        Query = new VaultQuery(store);
        writer = new VaultWriter(
            store, pi.GetPluginConfigDirectory(), log, OnPublished, OnLoaded, Config.ForgetFreeCompanyDays, Config.Backups ? Math.Clamp(Config.BackupDays, 1, 30) : 0);
        Collections = new CollectionCatalog(data, unlocks);
        CollectionSources = new CollectionSources(data, framework, log, Collections);
        capture = new InventoryCapture(inventory, framework, clientState, playerState, addonLifecycle, gameGui, unlocks, Collections, log, writer, Query, interop);
        ipc = new IpcService(pi, Query, Collections, () => capture.CurrentCharacterId, () => writer.IsLoaded);
        Search = new ItemSearch(data, Query);
        tooltip = new StorageTooltip(addonLifecycle, gameGui, data, Query, () => Config.ItemTooltips, () => capture.CurrentCharacterId);

        foreach (var module in ModuleDiscovery.Discover<ModuleUI>(typeof(Plugin).Assembly))
            ModuleRegistry.Instances.Add(module);
        window = new MogVaultWindow(
            typeof(Plugin).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Path.Combine(pi.AssemblyLocation.Directory!.FullName, "Images", "MogVault.png"));
        windows.AddWindow(window);

        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += window.Toggle;
        pi.UiBuilder.OpenConfigUi += OpenSettings;
        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = $"Toggle the MogVault window.\n{Command} find <item name> → Print where an item is stored.",
        });
    }

    internal static Plugin? P { get; private set; }

    internal Configuration Config { get; }
    internal VaultQuery Query { get; }
    internal ItemSearch Search { get; }
    internal CollectionCatalog Collections { get; }
    internal CollectionSources CollectionSources { get; }

    internal ulong CurrentCharacterId => capture.CurrentCharacterId;

    internal void SaveConfig() => pi.SavePluginConfig(Config);

    public void Dispose()
    {
        commands.RemoveHandler(Command);
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenMainUi -= window.Toggle;
        pi.UiBuilder.OpenConfigUi -= OpenSettings;
        windows.RemoveAllWindows();
        foreach (var module in ModuleRegistry.Instances.OfType<ModuleUI>())
            module.Dispose();
        ModuleRegistry.Instances.Clear();
        GameItemTooltip.Dispose();
        tooltip.Dispose();
        window.Dispose();
        capture.Dispose();
        ipc.Dispose();
        writer.Dispose();
        P = null;
    }

    internal void OpenFreeCompany(ulong id)
    {
        if (!ModuleRegistry.TryGet<FreeCompaniesUI>(out var freeCompanies))
            return;
        freeCompanies!.Focus(id);
        window.Open(freeCompanies);
    }

    internal void DeleteOwner(ulong id) => writer.Post(new DeleteCapture([id]));

    internal void ResetData()
    {
        writer.Post(new DeleteCapture(null));
        capture.Recapture();
    }

    private void OpenSettings()
    {
        if (ModuleRegistry.TryGet<SettingsUI>(out var settings))
            window.Open(settings!);
    }

    private void OnLoaded() => _ = framework.RunOnFrameworkThread(() => ipc?.RaiseReady());

    private void OnPublished(ContainerUpdate[] updates, ulong[] owners) => _ = framework.RunOnFrameworkThread(() => ipc?.RaiseChanged(updates, owners));

    private void OnCommand(string command, string args)
    {
        args = args.Trim();
        if (!args.StartsWith("find ", StringComparison.OrdinalIgnoreCase))
        {
            window.Toggle();
            return;
        }

        var term = args[5..].Trim();
        if (term.Length == 0)
            return;

        var staleDays = Config.StaleDays;
        _ = Task.Run(() =>
        {
            try
            {
                var result = Search.Run(term, default, staleDays);
                if (!window.IsOpen)
                    Search.ReleaseNames();
                _ = framework.RunOnFrameworkThread(() => PrintResult(term, result));
            }
            catch (Exception ex)
            {
                log.Error(ex, "MogVault find failed.");
            }
        });
    }

    private void PrintResult(string term, ResultView result)
    {
        if (result.Groups.Length == 0)
        {
            chat.Print($"[MogVault] \"{term}\": {result.Status}");
            return;
        }

        foreach (var group in result.Groups.AsSpan(0, Math.Min(ChatGroups, result.Groups.Length)))
        {
            chat.Print($"[MogVault] {group.Name}: {group.Total}");
            foreach (var line in group.Lines.AsSpan(0, Math.Min(ChatLinesPerGroup, group.Lines.Length)))
                chat.Print($"    {line.Location}: {line.Qty} ({line.Updated})");
            if (group.Lines.Length > ChatLinesPerGroup)
                chat.Print($"    ...and {group.Lines.Length - ChatLinesPerGroup} more location(s).");
        }

        if (result.Groups.Length > ChatGroups)
            chat.Print($"[MogVault] {result.Groups.Length - ChatGroups} more item(s); open /mogvault for all.");
    }
}

