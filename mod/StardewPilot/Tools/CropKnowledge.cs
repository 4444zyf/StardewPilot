using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.ItemTypeDefinitions;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>
/// get_crop_plan: which crops pay best for a season. Growth data is raw (Data/Crops), prices are asked of the game
/// (today's shop stock, the game's own sell-price function, which includes professions), and harvest counts and
/// gold per day are computed here.
/// </summary>
internal static class CropKnowledge
{
    /// <summary>Shops that sell seeds, with the map that must have been visited for the shop to count as reached.</summary>
    public static readonly (string Shop, string Name, string Map)[] SeedShops =
    {
        ("SeedShop", "Pierre's General Store", "SeedShop"),
        ("Joja", "JojaMart", "JojaMart"),
        ("Sandy", "Oasis (Sandy)", "SandyHouse"),
        ("Traveler", "Traveling Cart (Friday and Sunday, stock changes)", "Forest"),
    };

    public static ToolDef Tool() => new()
    {
        Name = "get_crop_plan",
        Description =
            "Compare crops for a season: seed price and where to buy it (today's stock for this season, the shops' regular stock for another season), days to grow, regrowth, sell price (with the farmer's professions), " +
            "how many harvests are still possible if planted today, and gold per day per seed. Growth facts are game data; prices come from the game; " +
            "harvest counts and gold per day are the mod's own calculation ('computed', no fertiliser, base quality). Crops whose seeds are not " +
            "sold in a shop the player has reached are left out unless includeAll is true.",
        InputSchema = Schema.Object(
            ("season", Schema.Enum("Season to plan. Default the current season; another season is planned from its first day.", "spring", "summer", "fall", "winter"), false),
            ("includeAll", Schema.Boolean("Also list crops without a seed source the player has reached (spoilers). Default false."), false)),
        Handler = Query,
    };

    /// <summary>Pierre's and Joja's regular seed entries, whatever season their condition names.</summary>
    public static void AddRegularSources(Dictionary<string, List<(string shop, int price, bool reached)>> sources)
    {
        var shops = DataLoader.Shops(Game1.content);
        foreach (var (shop, name, map) in SeedShops.Where(x => x.Shop is "SeedShop" or "Joja"))
        {
            if (!shops.TryGetValue(shop, out var data) || data.Items == null)
                continue;
            bool reached = Game1.player.locationsVisited.Contains(map);
            foreach (var entry in data.Items)
            {
                if (entry.ItemId == null || ItemRegistry.GetData(entry.ItemId) is not { } d || d.IsErrorItem || entry.TradeItemId != null)
                    continue;
                // Priced the way ShopBuilder.GetShopStock prices it: base price, then the shop's and the entry's modifiers.
                Item seedItem = ItemRegistry.Create(d.QualifiedItemId);
                float price = ShopBuilder.GetBasePrice(new ItemQueryResult(seedItem), data, entry, seedItem, outOfSeasonPrice: false, entry.UseObjectDataPrice);
                if (!entry.IgnoreShopPriceModifiers)
                    price = Utility.ApplyQuantityModifiers(price, data.PriceModifiers, data.PriceModifierMode, null, null, seedItem);
                price = Utility.ApplyQuantityModifiers(price, entry.PriceModifiers, entry.PriceModifierMode, null, null, seedItem);
                if (!sources.TryGetValue(d.QualifiedItemId, out var list))
                    sources[d.QualifiedItemId] = list = new();
                if (!list.Any(x => x.shop == name))
                    list.Add((name + " (regular stock in its season)", (int)price, reached));
            }
        }
    }

    private static object Query(JObject args)
    {
        Season current = Game1.GetSeasonForLocation(Game1.getFarm());
        Season season = args.Str("season") is { } s && Enum.TryParse(s, ignoreCase: true, out Season parsed) ? parsed : current;
        bool includeAll = args.Bool("includeAll") ?? false;
        int plantDay = season == current ? Game1.dayOfMonth : 1;

        // Where each seed is sold, and at what price. This season: today's stock, asked of the game's shop code.
        // Another season: the regular entries of Pierre's and Joja in Data/Shops, priced as the shop prices them.
        var sources = new Dictionary<string, List<(string shop, int price, bool reached)>>();
        if (season != current)
            AddRegularSources(sources);
        foreach (var (shop, name, map) in season == current ? SeedShops : Array.Empty<(string, string, string)>())
        {
            Dictionary<ISalable, ItemStockInformation> stock;
            try { stock = ShopBuilder.GetShopStock(shop); }
            catch { continue; } // a shop removed by a mod
            bool reached = Game1.player.locationsVisited.Contains(map);
            foreach ((ISalable item, ItemStockInformation info) in stock)
            {
                if (item is not Item i || info.TradeItem != null)
                    continue;
                if (!sources.TryGetValue(i.QualifiedItemId, out var list))
                    sources[i.QualifiedItemId] = list = new();
                list.Add((name, info.Price, reached));
            }
        }

