using Dalamud.Interface;
using Underlings.Modules;
using Underlings.Ui.V2;

namespace MogVault.Ui;

internal enum MogVaultCategory
{
    Characters,
    Retainers,
    FreeCompany,
    Houses,
    Settings,
}

internal sealed class MogVaultWindow(string version, string iconPath)
    : MainWindow(Plugin.Name, version, CategoryIcons, iconPath, categoryColors: CategoryColors, changelog: Changelog)
{
    private static readonly IReadOnlyList<ChangelogEntry> Changelog =
    [
        new("1.0.0.1",
        [
            "Collection tooltips show where to get an item: vendors with location, achievements, quests, recipes, fishing and more",
            "TT cards show their Card List source, orchestrion rolls their category",
            "New \"Card art\" toggle on the TT cards tab",
        ]),
        new("1.0.0.0",
        [
            "First release",
        ]),
    ];

    private static readonly IReadOnlyDictionary<Enum, FontAwesomeIcon> CategoryIcons = new Dictionary<Enum, FontAwesomeIcon>
    {
        [MogVaultCategory.Characters] = FontAwesomeIcon.Users,
        [MogVaultCategory.Retainers] = FontAwesomeIcon.UserTie,
        [MogVaultCategory.FreeCompany] = FontAwesomeIcon.Flag,
        [MogVaultCategory.Houses] = FontAwesomeIcon.Home,
        [MogVaultCategory.Settings] = FontAwesomeIcon.Cog,
    };

    private static readonly IReadOnlyDictionary<Enum, System.Numerics.Vector4> CategoryColors = new Dictionary<Enum, System.Numerics.Vector4>
    {
        [MogVaultCategory.Characters] = V2Palette.Blue,
        [MogVaultCategory.Retainers] = V2Palette.Teal,
        [MogVaultCategory.FreeCompany] = V2Palette.Purple,
        [MogVaultCategory.Houses] = V2Palette.Accent,
        [MogVaultCategory.Settings] = V2Palette.TextSoft,
    };

    protected override bool ShowTaskStatus => false;

    protected override bool OpenSingleFeatureCategories => true;

    protected override bool ShowHomeFilter => false;

    public override void OnClose()
    {
        foreach (var search in ModuleRegistry.Instances.OfType<SearchUI>())
            search.Reset();
    }
}

