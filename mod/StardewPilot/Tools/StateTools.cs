using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.Quests;
using StardewValley.TerrainFeatures;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>Read-only tools that describe the game state. All run on the main thread.</summary>
internal static class StateTools
{
    public static void Register(ToolRegistry registry, Func<string> modVersion)
    {
        registry.Add(new ToolDef
        {
            Name = "get_status",
            Description = "Overview of the current moment: whether a save is loaded, date and time, weather, the farmer's money, energy, health, location, tile and held item, and whether the game is in a menu, event or paused. Call this first.",
            NeedsSave = false,
            Handler = _ => GetStatus(modVersion()),
        });

        registry.Add(new ToolDef
        {
            Name = "get_inventory",
            Description = "The farmer's backpack: every slot with item name, id, stack, quality and sell price. Slot 0-11 is the toolbar row.",
            Handler = _ => GetInventory(),
        });

        registry.Add(new ToolDef
        {
            Name = "get_location",
            Description = "Describe a map: size, exits (warps and doors with their target maps), buildings, NPCs and players present, forage, machines and their output, chests (including unopened gift boxes and their contents) and other objects. Defaults to the farmer's current map, so call it without arguments to see where the farmer is standing.",
            InputSchema = Schema.Object(
                ("location", Schema.String("Map name such as Farm, Town, SeedShop, FarmHouse. Omit for the current map."), false),
                ("includeObjects", Schema.Boolean("Include a list of every placed object with its tile (can be long). Default false."), false)),
            Handler = GetLocation,
        });

        registry.Add(new ToolDef
        {
            Name = "get_crops",
            Description = "Crops planted on a map (default Farm, including garden pots): grouped by crop with counts that are ready to harvest, need water, or are dead, and the days until the next harvest. Set includeTiles for tile positions.",
            InputSchema = Schema.Object(
                ("location", Schema.String("Map name. Default Farm."), false),
                ("includeTiles", Schema.Boolean("List tile positions for each group. Default false."), false)),
            Handler = GetCrops,
        });

        registry.Add(new ToolDef
        {
            Name = "get_calendar",
            Description = "This season's calendar: festivals and villager birthdays with their days, plus today's and tomorrow's events and tomorrow's weather forecast.",
            Handler = _ => GetCalendar(),
        });

        registry.Add(new ToolDef
        {
            Name = "get_relationships",
            Description = "Every villager the farmer has met: hearts, friendship points, whether talked to today, gifts given today and this week, birthday, relationship status, and current map when known.",
            Handler = _ => GetRelationships(),
        });

        registry.Add(new ToolDef
        {
            Name = "get_gift_tastes",
            Description = "The gifts a villager loves, likes, dislikes and hates, from the game's data. Universal tastes apply on top of these.",
            InputSchema = Schema.Object(("npc", Schema.String("Villager name, for example Abigail."), true)),
            Handler = GetGiftTastes,
        });

        registry.Add(new ToolDef
        {
            Name = "get_item_info",
            Description = "Look up an item by name or id: type, category, description, sell price, energy and health if edible. For seeds also the crop's seasons, days to grow and regrowth; for crops, which seed grows them.",
            InputSchema = Schema.Object(("item", Schema.String("Item name (for example 'Parsnip Seeds') or qualified id (for example '(O)472')."), true)),
            Handler = GetItemInfo,
        });

        registry.Add(new ToolDef
        {
            Name = "get_skills",
            Description = "Skill levels, experience points and professions.",
            Handler = _ => GetSkills(),
        });

        registry.Add(new ToolDef
        {
            Name = "get_quests",
            Description = "The quest log: each quest's title, description, objectives and days left.",
            Handler = _ => GetQuests(),
        });

        registry.Add(new ToolDef
        {
            Name = "get_bundles",
            Description = "Community Center bundles the farmer can see: each room's bundles with items still needed and items already given. Returns why if bundles are not visible yet or the farm chose Joja.",
            Handler = _ => GetBundles(),
        });
    }

    // ---------------- status ----------------

    private static object GetStatus(string modVersion)
    {
        if (!Context.IsWorldReady)
        {
            return new
            {
                saveLoaded = false,
                screen = Game1.activeClickableMenu is TitleMenu ? "title" : Game1.activeClickableMenu?.GetType().Name,
                gameVersion = Game1.version,
                modVersion,
            };
        }

