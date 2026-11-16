using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewPilot.Apprentice.Model;
using StardewPilot.Autopilot;
using StardewPilot.Memory;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewPilot.Apprentice;

/// <summary>
/// Watches what the local player does and cuts the day into slices (one per map and per control). It reads only the local
/// farmer's own data: the game's per-player counters, tool use, the friendship record, the backpack and bedtime.
/// While the copilot is in control the signals are not counted: the jobs' own reports say what was done.
/// </summary>
internal sealed class Watcher
{
    /// <summary>Game counters (per player) and the activity each one counts.</summary>
    private static readonly (string Stat, string Act)[] Counters =
    {
        ("seedsSown", Act.Sow), ("dirtHoed", Act.Hoe), ("itemsForaged", Act.Forage), ("timesFished", Act.Fish),
        ("rocksCrushed", Act.Rocks), ("weedsEliminated", Act.Weeds), ("monstersKilled", Act.Combat), ("itemsCrafted", Act.Craft),
        ("itemsCooked", Act.Cook), ("geodesCracked", Act.Geodes),
    };

    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;
    private readonly JobRunner runner;
    private readonly SelfPlay selfPlay;

    private DayLog? day;
    private Slice? cur;
    private readonly Dictionary<string, uint> counters = new();
    private readonly Dictionary<string, (int gifts, bool talked)> friends = new();
    private readonly HashSet<long> pettedAnimals = new();
    private readonly HashSet<Guid> pettedPets = new();
    private readonly HashSet<Vector2> readyMachines = new();
    private bool wasUsingTool;
    private (string id, int tick) lastRemoved = ("", -999);
    private (string name, int time) lastJob = ("", 0);
    /// <summary>Ready crops on the current map, by tile, with the item they give.</summary>
    private readonly Dictionary<Vector2, string> readyCrops = new();

    /// <summary>Items of crops just harvested next to the farmer, so the items that arrive next count as the harvest.</summary>
    private readonly Dictionary<string, int> justHarvested = new();

    /// <summary>
    /// Crops and machines next to the farmer that were just emptied, waiting for their items to reach the local backpack.
    /// Crop and machine state is shared in co-op: only an item arriving in this player's backpack proves this player did it.
    /// </summary>
    private readonly List<(string item, int tick)> emptiedCrops = new();
    private readonly List<int> emptiedMachines = new();

    /// <summary>When the local player last pressed the action button (petting a farm animal sets a flag shared by all players).</summary>
    private int lastActionTick = -999;

    /// <summary>The last shop menu seen open: an item still on the cursor reaches the backpack just after the shop closes.</summary>
    private (ShopMenu? menu, int tick) lastShop = (null, -999);

    private readonly Stopwatch cost = new();
    private long ticks;

    public Watcher(IMonitor monitor, Func<ModConfig> config, JobRunner runner, SelfPlay selfPlay)
    {
        this.monitor = monitor;
        this.config = config;
        this.runner = runner;
        this.selfPlay = selfPlay;
    }

    public bool Watching => this.day != null;

    // ------------------------------------------------------------ day

    /// <summary>Day start (also after loading a save): a fresh log with what there is to do this morning.</summary>
    public void StartDay()
    {
        this.Reset();
        if (!this.config().Apprentice.Enabled)
            return;
        Farm farm = Game1.getFarm();
        var log = new DayLog
        {
            Date = MemoryStore.DateKey(),
            Season = Game1.currentSeason,
            Day = Game1.dayOfMonth,
            Year = Game1.year,
            Weekday = HabitFinder.Weekdays[(Game1.dayOfMonth - 1) % 7],
        };
        log.Facts.Rain = farm.IsRainingHere();
        log.Facts.Festival = Utility.isFestivalDay();
        foreach (GameLocation loc in new[] { farm, Game1.getLocationFromName("Greenhouse") }.Where(l => l != null))
        {
            foreach (TerrainFeature t in loc!.terrainFeatures.Values)
            {
                if (t is not HoeDirt { crop: { } crop } dirt || crop.dead.Value)
                    continue;
                if (dirt.readyForHarvest())
                    log.Facts.CropsReady++;
                else if (dirt.needsWatering() && dirt.state.Value != HoeDirt.watered)
                    log.Facts.DryCrops++;
            }
        }
        log.Facts.Animals = AnimalTools.AllAnimals().Count() + AnimalTools.Pets().Count;
        Utility.ForEachLocation(loc =>
        {
            if (loc is FarmHouse home && home != Utility.getHomeOfFarmer(Game1.player))
                return true;
            log.Facts.MachinesReady += loc.objects.Values.Count(IsReadyMachine);
            return true;
        });
        log.Facts.ForageOnFarm = farm.objects.Values.Count(o => o.IsSpawnedObject);
        this.day = log;
        this.Baseline();
        this.Open();
        this.monitor.Log($"Watcher: day {log.Date} started. Facts: rain={log.Facts.Rain} festival={log.Facts.Festival} dry={log.Facts.DryCrops} ready={log.Facts.CropsReady} animals={log.Facts.Animals} machines={log.Facts.MachinesReady} forage={log.Facts.ForageOnFarm}", LogLevel.Trace);
    }

