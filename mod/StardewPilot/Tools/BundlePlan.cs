using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewPilot.Guide;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.Locations;

namespace StardewPilot.Tools;

/// <summary>
/// get_bundle_plan: for each item a visible Community Center bundle still needs, whether the player already owns it,
/// and how to get it from three kinds of source the knowledge tools read well (crops, fish, today's shop stock).
/// Anything else is reported as "source not in the data I read" rather than guessed.
/// </summary>
internal static class BundlePlan
{
    /// <summary>Shops whose stock is checked for bundle items, with the map that must have been visited.</summary>
    private static readonly (string Shop, string Name, string Map)[] Shops =
    {
        ("SeedShop", "Pierre's General Store", "SeedShop"),
        ("Joja", "JojaMart", "JojaMart"),
        ("FishShop", "Willy's Fish Shop", "FishShop"),
        ("AnimalShop", "Marnie's Ranch", "AnimalShop"),
        ("Blacksmith", "Blacksmith", "Blacksmith"),
        ("Saloon", "The Stardrop Saloon", "Saloon"),
        ("Carpenter", "Carpenter's Shop", "ScienceHouse"),
        ("Hospital", "Harvey's Clinic", "Hospital"),
        ("Sandy", "Oasis (Sandy)", "SandyHouse"),
    };

    public static ToolDef Tool() => new()
    {
        Name = "get_bundle_plan",
        Description =
            "Plan for the Community Center bundles: for each item a visible bundle still needs, how many the player already owns at the needed quality and where, " +
            "and how to get it: a crop (seed, shop, price, whether it still ripens if planted today), a fish (maps, seasons, whether it can bite now), " +
            "or a shop that sells it today. For seasonal items, the last day this season it can still be got. Other kinds of items say the source is not read.",
        InputSchema = Schema.Object(("missingOnly", Schema.Boolean("Only items the player does not own enough of yet. Default false."), false)),
        Handler = Query,
    };

    /// <summary>One bundle item with its plan.</summary>
    public sealed class ItemPlan
    {
        public BundleHelper.Need Need = null!;
        public string Name = "";
        public int Owned;
        public string? Where;
        public List<Dictionary<string, object?>> Sources = new();
        public string? AvailableUntil;
        public int? DaysLeft;
        public bool? NextSeasonToo;
        public bool Unknown => this.Sources.Count == 0;
        public bool Enough => this.Owned >= this.Need.Quantity;
    }

    private static object Query(JObject args)
    {
        if (BundleHelper.HiddenReason() is { } reason)
            return new { visible = false, reason };
        bool missingOnly = args.Bool("missingOnly") ?? false;
        List<ItemPlan> plans = Plan();
        var bundles = plans
            .Where(p => !missingOnly || !p.Enough)
            .GroupBy(p => (p.Need.Room, p.Need.Bundle, p.Need.SlotsStillRequired))
            .Select(g => new
            {
                room = g.Key.Room,
                bundle = g.Key.Bundle,
                slotsStillNeeded = g.Key.SlotsStillRequired,
                items = g.Select(p => new
                {
                    item = p.Name,
                    id = p.Need.QualifiedId,
                    count = p.Need.Quantity,
                    minQuality = p.Need.MinQuality > 0 ? ItemFinder.QualityName(p.Need.MinQuality) : null,
                    owned = p.Owned,
                    where = p.Where,
                    getFrom = p.Unknown ? null : p.Sources,
                    source = p.Unknown ? "Source not in the data I read (only crops, fish and today's shop stock are checked)." : null,
                    availableUntil = p.AvailableUntil,
                    daysLeft = p.DaysLeft,
                    nextSeasonToo = p.NextSeasonToo,
                }),
            })
            .ToList();
        return new
        {
            visible = true,
            bundles,
            note = "slotsStillNeeded can be fewer than the items listed: those bundles take any few of them. 'owned' counts the backpack, chests and fridges at the needed quality.",
            how = "Needs: raw (the save's bundle data). Owned: computed (find_items). Crop and fish facts: raw game data; shop prices: today's stock from the game. " +
                  "availableUntil and daysLeft: computed (season ends, last planting day with the crop's growing days; the greenhouse removes the limit for crops).",
        };
    }

