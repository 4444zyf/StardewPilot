using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

/// <summary>Display names for ids in a log. The game side fills them in when a log is read, in the current language.</summary>
public sealed class Names
{
    public Func<string, string> Item = id => id;
    public Func<string, string> Map = id => id;
    public Func<string, string> Npc = id => id;
    public Func<string, string> Shop = id => id;

    /// <summary>Text from the game (modded names) is data: one line, short.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "?";
        string one = string.Join(" ", text.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length > 40 ? one.Substring(0, 40) : one;
    }
}

/// <summary>A day log as short plain lines, one per slice.</summary>
public static class LogText
{
    /// <summary>At most this many slice lines for a whole day, plus the bedtime line.</summary>
    public const int MaxLines = 20;

    public static List<string> Lines(DayLog log, Names names, int max = MaxLines)
    {
        var shown = log.Slices.Where(s => s.HasActivity || s.Minutes >= 30 || (s.Control != Control.Hand && s.Jobs.Count > 0)).ToList();
        if (shown.Count > max)
        {
            var keep = new HashSet<Slice>(shown.OrderByDescending(s => s.Minutes).Take(max));
            shown = shown.Where(keep.Contains).ToList();
        }
        var lines = shown.Select(s => Line(s, names)).ToList();
        if (End(log, names) is { } end)
            lines.Add(end);
        return lines;
    }

    public static string Line(Slice s, Names names)
    {
        string head = $"{Time.Format(s.Start)}-{Time.Format(s.End)} {Names.Clean(names.Map(s.Map))}";
        if (s.Control != Control.Hand)
        {
            string who = s.Control switch
            {
                Control.SelfPlay => "copilot in self-play",
                Control.Scheduled => "copilot, a routine that started by its schedule",
                Control.Idle => "idle where the copilot left the farmer",
                _ => "copilot, asked by the player",
            };
            var jobs = s.Jobs.Select(j => j.Split(' ').Skip(1).FirstOrDefault()?.TrimEnd(':') ?? j).Distinct().ToList();
            return $"{head}: {who}" + (jobs.Count > 0 ? $": {string.Join(", ", jobs)}" : "");
        }

        var parts = new List<string>();
        foreach (var (tool, n) in s.Swings.OrderByDescending(x => x.Value))
            parts.Add($"{Names.Clean(names.Item(tool))} {n} swing{(n == 1 ? "" : "s")}");
        if (s.Items.ContainsKey("harvested"))
            Items(parts, "harvested", s, "harvested", names);
        else
            Count(parts, s, Act.Harvest, n => $"harvested {n} crop{P(n)}");
        Count(parts, s, Act.Sow, n => $"sowed {n} seed{P(n)}");
        Count(parts, s, Act.Hoe, n => $"hoed {n} tile{P(n)}");
        if (s.Petted > 0)
            parts.Add($"petted {s.Petted} animal{P(s.Petted)}");
        Count(parts, s, Act.Machines, n => $"collected from {n} machine{P(n)}");
        Count(parts, s, Act.Forage, n => $"foraged {n} item{P(n)}");
        Count(parts, s, Act.Fish, n => $"{n} cast{P(n)}");
        if (s.MineTo > 0)
            parts.Add(s.MineFrom == s.MineTo ? $"floor {s.MineTo}" : $"floors {s.MineFrom} to {s.MineTo}");
        Count(parts, s, Act.Rocks, n => $"{n} rock{P(n)} broken");
        Count(parts, s, Act.Weeds, n => $"{n} weed{P(n)} cut");
        Count(parts, s, Act.Combat, n => $"{n} monster{P(n)} killed");
        Count(parts, s, Act.Craft, n => $"crafted {n}");
        Count(parts, s, Act.Cook, n => $"cooked {n}");
        Count(parts, s, Act.Geodes, n => $"cracked {n} geode{P(n)}");
        if (s.Items.ContainsKey("bought"))
        {
            string list = ItemList(s.Items["bought"], names);
            string shops = s.Shops.Count > 0 ? $" at {string.Join(", ", s.Shops.Select(x => Names.Clean(names.Shop(x))))}" : "";
            parts.Add($"bought {list}{shops}" + (s.Spent > 0 ? $" ({s.Spent:N0}g)" : ""));
        }
        Items(parts, "sold", s, "sold", names);
        Items(parts, "stored", s, "stored", names, " in a chest");
        Items(parts, "took", s, "fetched", names, " from a chest");
        if (s.Talked.Count > 0)
            parts.Add($"talked to {string.Join(", ", s.Talked.Select(n => Names.Clean(names.Npc(n))))}");
        foreach (string[] g in s.Gifts)
            parts.Add($"gave {Names.Clean(names.Npc(g[0]))} " + (g.Length > 1 && g[1].Length > 0 ? Names.Clean(names.Item(g[1])) : "a gift"));
        Items(parts, "gained", s, "gained", names);
        return parts.Count > 0 ? $"{head}: {string.Join(", ", parts)}" : head;
    }

    /// <summary>The bedtime and shipping line.</summary>
    public static string? End(DayLog log, Names names)
    {
        var parts = new List<string>();
        if (log.PassedOut)
            parts.Add("passed out");
        else if (log.Bedtime > 0)
            parts.Add($"{Time.Format(log.Bedtime)} went to bed" + (log.BedByHand ? "" : " (the copilot took the farmer to bed)"));
        if (log.Shipped.Count > 0)
            parts.Add((log.SharedBin ? "the farm shipped " : "shipped ") + ItemList(log.Shipped, names));
        return parts.Count > 0 ? Capital(string.Join(". ", parts)) : null;
    }

    private static string Capital(string s) => s.Length > 0 && char.IsLetter(s[0]) ? char.ToUpperInvariant(s[0]) + s.Substring(1) : s;

    private static string P(int n) => n == 1 ? "" : "s";

    private static void Count(List<string> parts, Slice s, string act, Func<int, string> text)
    {
        if (s.Acts.TryGetValue(act, out int n) && n > 0)
            parts.Add(text(n));
    }

    private static void Items(List<string> parts, string verb, Slice s, string kind, Names names, string suffix = "")
    {
        if (s.Items.TryGetValue(kind, out var items) && items.Count > 0)
            parts.Add($"{verb} {ItemList(items, names)}{suffix}");
    }

    /// <summary>"Parsnip x15, Wood x40 and 3 more".</summary>
    public static string ItemList(Dictionary<string, int> items, Names names, int max = 5)
    {
        var top = items.OrderByDescending(x => x.Value).Take(max).Select(x => $"{Names.Clean(names.Item(x.Key))} x{x.Value}").ToList();
        int more = items.Count - top.Count;
        return string.Join(", ", top) + (more > 0 ? $" and {more} more" : "");
    }
}