    /// <summary>Day end: close the last slice and add bedtime and shipping. Returns the day's log, or null when not watching.</summary>
    public DayLog? EndDay()
    {
        if (this.day == null)
            return null;
        if (!this.config().Apprentice.Enabled)
        {
            this.Reset(); // switched off during the day: keep nothing
            return null;
        }
        this.Poll();
        this.Cut();
        DayLog log = this.day;
        Farmer me = Game1.player;
        int bed = me.timeWentToBed.Value;
        log.PassedOut = bed == 0 || Game1.timeOfDay >= 2600;
        log.Bedtime = bed > 0 ? bed : Game1.timeOfDay;
        bool copilotBed = this.selfPlay.Enabled || (this.lastJob.name.StartsWith("go_to_bed") && Time.Between(this.lastJob.time, log.Bedtime) <= 30);
        log.BedByHand = !log.PassedOut && !copilotBed;
        log.SharedBin = Context.IsMultiplayer && !me.team.useSeparateWallets.Value;
        foreach (Item item in Game1.getFarm().getShippingBin(me).Where(i => i != null))
            log.Shipped[item.QualifiedItemId] = (log.Shipped.TryGetValue(item.QualifiedItemId, out int n) ? n : 0) + item.Stack;
        this.monitor.Log($"Watcher: day {log.Date} ended. Bed {log.Bedtime} (by hand {log.BedByHand}, passed out {log.PassedOut}), {log.Slices.Count} slices, {log.HandMinutes} hand minutes. " +
            $"Cost: {(this.ticks > 0 ? this.cost.Elapsed.TotalMilliseconds * 1000 / this.ticks : 0):0.0} microseconds per tick over {this.ticks} ticks.", LogLevel.Debug);
        this.day = null;
        this.cur = null;
        return log;
    }

    /// <summary>Forget the day in progress (return to title, or the player turned watching off).</summary>
    public void Reset()
    {
        this.day = null;
        this.cur = null;
        this.counters.Clear();
        this.friends.Clear();
        this.pettedAnimals.Clear();
        this.pettedPets.Clear();
        this.readyMachines.Clear();
        this.readyCrops.Clear();
        this.justHarvested.Clear();
        this.emptiedCrops.Clear();
        this.emptiedMachines.Clear();
        this.lastActionTick = -999;
        this.wasUsingTool = false;
        this.lastJob = ("", 0);
        this.lastShop = (null, -999);
        this.cost.Reset();
        this.ticks = 0;
    }

    /// <summary>Today so far, with the open slice ending now. Null when not watching.</summary>
    public DayLog? Today()
    {
        if (this.day == null || this.cur == null)
            return null;
        this.cur.End = Game1.timeOfDay;
        return this.day;
    }

    // ------------------------------------------------------------ slices

    private string ControlNow()
    {
        if (this.selfPlay.Enabled)
            return Control.SelfPlay;
        if (this.runner.IsRunning && !this.config().Dev.WatchJobsAsHand)
            return this.cur?.Control == Control.SelfPlay ? Control.SelfPlay : Control.Asked; // a self-play job runs on after self-play stops
        return Control.Hand;
    }