        Farmer p = Game1.player;
        GameLocation loc = Game1.currentLocation;
        return new
        {
            saveLoaded = true,
            farmer = p.Name,
            farm = p.farmName.Value,
            multiplayer = Context.IsMultiplayer ? (Context.IsMainPlayer ? "host" : "guest") : "single-player",
            date = new { season = Game1.currentSeason, day = Game1.dayOfMonth, year = Game1.year, weekday = Game1.shortDayNameFromDayOfSeason(Game1.dayOfMonth) },
            time = FormatTime(Game1.timeOfDay),
            timeValue = Game1.timeOfDay,
            weatherToday = loc.GetWeather().Weather,
            weatherTomorrow = Game1.weatherForTomorrow,
            dailyLuck = Math.Round(p.DailyLuck, 3),
            money = p.Money,
            energy = new { current = (int)p.Stamina, max = p.MaxStamina },
            health = new { current = p.health, max = p.maxHealth },
            location = loc.NameOrUniqueName,
            locationDisplayName = loc.DisplayName,
            tile = new { x = p.TilePoint.X, y = p.TilePoint.Y },
            facing = FacingName(p.FacingDirection),
            heldItem = p.CurrentItem is { } held ? DescribeItem(held) : null,
            toolbarSlot = p.CurrentToolIndex,
            state = new
            {
                menu = Game1.activeClickableMenu?.GetType().Name,
                eventRunning = Game1.eventUp,
                festival = Game1.isFestival(),
                paused = Game1.paused || Game1.HostPaused,
                canMove = Context.CanPlayerMove,
            },
        };
    }

    // ---------------- inventory ----------------

    private static object GetInventory()
    {
        Farmer p = Game1.player;
        var slots = new List<object>();
        for (int i = 0; i < p.Items.Count; i++)
        {
            if (p.Items[i] is { } item)
                slots.Add(new { slot = i, item = DescribeItem(item) });
        }
        return new
        {
            capacity = p.MaxItems,
            used = slots.Count,
            selectedSlot = p.CurrentToolIndex,
            money = p.Money,
            slots,
        };
    }

    // ---------------- location ----------------

    private static object GetLocation(JObject args)
    {
        GameLocation loc = ResolveLocation(args.Str("location"));
        bool includeObjects = args.Bool("includeObjects") ?? false;

        var exits = new List<object>();
        // Map edges have one warp per tile; merge them into one exit per target.
        foreach (var group in loc.warps.GroupBy(w => w.TargetName))
        {
            var tiles = group.Select(w => new Point(w.X, w.Y)).ToList();
            Warp first = group.First();
            exits.Add(new
            {
                kind = "warp",
                to = group.Key,
                tiles = DescribeTiles(tiles),
                arriveAt = Tile(first.TargetX, first.TargetY),
            });
        }
        foreach (var pair in loc.doors.Pairs)
            exits.Add(new { kind = "door", to = pair.Value, tiles = DescribeTiles(new List<Point> { pair.Key }) });

        var buildings = loc.buildings.Select(b => (object)new
        {
            type = b.buildingType.Value,
            tile = Tile(b.tileX.Value, b.tileY.Value),
            size = new { w = b.tilesWide.Value, h = b.tilesHigh.Value },
            door = b.humanDoor.Value.X >= 0 ? Tile(b.tileX.Value + b.humanDoor.Value.X, b.tileY.Value + b.humanDoor.Value.Y) : null,
            interior = b.GetIndoorsName(),
            underConstruction = b.isUnderConstruction(),
        }).ToList();

        var npcs = loc.characters.Select(c => (object)new { name = c.Name, displayName = c.displayName, tile = Tile(c.TilePoint.X, c.TilePoint.Y), isVillager = c.IsVillager }).ToList();
        var players = loc.farmers.Select(f => (object)new { name = f.Name, tile = Tile(f.TilePoint.X, f.TilePoint.Y), isLocal = f.IsLocalPlayer }).ToList();

