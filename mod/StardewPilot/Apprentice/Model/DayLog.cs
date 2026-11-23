using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

// Game-free: these files are also compiled into the test project (mod/Platform.Tests), which has no game or packages.

/// <summary>Who was in control during a slice of the day.</summary>
public static class Control
{
    /// <summary>The player played. The only control whose slices count as evidence for habits.</summary>
    public const string Hand = "hand";

    /// <summary>An autopilot job the player asked for was running.</summary>
    public const string Asked = "asked";

    /// <summary>Self-play was on: the copilot chose the work.</summary>
    public const string SelfPlay = "self-play";
}

/// <summary>Activity keys the watcher records. Habits are worked out over these.</summary>
public static class Act
{
    public const string Water = "water";
    public const string Harvest = "harvest";
    public const string Animals = "animals";
    public const string Machines = "machines";
    public const string Forage = "forage";
    public const string Sow = "sow";
    public const string Hoe = "hoe";
    public const string Fish = "fish";
    public const string Rocks = "rocks";
    public const string Chop = "chop";
    public const string Weeds = "weeds";
    public const string Combat = "combat";
    public const string Craft = "craft";
    public const string Cook = "cook";
    public const string Geodes = "geodes";
    public const string Talk = "talk";
    public const string Gift = "gift";
    public const string Buy = "buy";
    public const string Sell = "sell";
    public const string Store = "store";
    public const string Fetch = "fetch";

    /// <summary>Every activity key.</summary>
    public static readonly string[] All = { Water, Harvest, Animals, Machines, Forage, Sow, Hoe, Fish, Rocks, Chop, Weeds, Combat, Craft, Cook, Geodes, Talk, Gift, Buy, Sell, Store, Fetch };

    /// <summary>Words for an activity in the player's language, for what the player reads. The diary keeps <see cref="Word"/>.</summary>
    public static string Say(string act) => All.Contains(act) ? I18n.Get("act." + act) : act;

    /// <summary>Words for an activity, for habit lines.</summary>
    public static string Word(string act) => act switch
    {
        Water => "water",
        Harvest => "harvest",
        Animals => "tend the animals",
        Machines => "collect from machines",
        Forage => "forage",
        Sow => "sow",
        Hoe => "hoe",
        Fish => "fishing",
        Rocks => "breaking rocks",
        Chop => "chopping wood",
        Weeds => "cutting weeds",
        Combat => "fighting monsters",
        Craft => "crafting",
        Cook => "cooking",
        Geodes => "cracking geodes",
        Talk => "talking to villagers",
        Gift => "giving gifts",
        Buy => "shopping",
        Sell => "selling",
        Store => "storing things",
        Fetch => "fetching from chests",
        _ => act,
    };

    /// <summary>The autopilot job that does an activity, when there is one. "Do my usual morning" runs these.</summary>
    public static string? Job(string act) => act switch
    {
        Water => "water_crops",
        Harvest => "harvest_crops",
        Animals => "tend_animals",
        Machines => "tend_machines",
        Forage => "collect_forage",
        Store => "put_things_away",
        _ => null,
    };

    /// <summary>The activity an autopilot job does, so a day the copilot did it is not counted for or against the player.</summary>
    public static string? OfJob(string job) => job switch
    {
        "water_crops" => Water,
        "harvest_crops" => Harvest,
        "tend_animals" => Animals,
        "tend_machines" => Machines,
        "collect_forage" => Forage,
        "plant_seeds" => Sow,
        "fish" => Fish,
        "explore_mines" => Rocks,
        "buy_items" => Buy,
        "ship_items" => Sell,
        "store_in_chest" => Store,
        "put_things_away" => Store,
        "take_from_chest" => Fetch,
        "talk_to" => Talk,
        "give_gift" => Gift,
        _ => null,
    };
}

/// <summary>A stretch of the day on one map under one control.</summary>
public sealed class Slice
{
    /// <summary>Game time (600 = 6:00am, 2530 = 1:30am).</summary>
    public int Start;
    public int End;

    /// <summary>The map's id (all mine floors are "Mines", all Skull Cavern floors "SkullCave").</summary>
    public string Map = "";

    /// <summary>The farm, the farmhouse, the greenhouse and farm buildings.</summary>
    public bool OnFarm;

    public string Control = Model.Control.Hand;

    /// <summary>How often each activity happened (units differ: swings, items, crops, casts).</summary>
    public Dictionary<string, int> Acts = new();

    /// <summary>Game time each activity was first seen in this slice.</summary>
    public Dictionary<string, int> First = new();

    /// <summary>Tool swings by tool id.</summary>
    public Dictionary<string, int> Swings = new();

    /// <summary>Items by what happened to them ("harvested", "gained", "bought", "sold", "stored", "fetched"), then item id, then count.</summary>
    public Dictionary<string, Dictionary<string, int>> Items = new();

    /// <summary>Gold spent in shops (listed prices).</summary>
    public int Spent;

    /// <summary>Shop ids used.</summary>
    public List<string> Shops = new();

    /// <summary>Villagers talked to (internal names).</summary>
    public List<string> Talked = new();

    /// <summary>Gifts: villager and item id (empty when the item could not be told).</summary>
    public List<string[]> Gifts = new();

    /// <summary>Animals and pets petted.</summary>
    public int Petted;

    /// <summary>Mine floors entered, lowest and deepest.</summary>
    public int MineFrom, MineTo;

    /// <summary>Autopilot jobs that ended in this slice: "6:10 water_crops: done".</summary>
    public List<string> Jobs = new();

    public int Minutes => Time.Between(this.Start, this.End);