    /// <summary>The plan for every empty bundle slot. Runs on the main thread.</summary>
    public static List<ItemPlan> Plan()
    {
        List<BundleHelper.Need> needs = BundleHelper.MissingNeeds();
        if (needs.Count == 0)
            return new();
        List<FoundItem> scan = ItemFinder.Scan();
        Dictionary<string, List<(string shop, int price, bool reached)>> stock = TodaysStock();
        bool greenhouse = Game1.getFarm().greenhouseUnlocked.Value;
        Season season = Game1.GetSeasonForLocation(Game1.getFarm());
        int today = Game1.dayOfMonth;

        var plans = new List<ItemPlan>();
        foreach (BundleHelper.Need need in needs)
        {
            var plan = new ItemPlan
            {
                Need = need,
                Name = ItemRegistry.GetDataOrErrorItem(need.QualifiedId).DisplayName,
                Owned = ItemFinder.CountOwned(scan, need.QualifiedId, need.MinQuality),
                Where = ItemFinder.WhereIs(scan, need.QualifiedId, need.MinQuality),
            };

            // Crop: a crop whose harvest is this item.
            foreach ((string seedId, CropData crop) in Game1.cropData)
            {
                if (ItemRegistry.QualifyItemId(crop.HarvestItemId) != need.QualifiedId)
                    continue;
                string seedQid = "(O)" + seedId;
                int days = crop.DaysInPhase.Sum();
                bool inSeason = crop.Seasons.Contains(season);
                bool nextToo = crop.Seasons.Contains(Next(season));
                var buy = stock.TryGetValue(seedQid, out var s) ? s : new();
                if (buy.Count == 0)
                {
                    var regular = new Dictionary<string, List<(string shop, int price, bool reached)>>();
                    CropKnowledge.AddRegularSources(regular);
                    buy = regular.TryGetValue(seedQid, out var r) ? r : new();
                }
                bool ripens = greenhouse || (inSeason && DaysAvailable(crop, season, today) >= days);
                plan.Sources.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "crop",
                    ["seed"] = ItemRegistry.GetDataOrErrorItem(seedQid).DisplayName,
                    ["buy"] = buy.Count > 0 ? buy.Select(b => new { b.shop, b.price, b.reached }).ToList() : null,
                    ["seasons"] = crop.Seasons.Select(x => x.ToString().ToLowerInvariant()).ToList(),
                    ["daysToGrow"] = days,
                    ["ripensIfPlantedToday"] = ripens,
                    ["greenhouse"] = greenhouse ? true : null,
                });
                if (!greenhouse)
                {
                    if (!inSeason)
                        NotNow(plan, crop.Seasons, nextToo);
                    else if (!nextToo)
                    {
                        int last = 28 - days;
                        SetUntil(plan, last >= today ? $"{SeasonName(season)} {last} (last planting day)" : "too late to plant this season", last >= today ? last - today : -1, false);
                    }
                    else
                        SetUntil(plan, null, null, true);
                }
                break;
            }