    private void Open()
    {
        GameLocation loc = Game1.currentLocation ?? Game1.getFarm();
        this.cur = new Slice { Start = Game1.timeOfDay, End = Game1.timeOfDay, Map = MapKey(loc), OnFarm = IsFarmPlace(loc), Control = this.ControlNow() };
        if (loc is MineShaft shaft)
            this.Floor(shaft);
        this.day!.Slices.Add(this.cur);
        this.RefreshMachines();
        this.RefreshCrops();
        this.BaselineAnimals();
    }

    /// <summary>Close the open slice; merge it into the one before when that was on the same map under the same control.</summary>
    private void Cut()
    {
        if (this.cur == null || this.day == null)
            return;
        this.cur.End = Game1.timeOfDay;
        var slices = this.day.Slices;
        if (slices.Count >= 2 && slices[^2] is { } prev && prev.Map == this.cur.Map && prev.Control == this.cur.Control)
        {
            prev.Merge(this.cur);
            slices.RemoveAt(slices.Count - 1);
        }
        this.cur = null;
    }

    private void CutAndOpen(string why)
    {
        this.Poll();
        this.Cut();
        this.Open();
        this.monitor.Log($"Watcher: new slice ({why}) at {this.cur!.Start} on {this.cur.Map}, control {this.cur.Control}.", LogLevel.Trace);
    }

    // ------------------------------------------------------------ events

    public void OnWarped(WarpedEventArgs e)
    {
        if (this.day == null || !e.IsLocalPlayer)
            return;
        if (e.NewLocation is MineShaft shaft && this.cur != null && MapKey(shaft) == this.cur.Map)
        {
            this.Floor(shaft); // the next floor of the same mine: same slice
            this.RefreshMachines();
            this.RefreshCrops();
            return;
        }
        this.CutAndOpen("warp");
    }

    public void OnJobFinished(string name, JobResult result)
    {
        if (this.day == null)
            return;
        this.lastJob = (name, Game1.timeOfDay);
        this.cur?.Jobs.Add($"{Time.Format(Game1.timeOfDay)} {name}: {result.Outcome.ToString().ToLowerInvariant()}");
        if (result.Outcome != JobOutcome.Failed && Act.OfJob(name) is { } act && !this.day.CopilotDid.Contains(act))
            this.day.CopilotDid.Add(act);
    }

    public void OnInventoryChanged(InventoryChangedEventArgs e)
    {
        if (this.day == null || this.cur == null || !e.IsLocalPlayer)
            return;
        var gained = e.Added.Select(i => (i.QualifiedItemId, i.Stack))
            .Concat(e.QuantityChanged.Where(c => c.NewSize > c.OldSize).Select(c => (c.Item.QualifiedItemId, c.NewSize - c.OldSize))).ToList();
        var lost = e.Removed.Select(i => (i.QualifiedItemId, i.Stack))
            .Concat(e.QuantityChanged.Where(c => c.NewSize < c.OldSize).Select(c => (c.Item.QualifiedItemId, c.OldSize - c.NewSize))).ToList();
        if (lost.Count > 0)
            this.lastRemoved = (lost[0].QualifiedItemId, Game1.ticks);
        if (this.cur.Control != Control.Hand)
            return;

        int now = Game1.timeOfDay;
        IClickableMenu? menu = Game1.activeClickableMenu;
        if (menu == null && this.lastShop.menu != null && Game1.ticks - this.lastShop.tick <= 30)
            menu = this.lastShop.menu;
        switch (menu)
        {
            case ShopMenu shop:
                foreach (var (id, n) in gained)
                {
                    this.cur.AddItem("bought", id, n);
                    this.cur.Add(Act.Buy, n, now);
                    var stock = shop.itemPriceAndStock.FirstOrDefault(p => p.Key is Item i && i.QualifiedItemId == id);
                    if (stock.Value != null)
                        this.cur.Spent += stock.Value.Price * n;
                }
                foreach (var (id, n) in lost)
                {
                    this.cur.AddItem("sold", id, n);
                    this.cur.Add(Act.Sell, n, now);
                }
                if ((gained.Count > 0 || lost.Count > 0) && shop.ShopId is { Length: > 0 } shopId && !this.cur.Shops.Contains(shopId))
                    this.cur.Shops.Add(shopId);
                this.Trace($"shop {shop.ShopId}: +{string.Join(",", gained)} -{string.Join(",", lost)}");
                return;

            case ItemGrabMenu grab when grab.context is Chest:
                foreach (var (id, n) in gained) { this.cur.AddItem("fetched", id, n); this.cur.Add(Act.Fetch, n, now); }
                foreach (var (id, n) in lost) { this.cur.AddItem("stored", id, n); this.cur.Add(Act.Store, n, now); }
                this.Trace($"chest: +{string.Join(",", gained)} -{string.Join(",", lost)}");
                return;

            case ItemGrabMenu grab when grab.context is SObject { QualifiedItemId: AnimalTools.AutoGrabberId }:
                foreach (var (id, n) in gained) { this.cur.AddItem("gained", id, n); this.cur.Add(Act.Animals, n, now); }
                this.Trace($"auto-grabber: +{string.Join(",", gained)}");
                return;
        }

        if (gained.Count == 0)
            return;
        bool noMenu = Game1.activeClickableMenu == null;
        if (noMenu)
        {
            this.CheckCrops();
            this.CheckMachines();
            this.CreditEmptied(now);
        }
        foreach (var (id, n) in gained)
        {
            if (noMenu && this.justHarvested.TryGetValue(id, out int tick) && Game1.ticks - tick <= 180)
                this.cur.AddItem("harvested", id, n);
            else
                this.cur.AddItem("gained", id, n);
        }
        this.Trace($"gained {string.Join(",", gained)} (menu {Game1.activeClickableMenu?.GetType().Name ?? "none"})");
    }