        var crops = new List<Dictionary<string, object?>>();
        int skipped = 0;
        foreach ((string seedId, CropData crop) in Game1.cropData)
        {
            if (!crop.Seasons.Contains(season))
                continue;
            string seedQid = "(O)" + seedId;
            ParsedItemData seed = ItemRegistry.GetDataOrErrorItem(seedQid);
            ParsedItemData harvest = ItemRegistry.GetDataOrErrorItem("(O)" + crop.HarvestItemId);
            if (seed.IsErrorItem || harvest.IsErrorItem)
            {
                skipped++;
                continue;
            }
            var where = sources.TryGetValue(seedQid, out var w) ? w : new();
            bool reachable = where.Any(x => x.reached);
            if (!reachable && !includeAll)
                continue;

            int days = crop.DaysInPhase.Sum();
            int regrow = crop.RegrowDays;
            // Days available: the rest of this season, plus following seasons the crop also grows in.
            int available = 28 - plantDay;
            Season next = season;
            for (int k = 0; k < 3; k++)
            {
                next = (Season)(((int)next + 1) % 4);
                if (!crop.Seasons.Contains(next)) break;
                available += 28;
            }
            int harvests = days > available ? 0 : 1 + (regrow > 0 ? (available - days) / regrow : 0);
            int replantCycles = regrow > 0 || days > available ? harvests : available / days;

            int sellPrice = ItemRegistry.Create("(O)" + crop.HarvestItemId) is SObject o ? o.sellToStorePrice() : 0;
            double perHarvest = (crop.HarvestMinStack + Math.Max(crop.HarvestMinStack, crop.HarvestMaxStack)) / 2.0;
            // The cheapest reached shop; the Traveling Cart only when nothing else sells the seed (its stock and prices are random).
            var regular = where.Where(x => x.reached && !x.shop.StartsWith("Traveling")).ToList();
            int? seedPrice = regular.Count > 0 ? regular.Min(x => x.price) : where.Count > 0 ? where.Min(x => x.price) : null;
            int seedCost = seedPrice ?? 0;

            double? goldPerDay = null;
            if (harvests > 0 && available > 0)
            {
                goldPerDay = regrow > 0
                    ? (harvests * perHarvest * sellPrice - seedCost) / available
                    : (perHarvest * sellPrice - seedCost) / days; // replanted after each harvest
            }

            crops.Add(new Dictionary<string, object?>
            {
                ["crop"] = harvest.DisplayName,
                ["seed"] = seed.DisplayName,
                ["seedId"] = seedQid,
                ["buy"] = where.Count > 0 ? where.Select(x => new { shop = x.shop, price = x.price, reached = x.reached }).ToList() : null,
                ["seasons"] = crop.Seasons.Select(x => x.ToString().ToLowerInvariant()).ToList(),
                ["daysToGrow"] = days,
                ["regrowsEveryDays"] = regrow > 0 ? regrow : null,
                ["harvestCount"] = crop.HarvestMaxStack > crop.HarvestMinStack ? $"{crop.HarvestMinStack}-{crop.HarvestMaxStack}" : crop.HarvestMinStack.ToString(),
                ["sellPrice"] = sellPrice,
                ["harvestsIfPlantedNow"] = harvests,
                ["replantCyclesIfPlantedNow"] = regrow > 0 ? null : replantCycles,
                ["goldPerDayPerSeed"] = goldPerDay.HasValue ? Math.Round(goldPerDay.Value, 1) : null,
                ["needsTrellis"] = crop.IsRaised ? true : null,
                ["paddyCrop"] = crop.IsPaddyCrop ? true : null,
                ["needsWatering"] = crop.NeedsWatering ? null : false,
                ["noSeedSourceReached"] = reachable ? null : true,
            });
        }

        return new
        {
            season = season.ToString().ToLowerInvariant(),
            plantingDay = plantDay,
            daysLeftForGrowing = 28 - plantDay,
            crops = crops.OrderByDescending(c => (double?)c["goldPerDayPerSeed"] ?? double.MinValue).ToList(),
            entriesNotReadable = skipped > 0 ? skipped : (int?)null,
            how = "Growth data: raw (Data/Crops). Seed prices: today's shop stock from the game for this season; for another season the regular shop entries (Data/Shops). Sell price: the game's own price, with professions. " +
                  "harvestsIfPlantedNow and goldPerDayPerSeed: computed by the mod, assuming base quality, no fertiliser, no Agriculturist speed-up, " +
                  "and for single-harvest crops buying a new seed after each harvest. Seed price used: the cheapest reached shop; the Traveling Cart only if nothing else sells it.",
            agriculturist = Game1.player.professions.Contains(Farmer.agriculturist) ? "The farmer has Agriculturist: crops grow 10% faster than daysToGrow." : null,
        };
    }
}