            // Fish: the places whose rules name it. Only real fish: fishing rules also hand out junk such as Wood.
            List<FishKnowledge.FishSource> fish = ItemRegistry.GetDataOrErrorItem(need.QualifiedId).Category == StardewValley.Object.FishCategory
                ? FishKnowledge.Sources(need.QualifiedId)
                : new();
            if (fish.Count > 0)
            {
                plan.Sources.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "fish",
                    ["places"] = fish.Select(f => new
                    {
                        map = f.MapName,
                        reached = f.Reached,
                        seasons = f.Seasons?.Select(x => x.ToString().ToLowerInvariant()).OrderBy(x => x).ToList(),
                        canBiteNow = f.BlockedNow == null,
                        notNowBecause = f.BlockedNow,
                    }).ToList(),
                });
                // Seasons where the player can actually fish: the reached places, if any are reached.
                var usable = fish.Any(f => f.Reached) ? fish.Where(f => f.Reached).ToList() : fish;
                if (usable.All(f => f.Seasons != null))
                {
                    var seasons = new HashSet<Season>(usable.SelectMany(f => f.Seasons!));
                    if (seasons.Count < 4)
                    {
                        bool nextToo = seasons.Contains(Next(season));
                        if (!seasons.Contains(season))
                            NotNow(plan, seasons, nextToo);
                        else if (!nextToo)
                            SetUntil(plan, $"{SeasonName(season)} 28 (end of season)", 28 - today, false);
                        else
                            SetUntil(plan, null, null, true);
                    }
                }
            }

            // Shop: sold today.
            if (stock.TryGetValue(need.QualifiedId, out var sold))
            {
                plan.Sources.Add(new Dictionary<string, object?>
                {
                    ["kind"] = "shop",
                    ["today"] = sold.Select(b => new { b.shop, b.price, b.reached }).ToList(),
                });
            }

            // Sold in a shop today: no season limit worth warning about.
            if (plan.Sources.Any(x => (string?)x["kind"] == "shop"))
            {
                plan.AvailableUntil = null;
                plan.DaysLeft = null;
            }
            plans.Add(plan);
        }
        return plans;
    }

    /// <summary>With several seasonal sources the latest date wins: the item can be got while any source has it.</summary>
    private static void SetUntil(ItemPlan plan, string? until, int? daysLeft, bool nextToo)
    {
        if (until != null && (plan.DaysLeft == null || (daysLeft ?? 999) > plan.DaysLeft || plan.AvailableUntil?.StartsWith("not this season") == true))
        {
            plan.AvailableUntil = until;
            plan.DaysLeft = daysLeft;
        }
        plan.NextSeasonToo = (plan.NextSeasonToo ?? false) || nextToo;
    }

    /// <summary>Not available in this season: say when it is.</summary>
    private static void NotNow(ItemPlan plan, IEnumerable<Season> seasons, bool nextToo)
    {
        if (plan.AvailableUntil == null)
            plan.AvailableUntil = "not this season (only in " + string.Join(", ", seasons.OrderBy(x => x).Select(SeasonName)) + ")";
        plan.NextSeasonToo = (plan.NextSeasonToo ?? false) || nextToo;
    }

    /// <summary>Days from today until the crop's run of consecutive seasons ends.</summary>
    private static int DaysAvailable(CropData crop, Season season, int today)
    {
        int days = 28 - today;
        Season next = season;
        for (int k = 0; k < 3; k++)
        {
            next = Next(next);
            if (!crop.Seasons.Contains(next)) break;
            days += 28;
        }
        return days;
    }

    private static Season Next(Season s) => (Season)(((int)s + 1) % 4);

    private static string SeasonName(Season s) => s.ToString().ToLowerInvariant();

    /// <summary>Today's stock of the known shops, and of the Traveling Cart when it is in the forest today.</summary>
    public static Dictionary<string, List<(string shop, int price, bool reached)>> TodaysStock()
    {
        var result = new Dictionary<string, List<(string shop, int price, bool reached)>>();
        var shops = Shops.ToList();
        if (Game1.getLocationFromName("Forest") is Forest forest && forest.ShouldTravelingMerchantVisitToday())
            shops.Add(("Traveler", "Traveling Cart (in the forest today)", "Forest"));
        foreach (var (shop, name, map) in shops)
        {
            Dictionary<ISalable, ItemStockInformation> items;
            try { items = ShopBuilder.GetShopStock(shop); }
            catch { continue; } // a shop removed by a mod
            bool reached = Game1.player.locationsVisited.Contains(map);
            foreach ((ISalable item, ItemStockInformation info) in items)
            {
                if (item is not Item i || info.TradeItem != null || info.Stock == 0)
                    continue;
                if (!result.TryGetValue(i.QualifiedItemId, out var list))
                    result[i.QualifiedItemId] = list = new();
                if (!list.Any(x => x.shop == name))
                    list.Add((name, info.Price, reached));
            }
        }
        return result;
    }
}
