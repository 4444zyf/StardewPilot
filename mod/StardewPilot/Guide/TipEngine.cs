using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.TerrainFeatures;

namespace StardewPilot.Guide;

public enum Chattiness
{
    Off,
    Important,
    Talkative,
}

/// <summary>A proactive tip found by a rule. The agent words it; the fallback is shown if no agent answers.</summary>
internal sealed record TipTrigger(string Kind, bool Important, string Fallback, object Facts);

/// <summary>
/// Cheap rule checks that decide when the copilot should speak up.
/// Tips go to the model for wording. Warnings that must land before the player acts are shown at once as fixed text.
/// </summary>
internal sealed class TipEngine
{
    private readonly Func<ModConfig> config;
    private readonly Action<TipTrigger> sendTip;
    private readonly Action<string, string> sendWarning;
    private readonly HashSet<string> firedToday = new();
    private int tipsToday;
    private Dictionary<string, int> lastBin = new();

    /// <param name="sendTip">Deliver a tip (to the agent, or as fallback text).</param>
    /// <param name="sendWarning">Show a fixed-text warning now. Arguments: kind, text.</param>
    public TipEngine(Func<ModConfig> config, Action<TipTrigger> sendTip, Action<string, string> sendWarning)
    {
        this.config = config;
        this.sendTip = sendTip;
        this.sendWarning = sendWarning;
    }

    public void OnDayStarted()
    {
        this.firedToday.Clear();
        this.tipsToday = 0;
        this.lastBin = this.SnapshotBin();
    }

    public void OnTimeChanged(int time)
    {
        if (!Context.IsWorldReady)
            return;
        Farmer p = Game1.player;

        // Morning: birthdays and festivals today.
        if (time >= 610)
        {
            foreach (NPC npc in BirthdaysOn(Game1.dayOfMonth))
            {
                this.Tip($"birthday:{npc.Name}", important: true,
                    $"It's {npc.displayName}'s birthday today. Gifts count eight times as much.",
                    new { npc = npc.Name, displayName = npc.displayName, hearts = p.getFriendshipHeartLevelForNPC(npc.Name) });
            }
            if (FestivalOn(Game1.dayOfMonth) is { } festival)
                this.Tip("festival-today", important: true, $"The {festival} is today. Shops may be closed while it runs.", new { festival });
        }

        // Evening: crops that still need water.
        if (time >= 1700 && !Game1.IsRainingHere(Game1.getFarm()))
        {
            int thirsty = CountThirstyCrops(Game1.getFarm()) + CountThirstyCrops(Game1.getLocationFromName("Greenhouse"));
            if (thirsty > 0)
                this.Tip("unwatered-crops", important: true, $"{thirsty} crops on the farm still need water today.", new { cropsNeedingWater = thirsty });
        }

        // Evening: tomorrow's weather, festival and birthdays.
        if (time >= 1900)
        {
            if (Game1.weatherForTomorrow is "Rain" or "Storm" or "GreenRain")
                this.Tip("rain-tomorrow", important: false, "Rain is forecast for tomorrow, so you won't need to water outdoor crops.", new { weatherTomorrow = Game1.weatherForTomorrow });
            int tomorrow = Game1.dayOfMonth % 28 + 1;
            if (Game1.dayOfMonth < 28 && FestivalOn(tomorrow) is { } festival)
                this.Tip("festival-tomorrow", important: false, $"The {festival} is tomorrow.", new { festival });
            if (Game1.dayOfMonth < 28)
            {
                foreach (NPC npc in BirthdaysOn(tomorrow))
                    this.Tip($"birthday-tomorrow:{npc.Name}", important: false, $"Tomorrow is {npc.displayName}'s birthday.", new { npc = npc.Name, displayName = npc.displayName });
            }
        }

        // Low energy far from home after 6pm.
        if (time >= 1800 && p.Stamina < p.MaxStamina * 0.2f && !IsHome(p.currentLocation))
            this.Tip("low-energy-far", important: true, "Your energy is low and you're far from home. Head back before you run out.", new { energy = (int)p.Stamina, location = p.currentLocation.DisplayName });

        // Late night: passing out away from home costs gold. Fixed text, shown at once.
        if (time is >= 2400 and < 2500 && p.currentLocation is not FarmHouse)
            this.Warn("late-midnight", "It's midnight. At 2am you pass out wherever you are, and away from home that costs gold. Head home to bed now.");
        if (time >= 2500 && p.currentLocation is not FarmHouse)
            this.Warn("late-1am", "It's 1am. You pass out in an hour. Go home to bed now.");
    }

