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
            ChatLog.Add(ChatSender.System, I18n.Get("apprentice.notice"));
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

    /// <summary>
    /// The briefing's habit parts: the habit for today's kind of day and, on a morning briefing, the one-time offer of the
    /// usual morning. Nothing when watching is off or in self-play (the agent plans from the habits there).
    /// </summary>
    public void AddToBriefing(Dictionary<string, object?> facts, List<string> lines, bool morning, bool selfPlay)
    {
        if (!this.Enabled || selfPlay)
            return;
        List<Habit> habits = this.Habits();
        Farm farm = Game1.getFarm();
        if (BriefingHabits.UsualDay(habits, farm.IsRainingHere(), HabitFinder.Weekdays[(Game1.dayOfMonth - 1) % 7]) is { } day)
        {
            facts["usualDay"] = new { condition = day.Condition, text = day.Text, days = day.Days, ofDays = day.OfDays, how = "Computed from the activity logs: a habit, not a plan or advice." };
            lines.Add(BriefingHabits.UsualDayLine(day));
        }
        if (morning && BriefingHabits.Offer(habits, this.memory.HabitOfferShown()) is { } steps)
        {
            facts["habitOffer"] = new
            {
                steps = steps.Select(s => s.Words),
                say = BriefingHabits.OfferPhrase,
                note = "Shown once. Tell the player the exact phrase; nothing runs until they say it.",
            };
            lines.Add(BriefingHabits.OfferLine(steps));
        }
    }

    /// <summary>The morning briefing was shown: the offer in it (if any) is not made again for the same habit.</summary>
    public void BriefingShown(bool selfPlay)
    {
        if (!this.Enabled || selfPlay)
            return;
        var steps = BriefingHabits.Offer(this.Habits(), this.memory.HabitOfferShown());
        if (steps != null)
        {
            this.memory.MarkHabitOffer(BriefingHabits.OfferKey(steps));
            this.monitor.Log($"Apprentice: offered the usual morning ({BriefingHabits.OfferKey(steps)}).", LogLevel.Debug);
        }
    }

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

    /// <summary>Maps whose game name does not read well in a sentence: the key of their name in the i18n files.</summary>
    private static readonly Dictionary<string, string> MapNames = new()
    {
        ["Mines"] = "map.mines",
        ["SkullCave"] = "map.skull-cavern",
        ["Volcano"] = "map.volcano",
    };

    private static readonly Dictionary<string, string> ShopNames = new()
    {
        ["SeedShop"] = "shop.seed-shop",
        ["Joja"] = "shop.joja",
        ["FishShop"] = "shop.fish-shop",
        ["AnimalShop"] = "shop.animal-shop",
        ["Blacksmith"] = "shop.blacksmith",
        ["Saloon"] = "shop.saloon",
        ["Carpenter"] = "shop.carpenter",
        ["Hospital"] = "shop.hospital",
        ["Sandy"] = "shop.sandy",
        ["Traveler"] = "shop.traveler",
        ["AdventureShop"] = "shop.adventure",
    };

    /// <summary>Names in the game's current language. Text from the game is cleaned when shown (one line, at most 40 characters).</summary>
    public static Names GameNames() => new()
    {
        Item = id => ItemRegistry.GetDataOrErrorItem(id).DisplayName,
        Map = MapName,
        Npc = id => Game1.getCharacterFromName(id)?.displayName ?? id,
        Shop = id => ShopNames.TryGetValue(id, out string? key) ? I18n.Get(key) : id,
    };

    /// <summary>A map's name for a log line. The farm and its buildings show the farm's name, so they get plain words.</summary>
    private static string MapName(string id)
    {
        if (MapNames.TryGetValue(id, out string? special))
            return I18n.Get(special);
        return Game1.getLocationFromName(id) switch
        {
            Farm => I18n.Get("map.farm"),
            StardewValley.Locations.FarmHouse home when home == Utility.getHomeOfFarmer(Game1.player) => I18n.Get("map.farmhouse"),
            StardewValley.Locations.Cabin => I18n.Get("map.cabin"),
            StardewValley.Locations.FarmHouse => I18n.Get("map.farmhouse"),
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
