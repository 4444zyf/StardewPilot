using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Locations;
using StardewValley.TokenizableStrings;

namespace StardewPilot.Tools;

/// <summary>
/// get_fish_info: where and when fish can be caught, from the game's own data (Data/Fish, Data/Locations).
/// "Possible now" is our own check of the fixed conditions, in the order the game applies them
/// (GameLocation.GetFishFromLocationData and CheckGenericFishRequirements in 1.6.15). We never call the
/// game's catch function: it rolls dice and returns a single fish.
/// </summary>
internal static class FishKnowledge
{
    /// <summary>Condition keys that roll dice. "Possible" skips them: it means "can happen", not "will happen now".</summary>
    private static readonly HashSet<string> RandomKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "RANDOM", "SYNCED_RANDOM", "SYNCED_CHOICE", "SYNCED_SUMMER_RAIN_RANDOM",
    };

    /// <summary>
    /// Fish chosen in code before the data rules, by mine area (MineShaft.getFish, 1.6.15). The only hand-written table.
    /// Water to fish in is on mine floors 20, 60 and 100.
    /// </summary>
    private static readonly (string Area, string Floors, string[] Items, bool UsesMineData)[] MineTable =
    {
        ("upper mines", "floor 20", new[] { "(O)158" }, true),        // Stonefish, then the UndergroundMine data rules
        ("frozen mines", "floor 60", new[] { "(O)161" }, true),       // Ice Pip, then the UndergroundMine data rules
        ("lava mines", "floor 100", new[] { "(O)162", "(O)CaveJelly" }, false), // Lava Eel, else Cave Jelly or trash
    };

    public static ToolDef Tool() => new()
    {
        Name = "get_fish_info",
        Description =
            "Where and when fish can be caught, from the game's own data (so it is right for this game version and the player's mods). " +
            "Give 'fish' to ask where and when one fish bites. Give 'location' (or nothing, for the current map) to list the fish of a map, " +
            "with which ones are possible right now (time, weather, season, fishing level). 'possibleNow' is the mod's own check of the game's " +
            "rules ('computed'), and means the fish can bite, not that it will. Entries the player has not reached (maps never visited, fish never " +
            "caught) are marked; follow the spoiler rule for those.",
        InputSchema = Schema.Object(
            ("fish", Schema.String("A fish name or id, for example 'Catfish' or '(O)143'."), false),
            ("location", Schema.String("A map name such as Beach, Mountain, Forest, Town, Farm, UndergroundMine20. Omit for the current map."), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string? fish = args.Str("fish");
        if (!string.IsNullOrWhiteSpace(fish))
        {
            string qid = StateTools.FindItemId(fish) ?? throw new ToolError($"No item named '{fish}'. Check the spelling.");
            return WhereIs(qid);
        }
        string? where = args.Str("location");
        // Mine floors exist only while someone is on them; answer for any floor by number.
        if (where != null && System.Text.RegularExpressions.Regex.Match(where, @"^(?:UndergroundMine|mine floor |mines? )(\d+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } m
            && !MineShaft.activeMines.Any(mine => mine.Name.Equals(where, StringComparison.OrdinalIgnoreCase)))
            // (Game1.getLocationFromName would create the floor, so it is not used to test this.)
        {
            int level = int.Parse(m.Groups[1].Value);
            return FishAt(Game1.getLocationFromName("Mine") ?? Game1.currentLocation, MineAreaOf(level), level);
        }
        return FishAt(StateTools.ResolveLocation(where));
    }

    /// <summary>MineShaft.getMineArea for a floor of the regular mines (1.6.15). Floors above 120 are the Skull Cavern.</summary>
    private static int MineAreaOf(int level) =>
        level > 120 ? 121 : level >= 80 ? 80 : level >= 40 ? 40 : level > 10 && level < 30 ? 10 : 0;

    // ---------------- Data/Fish ----------------

    /// <summary>The generic requirements in Data/Fish for one fish (the row is "/"-separated; see CheckGenericFishRequirements).</summary>
    private sealed class FishRow
    {
        public bool IsTrap;
        public string TrapWater = "";
        public List<(int Start, int End)> Times = new();
        public string Weather = "both";
        public int MinLevel;
        public int Difficulty;
        public string Behavior = "";
        public bool Valid = true;

        public static FishRow? Get(string itemId)
        {
            if (!DataLoader.Fish(Game1.content).TryGetValue(itemId, out string? raw))
                return null;
            string[] f = raw.Split('/');
            var row = new FishRow();
            if (ArgUtility.Get(f, 1) == "trap")
            {
                row.IsTrap = true;
                row.TrapWater = ArgUtility.Get(f, 4) ?? "";
                return row;
            }
            string[] spans = ArgUtility.SplitBySpace(ArgUtility.Get(f, 5) ?? "");
            for (int i = 0; i + 1 < spans.Length; i += 2)
            {
                if (int.TryParse(spans[i], out int a) && int.TryParse(spans[i + 1], out int b))
                    row.Times.Add((a, b));
                else
                    row.Valid = false;
            }
            row.Weather = ArgUtility.Get(f, 7) ?? "both";
            row.Valid &= int.TryParse(ArgUtility.Get(f, 12), out row.MinLevel);
            int.TryParse(ArgUtility.Get(f, 1), out row.Difficulty);
            row.Behavior = ArgUtility.Get(f, 2) ?? "";
            return row;
        }

        public object Describe() => this.IsTrap
            ? new { crabPot = true, water = this.TrapWater }
            : new
            {
                hours = this.Times.Select(t => $"{StateTools.FormatTime(t.Start)}-{StateTools.FormatTime(t.End)}").ToList(),
                weather = this.Weather switch { "rainy" => "rain only", "sunny" => "no rain", _ => "any" },
                minFishingLevel = this.MinLevel,
                difficulty = this.Difficulty,
                movement = this.Behavior,
            };

        /// <summary>Why this fish cannot bite now by its Data/Fish rules, or null if it can.</summary>
        public string? BlockedNow(GameLocation loc)
        {
            if (this.IsTrap || !this.Valid)
                return null;
            if (!this.Times.Any(t => Game1.timeOfDay >= t.Start && Game1.timeOfDay < t.End))
                return "wrong time of day";
            if (this.Weather == "rainy" && !loc.IsRainingHere())
                return "only bites in rain";
            if (this.Weather == "sunny" && loc.IsRainingHere())
                return "does not bite in rain";
            if (Game1.player.FishingLevel < this.MinLevel)
                return $"needs fishing level {this.MinLevel}";
            return null;
        }
    }

    // ---------------- spawn rules (Data/Locations) ----------------

    private static IEnumerable<SpawnFishData> RulesFor(string locationName)
    {
        IEnumerable<SpawnFishData> rules = Game1.locationData.TryGetValue("Default", out LocationData? def) ? def.Fish ?? new() : new List<SpawnFishData>();
        // The game's own lookup: the farm uses an entry per farm type (Farm_Standard, Farm_Beach ...).
        if (GameLocation.GetData(locationName) is { Fish.Count: > 0 } data)
            rules = rules.Concat(data.Fish);
        return rules;
    }

    /// <summary>The items a rule can produce, when it names them directly. Item queries (RANDOM_ITEMS ...) give null.</summary>
    private static List<string>? ItemsOf(SpawnFishData rule)
    {
        var ids = new List<string>();
        foreach (string? id in new[] { rule.ItemId }.Concat(rule.RandomItemId ?? new List<string>()))
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (ItemRegistry.GetData(id) is { } data && !data.IsErrorItem)
                ids.Add(data.QualifiedItemId);
            else
                return null; // an item query; not something we can list
        }
        return ids;
    }

    /// <summary>Seasons a rule allows: its Season field, or a LOCATION_SEASON / SEASON part of its condition. Null means any.</summary>
    private static string? SeasonText(SpawnFishData rule)
    {
        if (rule.Season.HasValue)
            return rule.Season.Value.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(rule.Condition))
            return null;
        foreach (string part in GameStateQuery.SplitRaw(rule.Condition))
        {
            string[] t = ArgUtility.SplitBySpaceQuoteAware(part.Trim());
            if (t.Length >= 3 && t[0].Equals("LOCATION_SEASON", StringComparison.OrdinalIgnoreCase))
                return string.Join(" ", t.Skip(2)).ToLowerInvariant();
            if (t.Length >= 2 && t[0].Equals("SEASON", StringComparison.OrdinalIgnoreCase))
                return string.Join(" ", t.Skip(1)).ToLowerInvariant();
        }
        return null;
    }

    /// <summary>The first part of a condition that fails now, worded for the player; null when all parts pass.</summary>
    private static string? FailingCondition(string condition, GameLocation loc)
    {
        foreach (string raw in GameStateQuery.SplitRaw(condition))
        {
            string part = raw.Trim();
            if (part.Length == 0 || GameStateQuery.CheckConditions(part, loc, Game1.player, null, null, null, RandomKeys))
                continue;
            string[] t = ArgUtility.SplitBySpaceQuoteAware(part);
            string key = t[0].TrimStart('!').ToUpperInvariant();
            if (!t[0].StartsWith("!") && key == "LOCATION_SEASON" && t.Length >= 3)
                return "only in " + string.Join(" ", t.Skip(2)).ToLowerInvariant();
            if (!t[0].StartsWith("!") && key == "SEASON" && t.Length >= 2)
                return "only in " + string.Join(" ", t.Skip(1)).ToLowerInvariant();
            return "game condition not met: " + part;
        }
        return null;
    }

    private static string AreaName(string locationName, string? areaId)
    {
        if (areaId == null)
            return "";
        LocationData? data = Game1.locationData.TryGetValue(locationName, out LocationData? exact) ? exact : GameLocation.GetData(locationName);
        if (data?.FishAreas != null && data.FishAreas.TryGetValue(areaId, out FishAreaData? area))
            return TokenParser.ParseText(area.DisplayName) ?? areaId;
        return areaId;
    }

    private static string MapDisplayName(string locationName)
    {
        if (locationName is "Temp" or "fishingGame")
            return "a festival (only during the festival)";
        if (locationName.StartsWith("Farm_"))
            return $"the farm ({locationName.Substring(5)} farm type)";
        if (Game1.getLocationFromName(locationName) is { } loc)
            return loc.DisplayName;
        if (Game1.locationData.TryGetValue(locationName, out LocationData? data) && data.DisplayName != null)
            return TokenParser.ParseText(data.DisplayName) ?? locationName;
        return locationName;
    }

    private static bool Reached(string locationName) =>
        locationName == "Default"
        || (locationName.StartsWith("Farm_") ? locationName == "Farm_" + Game1.GetFarmTypeKey() : Game1.player.locationsVisited.Contains(locationName));

    private static object? Restrictions(string locationName, SpawnFishData rule)
    {
        var r = new Dictionary<string, object?>();
        if (rule.FishAreaId != null) r["onlyInArea"] = AreaName(locationName, rule.FishAreaId);
        if (rule.PlayerPosition.HasValue || rule.BobberPosition.HasValue) r["onlyAtPartOfMap"] = true;
        if (rule.MinDistanceFromShore > 0) r["minTilesFromShore"] = rule.MinDistanceFromShore;
        if (rule.MaxDistanceFromShore > -1) r["maxTilesFromShore"] = rule.MaxDistanceFromShore;
        if (rule.RequireMagicBait) r["needsMagicBait"] = true;
        if (rule.CatchLimit > -1) r["catchLimit"] = rule.CatchLimit;
        if (rule.IsBossFish) r["legendary"] = true;
        if (!string.IsNullOrWhiteSpace(rule.Condition)) r["condition"] = rule.Condition;
        return r.Count > 0 ? r : null;
    }

    /// <summary>Why a rule cannot produce this fish now, or null if it can (bobber position and area are not known, so they are not checked).</summary>
    private static string? RuleBlockedNow(GameLocation loc, SpawnFishData rule, string qid)
    {
        Season season = Game1.GetSeasonForLocation(loc);
        if (rule.Season.HasValue && rule.Season != season)
            return $"only in {SeasonText(rule)}";
        if (Game1.player.FishingLevel < rule.MinFishingLevel)
            return $"needs fishing level {rule.MinFishingLevel}";
        if (rule.RequireMagicBait)
            return "needs magic bait";
        if (rule.CatchLimit > -1 && Game1.player.fishCaught.TryGetValue(qid, out int[]? caught) && caught[0] >= rule.CatchLimit)
            return "already caught as often as allowed";
        if (rule.Condition != null && FailingCondition(rule.Condition, loc) is { } failing)
            return failing;
        return null;
    }

    private static bool CaughtBefore(string qid) => Game1.player.fishCaught.ContainsKey(qid);

    // ---------------- queries ----------------

    private static object FishAt(GameLocation loc, int? mineArea = null, int? mineLevel = null)
    {
        string name = loc.Name;
        var notes = new List<string>();
        var entries = new Dictionary<string, FishEntry>();
        var other = new SortedSet<string>();
        int unlisted = 0;

        if (loc is MineShaft mine)
            mineArea = mine.getMineArea();
        if (mineArea.HasValue)
        {
            int area = mineArea.Value;
            if (mineLevel.HasValue)
                notes.Add($"Mine floor {mineLevel} (not loaded now; area worked out from the floor number).");
            var row = area switch { 0 or 10 => MineTable[0], 40 => MineTable[1], 80 => MineTable[2], _ => default };
            if (row.Items != null)
            {
                foreach (string qid in row.Items)
                    AddEntry(entries, other, qid, $"mines ({row.Area}), chosen by the game's code first", null, null);
                notes.Add($"In the {row.Area} the game first rolls for {string.Join(" and ", row.Items.Select(Display))} in code (water is on {row.Floors}).");
                if (!row.UsesMineData)
                {
                    notes.Add("Otherwise the catch here is trash; the map's data rules are not used.");
                    return Result(loc, entries, other, unlisted, notes, mineLevel);
                }
            }
            name = "UndergroundMine";
        }

        if (loc is Farm farm)
        {
            string[] prop = farm.GetMapPropertySplitBySpaces("FarmFishLocationOverride");
            if (prop.Length >= 2 && float.TryParse(prop[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float chance) && chance > 0)
                notes.Add($"This farm type also gives fish from {MapDisplayName(prop[0])} ({chance:P0} of casts). Ask for location '{prop[0]}' to see those.");
        }

        foreach (SpawnFishData rule in RulesFor(name))
        {
            List<string>? items = ItemsOf(rule);
            if (items == null)
            {
                unlisted++;
                continue;
            }
            foreach (string qid in items)
                AddEntry(entries, other, qid, null, rule, loc);
        }
        if (loc.Name != name)
            notes.Add($"Fish rules come from the map data '{name}'.");
        if (GameLocation.GetData(name) == null)
            notes.Add("This map has no fish rules of its own in the game data; only the rules shared by every map apply. A map added by a mod may choose fish in its own code, which this tool cannot see.");
        return Result(loc, entries, other, unlisted, notes, mineLevel);
    }

    private sealed class FishEntry
    {
        public string Qid = "";
        public string? BlockedNow = "no rule";
        public List<object> Rules = new();
        public string? Source;
    }

    private static void AddEntry(Dictionary<string, FishEntry> entries, SortedSet<string> other, string qid, string? source, SpawnFishData? rule, GameLocation? loc)
    {
        var data = ItemRegistry.GetDataOrErrorItem(qid);
        if (data.Category != StardewValley.Object.FishCategory)
        {
            other.Add(data.DisplayName);
            return;
        }
        if (!entries.TryGetValue(qid, out FishEntry? e))
            entries[qid] = e = new FishEntry { Qid = qid };
        if (source != null)
        {
            e.Source = source;
            e.BlockedNow = null;
            return;
        }
        if (rule == null || loc == null)
            return;
        e.Rules.Add(new
        {
            season = SeasonText(rule) ?? "any",
            minFishingLevel = rule.MinFishingLevel > 0 ? rule.MinFishingLevel : (int?)null,
            restrictions = Restrictions(loc.Name, rule),
            ignoresFishData = rule.IgnoreFishDataRequirements ? true : (bool?)null,
        });
        string? blocked = RuleBlockedNow(loc, rule, qid);
        if (blocked == null && !rule.IgnoreFishDataRequirements)
            blocked = FishRow.Get(data.ItemId)?.BlockedNow(loc);
        // Possible now if any rule allows it; otherwise keep the first reason.
        if (blocked == null)
            e.BlockedNow = null;
        else if (e.BlockedNow == "no rule")
            e.BlockedNow = blocked;
    }

    private static object Result(GameLocation loc, Dictionary<string, FishEntry> entries, SortedSet<string> other, int unlisted, List<string> notes, int? mineLevel = null)
    {
        var fish = entries.Values
            .Select(e =>
            {
                var data = ItemRegistry.GetDataOrErrorItem(e.Qid);
                FishRow? row = FishRow.Get(data.ItemId);
                return new
                {
                    name = data.DisplayName,
                    id = e.Qid,
                    possibleNow = e.BlockedNow == null,
                    notNowBecause = e.BlockedNow,
                    caughtBefore = CaughtBefore(e.Qid),
                    biteRules = row?.Describe(),
                    mapRules = e.Rules.Count > 0 ? e.Rules : null,
                    chosenBy = e.Source,
                };
            })
            .OrderByDescending(f => f.possibleNow).ThenBy(f => f.name)
            .ToList();
        return new
        {
            map = mineLevel.HasValue ? $"UndergroundMine{mineLevel}" : loc.Name,
            mapName = mineLevel.HasValue ? $"The Mines, floor {mineLevel}" : loc.DisplayName,
            now = new
            {
                season = Game1.GetSeasonForLocation(loc).ToString().ToLowerInvariant(),
                time = StateTools.FormatTime(Game1.timeOfDay),
                raining = loc.IsRainingHere(),
                fishingLevel = Game1.player.FishingLevel,
            },
            fish,
            otherCatches = other.Count > 0 ? other.ToList() : null,
            itemQueriesNotListed = unlisted > 0 ? unlisted : (int?)null,
            notes = notes.Count > 0 ? notes : null,
            how = "Lists: raw game data. possibleNow: computed by the mod from the game's rules (assumes no magic bait; parts of a map and random rolls are not checked).",
            stale = StateTools.IsPossiblyStale(loc) ? true : (bool?)null,
        };
    }

    private static object WhereIs(string qid)
    {
        var data = ItemRegistry.GetDataOrErrorItem(qid);
        FishRow? row = FishRow.Get(data.ItemId);
        var places = new List<object>();

        foreach ((string mapName, LocationData loc) in Game1.locationData)
        {
            if (loc.Fish == null)
                continue;
            foreach (SpawnFishData rule in loc.Fish)
            {
                if (ItemsOf(rule) is not { } items || !items.Contains(qid))
                    continue;
                places.Add(new
                {
                    map = mapName == "Default" ? "any map with water" : mapName,
                    mapName = mapName == "Default" ? "any map with water" : MapDisplayName(mapName),
                    reached = Reached(mapName),
                    season = SeasonText(rule) ?? "any",
                    minFishingLevel = rule.MinFishingLevel > 0 ? rule.MinFishingLevel : (int?)null,
                    restrictions = Restrictions(mapName, rule),
                    ignoresBiteRules = rule.IgnoreFishDataRequirements ? true : (bool?)null,
                });
            }
        }
        foreach (var m in MineTable.Where(m => m.Items.Contains(qid)))
            places.Add(new { map = "UndergroundMine", mapName = "The Mines", reached = Game1.player.deepestMineLevel >= 1, where = $"{m.Area} ({m.Floors}), chosen by the game's code" });

        var crabPotAreas = new List<string>();
        if (row?.IsTrap == true)
        {
            foreach ((string mapName, LocationData loc) in Game1.locationData)
                foreach ((string areaId, FishAreaData area) in loc.FishAreas ?? new())
                    if (area.CrabPotFishTypes?.Contains(row.TrapWater) == true)
                        crabPotAreas.Add(MapDisplayName(mapName));
        }

        int[]? caught = Game1.player.fishCaught.TryGetValue(qid, out int[]? c) ? c : null;
        return new
        {
            name = data.DisplayName,
            id = qid,
            isFish = data.Category == StardewValley.Object.FishCategory,
            caughtBefore = caught != null,
            timesCaught = caught?[0],
            biteRules = row?.Describe(),
            places,
            crabPot = row?.IsTrap == true
                ? new { water = row.TrapWater, maps = crabPotAreas.Distinct().ToList(), note = "Caught in crab pots placed in this kind of water, not with a rod." }
                : null,
            notes = places.Count == 0 && row?.IsTrap != true
                ? "No fishing rule names this item directly. It may come from a fish pond, a treasure chest, a crab pot, an item query rule, or code added by another mod."
                : null,
            how = "Raw game data (Data/Fish and Data/Locations), plus the mine table read from the game's code.",
        };
    }

    /// <summary>One place a fish can be caught, for other tools.</summary>
    public sealed record FishSource(string Map, string MapName, bool Reached, HashSet<Season>? Seasons, string? BlockedNow);

    /// <summary>
    /// Where a fish can be caught: the maps whose rules name it, the mines, and crab pots. Seasons null means all year.
    /// BlockedNow is why it cannot bite there now (computed like possibleNow; null when it can).
    /// </summary>
    public static List<FishSource> Sources(string qid)
    {
        var list = new List<FishSource>();
        FishRow? row = FishRow.Get(ItemRegistry.GetDataOrErrorItem(qid).ItemId);
        foreach ((string mapName, LocationData data) in Game1.locationData)
        {
            if (data.Fish == null || mapName is "Temp" or "fishingGame")
                continue;
            if (mapName.StartsWith("Farm_") && mapName != "Farm_" + Game1.GetFarmTypeKey())
                continue;
            foreach (SpawnFishData rule in data.Fish)
            {
                if (ItemsOf(rule) is not { } items || !items.Contains(qid))
                    continue;
                string map = mapName == "Default" ? "Town" : mapName.StartsWith("Farm_") ? "Farm" : mapName;
                GameLocation? loc = Game1.getLocationFromName(map);
                string? blocked = loc == null ? "map not loaded" : RuleBlockedNow(loc, rule, qid) ?? (rule.IgnoreFishDataRequirements ? null : row?.BlockedNow(loc));
                list.Add(new FishSource(map, mapName == "Default" ? "any map with water" : MapDisplayName(mapName), Reached(mapName), ParseSeasons(SeasonText(rule)), blocked));
            }
        }
        foreach (var m in MineTable.Where(m => m.Items.Contains(qid)))
            list.Add(new FishSource("UndergroundMine", $"The Mines, {m.Area} ({m.Floors})", Game1.player.deepestMineLevel >= 1, null, row?.BlockedNow(Game1.getFarm()) is { } b && b != "wrong time of day" ? b : null));
        if (row?.IsTrap == true)
            list.Add(new FishSource("CrabPot", $"crab pots in {row.TrapWater} water", true, null, null));
        return list;
    }

    private static HashSet<Season>? ParseSeasons(string? text)
    {
        if (text == null)
            return null;
        var set = new HashSet<Season>();
        foreach (string part in text.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Enum.TryParse(part, ignoreCase: true, out Season season))
                set.Add(season);
        }
        return set.Count > 0 ? set : null;
    }

    private static string Display(string qid) => ItemRegistry.GetDataOrErrorItem(qid).DisplayName;
}