    /// <summary>The local player's own button presses (SMAPI raises them only for this game's player).</summary>
    public void OnButtonPressed(ButtonPressedEventArgs e)
    {
        if (this.day != null && (e.Button.IsActionButton() || e.Button == SButton.MouseRight))
            this.lastActionTick = Game1.ticks;
    }

    /// <summary>Items reached the backpack: the crops and machines emptied next to the farmer just before were emptied by this player.</summary>
    private void CreditEmptied(int now)
    {
        if (this.cur == null)
            return;
        int recent = Game1.ticks - 180;
        foreach (var (item, tick) in this.emptiedCrops.Where(x => x.tick >= recent))
        {
            this.cur.Add(Act.Harvest, 1, now);
            this.justHarvested[item] = Game1.ticks;
        }
        int machines = this.emptiedMachines.Count(t => t >= recent);
        this.cur.Add(Act.Machines, machines, now);
        if (machines > 0 || this.emptiedCrops.Count > 0)
            this.Trace($"credited {this.emptiedCrops.Count(x => x.tick >= recent)} crops, {machines} machines");
        this.emptiedCrops.Clear();
        this.emptiedMachines.Clear();
    }

    /// <summary>Every update tick while a save is loaded.</summary>
    public void Tick()
    {
        if (this.day == null || this.cur == null)
            return;
        this.cost.Start();
        this.ticks++;
        try
        {
            string control = this.ControlNow();
            if (control != this.cur.Control)
                this.CutAndOpen("control " + control);
            if (Game1.activeClickableMenu is ShopMenu openShop)
                this.lastShop = (openShop, Game1.ticks);

            // Tool swings: the moment the farmer starts using a tool.
            Farmer me = Game1.player;
            bool using_ = me.UsingTool;
            if (using_ && !this.wasUsingTool && this.cur.Control == Control.Hand && me.CurrentTool is { } tool && SwingAct(tool) is { } act)
            {
                this.cur.Swings[tool.QualifiedItemId] = (this.cur.Swings.TryGetValue(tool.QualifiedItemId, out int n) ? n : 0) + 1;
                if (act.Length > 0)
                    this.cur.Add(act, 1, Game1.timeOfDay);
                this.Trace($"swing {tool.QualifiedItemId}");
            }
            this.wasUsingTool = using_;

            if (Game1.ticks % 15 == 0)
                this.Poll();
            if (Game1.ticks % 30 == 0)
            {
                this.CheckMachines();
                this.RefreshMachines();
                this.CheckCrops();
                this.RefreshCrops();
            }
        }
        finally
        {
            this.cost.Stop();
        }
    }

