using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

/// <summary>A backpack slot, as the planner sees it.</summary>
public sealed class PackItem
{
    public int Slot;
    public string Id = "";
    public string Name = "";
    public int Category;
    public int Stack;
    public bool IsTool;
}

/// <summary>A chest or fridge the player stores things in, with what it holds.</summary>
public sealed class Box
{
    /// <summary>Index in the caller's list, so the plan can point back at the real chest.</summary>
    public int Index;
    public string Map = "";
    public int X, Y;

    /// <summary>One entry per stack: item id, category and count.</summary>
    public List<(string id, int category, int count)> Stacks = new();
}

/// <summary>Where one backpack item goes, or why it stays.</summary>
public sealed class PutAwayStep
{
    public PackItem Item = null!;

    /// <summary>The chest, or null when the item stays.</summary>
    public Box? To;

    /// <summary>"same item" or "same kind" when it goes; why it stays otherwise.</summary>
    public string Why = "";
}

/// <summary>
/// "Put my things away", planned the way the player already sorts: an item goes to the chest that holds most of it,
/// else to the chest that holds most stacks of its kind. Pure, so it is tested without the game.
/// </summary>
public static class PutAwayPlan
{
    /// <summary>The toolbar row: the first twelve backpack slots.</summary>
    public const int ToolbarSlots = 12;

    /// <summary>Stacks of a kind a chest must hold before new items of that kind go there.</summary>
    public const int MinKindStacks = 2;

    public const string Stays_NoChest = "no chest holds this kind of item yet";
    public const string Stays_Toolbar = "in the toolbar row";
    public const string Stays_InHand = "in your hand";
    public const string Stays_Tool = "a tool";
    public const string Stays_NotAsked = "not asked for";

    /// <summary>Categories that say nothing about where an item belongs: none, and litter.</summary>
    private static bool CountsAsKind(int category) => category != 0 && category != -999;

    public static List<PutAwayStep> Plan(IEnumerable<PackItem> pack, List<Box> boxes, bool includeToolbar, int handSlot, Func<PackItem, bool>? only = null)
    {
        var steps = new List<PutAwayStep>();
        foreach (PackItem item in pack.OrderBy(i => i.Slot))
        {
            string? stays =
                item.IsTool ? Stays_Tool :
                item.Slot == handSlot ? Stays_InHand :
                !includeToolbar && item.Slot < ToolbarSlots ? Stays_Toolbar :
                only != null && !only(item) ? Stays_NotAsked :
                null;
            if (stays != null)
            {
                steps.Add(new PutAwayStep { Item = item, Why = stays });
                continue;
            }
            steps.Add(Home(item, boxes));
        }
        return steps;
    }

    /// <summary>The chest for one item: the most of the same item (by count), else the most stacks of its kind (at least two).</summary>
    public static PutAwayStep Home(PackItem item, List<Box> boxes)
    {
        var same = boxes.Select(b => (box: b, n: b.Stacks.Where(s => s.id == item.Id).Sum(s => s.count))).Where(x => x.n > 0)
            .OrderByDescending(x => x.n).ThenBy(x => x.box.Index).FirstOrDefault();
        if (same.box != null)
            return new PutAwayStep { Item = item, To = same.box, Why = "same item" };
        if (CountsAsKind(item.Category))
        {
            var kind = boxes.Select(b => (box: b, n: b.Stacks.Count(s => s.category == item.Category))).Where(x => x.n >= MinKindStacks)
                .OrderByDescending(x => x.n).ThenBy(x => x.box.Index).FirstOrDefault();
            if (kind.box != null)
                return new PutAwayStep { Item = item, To = kind.box, Why = "same kind" };
        }
        return new PutAwayStep { Item = item, Why = Stays_NoChest };
    }

    /// <summary>The chests to visit, grouped by map: the current map first, then the farm, the farmhouse, then the rest by name.</summary>
    public static List<IGrouping<string, PutAwayStep>> ByMap(List<PutAwayStep> steps, string currentMap) =>
        steps.Where(s => s.To != null).GroupBy(s => s.To!.Map)
            .OrderBy(g => g.Key == currentMap ? 0 : g.Key == "Farm" ? 1 : g.Key == "FarmHouse" ? 2 : 3)
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .ToList();
}