        var forage = new List<object>();
        var machines = new List<object>();
        var chests = new List<object>();
        var counts = new Dictionary<string, int>();
        var allObjects = new List<object>();
        foreach (var pair in loc.objects.Pairs)
        {
            SObject obj = pair.Value;
            object tile = Tile((int)pair.Key.X, (int)pair.Key.Y);
            if (includeObjects)
                allObjects.Add(new { name = obj.DisplayName, id = obj.QualifiedItemId, tile });

            if (obj is Chest chest && chest.playerChest.Value)
                chests.Add(new { tile, name = chest.DisplayName, items = chest.Items.Count(i => i != null), capacity = chest.GetActualCapacity() });
            else if (obj is Chest giftBox)
                chests.Add(new { tile, name = "Gift box", contents = giftBox.Items.Where(i => i != null).Select(i => $"{i.DisplayName} x{i.Stack}") });
            else if (obj.IsSpawnedObject)
                forage.Add(new { name = obj.DisplayName, tile });
            else if (obj.bigCraftable.Value && (obj.heldObject.Value != null || obj.MinutesUntilReady > 0))
                machines.Add(new
                {
                    name = obj.DisplayName,
                    tile,
                    holding = obj.heldObject.Value?.DisplayName,
                    ready = obj.readyForHarvest.Value,
                    minutesLeft = obj.readyForHarvest.Value ? 0 : obj.MinutesUntilReady,
                });
            else
                counts[obj.DisplayName] = counts.TryGetValue(obj.DisplayName, out int n) ? n + 1 : 1;
        }

        return new
        {
            name = loc.NameOrUniqueName,
            displayName = loc.DisplayName,
            isOutdoors = loc.IsOutdoors,
            isFarm = loc.IsFarm,
            size = new { w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight },
            possiblyStale = IsPossiblyStale(loc),
            exits,
            buildings,
            npcs,
            players,
            forage,
            machines,
            chests,
            otherObjects = counts.OrderByDescending(kv => kv.Value).Select(kv => new { name = kv.Key, count = kv.Value }),
            objects = includeObjects ? allObjects : null,
        };
    }

    // ---------------- crops ----------------

    private static object GetCrops(JObject args)
    {
        GameLocation loc = ResolveLocation(args.Str("location") ?? "Farm");
        bool includeTiles = args.Bool("includeTiles") ?? false;

