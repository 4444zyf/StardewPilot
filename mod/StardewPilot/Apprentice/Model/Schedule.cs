using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

// Game-free: also compiled into the test project. Routines that start themselves: when a routine is due, the checks on a
// schedule, the record of today's runs, and the words.

/// <summary>When a routine is due. Stored in routines.json under "when".</summary>
public sealed class RoutineWhen
{
    /// <summary>Game time (600 = 6:00am), on the ten-minute grid.</summary>
    public int At;

    /// <summary>"Mon" ... "Sun"; null or empty for every day.</summary>
    public List<string>? Days;

    /// <summary>"rain", "dry", or null for any weather.</summary>
    public string? Weather;

    /// <summary>"offer" (a key starts it) or "self" (starts by itself, 6:00 only).</summary>
    public string Start = RoutineSchedule.Offer;

    /// <summary>The date key of the day it was set: it takes effect the next day.</summary>
    public string SetOn = "";

    public RoutineWhen Copy() => new() { At = this.At, Days = this.Days?.ToList(), Weather = this.Weather, Start = this.Start, SetOn = this.SetOn };
}

/// <summary>One routine's run today.</summary>
public sealed class RoutineRun
{
    /// <summary>Game time it started (for the player's information).</summary>
    public int Time;

    /// <summary>Finished for today (done, or ended by a step). False while running or after the player stopped it: the offer comes back.</summary>
    public bool Done;
}

/// <summary>What ran today (kept on the player's home), and how each scheduled routine last ended (kept in routine-runs.json).</summary>
public sealed class RunRecord
{
    public string Date = "";
    public Dictionary<string, RoutineRun> Runs = new();

    /// <summary>Per routine: how its last scheduled run ended ("step 2: reason"), to keep a repeated failure quiet. Kept across days.</summary>
    public Dictionary<string, string> LastStop = new();

    /// <summary>A new date clears the runs (the last stops are kept). Returns whether anything changed.</summary>
    public bool Reconcile(string date)
    {
        if (this.Date == date)
            return false;
        bool had = this.Runs.Count > 0 || this.Date.Length > 0;
        this.Date = date;
        this.Runs.Clear();
        return had;
    }

    /// <summary>
    /// Today's runs as text, kept in the modData of the player's home, so they follow the save: a day quit without sleeping is loaded
    /// again without them (its chores are undone), and a guest who reconnects keeps them (the host still holds the locations).
    /// "date|name=time:1|name=time:0"; names never hold '|', '=' or ':' (letters, digits and spaces).
    /// </summary>
    public string RunsText() =>
        string.Join("|", new[] { this.Date }.Concat(this.Runs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.Time}:{(kv.Value.Done ? 1 : 0)}")));

    /// <summary>Read <see cref="RunsText"/> back (the last stops are kept). Bad parts are skipped.</summary>
    public void ReadRuns(string? text)
    {
        this.Runs.Clear();
        string[] parts = (text ?? "").Split('|');
        this.Date = parts[0];
        foreach (string part in parts.Skip(1))
        {
            int eq = part.IndexOf('='), colon = part.LastIndexOf(':');
            if (eq > 0 && colon > eq && int.TryParse(part.Substring(eq + 1, colon - eq - 1), out int time))
                this.Runs[part.Substring(0, eq)] = new RoutineRun { Time = time, Done = part.Substring(colon + 1) == "1" };
        }
    }

    public bool RanToday(string name) => this.Runs.TryGetValue(name, out RoutineRun? r) && r.Done;
}

public static class RoutineSchedule
{
    public const string Offer = "offer";
    public const string Self = "self";
    public const int Morning = 600;

    /// <summary>A routine is due from its time for this long (game minutes).</summary>
    public const int WindowMinutes = 120;

    /// <summary>A schedule's time is at least this long before the work stop time (game minutes).</summary>
    public const int BeforeStopMinutes = 180;

    public static readonly string[] Weekdays = HabitFinder.Weekdays;

    /// <summary>"Fri", "friday", "FRIDAYS" → "Fri"; null when not a day.</summary>
    public static string? Day(string? text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant();
        if (t.Length < 3)
            return null;
        string? hit = Weekdays.FirstOrDefault(d => t.StartsWith(d.ToLowerInvariant()));
        return hit;
    }

