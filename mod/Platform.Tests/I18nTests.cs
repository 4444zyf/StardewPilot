using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewPilot;

/// <summary>The i18n files: every language has every key, with the same tokens. Also sets up English for the other tests.</summary>
internal static class I18nTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "i18n");
    private static readonly string[] Languages = { "zh", "ru", "fr" };

    /// <summary>Keys whose translations may pick any of these tokens (a 12- or 24-hour clock).</summary>
    private static readonly Dictionary<string, HashSet<string>> FreeTokens = new()
    {
        ["time.hour"] = new() { "h12", "h24", "mm", "ampm" },
        ["time.minutes"] = new() { "h12", "h24", "mm", "ampm" },
    };

    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    /// <summary>A file's keys and texts, read like SMAPI reads them (comments and trailing commas allowed).</summary>
    public static Dictionary<string, string> Read(string language)
    {
        string text = File.ReadAllText(Path.Combine(Folder, language + ".json"));
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument doc = JsonDocument.Parse(text, options);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    /// <summary>Fill {{tokens}} the way SMAPI does: from an object's properties or a dictionary, case-insensitively.</summary>
    public static string Fill(string text, object? tokens)
    {
        if (tokens == null)
            return text;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tokens is System.Collections.IDictionary dict)
            foreach (System.Collections.DictionaryEntry e in dict)
                values[e.Key.ToString()!] = e.Value?.ToString() ?? "";
        else
            foreach (PropertyInfo p in tokens.GetType().GetProperties())
                values[p.Name] = p.GetValue(tokens)?.ToString() ?? "";
        return Regex.Replace(text, @"{{\s*([\w\.\-]+)\s*}}", m => values.TryGetValue(m.Groups[1].Value, out string? v) ? v : m.Value);
    }

    public static void UseEnglish()
    {
        Dictionary<string, string> en = Read("default");
        I18n.Lookup = (key, tokens) => en.TryGetValue(key, out string? t) ? Fill(t, tokens) : $"(no translation:{key})";
    }

    private static HashSet<string> Tokens(string text) =>
        Regex.Matches(text, @"{{\s*([\w\.\-]+)\s*}}").Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("i18n: every language has every English key and no others", () =>
        {
            var en = Read("default");
            foreach (string lang in Languages)
            {
                var other = Read(lang);
                var missing = en.Keys.Except(other.Keys).ToList();
                var extra = other.Keys.Except(en.Keys).ToList();
                Check(missing.Count == 0, $"{lang}.json lacks: {string.Join(", ", missing)}");
                Check(extra.Count == 0, $"{lang}.json has keys English does not: {string.Join(", ", extra)}");
            }
        });
        yield return ("i18n: every translation uses the same tokens as English", () =>
        {
            var en = Read("default");
            foreach (string lang in Languages)
                foreach (var (key, text) in Read(lang))
                {
                    if (FreeTokens.TryGetValue(key, out var free))
                        Check(Tokens(text).IsSubsetOf(free), $"{lang}.json {key}: tokens {string.Join(",", Tokens(text))} are not all among {string.Join(",", free)}");
                    else if (en.TryGetValue(key, out string? english))
                        Check(Tokens(text).SetEquals(Tokens(english)), $"{lang}.json {key}: tokens {string.Join(",", Tokens(text))} but English has {string.Join(",", Tokens(english))}");
                    Check(text.Trim().Length > 0, $"{lang}.json {key} is empty: SMAPI would show its key instead");
                }
        });
        yield return ("i18n: every key the code uses is in default.json", () =>
        {
            var en = Read("default");
            string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../StardewPilot"));
            if (!Directory.Exists(src))
                return; // run from elsewhere: the source is not next to the tests
            // Any string literal shaped like a key in one of the files' groups ("toast.tip", "fail.no-can"), so keys picked
            // by a condition are found too. Keys built from parts end in "." and are skipped.
            var groups = en.Keys.Select(k => k.Split('.')[0]).ToHashSet();
            var used = new SortedSet<string>();
            foreach (string file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""([a-z][a-z0-9\-]*(?:\.[a-z0-9\-]+)+)"""))
                    if (groups.Contains(m.Groups[1].Value.Split('.')[0]))
                        used.Add(m.Groups[1].Value);
            var missing = used.Where(k => !en.ContainsKey(k)).ToList();
            Check(missing.Count == 0, "default.json lacks: " + string.Join(", ", missing));
        });
        yield return ("i18n: lists and times read naturally in English", () =>
        {
            Check(I18n.List(new[] { "a" }) == "a", I18n.List(new[] { "a" }));
            Check(I18n.List(new[] { "a", "b", "c" }) == "a, b and c", I18n.List(new[] { "a", "b", "c" }));
            Check(StardewPilot.Apprentice.Model.Time.Say(600) == "6am", StardewPilot.Apprentice.Model.Time.Say(600));
            Check(StardewPilot.Apprentice.Model.Time.Say(2130) == "9:30pm", StardewPilot.Apprentice.Model.Time.Say(2130));
            Check(StardewPilot.Apprentice.Model.Time.Say(2500) == "1am", StardewPilot.Apprentice.Model.Time.Say(2500));
        });
    }
}