        var groups = new Dictionary<string, CropGroup>();
        void Add(HoeDirt dirt, Vector2 tile, bool inPot)
        {
            Crop crop = dirt.crop;
            string name = crop.indexOfHarvest.Value is { Length: > 0 } harvest
                ? ItemRegistry.GetDataOrErrorItem("(O)" + harvest).DisplayName
                : "Unknown crop";
            if (crop.dead.Value)
                name += " (dead)";
            if (!groups.TryGetValue(name, out CropGroup? g))
                groups[name] = g = new CropGroup(name);

            g.Count++;
            bool ready = dirt.readyForHarvest();
            bool thirsty = !crop.dead.Value && dirt.needsWatering() && !dirt.isWatered();
            if (ready) g.Ready++;
            if (thirsty) g.NeedWater++;
            if (crop.dead.Value) g.Dead++;
            if (!crop.dead.Value && !ready)
            {
                int days = DaysToHarvest(crop);
                g.SoonestDays = Math.Min(g.SoonestDays, days);
            }
            if (inPot) g.InPots++;
            if (includeTiles)
                g.Tiles.Add(new { x = (int)tile.X, y = (int)tile.Y, ready, needsWater = thirsty });
        }

        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt { crop: not null } dirt)
                Add(dirt, pair.Key, false);
        }
        foreach (var pair in loc.objects.Pairs)
        {
            if (pair.Value is IndoorPot { hoeDirt.Value.crop: not null } pot)
                Add(pot.hoeDirt.Value, pair.Key, true);
        }

        int tilledEmpty = loc.terrainFeatures.Values.Count(t => t is HoeDirt { crop: null });
        return new
        {
            location = loc.NameOrUniqueName,
            possiblyStale = IsPossiblyStale(loc),
            totals = new
            {
                crops = groups.Values.Sum(g => g.Count),
                ready = groups.Values.Sum(g => g.Ready),
                needWater = groups.Values.Sum(g => g.NeedWater),
                dead = groups.Values.Sum(g => g.Dead),
                emptyTilledTiles = tilledEmpty,
            },
            groups = groups.Values.OrderBy(g => g.Name).Select(g => new
            {
                crop = g.Name,
                count = g.Count,
                ready = g.Ready,
                needWater = g.NeedWater,
                dead = g.Dead,
                inPots = g.InPots,
                nextHarvestInDays = g.SoonestDays == int.MaxValue ? (int?)null : g.SoonestDays,
                tiles = includeTiles ? g.Tiles : null,
            }),
        };
    }

    private sealed class CropGroup
    {
        public CropGroup(string name) => this.Name = name;
        public string Name { get; }
        public int Count, Ready, NeedWater, Dead, InPots;
        public int SoonestDays = int.MaxValue;
        public List<object> Tiles { get; } = new();
    }

    /// <summary>Days until a growing crop can next be harvested.</summary>
    public static int DaysToHarvest(Crop crop)
    {
        var phases = crop.phaseDays;
        int last = phases.Count - 1; // the last entry is the "fully grown" sentinel
        if (crop.fullyGrown.Value)
            return Math.Max(0, crop.dayOfCurrentPhase.Value); // regrowing crops count down here
        int days = 0;
        for (int i = crop.currentPhase.Value; i < last; i++)
        {
            int phaseLength = phases[i];
            days += i == crop.currentPhase.Value ? Math.Max(0, phaseLength - crop.dayOfCurrentPhase.Value) : phaseLength;
        }
        return days;
    }

    // ---------------- calendar ----------------

    private static object GetCalendar()
    {
        string seasonKey = Game1.currentSeason;
        var festivalDates = DataLoader.Festivals_FestivalDates(Game1.content);
        var festivals = new List<object>();
        for (int day = 1; day <= 28; day++)
        {
            if (festivalDates.TryGetValue($"{seasonKey}{day}", out string? name))
                festivals.Add(new { day, name });
        }

        var birthdays = new List<object>();
        Utility.ForEachVillager(npc =>
        {
            if (npc.Birthday_Season == seasonKey && npc.Birthday_Day > 0 && npc.CanSocialize)
                birthdays.Add(new { day = npc.Birthday_Day, npc = npc.Name, displayName = npc.displayName, met = Game1.player.friendshipData.ContainsKey(npc.Name) });
            return true;
        });

        int today = Game1.dayOfMonth;
        int tomorrow = today == 28 ? 1 : today + 1;
        return new
        {
            season = seasonKey,
            year = Game1.year,
            today,
            festivals,
            birthdays = birthdays.OrderBy(b => ((dynamic)b).day),
            todayEvents = Describe(today),
            tomorrowEvents = today == 28 ? new List<string> { "New season starts tomorrow" } : Describe(tomorrow),
            weatherTomorrow = Game1.weatherForTomorrow,
        };

        List<string> Describe(int day)
        {
            var list = new List<string>();
            if (festivalDates.TryGetValue($"{seasonKey}{day}", out string? f))
                list.Add($"Festival: {f}");
            Utility.ForEachVillager(npc =>
            {
                if (npc.Birthday_Season == seasonKey && npc.Birthday_Day == day && npc.CanSocialize)
                    list.Add($"Birthday: {npc.displayName}");
                return true;
            });
            return list;
        }
    }

    // ---------------- relationships ----------------

    private static object GetRelationships()
    {
        var list = new List<object>();
        foreach (var pair in Game1.player.friendshipData.Pairs)
        {
            NPC? npc = Game1.getCharacterFromName(pair.Key);
            Friendship f = pair.Value;
            GameLocation? where = npc?.currentLocation;
            list.Add(new
            {
                npc = pair.Key,
                displayName = npc?.displayName ?? pair.Key,
                hearts = f.Points / NPC.friendshipPointsPerHeartLevel,
                points = f.Points,
                status = f.Status.ToString(),
                talkedToday = f.TalkedToToday,
                giftsToday = f.GiftsToday,
                giftsThisWeek = f.GiftsThisWeek,
                birthday = npc != null && npc.Birthday_Day > 0 ? $"{npc.Birthday_Season} {npc.Birthday_Day}" : null,
                location = where != null && !IsPossiblyStale(where) ? where.NameOrUniqueName : null,
            });
        }
        return new { count = list.Count, villagers = list.OrderByDescending(v => ((dynamic)v).points) };
    }

    // ---------------- gift tastes ----------------

    private static object GetGiftTastes(JObject args)
    {
        string wanted = args.Str("npc") ?? throw new ToolError("npc is required.");
        var tastes = DataLoader.NpcGiftTastes(Game1.content);
        string? key = tastes.Keys.FirstOrDefault(k => !k.StartsWith("Universal_") && k.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (key == null)
        {
            NPC? byDisplay = null;
            Utility.ForEachVillager(n =>
            {
                if (n.displayName.Equals(wanted, StringComparison.OrdinalIgnoreCase)) { byDisplay = n; return false; }
                return true;
            });
            key = byDisplay?.Name;
        }
        if (key == null || !tastes.TryGetValue(key, out string? raw))
            throw new ToolError($"No gift data for '{wanted}'. Use the villager's name as shown in get_relationships.");

        // Format: loveText/loveIds/likeText/likeIds/dislikeText/dislikeIds/hateText/hateIds/neutralText/neutralIds
        string[] fields = raw.Split('/');
        string Ids(int index) => index < fields.Length ? fields[index] : "";
        return new
        {
            npc = key,
            loves = ResolveTasteIds(Ids(1)),
            likes = ResolveTasteIds(Ids(3)),
            dislikes = ResolveTasteIds(Ids(5)),
            hates = ResolveTasteIds(Ids(7)),
            note = "Universal tastes also apply: most villagers love Prismatic Shard and Rabbit's Foot, and like most artisan goods. Birthday gifts count 8x.",
        };
    }

    private static List<string> ResolveTasteIds(string ids)
    {
        var names = new List<string>();
        foreach (string id in ids.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(id, out int n) && n < 0)
                names.Add($"category: {SObject.GetCategoryDisplayName(n)}".TrimEnd(' ', ':'));
            else if (ItemRegistry.GetData(id) is { } data)
                names.Add(data.DisplayName);
        }
        return names;
    }

    // ---------------- item info ----------------

    private static Dictionary<string, string>? displayIndex;
    private static Dictionary<string, string>? internalIndex;

    /// <summary>Find a qualified item id from a name or id. Display names win over internal names; plain objects (O) win over other types.</summary>
    public static string? FindItemId(string query)
    {
        query = query.Trim();
        if (ItemRegistry.GetData(query) is { } direct && (query.StartsWith("(") || query.All(char.IsDigit)))
            return direct.QualifiedItemId;

        if (displayIndex == null || internalIndex == null)
        {
            displayIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internalIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            // Objects first, so "Stone" means the stone item, not a big craftable or a mine node.
            foreach (IItemDataDefinition type in ItemRegistry.ItemTypes.OrderBy(t => t.Identifier == "(O)" ? 0 : 1))
            {
                foreach (string id in type.GetAllIds())
                {
                    ParsedItemData? data = type.GetData(id);
                    if (data == null || data.IsErrorItem) continue;
                    displayIndex.TryAdd(data.DisplayName, data.QualifiedItemId);
                    internalIndex.TryAdd(data.InternalName, data.QualifiedItemId);
                }
            }
        }
        return displayIndex.TryGetValue(query, out string? qid) ? qid
            : internalIndex.TryGetValue(query, out qid) ? qid
            : null;
    }

    /// <summary>Drop the cached name index (call when the language changes).</summary>
    public static void ResetNameIndex() => displayIndex = internalIndex = null;

    private static object GetItemInfo(JObject args)
    {
        string query = args.Str("item") ?? throw new ToolError("item is required.");
        string qid = FindItemId(query) ?? throw new ToolError($"No item named '{query}'. Check the spelling, or use a qualified id like (O)24.");
        Item item = ItemRegistry.Create(qid);

        object? seedInfo = null;
        if (Crop.TryGetData(item.ItemId, out CropData? crop))
        {
            seedInfo = new
            {
                grows = ItemRegistry.GetDataOrErrorItem("(O)" + crop.HarvestItemId).DisplayName,
                seasons = crop.Seasons.Select(s => s.ToString()),
                daysToGrow = crop.DaysInPhase.Sum(),
                regrowDays = crop.RegrowDays > 0 ? crop.RegrowDays : (int?)null,
                needsTrellis = crop.IsRaised,
                paddyCrop = crop.IsPaddyCrop,
                harvestCountMin = crop.HarvestMinStack,
                harvestCountMax = crop.HarvestMaxStack,
            };
        }

        var grownFrom = Game1.cropData
            .Where(kv => kv.Value.HarvestItemId == item.ItemId && item.TypeDefinitionId == "(O)")
            .Select(kv => ItemRegistry.GetDataOrErrorItem("(O)" + kv.Key).DisplayName)
            .ToList();

        SObject? obj = item as SObject;
        return new
        {
            name = item.DisplayName,
            id = item.QualifiedItemId,
            category = item.getCategoryName(),
            description = CleanText(item.getDescription()),
            sellPrice = Utility.getSellToStorePriceOfItem(item, countStack: false),
            edible = obj != null && obj.Edibility != -300,
            energy = obj != null && obj.Edibility != -300 ? obj.staminaRecoveredOnConsumption() : (int?)null,
            health = obj != null && obj.Edibility != -300 ? obj.healthRecoveredOnConsumption() : (int?)null,
            seed = seedInfo,
            grownFromSeeds = grownFrom.Count > 0 ? grownFrom : null,
            inCollection = Game1.player.basicShipped.ContainsKey(item.ItemId) || Game1.player.fishCaught.ContainsKey(item.QualifiedItemId) || Game1.player.mineralsFound.ContainsKey(item.ItemId),
        };
    }

    // ---------------- skills ----------------

    private static object GetSkills()
    {
        Farmer p = Game1.player;
        string[] names = { "Farming", "Fishing", "Foraging", "Mining", "Combat" };
        int[] levels = { p.FarmingLevel, p.FishingLevel, p.ForagingLevel, p.MiningLevel, p.CombatLevel };
        return new
        {
            skills = names.Select((n, i) => new { skill = n, level = levels[i], experience = p.experiencePoints[i] }),
            professions = p.professions.Select(id => LevelUpMenu.getProfessionTitleFromNumber(id)),
        };
    }

    // ---------------- quests ----------------

    private static object GetQuests()
    {
        return new
        {
            quests = Game1.player.questLog.Select(q => new
            {
                title = q.GetName(),
                description = CleanText(q.GetDescription()),
                objectives = q.GetObjectiveDescriptions(),
                daysLeft = q.GetDaysLeft() > 0 ? q.GetDaysLeft() : (int?)null,
                completed = q.completed.Value,
            }),
            specialOrders = Game1.player.team.specialOrders.Select(o => new
            {
                title = o.GetName(),
                description = CleanText(o.GetDescription()),
                objectives = o.objectives.Select(obj => $"{obj.GetDescription()} ({obj.GetCount()}/{obj.GetMaxCount()})"),
                daysLeft = o.GetDaysLeft(),
            }),
        };
    }

    // ---------------- bundles ----------------

    private static object GetBundles()
    {
        Farmer p = Game1.player;
        if (p.mailReceived.Contains("JojaMember"))
            return new { visible = false, reason = "The farm joined Joja; the Community Center is being converted." };
        if (!p.hasOrWillReceiveMail("canReadJunimoText"))
            return new { visible = false, reason = "The farmer has not been able to read the Community Center's bundles yet." };

        var cc = Game1.RequireLocation<CommunityCenter>("CommunityCenter");
        var state = Game1.netWorldState.Value.Bundles;
        var rooms = new Dictionary<string, List<object>>();
        foreach (var pair in Game1.netWorldState.Value.BundleData)
        {
            // Key: "Room/index". Value: name/reward/items/color/required/sprite/displayName
            string[] keyParts = pair.Key.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int index))
                continue;
            string room = keyParts[0];
            string[] fields = pair.Value.Split('/');
            string[] itemFields = fields.Length > 2 ? fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries) : Array.Empty<string>();
            int slotCount = itemFields.Length / 3;
            int required = fields.Length > 4 && int.TryParse(fields[4], out int r) && r > 0 ? r : slotCount;
            bool[] done = state.TryGetValue(index, out bool[]? arr) ? arr : new bool[slotCount];

            var needed = new List<string>();
            var given = new List<string>();
            for (int i = 0; i < slotCount; i++)
            {
                string id = itemFields[i * 3];
                int qty = int.TryParse(itemFields[i * 3 + 1], out int q) ? q : 1;
                int quality = int.TryParse(itemFields[i * 3 + 2], out int ql) ? ql : 0;
                string label = id == "-1"
                    ? $"{qty}g"
                    : $"{ItemRegistry.GetDataOrErrorItem(id).DisplayName} x{qty}{QualityLabel(quality)}";
                (i < done.Length && done[i] ? given : needed).Add(label);
            }

            string displayName = fields.Length > 6 && fields[6].Length > 0 ? fields[6] : fields[0];
            bool complete = cc.isBundleComplete(index);
            if (!rooms.TryGetValue(room, out var list))
                rooms[room] = list = new List<object>();
            list.Add(new
            {
                bundle = displayName,
                complete,
                itemsRequired = required,
                itemsGiven = given.Count,
                stillNeeded = complete ? null : needed,
                given,
            });
        }

        return new
        {
            visible = true,
            rooms = rooms.Select(kv => new { room = kv.Key, bundles = kv.Value }),
        };
    }

    private static string QualityLabel(int quality) => quality switch
    {
        1 => " (silver+)",
        2 => " (gold+)",
        4 => " (iridium)",
        _ => "",
    };

    // ---------------- shared helpers ----------------

    public static GameLocation ResolveLocation(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return Game1.currentLocation;
        GameLocation? loc = Game1.getLocationFromName(name);
        if (loc == null)
        {
            // Accept display names too.
            loc = Game1.locations.FirstOrDefault(l => l.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        return loc ?? throw new ToolError($"No map named '{name}'. Use names like Farm, FarmHouse, Town, SeedShop, Beach, Forest, Mountain.");
    }

    /// <summary>In co-op, a guest only receives live updates for maps where a player is, plus always-active maps.</summary>
    public static bool IsPossiblyStale(GameLocation loc)
    {
        if (Context.IsMainPlayer)
            return false;
        if (loc.isAlwaysActive.Value || (loc.Root?.Value?.isAlwaysActive.Value ?? false))
            return false;
        return loc.farmers.Count == 0;
    }

    public static object DescribeItem(Item item) => new
    {
        name = item.DisplayName,
        id = item.QualifiedItemId,
        stack = item.Stack,
        quality = item.Quality > 0 ? QualityName(item.Quality) : null,
        category = item.getCategoryName() is { Length: > 0 } c ? c : null,
        isTool = item is Tool ? true : (bool?)null,
        toolLevel = item is Tool t && t.UpgradeLevel > 0 ? t.UpgradeLevel : (int?)null,
        waterLeft = item is StardewValley.Tools.WateringCan can ? can.WaterLeft : (int?)null,
        sellPrice = item is SObject ? Utility.getSellToStorePriceOfItem(item, countStack: false) : (int?)null,
    };

    private static string QualityName(int q) => q switch { 1 => "silver", 2 => "gold", 4 => "iridium", _ => q.ToString() };

    public static object Tile(int x, int y) => new { x, y };

    /// <summary>A tile list, or a range when the tiles form one straight line.</summary>
    private static object DescribeTiles(List<Point> tiles)
    {
        if (tiles.Count == 1)
            return Tile(tiles[0].X, tiles[0].Y);
        int minX = tiles.Min(t => t.X), maxX = tiles.Max(t => t.X), minY = tiles.Min(t => t.Y), maxY = tiles.Max(t => t.Y);
        bool line = (minX == maxX && maxY - minY + 1 == tiles.Count) || (minY == maxY && maxX - minX + 1 == tiles.Count);
        if (line)
            return new { from = Tile(minX, minY), to = Tile(maxX, maxY) };
        return tiles.Select(t => Tile(t.X, t.Y)).ToList();
    }

    /// <summary>Game text is wrapped for its own UI; collapse that into plain sentences.</summary>
    public static string CleanText(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s+", " ").Trim();

    public static string FormatTime(int time)
    {
        int hours = time / 100 % 24;
        int minutes = time % 100;
        string suffix = hours >= 12 ? "pm" : "am";
        int h12 = hours % 12 == 0 ? 12 : hours % 12;
        return $"{h12}:{minutes:00}{suffix}" + (time >= 2400 ? " (after midnight)" : "");
    }

    public static string FacingName(int dir) => dir switch { 0 => "up", 1 => "right", 2 => "down", 3 => "left", _ => dir.ToString() };
}
