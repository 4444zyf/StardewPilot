using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewPilot.Autopilot;
using StardewPilot.Bridge;
using StardewValley;
using StardewValley.Internal;

namespace StardewPilot.Tools;

/// <summary>Tools that make the farmer act. Each runs one autopilot job and returns its outcome.</summary>
internal static class ActionTools
{
    /// <summary>Every action job by name: its input schema and how to build it from arguments. Routines run their steps through these.</summary>
    public static readonly Dictionary<string, (JObject Schema, Func<JObject, Func<JobContext, IEnumerable>> Build)> Jobs = new(StringComparer.OrdinalIgnoreCase);

    public static void Register(ToolRegistry registry, JobRunner runner, Macros macros)
    {
        void Action(string name, string description, JObject schema, Func<JObject, Func<JobContext, IEnumerable>> build)
        {
            Jobs[name] = (schema, build);
            registry.Add(new ToolDef
            {
                Name = name,
                Description = description,
                InputSchema = schema,
                ReadOnly = false,
                AsyncHandler = async args =>
                {
                    Task<JobResult> job = await MainThread.Run(() => runner.Start(name, build(args), args));
                    JobResult result = await job;
                    return result.ToJson();
                },
            });
        }

        JObject Tile(string what) => Schema.Integer(what);

        Action("walk_to",
            "Walk the farmer to a map, a tile, or next to a villager. Crosses maps using doors and map edges, and respects shop hours. Give location (and optionally x, y), or npc.",
            Schema.Object(
                ("location", Schema.String("Map name, for example Town, SeedShop, Beach, FarmHouse. Defaults to the current map."), false),
                ("x", Tile("Tile x in that map."), false),
                ("y", Tile("Tile y in that map."), false),
                ("npc", Schema.String("Walk next to this villager instead."), false)),
            a => ctx => macros.WalkTo(ctx, a.Str("location"), a.Int("x") is int x && a.Int("y") is int y ? new Point(x, y) : null, a.Str("npc")));

        Action("water_crops",
            "Water every crop that needs it on a map (default Farm), refilling the watering can at the nearest water when empty. Skips if it is raining there.",
            Schema.Object(("location", Schema.String("Map name. Default Farm."), false)),
            a => ctx => macros.WaterCrops(ctx, a.Str("location")));

        Action("harvest_crops",
            "Harvest every ready crop on a map (default Farm), optionally only one kind. Stops if the backpack fills up.",
            Schema.Object(
                ("location", Schema.String("Map name. Default Farm."), false),
                ("crop", Schema.String("Only harvest this crop, for example Parsnip."), false)),
            a => ctx => macros.HarvestCrops(ctx, a.Str("location"), a.Str("crop")));

        Action("plant_seeds",
            "Plant seeds from the backpack: uses empty tilled soil first, otherwise clears weeds, stones and twigs and tills new soil near the farmhouse. Waters them afterwards unless it is raining.",
            Schema.Object(
                ("seeds", Schema.String("Seed item name, for example 'Parsnip Seeds'."), true),
                ("count", Schema.Integer("How many to plant. Default all of them."), false),
                ("location", Schema.String("Map name. Default Farm."), false),
                ("water", Schema.Boolean("Water after planting. Default true."), false)),
            a => ctx => macros.PlantSeeds(ctx, a.Str("seeds")!, a.Int("count"), a.Str("location"), a.Bool("water") ?? true));

        Action("collect_forage",
            "Pick up all forage (wild plants, shells and similar) on a map, default the current one, or only some kinds.",
            Schema.Object(
                ("location", Schema.String("Map name. Default the current map."), false),
                ("items", Schema.Array(Schema.String("Item name."), "Only these kinds, for example Truffle. Default all."), false)),
            a => ctx => macros.CollectForage(ctx, a.Str("location"), (a["items"] as JArray)?.Select(t => t.ToString()).ToList()));

        Action("buy_items",
            "Walk to the shop that sells an item and buy it through the shop menu. Fails if the shop is closed or the farmer cannot afford it. Large purchases ask the player first.",
            Schema.Object(
                ("item", Schema.String("Item name, for example 'Cauliflower Seeds'."), true),
                ("count", Schema.Integer("How many to buy."), true),
                ("shop", Schema.Enum("Optional shop id. Default: the first shop that sells it.", "SeedShop", "Joja", "FishShop", "Blacksmith", "AnimalShop", "Saloon", "Carpenter", "Hospital"), false)),
            a => ctx => macros.Buy(ctx, a.Str("item")!, Math.Max(1, a.Int("count") ?? 1), a.Str("shop")));

        Action("ship_items",
            "Put items from the backpack into the farm's shipping bin (sold overnight). Always asks the player first unless they gave standing permission. Keeps one of any item a Community Center bundle still needs unless includeBundleItems is true.",
            Schema.Object(
                ("items", Schema.Array(Schema.Object(
                    ("item", Schema.String("Item name."), true),
                    ("count", Schema.Integer("How many. Default all."), false)), "Items to ship."), true),
                ("includeBundleItems", Schema.Boolean("Also ship items a bundle still needs. Default false."), false)),
            a => ctx => macros.Ship(ctx, ((a["items"] as JArray) ?? new JArray()).OfType<JObject>().Select(o => (o.Str("item") ?? "", o.Int("count"))).ToList(), a.Bool("includeBundleItems") ?? false));

        Action("store_in_chest",
            "Put items from the backpack into a chest (the nearest one in the map, or the one at x, y).",
            Schema.Object(
                ("items", Schema.Array(Schema.String("Item name."), "Items to store."), false),
                ("allButTools", Schema.Boolean("Store everything except tools."), false),
                ("location", Schema.String("Map with the chest. Default the current map."), false),
                ("x", Tile("Chest tile x."), false),
                ("y", Tile("Chest tile y."), false),
                ("chest", Schema.String("A chest id saved in a routine (routines use it; leave it out)."), false)),
            a => ctx => macros.Store(ctx, (a["items"] as JArray)?.Select(t => t.ToString()).ToList(), a.Bool("allButTools") ?? false, a.Int("x") is int x && a.Int("y") is int y ? new Point(x, y) : null, a.Str("location"), a.Str("chest")));

        Action("put_things_away",
            "Put the backpack's items away into the player's own chests and fridges, the way the player already sorts them: each item goes to the chest that already holds most of it, " +
            "else to the chest holding most items of its kind. Items no chest has a kind for stay, and so do tools, the toolbar row (first 12 slots, unless includeToolbar) and the item in hand. " +
            "Mini-shipping bins, hoppers and Junimo chests are never used. Reports what went where and what stayed.",
            Schema.Object(
                ("includeToolbar", Schema.Boolean("Also put away items in the toolbar row. Default false."), false),
                ("items", Schema.Array(Schema.String("Item name."), "Only these items. Default all."), false)),
            a => ctx => macros.PutAway(ctx, a.Bool("includeToolbar") ?? false, (a["items"] as JArray)?.Select(t => t.ToString()).ToList()));

        Action("take_from_chest",
            "Take an item out of a chest, fridge or Junimo chest: the one at x, y, or else the nearest one in the map that has the item. find_items tells where things are. Optionally only items of at least a given quality (lowest good-enough quality is taken first).",
            Schema.Object(
                ("item", Schema.String("Item name."), true),
                ("count", Schema.Integer("How many. Default all."), false),
                ("location", Schema.String("Map with the chest. Default the current map."), false),
                ("x", Tile("Chest or fridge tile x."), false),
                ("y", Tile("Chest or fridge tile y."), false),
                ("minQuality", Schema.Enum("Only take items of this quality or better. Default any.", "normal", "silver", "gold", "iridium"), false),
                ("chest", Schema.String("A chest id saved in a routine (routines use it; leave it out)."), false)),
            a => ctx => macros.Take(ctx, a.Str("item")!, a.Int("count"), a.Int("x") is int x && a.Int("y") is int y ? new Point(x, y) : null, a.Str("location"), QualityValue(a.Str("minQuality")), a.Str("chest")));

        Action("talk_to",
            "Walk to a villager and talk to them (raises friendship once a day). Returns what they said.",
            Schema.Object(("npc", Schema.String("Villager name."), true)),
            a => ctx => macros.TalkTo(ctx, a.Str("npc")!));

        Action("give_gift",
            "Walk to a villager and give them an item from the backpack. Asks the player first unless they gave standing permission.",
            Schema.Object(
                ("npc", Schema.String("Villager name."), true),
                ("item", Schema.String("Item name."), true)),
            a => ctx => macros.Gift(ctx, a.Str("npc")!, a.Str("item")!));

        Action("fish",
            "Fish from the nearest open water in a map (default the current one): casts, waits for a bite and plays the fishing minigame. Needs a fishing rod; each cast costs energy.",
            Schema.Object(
                ("location", Schema.String("Map with water, for example Beach, Forest, Mountain, Town. Default the current map."), false),
                ("catches", Schema.Integer("How many fish to catch. Default 3."), false)),
            a => ctx => Fishing.Fish(ctx, macros.Config, a.Str("location"), Math.Clamp(a.Int("catches") ?? 3, 1, 30)));

        Action("explore_mines",
            "Go down into the mines a number of floors: takes ladders, breaks rocks with the pickaxe to find them, and fights monsters next to the farmer with the weapon. Stops when health or energy is low.",
            Schema.Object(("floors", Schema.Integer("How many floors to go down. Default 3."), false)),
            a => ctx => Mining.Descend(ctx, macros.Config, Math.Clamp(a.Int("floors") ?? 3, 1, 20)));

        var chores = new Chores(() => macros.Config);

        Action("tend_animals",
            "Look after the farm animals in one building or all: pick up produce on the floor, empty the auto-grabber, put out hay from the hopper (unless the building feeds itself), " +
            "pet every animal not petted today (and the cat or dog when all buildings are tended), and milk or shear the ones with produce ready when the farmer has the Milk Pail or Shears. Animals out on the farm are visited too. " +
            "Refuses after 7pm (animals are asleep). Buying hay is a separate buy_items call.",
            Schema.Object(("building", Schema.String("Only this building, for example Coop or Barn. Default all animal buildings."), false)),
            a => ctx => chores.TendAnimals(ctx, a.Str("building")));

        Action("tend_machines",
            "Collect the output of every ready machine on a map (default Farm), optionally only one kind. Optionally refill empty machines of that kind with an item from the backpack " +
            "(for example kegs with Hops); before walking it checks with the game that the machine takes the item, that there are enough of it, and any fuel (a furnace needs coal).",
            Schema.Object(
                ("location", Schema.String("Map name, for example Farm, Shed or Cellar. Default Farm."), false),
                ("machine", Schema.String("Only this kind of machine, for example Keg, Preserves Jar, Furnace."), false),
                ("refillWith", Schema.String("Item to load into the empty machines of that kind afterwards, for example Hops. Needs machine."), false)),
            a => ctx => chores.TendMachines(ctx, a.Str("location"), a.Str("machine"), a.Str("refillWith")));

        Action("go_to_bed",
            "Walk home and go to sleep, ending the day (the game saves overnight).",
            Schema.Object(),
            _ => ctx => macros.GoToBed(ctx));

        Action("use_tool",
            "Low-level fallback: walk next to a tile in the current map and use a tool on it (Axe, Hoe, Pickaxe, Watering Can, Scythe).",
            Schema.Object(
                ("tool", Schema.String("Tool name."), true),
                ("x", Tile("Tile x."), true),
                ("y", Tile("Tile y."), true)),
            a => ctx => macros.UseToolAt(ctx, a.Str("tool")!, new Point(a.Int("x") ?? 0, a.Int("y") ?? 0)));

        Action("interact",
            "Low-level fallback: walk next to a tile in the current map and press the action button on it (open, pick up, read a sign). Returns any dialogue shown.",
            Schema.Object(
                ("x", Tile("Tile x."), true),
                ("y", Tile("Tile y."), true)),
            a => ctx => macros.InteractAt(ctx, new Point(a.Int("x") ?? 0, a.Int("y") ?? 0)));

        registry.Add(new ToolDef
        {
            Name = "stop_autopilot",
            Description = "Stop whatever the autopilot is doing and hand control back to the player.",
            ReadOnly = false,
            Handler = _ =>
            {
                bool was = runner.IsRunning;
                runner.Stop(JobOutcome.Partial, I18n.Get("stop.agent"), StopCause.Replaced);
                return new { stopped = was };
            },
        });

        registry.Add(new ToolDef
        {
            Name = "get_shop_stock",
            Description = "What a shop sells today and at what price (does not need the shop to be open). Shops: SeedShop (Pierre), Joja, FishShop (Willy), Blacksmith (Clint), AnimalShop (Marnie), Saloon (Gus), Carpenter (Robin), Hospital, Traveler (the Traveling Cart; check travelingCartHereToday).",
            InputSchema = Schema.Object(("shop", Schema.Enum("Shop id.", Macros.ShopLocations.Keys.Append("Traveler").ToArray()), true)),
            Handler = a =>
            {
                string shop = a.Str("shop") ?? "SeedShop";
                var stock = ShopBuilder.GetShopStock(shop);
                return new
                {
                    shop,
                    location = shop == "Traveler" ? "Forest" : Macros.ShopLocations.GetValueOrDefault(shop),
                    travelingCartHereToday = shop == "Traveler" ? (Game1.getLocationFromName("Forest") as StardewValley.Locations.Forest)?.ShouldTravelingMerchantVisitToday() : null,
                    items = stock.Select(p => new
                    {
                        name = p.Key.DisplayName,
                        id = (p.Key as Item)?.QualifiedItemId,
                        price = p.Value.Price,
                        stock = p.Value.Stock == int.MaxValue ? (int?)null : p.Value.Stock,
                        tradeItem = p.Value.TradeItem,
                    }),
                };
            },
        });
    }

    private static int QualityValue(string? name) => name?.ToLowerInvariant() switch
    {
        "silver" => 1,
        "gold" => 2,
        "iridium" => 4,
        _ => 0,
    };
}