    /// <summary>Tools whose swings are recorded, and the activity a swing counts for ("" for none: a counter covers it).</summary>
    private static string? SwingAct(Tool tool) => tool switch
    {
        WateringCan => Act.Water,
        Axe => Act.Chop,
        MilkPail or Shears => Act.Animals,
        Pickaxe or Hoe or Pan => "",
        _ => null, // rods (casts are counted), weapons, slingshots
    };

    // ------------------------------------------------------------ polled signals

    private void Baseline()
    {
        Farmer me = Game1.player;
        foreach (var (stat, _) in Counters)
            this.counters[stat] = me.stats.Get(stat);
        foreach (var (name, f) in me.friendshipData.Pairs)
            this.friends[name] = (f.GiftsToday, f.TalkedToToday);
        foreach (Pet pet in AnimalTools.Pets())
            if (AnimalTools.PettedToday(pet))
                this.pettedPets.Add(pet.petId.Value);
    }

    /// <summary>Animals already petted when the farmer arrives were not petted by them here.</summary>
    private void BaselineAnimals()
    {
        foreach (FarmAnimal a in Game1.currentLocation?.animals.Values ?? Enumerable.Empty<FarmAnimal>())
            if (a.wasPet.Value)
                this.pettedAnimals.Add(a.myID.Value);
    }

    /// <summary>Read the counters, the friendship record and petting; credit changes to the open slice if the player is in control.</summary>
    private void Poll()
    {
        if (this.cur == null)
            return;
        Farmer me = Game1.player;
        bool hand = this.cur.Control == Control.Hand;
        int now = Game1.timeOfDay;

        foreach (var (stat, act) in Counters)
        {
            uint value = me.stats.Get(stat);
            uint before = this.counters.TryGetValue(stat, out uint b) ? b : value;
            this.counters[stat] = value;
            if (value > before && hand)
            {
                // Eggs and other produce picked up in an animal building count as tending the animals.
                string a = act == Act.Forage && Game1.currentLocation is AnimalHouse ? Act.Animals : act;
                this.cur.Add(a, (int)(value - before), now);
                this.Trace($"counter {stat} +{value - before}");
            }
        }

        foreach (var (name, f) in me.friendshipData.Pairs)
        {
            (int gifts, bool talked) before = this.friends.TryGetValue(name, out var x) ? x : (0, false);
            this.friends[name] = (f.GiftsToday, f.TalkedToToday);
            if (!hand)
                continue;
            if (f.TalkedToToday && !before.talked && !this.cur.Talked.Contains(name))
            {
                this.cur.Talked.Add(name);
                this.cur.Add(Act.Talk, 1, now);
                this.Trace($"talked to {name}");
            }
            for (int g = before.gifts; g < f.GiftsToday; g++)
            {
                string item = Game1.ticks - this.lastRemoved.tick <= 120 ? this.lastRemoved.id : "";
                this.cur.Gifts.Add(new[] { name, item });
                this.cur.Add(Act.Gift, 1, now);
                this.Trace($"gift to {name}: {item}");
            }
        }

        foreach (Pet pet in Game1.currentLocation?.characters.OfType<Pet>() ?? Enumerable.Empty<Pet>())
        {
            if (!AnimalTools.PettedToday(pet) || !this.pettedPets.Add(pet.petId.Value))
                continue;
            if (hand)
            {
                this.cur.Petted++;
                this.cur.Add(Act.Animals, 1, now);
                this.Trace($"petted {pet.Name}");
            }
        }
        foreach (FarmAnimal a in Game1.currentLocation?.animals.Values ?? Enumerable.Empty<FarmAnimal>())
        {
            if (!a.wasPet.Value || !this.pettedAnimals.Add(a.myID.Value))
                continue;
            // The petted flag is shared in co-op: count it only right after this player pressed the action button next to the animal.
            if (hand && Vector2.Distance(a.Tile, me.Tile) <= 3 && Game1.ticks - this.lastActionTick <= 60)
            {
                this.cur.Petted++;
                this.cur.Add(Act.Animals, 1, now);
                this.Trace($"petted {a.Name}");
            }
        }
    }

