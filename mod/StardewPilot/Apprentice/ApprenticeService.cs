using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewPilot.Apprentice.Model;
using StardewPilot.Autopilot;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewPilot.Tools;
using StardewValley;

namespace StardewPilot.Apprentice;

/// <summary>
/// Apprentice mode: the watcher's day logs, the habits worked out from them, and the tools that read both.
/// Logs and habits follow the memory's save rule: they reach disk only when the game saves.
/// </summary>
internal sealed class ApprenticeService
{
    /// <summary>Activity lines per day in the day record (and so in the day seed). The rest is reached with get_activity_log.</summary>
    public const int RecordLines = 8;

    private readonly MemoryStore memory;
    private readonly Func<ModConfig> config;
    private readonly IMonitor monitor;

    public Watcher Watcher { get; }

    public ApprenticeService(MemoryStore memory, Func<ModConfig> config, IMonitor monitor, JobRunner runner, SelfPlay selfPlay)
    {
        this.memory = memory;
        this.config = config;
        this.monitor = monitor;
        this.Watcher = new Watcher(monitor, config, runner, selfPlay);
    }

    public bool Enabled => this.config().Apprentice.Enabled;

    // ------------------------------------------------------------ day

    public void StartDay()
    {
        this.Watcher.StartDay();
        if (this.Enabled && !this.memory.ApprenticeNoticeShown())
        {
            ChatLog.Add(ChatSender.System,
                "New: the copilot keeps a diary of what you do in the game, to learn your habits (such as your usual morning). " +
                "It stays in the mod's data folder. Type /forget habits to delete it, or set Apprentice.Enabled to false in config.json to switch it off.");
            this.memory.MarkApprenticeNotice();
        }
    }

    /// <summary>
    /// Day end: store the day's log, rebuild habits.md, drop logs older than the kept days, and return the lines for the
    /// day record. All of it is pending until the game saves.
    /// </summary>
    public List<string> EndDay()
    {
        DayLog? log = this.Watcher.EndDay();
        if (log == null)
            return new();
        Names names = GameNames();
        this.memory.WriteActivity(log.Date, JsonConvert.SerializeObject(log));

        var dates = this.memory.ActivityLogs().Select(x => x.date).OrderByDescending(MemoryStore.SortKey).ToList();
        foreach (string old in dates.Skip(HabitFinder.KeptDays))
            this.memory.DeleteActivity(old);

        List<Habit> habits = this.Habits();
        this.memory.WriteHabits(HabitsFile(habits));
        this.monitor.Log($"Apprentice: day {log.Date} logged; {habits.Count} habits.", LogLevel.Debug);

        var all = LogText.Lines(log, names);
        var lines = LogText.Lines(log, names, RecordLines);
        int more = all.Count - lines.Count;
        var record = new List<string> { "What happened today, watched by the mod (computed):" };
        record.AddRange(lines.Select(l => "- " + l));
        if (more > 0)
            record.Add($"({more} more lines: get_activity_log)");
        return record;
    }

    // ------------------------------------------------------------ logs and habits

    public List<DayLog> Logs()
    {
        var logs = new List<DayLog>();
        foreach (var (date, json) in this.memory.ActivityLogs())
        {
            try
            {
                if (JsonConvert.DeserializeObject<DayLog>(json) is { } log)
                    logs.Add(log);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Apprentice: skipped unreadable activity log {date}: {ex.Message}", LogLevel.Warn);
            }
        }
        return logs;
    }

    public List<Habit> Habits() => HabitFinder.Find(this.Logs(), GameNames());

    /// <summary>Habit lines for the day seed (at most ten), or nothing when watching is off.</summary>
    public List<string> SeedLines() => this.Enabled ? this.Habits().Select(h => h.Text).Take(10).ToList() : new();

    public int Forget()
    {
        int removed = this.memory.ForgetApprentice();
        this.Watcher.StartDay(); // today's diary starts again from now
        return removed;
    }

    private static string HabitsFile(List<Habit> habits)
    {
        var lines = new List<string>
        {
            "# Habits",
            "",
            "Rebuilt every night by StardewPilot from what you did by hand; edits here are lost. Tell the copilot, or write in notes.md, to correct it.",
            "",
        };
        lines.AddRange(habits.Count > 0 ? habits.Select(h => "- " + h.Text) : new[] { "- Not enough days watched yet." });
        return string.Join("\n", lines) + "\n";
    }

    // ------------------------------------------------------------ names

    private static readonly Dictionary<string, string> MapNames = new()
    {
        ["Mines"] = "The Mines",
        ["SkullCave"] = "Skull Cavern",
        ["Volcano"] = "Volcano Dungeon",
    };

    private static readonly Dictionary<string, string> ShopNames = new()
    {
        ["SeedShop"] = "Pierre's General Store",
        ["Joja"] = "JojaMart",
        ["FishShop"] = "Willy's Fish Shop",
        ["AnimalShop"] = "Marnie's Ranch",
        ["Blacksmith"] = "the Blacksmith",
        ["Saloon"] = "The Stardrop Saloon",
        ["Carpenter"] = "the Carpenter's Shop",
        ["Hospital"] = "Harvey's Clinic",
        ["Sandy"] = "the Oasis",
        ["Traveler"] = "the Traveling Cart",
        ["AdventureShop"] = "the Adventurer's Guild",
    };