    /// <summary>Something was seen in this slice besides the time passing (autopilot jobs not counted).</summary>
    public bool HasActivity => this.Acts.Count > 0 || this.Swings.Count > 0 || this.Items.Count > 0 || this.Talked.Count > 0 || this.MineTo > 0;

    public void Add(string act, int count, int time)
    {
        if (count <= 0)
            return;
        this.Acts[act] = (this.Acts.TryGetValue(act, out int n) ? n : 0) + count;
        if (!this.First.ContainsKey(act))
            this.First[act] = time;
    }

    public void AddItem(string what, string itemId, int count)
    {
        if (count <= 0)
            return;
        if (!this.Items.TryGetValue(what, out var items))
            this.Items[what] = items = new();
        items[itemId] = (items.TryGetValue(itemId, out int n) ? n : 0) + count;
    }

    /// <summary>Take in a later slice on the same map under the same control.</summary>
    public void Merge(Slice next)
    {
        this.End = next.End;
        foreach (var (k, v) in next.Acts)
            this.Acts[k] = (this.Acts.TryGetValue(k, out int n) ? n : 0) + v;
        foreach (var (k, v) in next.First)
            if (!this.First.ContainsKey(k))
                this.First[k] = v;
        foreach (var (k, v) in next.Swings)
            this.Swings[k] = (this.Swings.TryGetValue(k, out int n) ? n : 0) + v;
        foreach (var (what, items) in next.Items)
            foreach (var (id, count) in items)
                this.AddItem(what, id, count);
        this.Spent += next.Spent;
        this.Shops.AddRange(next.Shops.Where(s => !this.Shops.Contains(s)));
        this.Talked.AddRange(next.Talked.Where(s => !this.Talked.Contains(s)));
        this.Gifts.AddRange(next.Gifts);
        this.Petted += next.Petted;
        if (next.MineTo > 0)
        {
            this.MineFrom = this.MineFrom == 0 ? next.MineFrom : Math.Min(this.MineFrom, next.MineFrom);
            this.MineTo = Math.Max(this.MineTo, next.MineTo);
        }
        this.Jobs.AddRange(next.Jobs);
    }
}

/// <summary>What there was to do this morning: an activity with nothing to do does not count against a habit.</summary>
public sealed class DayFacts
{
    public bool Rain;
    public bool Festival;

    /// <summary>Crops that needed water at the start of the day (none when it rains).</summary>
    public int DryCrops;
    public int CropsReady;
    public int Animals;
    public int MachinesReady;
    public int ForageOnFarm;
}

/// <summary>One game day of activity, as the watcher saw it.</summary>
public sealed class DayLog
{
    /// <summary>Memory date key, for example Y1-spring-04.</summary>
    public string Date = "";
    public string Season = "";
    public int Day;
    public int Year;

    /// <summary>Weekday, Mon to Sun.</summary>
    public string Weekday = "";
    public DayFacts Facts = new();
    public List<Slice> Slices = new();

    /// <summary>The farmer's own bedtime (game time), 0 if unknown.</summary>
    public int Bedtime;
    public bool PassedOut;

    /// <summary>The farmer went to bed by hand (not the autopilot's bedtime or a go_to_bed job).</summary>
    public bool BedByHand;

    /// <summary>Shipped tonight, item id to count (only when the bin is the player's own).</summary>
    public Dictionary<string, int> Shipped = new();

    /// <summary>The bin is shared by the farm (co-op with a shared wallet).</summary>
    public bool SharedBin;

    /// <summary>Activities the copilot did today (asked or self-play), from its jobs.</summary>
    public List<string> CopilotDid = new();

    public int HandMinutes => this.Slices.Where(s => s.Control == Control.Hand).Sum(s => s.Minutes);

    /// <summary>Whether an activity had something to do today and the copilot did not do it.</summary>
    public bool Applies(string act)
    {
        if (this.CopilotDid.Contains(act))
            return false;
        return act switch
        {
            Act.Water => this.Facts.DryCrops > 0 && !this.Facts.Rain,
            Act.Harvest => this.Facts.CropsReady > 0,
            Act.Animals => this.Facts.Animals > 0,
            Act.Machines => this.Facts.MachinesReady > 0,
            Act.Forage => this.Facts.ForageOnFarm > 0,
            _ => true,
        };
    }
}

/// <summary>Game time helpers (600 = 6:00am; the day runs to 2600 = 2:00am).</summary>
public static class Time
{
    public static int ToMinutes(int time) => time / 100 * 60 + time % 100;

    public static int FromMinutes(int minutes) => minutes / 60 * 100 + minutes % 60;

    public static int Between(int from, int to) => Math.Max(0, ToMinutes(to) - ToMinutes(from));

    public static string Format(int time)
    {
        int h = time / 100 % 24, m = time % 100;
        return $"{h}:{m:00}";
    }

    /// <summary>"10pm", "9:30am".</summary>
    public static string Clock(int time)
    {
        int h = time / 100 % 24, m = time % 100;
        string ampm = h < 12 ? "am" : "pm";
        int h12 = h % 12 == 0 ? 12 : h % 12;
        return m == 0 ? $"{h12}{ampm}" : $"{h12}:{m:00}{ampm}";
    }

    /// <summary>The time for the player, in their language: "10pm" in English, "22 h" in French.</summary>
    public static string Say(int time)
    {
        int h = time / 100 % 24, m = time % 100;
        int h12 = h % 12 == 0 ? 12 : h % 12;
        return I18n.Get(m == 0 ? "time.hour" : "time.minutes", new { h12, h24 = h, mm = m.ToString("00"), ampm = I18n.Get(h < 12 ? "time.am" : "time.pm") });
    }
}
