using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewValley;

namespace StardewPilot.Memory;

/// <summary>
/// The agent's memory: plain text files in the mod's own data folder, one folder per farm and player
/// (a co-op guest has no local save file, so nothing lives next to the save).
/// Memory follows the game's save: writes during a day are pending and only reach disk when the game
/// reports that it saved. Quitting mid-day discards them, just as the game discards the day.
/// </summary>
internal sealed class MemoryStore
{
    private readonly string root;
    private readonly IMonitor monitor;
    private readonly Dictionary<string, string> pending = new();

    public MemoryStore(string modFolder, IMonitor monitor)
    {
        this.root = Path.Combine(modFolder, "data");
        this.monitor = monitor;
    }

    /// <summary>Folder for the current farm and player, for example data/Test_454395324/player-123.</summary>
    public string FarmFolder
    {
        get
        {
            string farm = Regex.Replace($"{Game1.player.farmName.Value}_{Game1.uniqueIDForThisGame}", @"[^\w\-]", "_");
            return Path.Combine(this.root, farm, $"player-{Game1.player.UniqueMultiplayerID}");
        }
    }

    public static string DateKey() => $"Y{Game1.year}-{Game1.currentSeason}-{Game1.dayOfMonth:00}";

    private string NotesPath => Path.Combine(this.FarmFolder, "notes.md");
    private string GoalPath => Path.Combine(this.FarmFolder, "goal.txt");
    private string PlanPath => Path.Combine(this.FarmFolder, "plan.md");
    private string DaysFolder => Path.Combine(this.FarmFolder, "days");
    private string SummaryPath(string dateKey) => Path.Combine(this.DaysFolder, dateKey + ".md");

    // ------------------------------------------------------------ reading (pending writes win)

    private string Read(string path)
    {
        if (this.pending.TryGetValue(path, out string? text))
            return text;
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public string Notes => this.Read(this.NotesPath);
    public string Goal => this.Read(this.GoalPath).Trim();

    /// <summary>Today's plan, or empty if the stored plan is from another day.</summary>
    public string TodayPlan
    {
        get
        {
            string plan = this.Read(this.PlanPath);
            return plan.StartsWith($"# Plan for {DateKey()}") ? plan : "";
        }
    }

    public string TodaySummary => this.Read(this.SummaryPath(DateKey()));

    /// <summary>The most recent day summaries before today, newest first.</summary>
    public List<(string date, string text)> RecentSummaries(int count)
    {
        var keys = new HashSet<string>();
        if (Directory.Exists(this.DaysFolder))
            foreach (string f in Directory.GetFiles(this.DaysFolder, "*.md"))
                keys.Add(Path.GetFileNameWithoutExtension(f));
        foreach (string p in this.pending.Keys.Where(k => k.StartsWith(this.DaysFolder)))
            keys.Add(Path.GetFileNameWithoutExtension(p));
        string today = DateKey();
        return keys.Where(k => k != today)
            .OrderByDescending(SortKey)
            .Take(count)
            .Select(k => (k, this.Read(this.SummaryPath(k))))
            .ToList();
    }

    /// <summary>Sortable number for Y1-spring-04 style keys.</summary>
    private static int SortKey(string key)
    {
        var m = Regex.Match(key, @"^Y(\d+)-(\w+)-(\d+)$");
        if (!m.Success) return 0;
        int season = Array.IndexOf(new[] { "spring", "summer", "fall", "winter" }, m.Groups[2].Value);
        return int.Parse(m.Groups[1].Value) * 1000 + season * 100 + int.Parse(m.Groups[3].Value);
    }

    // ------------------------------------------------------------ morning briefing record (written at once)

    private string BriefingPath => Path.Combine(this.FarmFolder, "briefing-shown.txt");

    /// <summary>
    /// Whether today's morning briefing was already shown. Kept apart from the day's pending memory on purpose:
    /// reloading the save on the same game day must not show it again.
    /// </summary>
    public bool BriefingShownToday() =>
        File.Exists(this.BriefingPath) && File.ReadAllText(this.BriefingPath).Trim() == DateKey();

    public void MarkBriefingShown()
    {
        try
        {
            Directory.CreateDirectory(this.FarmFolder);
            File.WriteAllText(this.BriefingPath, DateKey() + "\n");
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Could not record the morning briefing: {ex.Message}", LogLevel.Warn);
        }
    }

    // ------------------------------------------------------------ writing (pending until saved)

    public void WriteNotes(string text) => this.pending[this.NotesPath] = text.Trim() + "\n";
    public void WriteGoal(string text) => this.pending[this.GoalPath] = text.Trim() + "\n";
    public void WritePlan(string text) => this.pending[this.PlanPath] = $"# Plan for {DateKey()}\n\n{text.Trim()}\n";
    public void WriteSummary(string text) => this.pending[this.SummaryPath(DateKey())] = $"# {DateKey()}\n\n{text.Trim()}\n";

    public bool HasPending => this.pending.Count > 0;

    /// <summary>The game saved: commit pending memory to disk.</summary>
    public void Commit()
    {
        foreach (var (path, text) in this.pending)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                this.monitor.Log($"Could not write memory file {path}: {ex.Message}", LogLevel.Warn);
            }
        }
        if (this.pending.Count > 0)
            this.monitor.Log($"Memory committed ({this.pending.Count} files) to {this.FarmFolder}.", LogLevel.Trace);
        this.pending.Clear();
    }

    /// <summary>The day was discarded (quit without sleeping): drop pending memory.</summary>
    public void Discard()
    {
        if (this.pending.Count > 0)
            this.monitor.Log($"Discarded {this.pending.Count} unsaved memory writes with the unsaved day.", LogLevel.Trace);
        this.pending.Clear();
    }
}