    /// <summary>Called a few times per second.</summary>
    public void OnUpdate()
    {
        if (!Context.IsWorldReady)
            return;
        Farmer p = Game1.player;

        if (p.Stamina > 0 && p.Stamina <= 10)
            this.Warn("energy-critical", "Energy is almost gone. Using tools now will exhaust you. Eat something or stop working.");

        // Items dropped into the shipping bin that a bundle still needs.
        Dictionary<string, int> bin = this.SnapshotBin();
        foreach (var (id, count) in bin)
        {
            if (count <= this.lastBin.GetValueOrDefault(id))
                continue;
            Item? item = Game1.getFarm().getShippingBin(p).FirstOrDefault(i => i != null && KeyOf(i) == id);
            if (item != null && BundleHelper.BundleNeeding(item) is { } bundle)
                this.Warn($"bin:{id}", $"{item.DisplayName} is still needed for the {bundle}. Take it back out of the shipping bin before you sleep if you want it for the bundle.");
        }
        this.lastBin = bin;
    }

    /// <summary>Warn when the player sells a bundle item to a shop.</summary>
    public void OnInventoryChanged(InventoryChangedEventArgs e)
    {
        if (!e.IsLocalPlayer || Game1.activeClickableMenu is not ShopMenu)
            return;
        var lost = e.Removed.Concat(e.QuantityChanged.Where(c => c.NewSize < c.OldSize).Select(c => c.Item));
        foreach (Item item in lost)
        {
            if (BundleHelper.BundleNeeding(item) is { } bundle)
                this.Warn($"sold:{KeyOf(item)}", $"You just sold {item.DisplayName}, which the {bundle} still needs. You can buy it back from this shop before you leave.");
        }
    }

    private void Tip(string key, bool important, string fallback, object facts)
    {
        ModConfig cfg = this.config();
        if (cfg.Chattiness == Chattiness.Off || (!important && cfg.Chattiness == Chattiness.Important))
            return;
        if (this.firedToday.Contains(key) || this.tipsToday >= cfg.MaxTipsPerDay)
            return;
        this.firedToday.Add(key);
        this.tipsToday++;
        this.sendTip(new TipTrigger(key.Split(':')[0], important, fallback, facts));
    }

    private void Warn(string key, string text)
    {
        if (!this.config().Warnings || !this.firedToday.Add(key))
            return;
        this.sendWarning(key.Split(':')[0], text);
    }

    private Dictionary<string, int> SnapshotBin()
    {
        var counts = new Dictionary<string, int>();
        if (!Context.IsWorldReady)
            return counts;
        foreach (Item? item in Game1.getFarm().getShippingBin(Game1.player))
        {
            if (item == null) continue;
            string key = KeyOf(item);
            counts[key] = counts.GetValueOrDefault(key) + item.Stack;
        }
        return counts;
    }

    private static string KeyOf(Item item) => $"{item.QualifiedItemId}@{item.Quality}";

    private static IEnumerable<NPC> BirthdaysOn(int day)
    {
        var list = new List<NPC>();
        Utility.ForEachVillager(npc =>
        {
            if (npc.Birthday_Season == Game1.currentSeason && npc.Birthday_Day == day && npc.CanSocialize && Game1.player.friendshipData.ContainsKey(npc.Name))
                list.Add(npc);
            return true;
        });
        return list;
    }

    private static string? FestivalOn(int day) =>
        DataLoader.Festivals_FestivalDates(Game1.content).TryGetValue($"{Game1.currentSeason}{day}", out string? name) ? name : null;

    private static int CountThirstyCrops(GameLocation? loc)
    {
        if (loc == null)
            return 0;
        return loc.terrainFeatures.Values.Count(t => t is HoeDirt { crop: not null } dirt && !dirt.crop.dead.Value && dirt.needsWatering() && !dirt.isWatered());
    }

    private static bool IsHome(GameLocation loc) =>
        loc is FarmHouse || loc.IsFarm || loc.NameOrUniqueName is "Greenhouse";
}