    private static bool IsReadyMachine(SObject o) => o.readyForHarvest.Value && o.heldObject.Value is { } h && h is not Chest && o is not Chest;

    private void RefreshMachines()
    {
        this.readyMachines.Clear();
        if (Game1.currentLocation is { } loc)
            foreach (var (tile, o) in loc.objects.Pairs)
                if (IsReadyMachine(o))
                    this.readyMachines.Add(tile);
    }

    /// <summary>A ready machine next to the farmer is no longer ready: emptied, by this player if its output reaches the backpack.</summary>
    private void CheckMachines()
    {
        if (this.cur == null || this.readyMachines.Count == 0 || Game1.currentLocation is not { } loc)
            return;
        foreach (Vector2 tile in this.readyMachines.ToList())
        {
            if (loc.objects.TryGetValue(tile, out SObject? o) && IsReadyMachine(o))
                continue;
            this.readyMachines.Remove(tile);
            if (this.cur.Control == Control.Hand && Vector2.Distance(tile, Game1.player.Tile) <= 2.5f)
                this.emptiedMachines.Add(Game1.ticks);
        }
    }

    private void RefreshCrops()
    {
        this.readyCrops.Clear();
        if (Game1.currentLocation is not { } loc)
            return;
        foreach (var (tile, t) in loc.terrainFeatures.Pairs)
            if (t is HoeDirt { crop: { } crop } dirt && !crop.dead.Value && dirt.readyForHarvest())
                this.readyCrops[tile] = ItemRegistry.QualifyItemId(crop.indexOfHarvest.Value) ?? "";
        foreach (var (tile, o) in loc.objects.Pairs)
            if (o is IndoorPot { hoeDirt.Value: { crop: { } crop } dirt } && !crop.dead.Value && dirt.readyForHarvest())
                this.readyCrops[tile] = ItemRegistry.QualifyItemId(crop.indexOfHarvest.Value) ?? "";
    }

    /// <summary>A ready crop next to the farmer is no longer ready: harvested, by this player if its items reach the backpack.</summary>
    private void CheckCrops()
    {
        if (this.cur == null || this.readyCrops.Count == 0 || Game1.currentLocation is not { } loc)
            return;
        foreach (var (tile, item) in this.readyCrops.ToList())
        {
            HoeDirt? dirt = loc.terrainFeatures.TryGetValue(tile, out TerrainFeature? t) ? t as HoeDirt
                : loc.objects.TryGetValue(tile, out SObject? o) && o is IndoorPot pot ? pot.hoeDirt.Value : null;
            if (dirt?.crop is { } crop && !crop.dead.Value && dirt.readyForHarvest())
                continue;
            this.readyCrops.Remove(tile);
            // The scythe reaches a little further than the hand.
            if (this.cur.Control == Control.Hand && Vector2.Distance(tile, Game1.player.Tile) <= 3f)
                this.emptiedCrops.Add((item, Game1.ticks));
        }
    }

    private void Floor(MineShaft shaft)
    {
        if (this.cur == null)
            return;
        int floor = shaft.mineLevel > 120 ? shaft.mineLevel - 120 : shaft.mineLevel;
        this.cur.MineFrom = this.cur.MineFrom == 0 ? floor : Math.Min(this.cur.MineFrom, floor);
        this.cur.MineTo = Math.Max(this.cur.MineTo, floor);
    }

    // ------------------------------------------------------------ places

    /// <summary>The map's id in a log: all mine floors are one map, and so are the Skull Cavern's.</summary>
    public static string MapKey(GameLocation loc) => loc switch
    {
        MineShaft { mineLevel: > 120 } => "SkullCave",
        MineShaft => "Mines",
        VolcanoDungeon => "Volcano",
        _ => loc.NameOrUniqueName,
    };

    /// <summary>The farm, farmhouses, the greenhouse and farm buildings.</summary>
    public static bool IsFarmPlace(GameLocation loc) =>
        loc is Farm or FarmHouse || loc.Name == "Greenhouse" || loc.GetParentLocation() is Farm;

    private void Trace(string what) => this.monitor.Log($"Watcher {Game1.timeOfDay}: {what}", LogLevel.Trace);
}