    /// <summary>Names in the game's current language. Text from the game is cleaned when shown (one line, at most 40 characters).</summary>
    public static Names GameNames() => new()
    {
        Item = id => ItemRegistry.GetDataOrErrorItem(id).DisplayName,
        Map = MapName,
        Npc = id => Game1.getCharacterFromName(id)?.displayName ?? id,
        Shop = id => ShopNames.TryGetValue(id, out string? n) ? n : id,
    };

    /// <summary>A map's name for a log line. The farm and its buildings show the farm's name, so they get plain words.</summary>
    private static string MapName(string id)
    {
        if (MapNames.TryGetValue(id, out string? special))
            return special;
        return Game1.getLocationFromName(id) switch
        {
            Farm => "the farm",
            StardewValley.Locations.FarmHouse home when home == Utility.getHomeOfFarmer(Game1.player) => "the farmhouse",
            StardewValley.Locations.Cabin => "a cabin",
            StardewValley.Locations.FarmHouse => "the farmhouse",
            { ParentBuilding: { } building } => AnimalTools.BuildingName(building),
            { } loc => loc.DisplayName,
            null => id,
        };
    }

    // ------------------------------------------------------------ tools

    public void Register(ToolRegistry registry)
    {
        registry.Add(new ToolDef
        {
            Name = "get_activity_log",
            Description =
                "What the player did on recent days, as the mod watched it: one line per stretch on one map (time, place, tool swings, harvests, purchases, chests, " +
                "villagers, mine floors), marked when the copilot was in control, plus bedtime and shipping. Today so far is included. " +
                "Use it for questions such as 'what did I do yesterday?' or 'when did I last go to the mines?'.",
            InputSchema = Schema.Object(("days", Schema.Integer("Days back to include, 1 to 14 (default 7). 1 is today only."), false)),
            Handler = args => this.ActivityLog(Math.Clamp(args.Int("days") ?? 7, 1, 14)),
        });
        registry.Add(new ToolDef
        {
            Name = "get_habits",
            Description =
                "The player's habits, worked out by the mod from what they did by hand on recent days: their usual morning (in order, with the job that does each step), " +
                "their usual bedtime, and what they usually do on rainy days or a given weekday. Each habit has its evidence (days done of days counted).",
            Handler = _ => this.HabitsResult(),
        });
    }

    private object ActivityLog(int days)
    {
        if (!this.Enabled)
            return new { enabled = false, reason = "Watching is switched off (Apprentice.Enabled in config.json)." };
        Names names = GameNames();
        var result = new List<object>();
        if (this.Watcher.Today() is { } today)
            result.Add(new { date = today.Date, today = true, lines = LogText.Lines(today, names) });
        foreach (DayLog log in this.Logs().Where(l => l.Date != MemoryStore.DateKey()).OrderByDescending(HabitFinder.SortKey).Take(days - 1))
            result.Add(new { date = log.Date, today = false, lines = LogText.Lines(log, names) });
        return new
        {
            days = result,
            note = "Lines marked 'copilot' were done by the autopilot; the rest the player did by hand. Swings are tool uses, not tiles. Days that were not saved (quit without sleeping) are not kept.",
            how = "Computed: the mod reads the player's own counters, tool use, friendship record, backpack and bedtime while they play, and groups them by map. " +
                  "Harvests, machines emptied and farm animals petted are inferred from what changed next to the farmer.",
        };
    }

    private object HabitsResult()
    {
        if (!this.Enabled)
            return new { enabled = false, reason = "Watching is switched off (Apprentice.Enabled in config.json)." };
        var logs = this.Logs();
        Names names = GameNames();
        var habits = HabitFinder.Find(logs, names);
        return new
        {
            habits = habits.Select(h => new
            {
                kind = h.Kind,
                text = h.Text,
                days = h.Days,
                ofDays = h.OfDays,
                steps = h.Steps?.Select(s => new { activity = s.Activity, words = s.Words, job = s.Job, usualStart = Time.Clock(s.UsualStart), days = s.Days, ofDays = s.OfDays }),
                condition = h.Condition,
                map = h.Map != null ? Names.Clean(names.Map(h.Map)) : null,
                activity = h.Activity != null ? Act.Word(h.Activity) : null,
            }),
            daysWatched = logs.Count,
            daysCounted = logs.Count(HabitFinder.Countable),
            note = habits.Count == 0
                ? "Not enough days yet: a habit needs at least four days played by hand (three for rainy days and weekdays)."
                : "Observed, not instructions: the player's notes and requests come first. A habit is never permission for shipping, selling, gifting or spending.",
            how = "Computed from the activity logs of the last 14 days (56 for rainy days and weekdays). Only time the player played by hand counts; " +
                  "festival days, days the player passed out and days with less than six hours played by hand are left out.",
        };
    }
}