    /// <summary>"6am", "6:00am", "4:30 pm", "16:30", "1630", "morning" → game time; null when not a time.</summary>
    public static int? ParseTime(string? text)
    {
        string t = (text ?? "").Trim().ToLowerInvariant().Replace(" ", "").Replace(".", "");
        if (t is "morning" or "everymorning" or "wakeup")
            return Morning;
        bool pm = t.EndsWith("pm"), am = t.EndsWith("am");
        if (pm || am)
            t = t.Substring(0, t.Length - 2);
        int h, m = 0;
        if (t.Contains(':'))
        {
            string[] parts = t.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out h) || !int.TryParse(parts[1], out m))
                return null;
        }
        else if (!int.TryParse(t, out int n))
            return null;
        else if (n >= 100)
        {
            h = n / 100;
            m = n % 100;
        }
        else
            h = n;
        if (m is < 0 or > 59 || h < 0)
            return null;
        if (am || pm)
        {
            if (h is < 1 or > 12)
                return null;
            h = h % 12 + (pm ? 12 : 0);
        }
        if (h > 26)
            return null;
        return h * 100 + m;
    }

    /// <summary>Problems with a schedule for this routine; empty when it can be in effect.</summary>
    public static List<string> Check(RoutineWhen w, Routine r, int stopWorkAt)
    {
        var problems = new List<string>();
        if (w.At < Morning || w.At % 100 >= 60 || w.At % 10 != 0)
            problems.Add($"The time must be on the ten-minute grid, from 6am (got {w.At}).");
        else if (Time.ToMinutes(w.At) + BeforeStopMinutes > Time.ToMinutes(stopWorkAt))
            problems.Add($"The time must be at least three hours before work stops at {Time.Clock(stopWorkAt)}: {Time.Clock(Time.FromMinutes(Time.ToMinutes(stopWorkAt) - BeforeStopMinutes))} at the latest.");
        foreach (string d in w.Days ?? new())
            if (!Weekdays.Contains(d))
                problems.Add($"'{d}' is not a day (Mon ... Sun).");
        if (w.Weather is not (null or "rain" or "dry"))
            problems.Add("The weather must be 'rain' or 'dry'.");
        if (w.Start is not (Offer or Self))
            problems.Add("start must be 'offer' or 'self'.");
        if (w.Start == Self)
        {
            if (w.At != Morning)
                problems.Add("Only a 6am (morning) routine can start by itself; at other times it can be offered.");
            if (RoutineRules.NeedsAsking(r))
                problems.Add("A routine that buys, ships or gives gifts cannot start by itself, because its questions would open unasked; it can be offered.");
        }
        return problems;
    }

    /// <summary>Whether the schedule matches today (and has taken effect).</summary>
    public static bool Today(RoutineWhen w, string date, string weekday, bool rain) =>
        w.SetOn != date
        && (w.Days == null || w.Days.Count == 0 || w.Days.Contains(weekday))
        && (w.Weather == null || (w.Weather == "rain") == rain);

    /// <summary>Inside the window: from the time, for two game hours.</summary>
    public static bool InWindow(RoutineWhen w, int now)
    {
        int from = Time.ToMinutes(w.At), t = Time.ToMinutes(now);
        return t >= from && t < from + WindowMinutes;
    }

    /// <summary>The routines due now, in order: the earliest time first, then by name.</summary>
    public static List<Routine> Due(IEnumerable<Routine> routines, string date, string weekday, bool rain, int now, RunRecord record, Func<Routine, bool> inEffect) =>
        routines
            .Where(r => r.When is { } w && Today(w, date, weekday, rain) && InWindow(w, now) && !record.RanToday(r.Name) && inEffect(r))
            .OrderBy(r => r.When!.At).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToList();

    /// <summary>"every morning", "at 4pm on Fridays", "every morning when it rains", "at 9am on Mondays and Thursdays when it does not rain", in the player's language.</summary>
    public static string Words(RoutineWhen w)
    {
        bool everyDay = w.Days == null || w.Days.Count == 0 || w.Days.Count == 7;
        string when = w.At == Morning
            ? I18n.Get(everyDay ? "schedule.every-morning" : "schedule.morning")
            : I18n.Get(everyDay ? "schedule.at-every-day" : "schedule.at", new { time = Time.Say(w.At) });
        string days = everyDay ? "" : I18n.Get("schedule.on-days", new { days = I18n.List(Weekdays.Where(d => w.Days!.Contains(d)).Select(d => I18n.Get("schedule.days." + d))) });
        string weather = w.Weather switch
        {
            "rain" => I18n.Get("schedule.rain"),
            "dry" => I18n.Get("schedule.dry"),
            _ => "",
        };
        return I18n.Get("schedule.words", new { when, days, weather }).Trim();
    }

    /// <summary>"starts by itself every morning", "offered at 4pm on Fridays".</summary>
    public static string Sentence(RoutineWhen w) => I18n.Get(w.Start == Self ? "schedule.by-itself" : "schedule.offered", new { when = Words(w) });
}
