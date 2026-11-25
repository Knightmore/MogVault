using System.Globalization;
using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Layer;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using MogVault.Contract;
using Underlings.Localization;
using UnderlingsSvc = Underlings.Svc;

namespace MogVault.Game;

internal sealed class CollectionSources(IDataManager data, IFramework framework, IPluginLog log, CollectionCatalog catalog)
{
    private const string Section = "CollectionsUI";
    private const byte NpcLevelType = 8;
    private const byte TomestoneCurrencyType = 2;
    private const byte SpecialCurrencyType = 16;
    private const uint MaxSpecialCurrency = 8;
    private const uint Gil = 1;
    private const uint FirstSealItem = 19;

    private readonly Dictionary<(CollectionKind, uint), string[]> cache = [];
    private Task<Index>? index;

    public string[]? For(CollectionKind kind, uint rowId)
    {
        index ??= Load();
        if (!index.IsCompleted)
            return null;
        if (!index.IsCompletedSuccessfully)
            return [];
        if (cache.TryGetValue((kind, rowId), out var cached))
            return cached;

        var lines = new List<string>();
        if (kind == CollectionKind.TripleTriadCards && Card(rowId) is { Length: > 0 } card)
            lines.Add(card);
        if (kind == CollectionKind.Emotes && index.Result.Emotes.TryGetValue(rowId, out var emote))
            lines.AddRange(emote);
        foreach (var item in catalog.ItemsFor(kind, rowId))
            if (index.Result.Items.TryGetValue(item, out var found))
                lines.AddRange(found);
        if (kind == CollectionKind.Orchestrion &&
            data.GetExcelSheet<OrchestrionUiparam>().GetRowOrDefault(rowId)?.OrchestrionCategory.ValueNullable?.Name.ExtractText() is { Length: > 0 } category)
            lines.Add(string.Format(Loc.F(Section, "SourceOrchestrion"), category));
        return cache[(kind, rowId)] = [.. lines.Distinct()];
    }

