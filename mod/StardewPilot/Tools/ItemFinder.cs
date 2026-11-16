using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>Where one stack of an item is.</summary>
internal sealed record FoundItem(
    Item Item,
    string Place,
    GameLocation? Location,
    Point? Tile,
    string? Container,
    bool Fetchable,
    bool PossiblyStale);

/// <summary>
/// find_items: where the player's items are. Walks every map in the game's own list (building interiors included)
/// and keeps a fixed set of places: the local backpack, chests the player placed, fridges, the shared Junimo chest
/// store, and machines whose output is ready. Everything else the game counts as an item (stones and weeds on maps,
/// machines themselves, furniture, hats, shop buy-back lists, other players' backpacks) is left out on purpose.
/// </summary>
internal static class ItemFinder
{
    public static ToolDef Tool() => new()
    {
        Name = "find_items",
        Description =
            "Where the player's items are: backpack, chests on every map (including farm buildings), fridges, the shared Junimo chest, and machines with output ready. " +
            "Give an item (name, part of a name, or id) to find it, or nothing for a list of everything stored, grouped by item. " +
            "Each place says whether take_from_chest can fetch from it.",
        InputSchema = Schema.Object(
            ("item", Schema.String("Item name, part of a name (for example 'copper'), or qualified id. Omit to list everything."), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string? query = args.Str("item");
        List<FoundItem> all = Scan();
        IEnumerable<FoundItem> hits = all;
        string? matched = null;
        if (!string.IsNullOrWhiteSpace(query))
        {
            string? qid = StateTools.FindItemId(query);
            if (qid != null && all.Any(f => f.Item.QualifiedItemId == qid))
            {
                hits = all.Where(f => f.Item.QualifiedItemId == qid);
                matched = "exact";
            }
            else
            {
                string q = query.Trim();
                hits = all.Where(f => f.Item.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) || f.Item.Name.Contains(q, StringComparison.OrdinalIgnoreCase));
                matched = "name contains";
            }
        }

        var groups = hits
            .GroupBy(f => f.Item.QualifiedItemId)
            .Select(g => new
            {
                item = g.First().Item.DisplayName,
                id = g.Key,
                total = g.Sum(f => f.Item.Stack),
                places = g
                    .GroupBy(f => (f.Place, loc: f.Location?.NameOrUniqueName, tile: f.Tile, f.Container))
                    .Select(p => DescribePlace(p.First(), p.ToList())),
            })
            .OrderByDescending(g => g.total)
            .ToList();

        return new
        {
            query,
            matched,
            found = groups.Count,
            items = groups,
            note = groups.Count == 0 && query != null
                ? "Not in the backpack, any chest, fridge, the Junimo chest or ready machine output. Items on the ground, in dressers, shipping bins and other players' backpacks are not searched."
                : "Places searched: backpack, chests the player placed (any map, including farm buildings), fridges, the Junimo chest, machines with output ready. Other players' homes and backpacks are not searched.",
            how = "Computed: the mod walks the game's maps and keeps these places.",
        };
    }

    private static object DescribePlace(FoundItem first, List<FoundItem> stacks) => new
    {
        place = first.Place,
        map = first.Location?.NameOrUniqueName,
        mapName = first.Location?.DisplayName,
        tile = first.Tile is { } t ? StateTools.Tile(t.X, t.Y) : null,
        container = first.Container,
        count = stacks.Sum(s => s.Item.Stack),
        quality = stacks.GroupBy(s => s.Item.Quality).Count() > 1 || stacks[0].Item.Quality > 0
            ? stacks.GroupBy(s => s.Item.Quality).OrderBy(q => q.Key).Select(q => $"{QualityName(q.Key)} x{q.Sum(s => s.Item.Stack)}")
            : null,
        fetchable = first.Fetchable,
        possiblyStale = first.PossiblyStale ? true : (bool?)null,
    };

    public static string QualityName(int q) => q switch { 1 => "silver", 2 => "gold", 4 => "iridium", _ => "normal" };

    /// <summary>Every stack in the kept places. Runs on the main thread.</summary>
    public static List<FoundItem> Scan()
    {
        var found = new List<FoundItem>();
        Farmer me = Game1.player;

        foreach (Item? item in me.Items)
        {
            if (item != null)
                found.Add(new FoundItem(item, "backpack", null, null, null, Fetchable: false, PossiblyStale: false));
        }

        FarmHouse myHome = Utility.getHomeOfFarmer(me);
        var junimoChests = new List<(GameLocation loc, Point tile)>();
        Utility.ForEachLocation(loc =>
        {
            // Other players' homes are theirs: the copilot does not look into them.
            if (loc is FarmHouse home && home != myHome)
                return true;
            bool stale = StateTools.IsPossiblyStale(loc);

            if (loc.GetFridge(onlyUnlocked: true) is { } fridge && loc.GetFridgePosition() is { } fridgeTile)
            {
                foreach (Item? item in fridge.Items)
                {
                    if (item != null)
                        found.Add(new FoundItem(item, "fridge", loc, fridgeTile, "Fridge", Fetchable: true, stale));
                }
            }

            foreach (var pair in loc.objects.Pairs)
            {
                Point tile = new((int)pair.Key.X, (int)pair.Key.Y);
                SObject obj = pair.Value;
                if (obj is Chest chest)
                {
                    if (!chest.playerChest.Value || chest.giftbox.Value)
                        continue;
                    if (chest.SpecialChestType == Chest.SpecialChestTypes.JunimoChest)
                    {
                        junimoChests.Add((loc, tile));
                        continue;
                    }
                    string label = chest.fridge.Value ? "Mini-Fridge" : chest.DisplayName;
                    foreach (Item? item in chest.Items)
                    {
                        if (item != null)
                            found.Add(new FoundItem(item, "chest", loc, tile, label, Fetchable: true, stale));
                    }
                }
                else if (obj.readyForHarvest.Value && obj.heldObject.Value is { } output and not Chest)
                {
                    found.Add(new FoundItem(output, "machine output", loc, tile, obj.DisplayName, Fetchable: false, stale));
                }
            }
            return true;
        });

        // One shared store, however many Junimo chests are placed. Fetchable through any placed Junimo chest.
        if (junimoChests.Count > 0)
        {
            var (loc, tile) = junimoChests.OrderBy(j => j.loc == Game1.currentLocation ? 0 : 1).First();
            foreach (Item? item in me.team.GetOrCreateGlobalInventory("JunimoChests"))
            {
                if (item != null)
                    found.Add(new FoundItem(item, "Junimo chest", loc, tile, "Junimo Chest", Fetchable: true, PossiblyStale: false));
            }
        }
        return found;
    }

    /// <summary>Total count of an item (any quality at or above minQuality) across all kept places.</summary>
    public static int CountOwned(List<FoundItem> scan, string qualifiedId, int minQuality = 0) =>
        scan.Where(f => f.Item.QualifiedItemId == qualifiedId && f.Item.Quality >= minQuality).Sum(f => f.Item.Stack);

    /// <summary>A short "where" for one item, for other tools: the backpack first, then the nearest place.</summary>
    public static string? WhereIs(List<FoundItem> scan, string qualifiedId, int minQuality = 0)
    {
        var hits = scan.Where(f => f.Item.QualifiedItemId == qualifiedId && f.Item.Quality >= minQuality).ToList();
        if (hits.Count == 0)
            return null;
        FoundItem best = hits.FirstOrDefault(h => h.Place == "backpack")
            ?? hits.FirstOrDefault(h => h.Location == Game1.currentLocation)
            ?? hits[0];
        return best.Place == "backpack"
            ? "backpack"
            : $"{best.Container ?? best.Place} in {best.Location?.DisplayName}" + (best.Tile is { } t ? $" ({t.X},{t.Y})" : "");
    }
}
