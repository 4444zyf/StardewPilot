using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

/// <summary>A habit with its evidence. Always computed: worded as "usually", never as something the player said.</summary>
public sealed class Habit
{
    /// <summary>"morning", "bedtime" or "day-type".</summary>
    public string Kind = "";
    public string Text = "";
    public int Days;
    public int OfDays;

    /// <summary>Morning order: the activities in their usual order, with the job that does each (null when no job does it).</summary>
    public List<MorningStep>? Steps;

    /// <summary>Day-type: the condition ("rain", "Mon" ...), the map id and the main activity.</summary>
    public string? Condition, Map, Activity;
}

public sealed class MorningStep
{
    public string Activity = "";
    public string Words = "";
    public string? Job;
    public int UsualStart;
    public int Days;
    public int OfDays;
}

/// <summary>Works out habits from day logs. Pure: no game calls, so it is tested without the game.</summary>
public static class HabitFinder
{
    /// <summary>Days looked at for the morning order and bedtime.</summary>
    public const int RecentDays = 14;

    /// <summary>Days kept, and looked at for day-type habits (14 days hold only two of each weekday).</summary>
    public const int KeptDays = 56;

    /// <summary>The "morning" ends at noon or when the farmer first leaves the farm, whichever is first.</summary>
    public const int MorningEnds = 1200;

    /// <summary>Days with less than this much hand-played time teach nothing.</summary>
    public const int MinHandMinutes = 360;

    /// <summary>Activities that can make up a morning order.</summary>
    private static readonly string[] MorningActs =
    {
        Act.Water, Act.Harvest, Act.Animals, Act.Machines, Act.Forage, Act.Sow, Act.Hoe, Act.Fish, Act.Rocks, Act.Chop, Act.Weeds,
        Act.Craft, Act.Cook, Act.Store, Act.Fetch,
    };

    public static readonly string[] Weekdays = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

    /// <summary>Whether a day is evidence at all: no festival, did not pass out, enough time played by hand.</summary>
    public static bool Countable(DayLog d) => !d.Facts.Festival && !d.PassedOut && d.HandMinutes >= MinHandMinutes;

    /// <summary>All habits, from logs in any order. Map names come from <paramref name="names"/>.</summary>
    public static List<Habit> Find(IEnumerable<DayLog> logs, Names names)
    {
        var days = logs.Where(Countable).OrderByDescending(SortKey).Take(KeptDays).ToList();
        var habits = new List<Habit>();
        if (Morning(days.Take(RecentDays).ToList()) is { } morning)
            habits.Add(morning);
        if (Bedtime(days.Take(RecentDays).ToList()) is { } bed)
            habits.Add(bed);
        habits.AddRange(DayTypes(days, names));
        return habits;
    }

    /// <summary>Activities done by hand in the morning on two thirds of the days they applied, not dropped by three misses in a row.</summary>
    public static Habit? Morning(List<DayLog> newestFirst)
    {
        var steps = new List<MorningStep>();
        foreach (string act in MorningActs)
        {
            var applied = newestFirst.Where(d => d.Applies(act)).Take(RecentDays).ToList();
            if (applied.Count < 4)
                continue;
            var starts = applied.Select(d => MorningStart(d, act)).ToList();
            int done = starts.Count(t => t != null);
            if (done * 3 < applied.Count * 2)
                continue;
            if (starts.Take(3).All(t => t == null))
                continue; // faded: not done on the last three days it applied
            steps.Add(new MorningStep
            {
                Activity = act,
                Words = Act.Word(act),
                Job = Act.Job(act),
                UsualStart = Median(starts.Where(t => t != null).Select(t => t!.Value).ToList()),
                Days = done,
                OfDays = applied.Count,
            });
        }
        if (steps.Count == 0)
            return null;
        // Storing comes last whatever its usual time, so "do my usual morning" does not put away the seeds a later step plants.
        steps = steps.OrderBy(s => s.Activity == Act.Store ? 1 : 0).ThenBy(s => Time.ToMinutes(s.UsualStart)).ToList();
        var first = steps[0];
        return new Habit
        {
            Kind = "morning",
            Text = "Mornings: usually " + string.Join(", ", steps.Select(s => $"{s.Words} ({s.Days} of {s.OfDays} days)")),
            Days = steps.Min(s => s.Days),
            OfDays = steps.Max(s => s.OfDays),
            Steps = steps,
        };
    }

