using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewValley;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>
/// get_day_briefing and the morning briefing: the day's facts gathered in one place. Lines with nothing to report are
/// left out. What the copilot says unasked follows the spoiler rule: only discovered gift tastes unless "tell me everything".
/// </summary>
internal static class DayBriefing
{
    public static ToolDef Tool(MemoryStore memory, Func<ModConfig> config) => new()
    {
        Name = "get_day_briefing",
        Description =
            "Today's briefing facts: weather today and tomorrow, luck (the TV fortune teller's levels), birthdays with the best gift the player owns, a festival, " +
            "crops ready, crops that die tomorrow, machines with output ready, the Traveling Cart's bundle items, quests ending soon, a finished tool upgrade, " +
            "bundle items about to go out of season, and yesterday's record. Only parts with something to report are included.",
        Handler = _ => Build(memory, config()).facts,
    };

    /// <summary>The facts, and a plain-text version the mod shows when no agent words them.</summary>
    public static (Dictionary<string, object?> facts, List<string> lines) Build(MemoryStore memory, ModConfig cfg)
    {
        var facts = new Dictionary<string, object?>();
        var lines = new List<string>();
        Farmer me = Game1.player;
        Farm farm = Game1.getFarm();
        bool everything = cfg.TellMeEverything;

        facts["date"] = $"{Game1.currentSeason} {Game1.dayOfMonth}, year {Game1.year} ({Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth)})";

        // Festival first: it shapes the whole day.
        if (DataLoader.Festivals_FestivalDates(Game1.content).TryGetValue($"{Game1.currentSeason}{Game1.dayOfMonth}", out string? festival))
        {
            facts["festivalToday"] = festival;
            lines.Add($"Today is the {festival}.");
        }

        string weather = farm.GetWeather().Weather;
        facts["weatherToday"] = weather;
        facts["weatherTomorrow"] = Game1.weatherForTomorrow;
        string luck = LuckLevel(me);
        facts["luck"] = luck;
        lines.Add($"Weather: {WeatherWord(weather)} today, {WeatherWord(Game1.weatherForTomorrow)} tomorrow. Luck: {luck}.");

        // Birthdays of villagers the player has met, with the best owned gift (discovered tastes only unless "tell me everything").
        List<FoundItem>? scan = null;
        List<BundleHelper.Need> needs = BundleHelper.MissingNeeds();
        var birthdays = new List<object>();
        foreach (NPC npc in GiftIdeas.MetVillagers().Where(n => n.isBirthday()))
        {
            scan ??= ItemFinder.Scan();
            var idea = GiftIdeas.IdeasFor(npc, scan, needs, max: 1, onlyDiscovered: !everything).FirstOrDefault();
            birthdays.Add(new
            {
                npc = npc.displayName,
                giftsLeftToday = GiftIdeas.GiftLimit(npc).today,
                bestGiftOwned = idea.item == null ? null : new { item = idea.item.DisplayName, taste = idea.taste == 0 ? "loves" : "likes", where = idea.where },
                giftNote = idea.item != null ? null : everything ? "The player owns nothing this villager loves or likes." : "The player owns no gift whose taste they have discovered for this villager (others may still be liked; get_gift_ideas can tell if the player asks).",
            });
            lines.Add($"{npc.displayName}'s birthday today." + (idea.item != null ? $" You have {idea.item.DisplayName} ({(idea.taste == 0 ? "loved" : "liked")}, {idea.where})." : ""));
        }
        if (birthdays.Count > 0)
            facts["birthdays"] = birthdays;

        // Crops ready, crops that die tomorrow (last day of a season they do not continue into), machines ready.
        int ready = 0, dying = 0;
        foreach (GameLocation loc in new[] { farm, Game1.getLocationFromName("Greenhouse") }.Where(l => l != null))
        {
            foreach (TerrainFeature t in loc!.terrainFeatures.Values)
            {
                if (t is not HoeDirt { crop: { } crop } dirt || crop.dead.Value)
                    continue;
                if (dirt.readyForHarvest())
                    ready++;
                if (Game1.dayOfMonth == 28 && loc.IsOutdoors && crop.GetData() is CropData data && !data.Seasons.Contains(Next(Game1.season)))
                    dying++;
            }
        }
        int machines = 0;
        Utility.ForEachLocation(loc =>
        {
            if (loc is FarmHouse home && home != Utility.getHomeOfFarmer(me))
                return true;
            machines += loc.objects.Values.Count(o => o.readyForHarvest.Value && o.heldObject.Value is { } h && h is not StardewValley.Objects.Chest && o is not StardewValley.Objects.Chest);
            return true;
        });
        if (ready > 0) { facts["cropsReady"] = ready; lines.Add($"{ready} crops ready to harvest."); }
        if (dying > 0) { facts["cropsDyingTomorrow"] = dying; lines.Add($"{dying} crops die tomorrow when the season changes. Harvest what you can."); }
        if (machines > 0) { facts["machinesReady"] = machines; lines.Add($"{machines} machines have something ready."); }

        // The Traveling Cart: only items a bundle still needs.
        if (Game1.getLocationFromName("Forest") is Forest forest && forest.ShouldTravelingMerchantVisitToday() && needs.Count > 0)
        {
            var cart = new List<object>();
            try
            {
                foreach ((ISalable item, ItemStockInformation info) in ShopBuilder.GetShopStock("Traveler"))
                {
                    if (item is not Item i || info.TradeItem != null || !needs.Any(n => n.QualifiedId == i.QualifiedItemId && i.Quality >= n.MinQuality))
                        continue;
                    cart.Add(new { item = i.DisplayName, price = info.Price, forBundle = needs.First(n => n.QualifiedId == i.QualifiedItemId).Bundle });
                    lines.Add($"The Traveling Cart is in the forest and sells {i.DisplayName} ({info.Price}g), which a bundle needs.");
                }
            }
            catch (Exception)
            {
                // a mod removed the cart's shop
            }
            if (cart.Count > 0)
                facts["travelingCartBundleItems"] = cart;
        }

        // Quests that run out today or tomorrow, and a finished tool upgrade.
        var ending = me.questLog.Where(q => q.daysLeft.Value is > 0 and <= 2 && !q.completed.Value)
            .Select(q => new { quest = q.GetName(), daysLeft = q.daysLeft.Value })
            .Concat(me.team.specialOrders.Where(o => o.GetDaysLeft() is > 0 and <= 2).Select(o => new { quest = o.GetName(), daysLeft = o.GetDaysLeft() }))
            .ToList();
        if (ending.Count > 0)
        {
            facts["questsEnding"] = ending;
            foreach (var q in ending)
                lines.Add($"Quest \"{q.quest}\" ends {(q.daysLeft <= 1 ? "today" : "tomorrow")}.");
        }
        if (me.toolBeingUpgraded.Value is { } tool && me.daysLeftForToolUpgrade.Value <= 0)
        {
            facts["toolUpgradeReady"] = tool.DisplayName;
            lines.Add($"Your {tool.DisplayName} is ready at the blacksmith.");
        }

        // Bundle items about to go out of season (at most three more days, and not in the next season either).
        if (needs.Count > 0)
        {
            var soon = BundlePlan.Plan()
                .Where(p => !p.Enough && p.DaysLeft is >= 0 and <= 3 && p.NextSeasonToo != true)
                .GroupBy(p => p.Need.QualifiedId)
                .Select(g => new { item = g.First().Name, until = g.First().AvailableUntil, daysLeft = g.First().DaysLeft, bundle = g.First().Need.Bundle })
                .ToList();
            if (soon.Count > 0)
            {
                facts["bundleLastChance"] = soon;
                foreach (var s in soon)
                    lines.Add($"Last chance soon for {s.item} ({s.bundle}): {s.until}.");
            }
        }

        // Yesterday, from the day record.
        if (memory.RecentSummaries(1).FirstOrDefault() is { text: { Length: > 0 } text } last)
        {
            var bin = Regex.Match(text, @"In the shipping bin tonight: (\d+)g");
            var money = Regex.Match(text, @"\(([+-]\d+)g today");
            facts["yesterday"] = bin.Success || money.Success
                ? new { date = last.date, shippedValue = bin.Success ? int.Parse(bin.Groups[1].Value) : (int?)null, moneyChangeBeforeShipping = money.Success ? int.Parse(money.Groups[1].Value) : (int?)null }
                : new { date = last.date, summary = text.Length > 300 ? text.Substring(0, 300) : text };
            if (bin.Success && int.Parse(bin.Groups[1].Value) > 0)
                lines.Add($"Yesterday's shipping paid {bin.Groups[1].Value}g.");
        }

        facts["how"] = "Weather, birthdays, festival, quests, cart stock: raw or asked of the game. Luck: the TV's thresholds applied by the mod. Crops ready and dying, gift ideas, last chances: computed.";
        return (facts, lines);
    }

    /// <summary>The TV fortune teller's levels, from its thresholds (its own function draws random numbers, so it is not called).</summary>
    public static string LuckLevel(Farmer who)
    {
        double luck = who.DailyLuck;
        double shared = who.team.sharedDailyLuck.Value;
        if (luck == 0.0) return "neutral";
        if (shared == -0.12) return "very bad";
        if (luck < -0.07) return "bad";
        if (luck < -0.02) return "slightly bad";
        if (shared == 0.12) return "the best";
        if (luck > 0.07) return "very good";
        if (luck > 0.02) return "good";
        return "neutral";
    }

    private static string WeatherWord(string weather) => weather switch
    {
        "Sun" => "sunny",
        "Rain" => "rain",
        "Storm" => "storm",
        "Snow" => "snow",
        "Wind" => "windy",
        "GreenRain" => "green rain",
        "Festival" => "festival weather",
        "Wedding" => "wedding",
        _ => weather.ToLowerInvariant(),
    };

    private static Season Next(Season s) => (Season)(((int)s + 1) % 4);
}
