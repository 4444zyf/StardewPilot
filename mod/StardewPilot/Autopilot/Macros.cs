using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewPilot.Guide;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData.Crops;
using StardewValley.Internal;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewPilot.Autopilot;

/// <summary>
/// Deterministic multi-step tasks. The model chooses what to do; these do it the way a player would:
/// walking, spending energy and time, and using the game's own code for every action.
/// </summary>
internal sealed class Macros
{
    private readonly Func<ModConfig> config;
    private readonly IModHelper helper;

    public Macros(Func<ModConfig> config, IModHelper helper)
    {
        this.config = config;
        this.helper = helper;
    }

    private ModConfig Cfg => this.config();

    public ModConfig Config => this.config();

    // ---------------------------------------------------------------- walking

    public IEnumerable WalkTo(JobContext ctx, string? location, Point? tile, string? npc)
    {
        if (npc != null)
        {
            NPC who = FindNpc(npc);
            foreach (object? s in Navigation.WalkTo(ctx, who.currentLocation.NameOrUniqueName, null))
                yield return s;
            foreach (object? s in this.ApproachNpc(ctx, who))
                yield return s;
            ctx.Report["arrivedNextTo"] = who.displayName;
        }
        else
        {
            string target = location is { Length: > 0 } ? StateTools.ResolveLocation(location).NameOrUniqueName : Game1.currentLocation.NameOrUniqueName;
            foreach (object? s in Navigation.WalkTo(ctx, target, tile))
                yield return s;
        }
        ctx.Report["location"] = Game1.currentLocation.NameOrUniqueName;
        ctx.Report["tile"] = new { x = Game1.player.TilePoint.X, y = Game1.player.TilePoint.Y };
    }