    /// <summary>When the activity was first done by hand in the morning of a day, or null.</summary>
    public static int? MorningStart(DayLog d, string act)
    {
        int? first = null;
        foreach (Slice s in d.Slices.OrderBy(x => Time.ToMinutes(x.Start)))
        {
            if (!s.OnFarm || s.Start >= MorningEnds)
                break; // left the farm, or the morning is over
            if (s.Control != Control.Hand || !s.First.TryGetValue(act, out int t) || t >= MorningEnds)
                continue;
            if (first == null || Time.ToMinutes(t) < Time.ToMinutes(first.Value))
                first = t;
        }
        return first;
    }

    /// <summary>The middle bedtime, when two thirds of the bedtimes are within an hour of it.</summary>
    public static Habit? Bedtime(List<DayLog> newestFirst)
    {
        var times = newestFirst.Where(d => d.BedByHand && d.Bedtime > 0).Select(d => d.Bedtime).ToList();
        if (times.Count < 4)
            return null;
        int median = Median(times);
        int near = times.Count(t => Math.Abs(Time.ToMinutes(t) - Time.ToMinutes(median)) <= 60);
        if (near * 3 < times.Count * 2)
            return null;
        int rounded = Time.FromMinutes((Time.ToMinutes(median) + 15) / 30 * 30);
        return new Habit { Kind = "bedtime", Text = $"Usually in bed around {Time.Clock(rounded)} ({near} of {times.Count} days within an hour)", Days = near, OfDays = times.Count };
    }

    /// <summary>For rainy days and each weekday: the map where most hand-played time went, if two thirds of those days agree.</summary>
    public static List<Habit> DayTypes(List<DayLog> days, Names names)
    {
        var habits = new List<Habit>();
        var conditions = new List<(string key, string words, Func<DayLog, bool> match)> { ("rain", "Rainy days", d => d.Facts.Rain) };
        foreach (string w in Weekdays)
            conditions.Add((w, $"{FullDay(w)}s", d => d.Weekday == w));

        foreach (var (key, words, match) in conditions)
        {
            var matching = days.Where(match).ToList();
            if (matching.Count < 3)
                continue;
            var mains = matching.Select(MainMap).Where(m => m != null).ToList();
            var top = mains.GroupBy(m => m).OrderByDescending(g => g.Count()).First();
            int agree = top.Count();
            if (agree * 3 < matching.Count * 2)
                continue;
            string map = top.Key!;
            // The activity that moved most on that map over those days.
            var acts = new Dictionary<string, int>();
            foreach (DayLog d in matching)
                foreach (Slice s in d.Slices.Where(s => s.Control == Control.Hand && s.Map == map))
                    foreach (var (a, n) in s.Acts)
                        acts[a] = (acts.TryGetValue(a, out int c) ? c : 0) + n;
            string? act = acts.Count > 0 ? acts.OrderByDescending(x => x.Value).First().Key : null;
            habits.Add(new Habit
            {
                Kind = "day-type",
                Condition = key,
                Map = map,
                Activity = act,
                Days = agree,
                OfDays = matching.Count,
                Text = $"{words}: mostly at {Names.Clean(names.Map(map))}" + (act != null ? $", {Act.Word(act)}" : "") + $" ({agree} of {matching.Count})",
            });
        }
        return habits;
    }

    /// <summary>The map with the most hand-played minutes that day.</summary>
    public static string? MainMap(DayLog d) =>
        d.Slices.Where(s => s.Control == Control.Hand).GroupBy(s => s.Map).Select(g => (map: g.Key, minutes: g.Sum(s => s.Minutes)))
            .Where(x => x.minutes > 0).OrderByDescending(x => x.minutes).Select(x => x.map).FirstOrDefault();

    private static string FullDay(string w) => w switch
    {
        "Mon" => "Monday", "Tue" => "Tuesday", "Wed" => "Wednesday", "Thu" => "Thursday", "Fri" => "Friday", "Sat" => "Saturday", _ => "Sunday",
    };

    private static int Median(List<int> times)
    {
        var m = times.Select(Time.ToMinutes).OrderBy(x => x).ToList();
        return Time.FromMinutes(m[m.Count / 2]);
    }

    /// <summary>Sortable number for a log's date.</summary>
    public static int SortKey(DayLog d)
    {
        int season = Array.IndexOf(new[] { "spring", "summer", "fall", "winter" }, d.Season);
        return d.Year * 1000 + season * 100 + d.Day;
    }
}