    private string Card(uint id)
    {
        if (data.GetExcelSheet<TripleTriadCardResident>().GetRowOrDefault(id) is not { } card || card.AcquisitionType.ValueNullable?.Text.ValueNullable is not { } text)
            return string.Empty;
        var evaluated = UnderlingsSvc.SeStringEvaluator.Evaluate(text.Text, [card.Acquisition.RowId, card.Location.RowId]).ExtractText();
        return string.Join(", ", evaluated.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private async Task<Index> Load()
    {
        try
        {
            var currencies = await framework.RunOnFrameworkThread(SpecialCurrencies);
            return await Task.Run(() => Build(currencies));
        }
        catch (Exception e)
        {
            log.Error(e, "Could not load collection sources.");
            throw;
        }
    }

    private static unsafe Dictionary<uint, uint> SpecialCurrencies()
    {
        var currencies = new Dictionary<uint, uint>();
        var manager = CurrencyManager.Instance();
        if (manager == null)
            return currencies;
        for (var id = 1u; id < MaxSpecialCurrency; id++)
            if (manager->GetItemIdBySpecialId((byte)id) is var item and not 0)
                currencies[id] = item;
        return currencies;
    }

    private Index Build(Dictionary<uint, uint> currencies)
    {
        var wanted = catalog.UnlockItems();
        var items = new Dictionary<uint, List<string>>();
        var emotes = new Dictionary<uint, List<string>>();
        var (shopNpcs, grandCompanyNpcs) = ShopNpcs();
        var shops = new List<(uint Item, string Cost, List<uint>? Npcs)>();
        var itemSheet = data.GetExcelSheet<Item>();
        var tomestones = new Dictionary<uint, uint>();
        foreach (var tomestone in data.GetExcelSheet<TomestonesItem>())
            if (tomestone.Tomestones.RowId != 0)
                tomestones.TryAdd(tomestone.Tomestones.RowId, tomestone.Item.RowId);

        string Cost(uint amount, uint item) => $"{amount:N0} {itemSheet.GetRowOrDefault(item)?.Name.ExtractText()}";

        uint Currency(uint id, byte type) => id is 0 or >= MaxSpecialCurrency ? id : type switch
        {
            SpecialCurrencyType => currencies.GetValueOrDefault(id, id),
            TomestoneCurrencyType => tomestones.GetValueOrDefault(id, id),
            _ => id,
        };

        foreach (var shop in data.GetSubrowExcelSheet<GilShopItem>())
            foreach (var row in shop)
                if (wanted.Contains(row.Item.RowId))
                    shops.Add((row.Item.RowId, Cost(row.Item.Value.PriceMid, Gil), shopNpcs.GetValueOrDefault(shop.RowId)));

        foreach (var shop in data.GetExcelSheet<SpecialShop>())
            foreach (var entry in shop.Item)
                foreach (var receive in entry.ReceiveItems)
                {
                    if (!wanted.Contains(receive.Item.RowId))
                        continue;
                    var cost = string.Join(" + ", entry.ItemCosts.Where(c => c.ItemCost.RowId != 0 && c.CurrencyCost > 0)
                                                               .Select(c => Cost(c.CurrencyCost, Currency(c.ItemCost.RowId, shop.UseCurrencyType))));
                    if (cost.Length > 0)
                        shops.Add((receive.Item.RowId, cost, shopNpcs.GetValueOrDefault(shop.RowId)));
                }

        var categories = data.GetExcelSheet<GCScripShopCategory>();
        foreach (var category in data.GetSubrowExcelSheet<GCScripShopItem>())
        {
            var grandCompany = categories.GetRowOrDefault(category.RowId)?.GrandCompany.RowId ?? 0;
            if (grandCompany == 0)
                continue;
            foreach (var row in category)
                if (wanted.Contains(row.Item.RowId))
                    shops.Add((row.Item.RowId, Cost(row.CostGCSeals, FirstSealItem + grandCompany), grandCompanyNpcs.GetValueOrDefault(grandCompany)));
        }

        foreach (var shop in data.GetExcelSheet<FccShop>())
            foreach (var row in shop.ItemData)
                if (wanted.Contains(row.Item.RowId))
                    shops.Add((row.Item.RowId, string.Format(Loc.F(Section, "CompanyCredits"), row.Cost), shopNpcs.GetValueOrDefault(shop.RowId)));

        var places = Places([.. shops.SelectMany(s => s.Npcs ?? [])]);
        foreach (var (item, cost, npcs) in shops)
        {
            var placed = (npcs ?? []).Where(places.ContainsKey).Select(n => string.Format(Loc.F(Section, "SourceShop"), cost, places[n])).ToArray();
            foreach (var line in placed.Length > 0 ? placed : [cost])
                Add(items, item, line);
        }

        foreach (var achievement in data.GetExcelSheet<Achievement>())
            if (wanted.Contains(achievement.Item.RowId))
                Add(items, achievement.Item.RowId, string.Format(Loc.F(Section, "SourceAchievement"), achievement.Name.ExtractText()));

        foreach (var quest in data.GetExcelSheet<Quest>())
        {
            var rewards = quest.Reward.Where(r => r.Is<Item>()).Select(r => r.RowId)
                               .Concat(quest.OptionalItemReward.Select(r => r.RowId))
                               .Where(wanted.Contains)
                               .ToArray();
            if (quest.Name.IsEmpty || rewards.Length == 0 && quest.EmoteReward.RowId == 0)
                continue;
            var line = QuestLine(quest);
            foreach (var item in rewards)
                Add(items, item, line);
            if (quest.EmoteReward.RowId != 0)
                Add(emotes, quest.EmoteReward.RowId, line);
        }

        foreach (var recipe in data.GetExcelSheet<Recipe>())
            if (wanted.Contains(recipe.ItemResult.RowId) && recipe.CraftType.ValueNullable is { } craft)
                Add(items, recipe.ItemResult.RowId,
                    string.Format(Loc.F(Section, "SourceCraft"), craft.Name.ExtractText(), recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0));

        foreach (var spot in data.GetExcelSheet<FishingSpot>())
        {
            if (spot.PlaceName.ValueNullable?.Name.ExtractText() is not { Length: > 0 } name)
                continue;
            var zone = spot.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText();
            foreach (var fish in spot.Item)
                if (wanted.Contains(fish.RowId))
                    Add(items, fish.RowId, string.Format(Loc.F(Section, "SourceFishing"), zone is { Length: > 0 } && zone != name ? $"{name}, {zone}" : name));
        }

        foreach (var fish in data.GetExcelSheet<SpearfishingItem>())
            if (wanted.Contains(fish.Item.RowId))
                Add(items, fish.Item.RowId, string.Format(Loc.F(Section, "SourceSpearfishing"), fish.TerritoryType.ValueNullable?.PlaceName.ValueNullable?.Name.ExtractText()));

        foreach (var gathered in data.GetExcelSheet<GatheringItem>())
            if (gathered.Item.Is<Item>() && wanted.Contains(gathered.Item.RowId))
                Add(items, gathered.Item.RowId, string.Format(Loc.F(Section, "SourceGathering"), gathered.GatheringItemLevel.ValueNullable?.GatheringItemLevel ?? 0));

        foreach (var seed in data.GetExcelSheet<GardeningSeed>())
            if (wanted.Contains(seed.Item.RowId))
                Add(items, seed.Item.RowId, Loc.F(Section, "SourceGardening"));

        foreach (var series in data.GetExcelSheet<PvPSeries>())
            foreach (var level in series.LevelRewards)
                foreach (var reward in level.LevelRewardItem)
                    if (wanted.Contains(reward.RowId))
                        Add(items, reward.RowId, string.Format(Loc.F(Section, "SourcePvPSeries"), series.RowId));

        return new Index(items.ToDictionary(p => p.Key, p => p.Value.Distinct().ToArray()), emotes.ToDictionary(p => p.Key, p => p.Value.Distinct().ToArray()));
    }

    private static void Add(Dictionary<uint, List<string>> map, uint id, string line)
    {
        if (!map.TryGetValue(id, out var lines))
            map[id] = lines = [];
        lines.Add(line);
    }

    private static string QuestLine(Quest quest)
    {
        var name = quest.Name.ExtractText();
        if (!quest.IssuerStart.TryGetValue<ENpcResident>(out var npc) || npc.Singular.IsEmpty)
            return string.Format(Loc.F(Section, "SourceQuest"), name);
        var level = quest.IssuerLocation.ValueNullable;
        return string.Format(Loc.F(Section, "SourceQuestFrom"), name, Where(npc.Singular.ExtractText(), level?.Map.ValueNullable, level?.X ?? 0, level?.Z ?? 0));
    }

    private (Dictionary<uint, List<uint>> Shops, Dictionary<uint, List<uint>> GrandCompanies) ShopNpcs()
    {
        var shops = new Dictionary<uint, List<uint>>();
        var grandCompanies = new Dictionary<uint, List<uint>>();
        var specialShops = data.GetExcelSheet<SpecialShop>();
        var gilShops = data.GetExcelSheet<GilShop>();
        var series = data.GetSubrowExcelSheet<InclusionShopSeries>();

        void Link(Dictionary<uint, List<uint>> map, uint id, uint npc)
        {
            if (!map.TryGetValue(id, out var npcs))
                map[id] = npcs = [];
            if (!npcs.Contains(npc))
                npcs.Add(npc);
        }

        void Resolve(RowRef target, uint npc, int depth)
        {
            if (target.RowId == 0 || depth > 2)
                return;
            if (target.Is<GilShop>() || target.Is<SpecialShop>() || target.Is<FccShop>())
            {
                Link(shops, target.RowId, npc);
            }
            else if (target.TryGetValue<GCShop>(out var grandCompanyShop))
            {
                Link(grandCompanies, grandCompanyShop.GrandCompany.RowId, npc);
            }
            else if (target.TryGetValue<TopicSelect>(out var topic))
            {
                foreach (var shop in topic.Shop)
                    Resolve(shop, npc, depth + 1);
            }
            else if (target.TryGetValue<PreHandler>(out var handler))
            {
                Resolve(handler.Target, npc, depth + 1);
            }
            else if (target.TryGetValue<InclusionShop>(out var inclusion))
            {
                foreach (var category in inclusion.Category)
                    if (category.ValueNullable is { } c && series.GetRowOrDefault(c.InclusionShopSeries.RowId) is { } rows)
                        foreach (var row in rows)
                            Link(shops, row.SpecialShop.RowId, npc);
            }
            else if (target.TryGetValue<CustomTalk>(out var talk))
            {
                foreach (var script in talk.Script)
                    if (script.ScriptArg != 0 && (specialShops.HasRow(script.ScriptArg) || gilShops.HasRow(script.ScriptArg)))
                        Link(shops, script.ScriptArg, npc);
            }
        }

        foreach (var npc in data.GetExcelSheet<ENpcBase>())
            foreach (var entry in npc.ENpcData)
                Resolve(entry, npc.RowId, 0);
        return (shops, grandCompanies);
    }

    private Dictionary<uint, string> Places(HashSet<uint> npcs)
    {
        var found = new Dictionary<uint, (Map Map, float X, float Z)>();
        foreach (var level in data.GetExcelSheet<Level>())
            if (level.Type == NpcLevelType && npcs.Contains(level.Object.RowId) && level.Map.ValueNullable is { } map)
                found.TryAdd(level.Object.RowId, (map, level.X, level.Z));

        foreach (var territory in data.GetExcelSheet<TerritoryType>())
        {
            var bg = territory.Bg.ExtractText();
            var cut = bg.IndexOf("/level/", StringComparison.Ordinal);
            if (cut < 0 || territory.Map.ValueNullable is not { } map || data.GetFile<LgbFile>($"bg/{bg[..(cut + 7)]}planevent.lgb") is not { } lgb)
                continue;
            foreach (var layer in lgb.Layers)
                foreach (var instance in layer.InstanceObjects)
                    if (instance.Object is LayerCommon.ENPCInstanceObject npc && npcs.Contains(npc.ParentData.ParentData.BaseId))
                        found.TryAdd(npc.ParentData.ParentData.BaseId, (map, instance.Transform.Translation.X, instance.Transform.Translation.Z));
        }

        var residents = data.GetExcelSheet<ENpcResident>();
        var places = new Dictionary<uint, string>();
        foreach (var id in npcs)
        {
            if (residents.GetRowOrDefault(id)?.Singular.ExtractText() is not { Length: > 0 } name)
                continue;
            places[id] = found.TryGetValue(id, out var spot) ? Where(name, spot.Map, spot.X, spot.Z) : Where(name, null, 0, 0);
        }

        return places;
    }

    private static string Where(string npc, Map? map, float x, float z)
    {
        npc = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(npc);
        if (map is not { } m || m.PlaceName.ValueNullable?.Name.ExtractText() is not { Length: > 0 } zone)
            return npc;
        var position = MapUtil.WorldToMap(new Vector2(x, z), m);
        return string.Format(Loc.F(Section, "SourceWhere"), npc, $"{zone} ({position.X:0.0}, {position.Y:0.0})");
    }

    private sealed record Index(Dictionary<uint, string[]> Items, Dictionary<uint, string[]> Emotes);
}