    /// <summary>Walk next to an NPC who may be moving.</summary>
    private IEnumerable ApproachNpc(JobContext ctx, NPC who)
    {
        for (int i = 0; i < 6; i++)
        {
            if (who.currentLocation != Game1.currentLocation)
            {
                foreach (object? s in Navigation.WalkTo(ctx, who.currentLocation.NameOrUniqueName, null))
                    yield return s;
            }
            Point target = who.TilePoint;
            Point me = Game1.player.TilePoint;
            if (Math.Abs(me.X - target.X) + Math.Abs(me.Y - target.Y) == 1)
                yield break;
            // The villager may walk off (even to another map) while we approach: on failure, look again.
            IEnumerator walk = Navigation.WalkWithinMap(ctx, target, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); } catch (JobStop) { break; }
                if (!more) break;
                yield return walk.Current;
            }
        }
        Point t = who.TilePoint, m = Game1.player.TilePoint;
        if (Math.Abs(m.X - t.X) + Math.Abs(m.Y - t.Y) == 1)
            yield break;

        // Shopkeepers stand behind counters: talk across the counter from two tiles away, as players do.
        foreach (Point across in new[] { new Point(t.X, t.Y + 2), new Point(t.X, t.Y - 2), new Point(t.X + 2, t.Y), new Point(t.X - 2, t.Y) })
        {
            if (!Navigation.IsWalkable(Game1.currentLocation, across))
                continue;
            bool reached = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, across, adjacent: false).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); } catch (JobStop) { reached = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            if (reached && Game1.player.TilePoint == across)
                yield break;
        }
        throw JobContext.Fail($"Could not get close enough to {who.displayName}.");
    }

    private static NPC FindNpc(string name)
    {
        NPC? found = Game1.getCharacterFromName(name);
        if (found == null)
        {
            Utility.ForEachVillager(n =>
            {
                if (n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase)) { found = n; return false; }
                return true;
            });
        }
        if (found?.currentLocation == null)
            throw JobContext.Fail($"Could not find a villager called '{name}' right now.");
        return found;
    }

    // ---------------------------------------------------------------- tools on tiles

    public IEnumerable UseToolAt(JobContext ctx, string toolName, Point tile)
    {
        Tool tool = Game1.player.Items.OfType<Tool>().FirstOrDefault(t =>
                t.DisplayName.Equals(toolName, StringComparison.OrdinalIgnoreCase) || t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase) || t.GetType().Name.Equals(toolName, StringComparison.OrdinalIgnoreCase))
            ?? throw JobContext.Fail($"No tool called '{toolName}' in the backpack.");
        foreach (object? s in Navigation.WalkWithinMap(ctx, tile, adjacent: true))
            yield return s;
        foreach (object? s in Actions.UseTool(ctx, this.Cfg, tool, tile))
            yield return s;
        ctx.Report["energy"] = (int)Game1.player.Stamina;
    }

    public IEnumerable InteractAt(JobContext ctx, Point tile)
    {
        foreach (object? s in Navigation.WalkWithinMap(ctx, tile, adjacent: true))
            yield return s;
        Actions.SelectSafeHand();
        ctx.ExpectingMenu = true;
        bool acted = Actions.Interact(tile);
        foreach (object? s in Actions.Settle(ctx))
            yield return s;
        var lines = new List<string>();
        foreach (object? s in Actions.ReadAndCloseDialogue(ctx, t => { if (lines.LastOrDefault() != t) lines.Add(t); }))
            yield return s;
        if (Game1.activeClickableMenu != null && Game1.activeClickableMenu is not ChatOverlayMenu)
        {
            ctx.Report["openedMenu"] = Game1.activeClickableMenu.GetType().Name;
            Game1.exitActiveMenu();
        }
        ctx.ExpectingMenu = false;
        ctx.Report["somethingHappened"] = acted;
        if (lines.Count > 0)
            ctx.Report["dialogue"] = lines;
    }

    // ---------------------------------------------------------------- watering

    public IEnumerable WaterCrops(JobContext ctx, string? location)
    {
        GameLocation loc = StateTools.ResolveLocation(location ?? "Farm");
        WateringCan can = Actions.FindTool<WateringCan>() ?? throw JobContext.Fail("There is no watering can in the backpack.");
        if (loc.IsOutdoors && Game1.IsRainingHere(loc))
        {
            ctx.Report["note"] = "It is raining here, so outdoor crops are already watered.";
            yield break;
        }
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;

        int watered = 0;
        var gaveUp = new HashSet<Vector2>();
        while (true)
        {
            Vector2? next = Nearest(ThirstyTiles(loc).Where(t => !gaveUp.Contains(t)));
            if (next is not { } tile)
                break;
            if (can.WaterLeft <= 0)
            {
                foreach (object? s in this.Refill(ctx, can))
                    yield return s;
            }
            Point target = new((int)tile.X, (int)tile.Y);
            IEnumerable walk = Navigation.WalkWithinMap(ctx, target, adjacent: true);
            foreach (object? s in walk)
                yield return s;
            foreach (object? s in Actions.UseTool(ctx, this.Cfg, can, target))
                yield return s;
            if (loc.terrainFeatures.TryGetValue(tile, out TerrainFeature f) && f is HoeDirt dirt && dirt.isWatered())
                watered++;
            else
                gaveUp.Add(tile);
            ctx.Progress($"watering crops ({watered} done)");
        }
        ctx.Report["watered"] = watered;
        ctx.Report["waterLeftInCan"] = can.WaterLeft;
        if (gaveUp.Count > 0)
            ctx.Problems.Add($"{gaveUp.Count} tiles could not be watered.");
    }

    private static IEnumerable<Vector2> ThirstyTiles(GameLocation loc) =>
        loc.terrainFeatures.Pairs
            .Where(p => p.Value is HoeDirt { crop: not null } d && !d.crop.dead.Value && d.needsWatering() && !d.isWatered())
            .Select(p => p.Key);

    private IEnumerable Refill(JobContext ctx, WateringCan can)
    {
        GameLocation loc = Game1.currentLocation;
        Point me = Game1.player.TilePoint;
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var water = new List<Point>();
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
                if (loc.CanRefillWateringCanOnTile(x, y))
                    water.Add(new Point(x, y));
        if (water.Count == 0)
            throw JobContext.Partial($"The watering can is empty and there is no water in {loc.DisplayName} to refill it.");

        foreach (Point spot in water.OrderBy(p => Math.Abs(p.X - me.X) + Math.Abs(p.Y - me.Y)).Take(8))
        {
            bool reached = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, spot, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); }
                catch (JobStop) { reached = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            if (!reached)
                continue;
            int before = can.WaterLeft;
            foreach (object? s in Actions.UseTool(ctx, this.Cfg, can, spot))
                yield return s;
            if (can.WaterLeft > before)
            {
                ctx.Progress("refilled the watering can");
                yield break;
            }
        }
        throw JobContext.Partial("Could not reach water to refill the watering can.");
    }

    // ---------------------------------------------------------------- harvesting

    public IEnumerable HarvestCrops(JobContext ctx, string? location, string? cropFilter)
    {
        GameLocation loc = StateTools.ResolveLocation(location ?? "Farm");
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;

        int harvested = 0;
        var gaveUp = new HashSet<Vector2>();
        var gained = new Dictionary<string, int>();
        while (true)
        {
            var ready = loc.terrainFeatures.Pairs
                .Where(p => p.Value is HoeDirt { crop: not null } d && d.readyForHarvest() && !gaveUp.Contains(p.Key)
                    && (cropFilter == null || CropName(d.crop).Contains(cropFilter, StringComparison.OrdinalIgnoreCase)))
                .Select(p => p.Key);
            if (Nearest(ready) is not { } tile)
                break;
            if (Game1.player.isInventoryFull())
                throw JobContext.Partial($"The backpack is full after harvesting {harvested} crops.");

            var dirt = (HoeDirt)loc.terrainFeatures[tile];
            Point target = new((int)tile.X, (int)tile.Y);
            foreach (object? s in Navigation.WalkWithinMap(ctx, target, adjacent: true))
                yield return s;

            var before = InventoryCounts();
            if (dirt.crop.GetHarvestMethod() == HarvestMethod.Scythe)
            {
                MeleeWeapon scythe = Actions.FindScythe() ?? throw JobContext.Fail("This crop needs a scythe, and there is none in the backpack.");
                foreach (object? s in Actions.UseTool(ctx, this.Cfg, scythe, target))
                    yield return s;
            }
            else
            {
                Actions.SelectSafeHand();
                Actions.Interact(target);
                foreach (object? s in Actions.Settle(ctx))
                    yield return s;
            }
            if (!(loc.terrainFeatures.TryGetValue(tile, out var f) && f is HoeDirt { crop: not null } after && after.readyForHarvest()))
            {
                harvested++;
                foreach (var (name, n) in Diff(before, InventoryCounts()))
                    gained[name] = gained.GetValueOrDefault(name) + n;
            }
            else
            {
                gaveUp.Add(tile);
            }
            ctx.Progress($"harvesting ({harvested} done)");
        }
        ctx.Report["harvested"] = harvested;
        ctx.Report["itemsGained"] = gained;
        if (gaveUp.Count > 0)
            ctx.Problems.Add($"{gaveUp.Count} ready crops could not be harvested.");
    }

    private static string CropName(Crop crop) =>
        crop.indexOfHarvest.Value is { Length: > 0 } id ? ItemRegistry.GetDataOrErrorItem("(O)" + id).DisplayName : "";

    // ---------------------------------------------------------------- planting

    public IEnumerable PlantSeeds(JobContext ctx, string seedQuery, int? count, string? location, bool water)
    {
        string qid = StateTools.FindItemId(seedQuery) ?? throw JobContext.Fail($"No item called '{seedQuery}'.");
        Item? seed = Game1.player.Items.FirstOrDefault(i => i?.QualifiedItemId == qid);
        if (seed == null)
            throw JobContext.Fail($"There are no {ItemRegistry.GetDataOrErrorItem(qid).DisplayName} in the backpack.");
        if (!Crop.TryGetData(seed.ItemId, out CropData? data))
            throw JobContext.Fail($"{seed.DisplayName} are not seeds.");

        GameLocation loc = StateTools.ResolveLocation(location ?? "Farm");
        bool seasonless = !loc.IsOutdoors || loc.SeedsIgnoreSeasonsHere();
        if (!seasonless && !data.Seasons.Contains(Game1.season))
            throw JobContext.Fail($"{seed.DisplayName} cannot grow in {Game1.currentSeason} here. They grow in {string.Join(", ", data.Seasons)}.");

        int have = Game1.player.Items.Where(i => i?.QualifiedItemId == qid).Sum(i => i.Stack);
        int want = Math.Min(count ?? have, have);
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;

        Hoe? hoe = Actions.FindTool<Hoe>();
        List<Point> tiles = PickPlantingTiles(loc, want, canTill: hoe != null);
        if (tiles.Count == 0)
            throw JobContext.Fail($"No free soil found in {loc.DisplayName} to plant on.");

        int planted = 0;
        var plantedTiles = new List<Point>();
        foreach (Point tile in OrderNearest(tiles))
        {
            foreach (object? s in Navigation.WalkWithinMap(ctx, tile, adjacent: true))
                yield return s;

            // Clear weeds, stones and twigs, cut grass, then till.
            foreach (object? s in this.ClearTile(ctx, loc, tile))
                yield return s;
            var vec = new Vector2(tile.X, tile.Y);
            if (!loc.terrainFeatures.TryGetValue(vec, out var tf) || tf is not HoeDirt)
            {
                if (hoe == null)
                    continue;
                foreach (object? s in Actions.UseTool(ctx, this.Cfg, hoe, tile))
                    yield return s;
            }
            if (loc.terrainFeatures.TryGetValue(vec, out tf) && tf is HoeDirt { crop: null })
            {
                if (!Actions.Select(i => i.QualifiedItemId == qid))
                    break; // out of seeds
                if (Actions.PlaceHeld(tile) && loc.terrainFeatures.TryGetValue(vec, out tf) && tf is HoeDirt { crop: not null })
                {
                    planted++;
                    plantedTiles.Add(tile);
                    ctx.Progress($"planting ({planted}/{want})");
                }
                yield return new WaitTicks(6);
            }
        }
        ctx.Report["planted"] = planted;
        ctx.Report["tiles"] = plantedTiles.Select(t => new { x = t.X, y = t.Y });

        if (water && planted > 0 && !(loc.IsOutdoors && Game1.IsRainingHere(loc)))
        {
            foreach (object? s in this.WaterCrops(ctx, loc.NameOrUniqueName))
                yield return s;
        }
        if (planted < want)
            ctx.Problems.Add($"Planted {planted} of {want}.");
    }

    private IEnumerable ClearTile(JobContext ctx, GameLocation loc, Point tile)
    {
        var vec = new Vector2(tile.X, tile.Y);
        if (loc.objects.TryGetValue(vec, out SObject obj))
        {
            Tool? tool = obj.IsWeeds() ? Actions.FindScythe() ?? (Tool?)Actions.FindTool<Pickaxe>()
                : obj.IsBreakableStone() ? Actions.FindTool<Pickaxe>()
                : obj.IsTwig() ? Actions.FindTool<Axe>()
                : null;
            for (int i = 0; i < 6 && tool != null && loc.objects.ContainsKey(vec); i++)
            {
                foreach (object? s in Actions.UseTool(ctx, this.Cfg, tool, tile))
                    yield return s;
            }
        }
        if (loc.terrainFeatures.TryGetValue(vec, out var tf) && tf is Grass && Actions.FindScythe() is { } scythe)
        {
            foreach (object? s in Actions.UseTool(ctx, this.Cfg, scythe, tile))
                yield return s;
        }
    }

    /// <summary>Choose soil tiles near the farmhouse (or the farmer): empty tilled soil first, then clearable ground.</summary>
    private static List<Point> PickPlantingTiles(GameLocation loc, int count, bool canTill)
    {
        var chosen = new List<Point>();
        foreach (var pair in loc.terrainFeatures.Pairs)
        {
            if (chosen.Count >= count) break;
            if (pair.Value is HoeDirt { crop: null } && !loc.objects.ContainsKey(pair.Key))
                chosen.Add(new Point((int)pair.Key.X, (int)pair.Key.Y));
        }
        if (chosen.Count >= count || !canTill)
            return chosen;

        Point anchor = loc is Farm farm ? farm.GetMainFarmHouseEntry() + new Point(0, 4) : Game1.player.TilePoint;
        var warpTiles = new HashSet<Point>(loc.warps.Select(w => new Point(w.X, w.Y)));
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var candidates = new List<Point>();
        for (int r = 1; r < 25 && candidates.Count < count * 3; r++)
        {
            for (int x = anchor.X - r; x <= anchor.X + r; x++)
            {
                for (int y = anchor.Y - r; y <= anchor.Y + r; y++)
                {
                    if (Math.Max(Math.Abs(x - anchor.X), Math.Abs(y - anchor.Y)) != r || x < 1 || y < 1 || x >= w - 1 || y >= h - 1)
                        continue;
                    var p = new Point(x, y);
                    if (x == anchor.X || warpTiles.Contains(p))
                        continue; // keep the path in front of the house clear
                    if (IsPlantable(loc, p))
                        candidates.Add(p);
                }
            }
        }
        foreach (Point p in candidates.OrderBy(p => Math.Abs(p.X - anchor.X) + Math.Abs(p.Y - anchor.Y)))
        {
            if (chosen.Count >= count) break;
            if (!chosen.Contains(p)) chosen.Add(p);
        }
        return chosen;
    }

    private static bool IsPlantable(GameLocation loc, Point p)
    {
        var vec = new Vector2(p.X, p.Y);
        if (loc.doesTileHaveProperty(p.X, p.Y, "Diggable", "Back") == null)
            return false;
        if (loc.getBuildingAt(vec) != null)
            return false;
        if (loc.terrainFeatures.TryGetValue(vec, out var tf) && tf is not Grass and not HoeDirt)
            return false;
        if (loc.objects.TryGetValue(vec, out SObject obj) && !(obj.IsWeeds() || obj.IsBreakableStone() || obj.IsTwig()))
            return false;
        if (loc.getLargeTerrainFeatureAt(p.X, p.Y) != null || loc.IsTileOccupiedBy(vec, CollisionMask.Buildings | CollisionMask.Furniture | CollisionMask.Characters | CollisionMask.LocationSpecific))
            return false;
        return true;
    }

    // ---------------------------------------------------------------- forage

    public IEnumerable CollectForage(JobContext ctx, string? location)
    {
        GameLocation loc = StateTools.ResolveLocation(location);
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;
        var got = new List<string>();
        var gaveUp = new HashSet<Vector2>();
        while (true)
        {
            var forage = loc.objects.Pairs.Where(p => p.Value.IsSpawnedObject && !gaveUp.Contains(p.Key)).Select(p => p.Key);
            if (Nearest(forage) is not { } tile)
                break;
            SObject obj = loc.objects[tile];
            if (!Game1.player.couldInventoryAcceptThisItem(obj))
                throw JobContext.Partial($"The backpack is full after picking up {got.Count} items.");
            Point target = new((int)tile.X, (int)tile.Y);
            bool reached = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, target, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); }
                catch (JobStop) { reached = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            if (!reached) { gaveUp.Add(tile); continue; }
            string name = obj.DisplayName;
            Actions.SelectSafeHand();
            Actions.Interact(target);
            foreach (object? s in Actions.Settle(ctx))
                yield return s;
            if (loc.objects.ContainsKey(tile)) gaveUp.Add(tile);
            else { got.Add(name); ctx.Progress($"foraging ({got.Count} picked up)"); }
        }
        ctx.Report["pickedUp"] = got.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}");
        if (gaveUp.Count > 0)
            ctx.Problems.Add($"{gaveUp.Count} forage items could not be reached.");
    }

    // ---------------------------------------------------------------- shopping

    /// <summary>Shop id to the map its counter is in.</summary>
    public static readonly Dictionary<string, string> ShopLocations = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SeedShop"] = "SeedShop",
        ["Joja"] = "JojaMart",
        ["FishShop"] = "FishShop",
        ["Blacksmith"] = "Blacksmith",
        ["AnimalShop"] = "AnimalShop",
        ["Saloon"] = "Saloon",
        ["Carpenter"] = "ScienceHouse",
        ["Hospital"] = "Hospital",
    };

    /// <summary>Find which shop sells an item and at what price. Joja is only used if asked for or nothing else sells it.</summary>
    public static (string shop, ISalable salable, int price, int stock)? FindInShops(string qid, string? onlyShop)
    {
        IEnumerable<string> shops = onlyShop != null ? new[] { onlyShop } : ShopLocations.Keys.OrderBy(s => s == "Joja" ? 1 : 0);
        foreach (string shop in shops)
        {
            Dictionary<ISalable, ItemStockInformation> stock;
            try { stock = ShopBuilder.GetShopStock(shop); }
            catch { continue; }
            foreach (var pair in stock)
            {
                if (pair.Key is Item item && item.QualifiedItemId == qid && pair.Value.TradeItem == null)
                    return (shop, pair.Key, pair.Value.Price, pair.Value.Stock);
            }
        }
        return null;
    }

    public IEnumerable Buy(JobContext ctx, string itemQuery, int count, string? shopId)
    {
        string qid = StateTools.FindItemId(itemQuery) ?? throw JobContext.Fail($"No item called '{itemQuery}'.");
        string name = ItemRegistry.GetDataOrErrorItem(qid).DisplayName;
        var found = FindInShops(qid, shopId) ?? throw JobContext.Fail($"No shop sells {name} today{(shopId != null ? $" at {shopId}" : "")}.");
        if (found.stock != int.MaxValue && found.stock < count)
            count = found.stock;
        int total = found.price * count;
        if (Game1.player.Money < total)
            throw JobContext.Fail($"{count} {name} cost {total}g, but the farmer has {Game1.player.Money}g.");
        if (total > this.Cfg.Autopilot.ConfirmSpendAbove)
        {
            bool yes = false;
            foreach (object? s in Confirm.Ask(ctx, this.Cfg, this.helper, Permission.Spend, $"Buy {count} {name} for {total}g?", a => yes = a))
                yield return s;
            if (!yes)
                throw JobContext.Fail("The player said no to the purchase.");
        }

        string shopMap = ShopLocations[found.shop];
        foreach (object? s in Navigation.WalkTo(ctx, shopMap, null))
            yield return s;
        (Point counter, Point stand) = FindCounter(Game1.currentLocation, found.shop)
            ?? throw JobContext.Fail($"Could not find the counter for {found.shop} in {Game1.currentLocation.DisplayName}.");
        foreach (object? s in Navigation.WalkWithinMap(ctx, stand, adjacent: false))
            yield return s;

        ctx.ExpectingMenu = true;
        Actions.SelectSafeHand();
        Actions.Interact(counter);
        yield return new WaitTicks(4);
        if (Game1.activeClickableMenu is DialogueBox)
        {
            string? why = Actions.CloseDialogue();
            throw JobContext.Fail($"The shop did not open: {why}");
        }
        if (Game1.activeClickableMenu is not ShopMenu shopMenu)
            throw JobContext.Fail("The shop did not open (the shopkeeper may be away or the shop closed).");

        ISalable? salable = shopMenu.forSale.FirstOrDefault(s => s is Item i && i.QualifiedItemId == qid);
        if (salable == null)
        {
            shopMenu.exitThisMenu();
            throw JobContext.Fail($"{name} is not on sale here right now.");
        }

        var tryPurchase = this.helper.Reflection.GetMethod(shopMenu, "tryToPurchaseItem");
        int bought = 0;
        while (bought < count)
        {
            Item probe = ItemRegistry.Create(qid);
            if (!Game1.player.couldInventoryAcceptThisItem(probe))
            {
                ctx.Problems.Add($"The backpack is full; bought {bought} of {count}.");
                break;
            }
            int batch = Math.Min(count - bought, Math.Max(1, probe.maximumStackSize()));
            int moneyBefore = Game1.player.Money;
            tryPurchase.Invoke<bool>(salable, null, batch, 0, 0);
            if (shopMenu.heldItem is Item held)
            {
                shopMenu.heldItem = null;
                Item? leftover = Game1.player.addItemToInventory(held);
                if (leftover != null)
                    Game1.createItemDebris(leftover, Game1.player.getStandingPosition(), Game1.player.FacingDirection);
            }
            if (Game1.player.Money >= moneyBefore)
            {
                ctx.Problems.Add($"The shop refused the purchase after {bought} {name}.");
                break;
            }
            bought += batch;
            ctx.Progress($"buying {name} ({bought}/{count})");
            yield return new WaitTicks(5);
        }
        shopMenu.exitThisMenu();
        ctx.ExpectingMenu = false;
        ctx.Report["bought"] = $"{name} x{bought}";
        ctx.Report["pricePaidEach"] = found.price;
        ctx.Report["moneyLeft"] = Game1.player.Money;
    }

    /// <summary>Which shop a counter tile's action opens. Older shops use their own action names.</summary>
    private static string? CounterShopId(string[] action) => action[0] switch
    {
        "OpenShop" when action.Length > 1 => action[1],
        "Buy" when action.Length > 1 && action[1] == "General" => "SeedShop",
        "Buy" when action.Length > 1 && action[1] == "Fish" => "FishShop",
        "JojaShop" => "Joja",
        "HospitalShop" => "Hospital",
        _ => null,
    };

    /// <summary>The counter tile that opens this shop, and where the customer stands.</summary>
    private static (Point counter, Point stand)? FindCounter(GameLocation loc, string shopId)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int x = 0; x < w; x++)
        {
            for (int y = 0; y < h; y++)
            {
                string[] action = loc.GetTilePropertySplitBySpaces("Action", "Buildings", x, y);
                if (action.Length == 0 || !shopId.Equals(CounterShopId(action), StringComparison.OrdinalIgnoreCase))
                    continue;
                string side = action[0] == "OpenShop" && action.Length > 2 ? action[2] : "down";
                Point stand = side switch
                {
                    "up" => new Point(x, y - 1),
                    "left" => new Point(x - 1, y),
                    "right" => new Point(x + 1, y),
                    _ => new Point(x, y + 1),
                };
                return (new Point(x, y), stand);
            }
        }
        return null;
    }

    // ---------------------------------------------------------------- shipping

    public IEnumerable Ship(JobContext ctx, List<(string item, int? count)> wanted, bool includeBundleItems)
    {
        var toShip = new List<(Item item, int count)>();
        var skipped = new List<string>();
        foreach (var (query, count) in wanted)
        {
            string qid = StateTools.FindItemId(query) ?? throw JobContext.Fail($"No item called '{query}'.");
            var stacks = Game1.player.Items.Where(i => i?.QualifiedItemId == qid && i.canBeShipped()).ToList();
            if (stacks.Count == 0)
            {
                skipped.Add($"{ItemRegistry.GetDataOrErrorItem(qid).DisplayName}: none in the backpack or it cannot be shipped");
                continue;
            }
            int remaining = count ?? stacks.Sum(s => s.Stack);
            foreach (Item stack in stacks)
            {
                if (remaining <= 0) break;
                if (!includeBundleItems && BundleHelper.BundleNeeding(stack) is { } bundle)
                {
                    // Keep one for the bundle; ship the rest.
                    int keep = 1;
                    int canShip = Math.Max(0, Math.Min(remaining, stack.Stack - keep));
                    skipped.Add($"kept one {stack.DisplayName} for the {bundle}");
                    if (canShip > 0) { toShip.Add((stack, canShip)); remaining -= canShip; }
                    continue;
                }
                int n = Math.Min(remaining, stack.Stack);
                toShip.Add((stack, n));
                remaining -= n;
            }
        }
        if (toShip.Count == 0)
            throw JobContext.Fail("Nothing to ship. " + string.Join("; ", skipped));

        int value = toShip.Sum(t => Utility.getSellToStorePriceOfItem(t.item, countStack: false) * t.count);
        string list = string.Join(", ", toShip.Select(t => $"{t.item.DisplayName} x{t.count}"));
        bool yes = false;
        foreach (object? s in Confirm.Ask(ctx, this.Cfg, this.helper, Permission.Ship, $"Ship {list} (about {value}g)?", a => yes = a))
            yield return s;
        if (!yes)
            throw JobContext.Fail("The player said no to shipping.");

        Farm farm = Game1.getFarm();
        Building bin = farm.buildings.FirstOrDefault(b => b is ShippingBin) ?? throw JobContext.Fail("The farm has no shipping bin.");
        foreach (object? s in Navigation.WalkTo(ctx, "Farm", null))
            yield return s;
        foreach (object? s in Navigation.WalkWithinMap(ctx, new Point(bin.tileX.Value, bin.tileY.Value), adjacent: true))
            yield return s;
        Actions.Face(new Point(bin.tileX.Value, bin.tileY.Value));

        var shipped = new List<string>();
        foreach (var (item, n) in toShip)
        {
            if (!Game1.player.Items.Contains(item))
                continue;
            if (n >= item.Stack)
            {
                farm.shipItem(item, Game1.player);
            }
            else
            {
                Item part = item.getOne();
                part.Stack = n;
                item.Stack -= n;
                farm.getShippingBin(Game1.player).Add(part);
                farm.showShipment(part, playThrowSound: false);
                farm.lastItemShipped = part;
            }
            shipped.Add($"{item.DisplayName} x{n}");
            ctx.Progress("shipping");
            yield return new WaitTicks(10);
        }
        ctx.Report["shipped"] = shipped;
        ctx.Report["estimatedValue"] = value;
        if (skipped.Count > 0)
            ctx.Report["skipped"] = skipped;
    }

    // ---------------------------------------------------------------- chests

    public IEnumerable Store(JobContext ctx, List<string>? items, bool allButTools, Point? chestTile, string? location)
    {
        foreach (object? s in this.GoToChest(ctx, chestTile, location, c => chest = c))
            yield return s;
        Chest box = chest!;
        var stored = new List<string>();
        var toStore = Game1.player.Items.Where(i => i != null && !(i is Tool) &&
            (allButTools || (items?.Any(q => StateTools.FindItemId(q) == i.QualifiedItemId || i.DisplayName.Equals(q.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false))).ToList();
        foreach (Item item in toStore)
        {
            Item? left = box.addItem(item);
            if (left == null) { Game1.player.removeItemFromInventory(item); stored.Add($"{item.DisplayName} x{item.Stack}"); }
            else { ctx.Problems.Add("The chest is full."); break; }
        }
        ctx.Report["stored"] = stored;
        ctx.Progress();
    }

    public IEnumerable Take(JobContext ctx, string itemQuery, int? count, Point? chestTile, string? location, int minQuality = 0)
    {
        string qid = StateTools.FindItemId(itemQuery) ?? throw JobContext.Fail($"No item called '{itemQuery}'.");
        string name = ItemRegistry.GetDataOrErrorItem(qid).DisplayName;
        // Several items can share a shown name (there are many "Stone"s): match the name as well as the id.
        bool Matches(Item? i) => i != null && (i.QualifiedItemId == qid || i.DisplayName.Equals(itemQuery.Trim(), StringComparison.OrdinalIgnoreCase)) && i.Quality >= minQuality;
        // Without a tile, go to the nearest chest (or fridge) that has the item.
        foreach (object? s in this.GoToChest(ctx, chestTile, location, c => chest = c, prefer: c => c.GetItemsForPlayer().Any(Matches), allowFridge: true))
            yield return s;
        Chest box = chest!;
        StardewValley.Inventories.IInventory items = box.GetItemsForPlayer();
        int want = count ?? int.MaxValue, got = 0;
        // Lowest quality that is good enough first, so better items stay for later.
        foreach (Item item in items.Where(Matches).OrderBy(i => i.Quality).ToList())
        {
            if (got >= want) break;
            int n = Math.Min(want - got, item.Stack);
            Item part = n == item.Stack ? item : item.getOne();
            part.Stack = n;
            if (!Game1.player.couldInventoryAcceptThisItem(part)) { ctx.Problems.Add("The backpack is full."); break; }
            if (part == item) items.Remove(item); else item.Stack -= n;
            Game1.player.addItemToInventory(part);
            got += n;
        }
        items.RemoveEmptySlots();
        if (got == 0 && ctx.Problems.Contains("The backpack is full."))
            throw JobContext.Fail($"The backpack is full, so no {name} could be taken.");
        if (got == 0)
            throw JobContext.Fail(minQuality > 0 ? $"That chest has no {name} of {ItemFinder.QualityName(minQuality)} quality or better." : $"That chest has no {name}.");
        ctx.Report["took"] = $"{name} x{got}";
        ctx.Progress();
    }

    private Chest? chest;

    /// <summary>
    /// Walk next to a chest: the one at the tile, else the nearest one <paramref name="prefer"/> accepts, else the nearest.
    /// Player chests (Junimo chests and mini-fridges included) always count; the house fridge only when allowed or named by its tile.
    /// </summary>
    private IEnumerable GoToChest(JobContext ctx, Point? chestTile, string? location, Action<Chest> found, Func<Chest, bool>? prefer = null, bool allowFridge = false)
    {
        GameLocation loc = StateTools.ResolveLocation(location);
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;
        Point me = Game1.player.TilePoint;
        var chests = loc.objects.Pairs
            .Where(p => p.Value is Chest c && c.playerChest.Value && !c.giftbox.Value)
            .Select(p => (tile: new Point((int)p.Key.X, (int)p.Key.Y), box: (Chest)p.Value))
            .ToList();
        if (loc.GetFridge(onlyUnlocked: true) is { } fridge && loc.GetFridgePosition() is { } fridgeTile && (allowFridge || chestTile == fridgeTile))
            chests.Add((fridgeTile, fridge));
        if (chests.Count == 0)
            throw JobContext.Fail($"There is no chest in {loc.DisplayName}.");
        (Point tile, Chest box) pick;
        if (chestTile is { } t)
        {
            pick = chests.FirstOrDefault(c => c.tile == t);
            if (pick.box == null)
                throw JobContext.Fail("No chest or fridge at that tile.");
        }
        else
        {
            var candidates = prefer == null ? chests : chests.Where(c => prefer(c.box)).ToList();
            if (candidates.Count == 0)
                candidates = chests;
            pick = candidates.OrderBy(c => Math.Abs(c.tile.X - me.X) + Math.Abs(c.tile.Y - me.Y)).First();
        }
        foreach (object? s in Navigation.WalkWithinMap(ctx, pick.tile, adjacent: true))
            yield return s;
        Actions.Face(pick.tile);
        found(pick.box);
    }

    // ---------------------------------------------------------------- people

    public IEnumerable TalkTo(JobContext ctx, string npcName)
    {
        NPC who = FindNpc(npcName);
        foreach (object? s in this.ApproachNpc(ctx, who))
            yield return s;
        Actions.SelectSafeHand();
        ctx.ExpectingMenu = true;
        // The villager may have stepped since we arrived: face them and use the game's own NPC interaction,
        // which is what the action button calls when it hits a villager.
        Actions.Face(who.TilePoint);
        if (!who.checkAction(Game1.player, Game1.currentLocation))
            Actions.Interact(who.TilePoint);
        foreach (object? s in Actions.Settle(ctx))
            yield return s;
        var lines = new List<string>();
        foreach (object? s in Actions.ReadAndCloseDialogue(ctx, t => { if (lines.LastOrDefault() != t) lines.Add(t); }))
            yield return s;
        ctx.ExpectingMenu = false;
        ctx.Report["said"] = lines;
        ctx.Report["talkedToday"] = Game1.player.friendshipData.TryGetValue(who.Name, out Friendship f) && f.TalkedToToday;
    }

    public IEnumerable Gift(JobContext ctx, string npcName, string itemQuery)
    {
        NPC who = FindNpc(npcName);
        string qid = StateTools.FindItemId(itemQuery) ?? throw JobContext.Fail($"No item called '{itemQuery}'.");
        Item item = Game1.player.Items.FirstOrDefault(i => i?.QualifiedItemId == qid) ?? throw JobContext.Fail($"There is no {ItemRegistry.GetDataOrErrorItem(qid).DisplayName} in the backpack.");
        bool yes = false;
        foreach (object? s in Confirm.Ask(ctx, this.Cfg, this.helper, Permission.Gift, $"Give {item.DisplayName} to {who.displayName}?", a => yes = a))
            yield return s;
        if (!yes)
            throw JobContext.Fail("The player said no to the gift.");
        foreach (object? s in this.ApproachNpc(ctx, who))
            yield return s;
        int giftsBefore = Game1.player.friendshipData.TryGetValue(who.Name, out Friendship f0) ? f0.GiftsToday : 0;
        Actions.Select(i => i == item);
        ctx.ExpectingMenu = true;
        // The villager may have stepped since we arrived: face them and use the game's own NPC interaction,
        // which is what the action button calls when it hits a villager.
        Actions.Face(who.TilePoint);
        if (!who.checkAction(Game1.player, Game1.currentLocation))
            Actions.Interact(who.TilePoint);
        foreach (object? s in Actions.Settle(ctx))
            yield return s;
        var lines = new List<string>();
        foreach (object? s in Actions.ReadAndCloseDialogue(ctx, t => { if (lines.LastOrDefault() != t) lines.Add(t); }))
            yield return s;
        ctx.ExpectingMenu = false;
        int giftsAfter = Game1.player.friendshipData.TryGetValue(who.Name, out Friendship f1) ? f1.GiftsToday : 0;
        if (giftsAfter <= giftsBefore)
            throw JobContext.Fail($"{who.displayName} did not take the gift. {string.Join(" ", lines)}");
        ctx.Report["reaction"] = lines;
    }

    // ---------------------------------------------------------------- bed

    public IEnumerable GoToBed(JobContext ctx)
    {
        FarmHouse home = Utility.getHomeOfFarmer(Game1.player);
        Point bed = home.GetPlayerBedSpot();
        foreach (object? s in Navigation.WalkTo(ctx, home.NameOrUniqueName, null))
            yield return s;

        // Stepping onto the bed makes the game ask "Go to sleep for the night?", usually before the walk ends.
        ctx.ExpectingMenu = true;
        IEnumerator walk = Navigation.WalkWithinMap(ctx, bed, adjacent: false).GetEnumerator();
        while (Game1.activeClickableMenu is not DialogueBox)
        {
            bool more;
            try { more = walk.MoveNext(); }
            catch (JobStop) when (Game1.activeClickableMenu is DialogueBox) { break; }
            if (!more) break;
            yield return walk.Current;
        }
        Game1.player.controller = null;
        for (int i = 0; i < 180 && Game1.activeClickableMenu is not DialogueBox; i++)
        {
            ctx.Touch();
            yield return null;
        }
        if (Game1.activeClickableMenu is DialogueBox box && home.lastQuestionKey == "Sleep")
        {
            Response yes = box.responses.FirstOrDefault(r => r.responseKey == "Yes") ?? box.responses.First();
            home.answerDialogue(yes);
            box.closeDialogue();
            ctx.Report["sleeping"] = true;
            ctx.Progress("going to sleep");
        }
        else
        {
            Actions.CloseDialogue();
            ctx.ExpectingMenu = false;
            throw JobContext.Fail("Reached the bed, but the game did not offer to sleep.");
        }
        ctx.ExpectingMenu = false;
    }

    // ---------------------------------------------------------------- helpers

    private static Vector2? Nearest(IEnumerable<Vector2> tiles)
    {
        Point me = Game1.player.TilePoint;
        Vector2? best = null;
        int bestD = int.MaxValue;
        foreach (Vector2 t in tiles)
        {
            int d = Math.Abs((int)t.X - me.X) + Math.Abs((int)t.Y - me.Y);
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    private static List<Point> OrderNearest(List<Point> tiles)
    {
        var left = new List<Point>(tiles);
        var order = new List<Point>();
        Point cur = Game1.player.TilePoint;
        while (left.Count > 0)
        {
            Point next = left.OrderBy(p => Math.Abs(p.X - cur.X) + Math.Abs(p.Y - cur.Y)).First();
            left.Remove(next);
            order.Add(next);
            cur = next;
        }
        return order;
    }

    private static Dictionary<string, int> InventoryCounts() =>
        Game1.player.Items.Where(i => i != null).GroupBy(i => i.DisplayName).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));

    private static IEnumerable<(string, int)> Diff(Dictionary<string, int> before, Dictionary<string, int> after) =>
        after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key)).Select(kv => (kv.Key, kv.Value - before.GetValueOrDefault(kv.Key)));
}
