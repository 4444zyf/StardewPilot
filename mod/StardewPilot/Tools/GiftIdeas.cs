using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewPilot.Guide;
using StardewValley;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>
/// get_gift_ideas: which items the player already owns a villager loves or likes, and how many gifts they can still take.
/// Tastes are asked of the game (NPC.getGiftTasteForThisItem, universal tastes included). The gift limit is the
/// game's rule applied by us, because the game only checks it while the item is held.
/// </summary>
internal static class GiftIdeas
{
    private const int Love = 0, Like = 2;

    public static ToolDef Tool() => new()
    {
        Name = "get_gift_ideas",
        Description =
            "Gift ideas from what the player already owns (backpack, chests, fridge): items a villager loves or likes, best first, with where each one is, " +
            "and how many gifts they can still take today and this week. Without a villager: those with a birthday in the next 7 days, then others who can still take a gift. " +
            "Items a Community Center bundle still needs are held back. Each idea says whether the player has discovered that taste in the game.",
        InputSchema = Schema.Object(("npc", Schema.String("Villager name. Omit for birthdays soon and others."), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string? name = args.Str("npc");
        List<FoundItem> scan = ItemFinder.Scan();
        List<BundleHelper.Need> needs = BundleHelper.MissingNeeds();

        List<NPC> villagers;
        if (!string.IsNullOrWhiteSpace(name))
        {
            NPC npc = FindVillager(name) ?? throw new ToolError($"No villager called '{name}'.");
            villagers = new() { npc };
        }
        else
        {
            villagers = MetVillagers()
                .Select(n => (npc: n, days: DaysToBirthday(n)))
                .OrderBy(x => x.days <= 7 ? x.days : 100)
                .ThenBy(x => x.npc.displayName)
                .Select(x => x.npc)
                .ToList();
        }

        var result = new List<object>();
        foreach (NPC npc in villagers)
        {
            var limit = GiftLimit(npc);
            var ideas = IdeasFor(npc, scan, needs, max: 6);
            if (name == null && (ideas.Count == 0 || limit.today == 0))
                continue;
            int days = DaysToBirthday(npc);
            result.Add(new
            {
                npc = npc.Name,
                displayName = npc.displayName,
                birthday = days == 0 ? "today" : days <= 7 ? $"in {days} days" : null,
                giftsLeftToday = limit.today,
                giftsLeftThisWeek = limit.week,
                limitNote = limit.note,
                ideas = ideas.Select(i => new
                {
                    item = i.item.DisplayName,
                    id = i.item.QualifiedItemId,
                    taste = i.taste == Love ? "loves" : "likes",
                    discovered = i.discovered,
                    spare = i.owned,
                    where = i.where,
                }),
            });
            if (name == null && result.Count >= 8)
                break;
        }

        return new
        {
            villagers = result,
            note = "Birthday gifts count eight times. Never suggested: tools, quest items, and the number of each item a visible bundle still needs.",
            how = "Tastes: asked of the game. Gifts left: the game's rule (one a day, two a week, no weekly limit on a birthday or for a spouse) applied by the mod, so computed. Where: computed (find_items).",
        };
    }

    /// <summary>The owned items a villager loves or likes, loved first, discovered tastes first, then by value.</summary>
    public static List<(Item item, int taste, bool discovered, int owned, string? where)> IdeasFor(NPC npc, List<FoundItem> scan, List<BundleHelper.Need> needs, int max, bool onlyDiscovered = false)
    {
        var list = new List<(Item item, int taste, bool discovered, int owned, string? where)>();
        foreach (var group in scan.Where(f => f.Item is SObject o && !o.questItem.Value && o.canBeGivenAsGift() && f.Place != "machine output").GroupBy(f => f.Item.QualifiedItemId))
        {
            Item item = group.OrderByDescending(f => f.Item.Quality).First().Item;
            int taste;
            try
            {
                taste = npc.getGiftTasteForThisItem(item);
            }
            catch
            {
                continue;
            }
            if (taste != Love && taste != Like)
                continue;
            int spare = BundleHelper.Spare(needs, item.QualifiedItemId, group.Select(f => (f.Item.Quality, f.Item.Stack)));
            if (spare <= 0)
                continue;
            bool discovered = Game1.player.hasGiftTasteBeenRevealed(npc, item.ItemId);
            if (onlyDiscovered && !discovered)
                continue;
            list.Add((item, taste, discovered, spare, ItemFinder.WhereIs(scan, item.QualifiedItemId)));
        }
        return list
            .OrderBy(i => i.taste)
            .ThenByDescending(i => i.discovered)
            .ThenByDescending(i => i.item.sellToStorePrice())
            .Take(max)
            .ToList();
    }

    /// <summary>Gifts the villager can still take: the game's rule from NPC.tryToReceiveActiveObject.</summary>
    public static (int today, int? week, string? note) GiftLimit(NPC npc)
    {
        Farmer me = Game1.player;
        if (!npc.CanReceiveGifts())
            return (0, 0, "Does not take gifts.");
        if (!me.friendshipData.TryGetValue(npc.Name, out Friendship? f))
            return (0, 0, "Not met yet.");
        if (f.IsDivorced())
            return (0, 0, "Divorced: refuses gifts.");
        if (Game1.IsGreenRainingHere() && Game1.year == 1 && !npc.isMarried())
            return (0, 0, "Villagers take no gifts during the green rain.");
        bool noWeeklyLimit = me.spouse == npc.Name || npc.isBirthday();
        int? week = noWeeklyLimit ? null : Math.Max(0, 2 - f.GiftsThisWeek);
        int today = f.GiftsToday >= 1 || week == 0 ? 0 : 1;
        string? note = npc.isBirthday() ? "Birthday today: the weekly limit does not apply." : me.spouse == npc.Name ? "Spouse: no weekly limit." : null;
        return (today, week, note);
    }

    public static int DaysToBirthday(NPC npc)
    {
        if (npc.Birthday_Day <= 0 || string.IsNullOrEmpty(npc.Birthday_Season))
            return 999;
        int season = Array.IndexOf(new[] { "spring", "summer", "fall", "winter" }, npc.Birthday_Season.ToLowerInvariant());
        if (season < 0)
            return 999;
        int today = Game1.seasonIndex * 28 + Game1.dayOfMonth;
        int birthday = season * 28 + npc.Birthday_Day;
        return (birthday - today + 112) % 112;
    }

    public static IEnumerable<NPC> MetVillagers()
    {
        var list = new List<NPC>();
        Utility.ForEachVillager(n =>
        {
            if (n.CanSocialize && Game1.player.friendshipData.ContainsKey(n.Name))
                list.Add(n);
            return true;
        });
        return list;
    }

    private static NPC? FindVillager(string name)
    {
        NPC? found = Game1.getCharacterFromName(name);
        if (found != null)
            return found;
        Utility.ForEachVillager(n =>
        {
            if (n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = n;
                return false;
            }
            return true;
        });
        return found;
    }
}
