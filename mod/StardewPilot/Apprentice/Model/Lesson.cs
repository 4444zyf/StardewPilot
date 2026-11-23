using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

// Game-free: also compiled into the test project. Teach by showing: what a lesson saw, the routine proposed from it,
// and the checks every routine passes when it is saved and every time it is run.

/// <summary>Kinds of lesson events beyond the activity keys in <see cref="Act"/>.</summary>
public static class LessonAct
{
    /// <summary>Items loaded into a machine by hand (Detail: the machine's id; Item: what was loaded).</summary>
    public const string Load = "load";

    /// <summary>A job the player asked for during the lesson (Job and Args).</summary>
    public const string Job = "job";

    /// <summary>What was still ready on a map when the player left it (Left: "crop:id", "forage:id", "machine:id").</summary>
    public const string Left = "left";
}

/// <summary>One thing seen during a lesson, in order.</summary>
public sealed class LessonEvent
{
    public int Time;
    public string Map = "";

    /// <summary>An <see cref="Act"/> key, or a <see cref="LessonAct"/> key.</summary>
    public string Act = "";

    /// <summary>Item id: the crop's harvest, the forage, what was stored, fetched or loaded.</summary>
    public string Item = "";
    public int Count;

    /// <summary>The machine's id, the animal building's map, the villager, or the chest ("x,y"; "junimo" for a Junimo chest).</summary>
    public string Detail = "";

    public string Job = "";
    public Dictionary<string, object?>? Args;
    public bool Failed;
    public List<string>? Left;
}

/// <summary>One step of a routine: an action job and its arguments.</summary>
public sealed class RoutineStep
{
    public string Job = "";

    /// <summary>Values are string, int, bool, a list of strings, or a list of string-keyed dictionaries.</summary>
    public Dictionary<string, object?> Args = new();

    public RoutineStep Copy() => new() { Job = this.Job, Args = Plain.CopyArgs(this.Args) };

    public string Key => this.Job + " " + Plain.Text(this.Args);
}

/// <summary>A saved routine.</summary>
public sealed class Routine
{
    public string Name = "";
    public string Saved = "";
    public List<RoutineStep> Steps = new();

    /// <summary>When it is due, if the player gave it a schedule.</summary>
    public RoutineWhen? When;
}

/// <summary>What a lesson proposes.</summary>
public sealed class Proposal
{
    public List<RoutineStep> Steps = new();

    /// <summary>What was seen and no step covers.</summary>
    public List<string> LeftToYou = new();

    /// <summary>Per map, kinds still ready when the player left ("crop:id" ...): only used to word the playback.</summary>
    public Dictionary<string, List<string>> LeftReady = new();
}

public static class LessonSteps
{
    /// <summary>At most this many events in a lesson; it ends by itself after that.</summary>
    public const int MaxEvents = 60;

    /// <summary>Chest placeholder in a proposal, replaced by the chest's stamped id when the routine is saved: "tile:Map:x,y".</summary>
    public static string ChestTile(string map, string xy) => $"tile:{map}:{xy}";

    /// <summary>Activities seen by hand that never become steps in this round, and why.</summary>
    private static string? NotAStep(string act) => act switch
    {
        Model.Act.Buy or Model.Act.Sell or Model.Act.Gift => "costs something, so it is only included when you ask me to do it during the lesson",
        Model.Act.Fish or Model.Act.Rocks or Model.Act.Combat => "fishing and the mines are not in routines yet",
        Model.Act.Sow or Model.Act.Hoe => "planting is not in routines yet",
        Model.Act.Craft or Model.Act.Cook => "there is no job for it",
        Model.Act.Chop or Model.Act.Weeds or Model.Act.Geodes => "tile work is not in routines",
        _ => null,
    };

    public static Proposal Make(IEnumerable<LessonEvent> events)
    {
        var p = new Proposal();
        var left = new List<(string act, string why)>();
        void Leave(string what, string why) { if (!left.Any(l => l.act == what)) left.Add((what, why)); }

        foreach (LessonEvent e in events)
        {
            RoutineStep? step = null;
            switch (e.Act)
            {
                case Act.Water:
                    step = Step("water_crops", ("location", e.Map));
                    break;
                case Act.Harvest when e.Item.Length > 0:
                    step = Step("harvest_crops", ("location", e.Map), ("crop", e.Item));
                    break;
                case Act.Forage when e.Item.Length > 0:
                    step = Step("collect_forage", ("location", e.Map), ("items", new List<object?> { e.Item }));
                    break;
                case Act.Animals when e.Detail.Length > 0:
                    step = Step("tend_animals", ("building", e.Detail));
                    break;
                case Act.Animals:
                    Leave("tending animals outside their building", "only tending inside a building becomes a step");
                    break;
                case Act.Machines when e.Detail.Length > 0:
                    step = Step("tend_machines", ("location", e.Map), ("machine", e.Detail));
                    break;
                case LessonAct.Load when e.Detail.Length > 0 && e.Item.Length > 0:
                    {
                        // Loading follows collecting from the same kind: the collecting step refills them too.
                        RoutineStep? collect = p.Steps.LastOrDefault(s => s.Job == "tend_machines" && Str(s, "location") == e.Map && Str(s, "machine") == e.Detail);
                        if (collect != null)
                        {
                            if (Str(collect, "refillWith") is null)
                                collect.Args["refillWith"] = e.Item;
                            continue;
                        }
                        step = Step("tend_machines", ("location", e.Map), ("machine", e.Detail), ("refillWith", e.Item));
                        break;
                    }
                case Act.Store or Act.Fetch when e.Detail == "junimo":
                    Leave("using the Junimo chest", "routines do not use Junimo chests");
                    break;
                case Act.Store when e.Item.Length > 0 && e.Detail.Length > 0:
                    {
                        string chest = ChestTile(e.Map, e.Detail);
                        if (p.Steps.LastOrDefault() is { Job: "store_in_chest" } prev && Str(prev, "chest") == chest)
                        {
                            var items = (List<object?>)prev.Args["items"]!;
                            if (!items.Contains(e.Item))
                                items.Add(e.Item);
                            continue;
                        }
                        step = Step("store_in_chest", ("items", new List<object?> { e.Item }), ("chest", chest));
                        break;
                    }
                case Act.Fetch when e.Item.Length > 0 && e.Detail.Length > 0:
                    {
                        string chest = ChestTile(e.Map, e.Detail);
                        if (p.Steps.LastOrDefault() is { Job: "take_from_chest" } prev && Str(prev, "chest") == chest && Str(prev, "item") == e.Item)
                        {
                            prev.Args["count"] = (prev.Args["count"] is int n ? n : 0) + e.Count;
                            continue;
                        }
                        step = Step("take_from_chest", ("item", e.Item), ("count", e.Count), ("chest", chest));
                        break;
                    }
                case Act.Talk when e.Detail.Length > 0:
                    step = Step("talk_to", ("npc", e.Detail));
                    break;
                case LessonAct.Job when !e.Failed:
                    if (RoutineRules.Allowed.Contains(e.Job))
                    {
                        step = new RoutineStep { Job = e.Job, Args = Plain.CopyArgs(e.Args ?? new()) };
                        // Jobs whose map defaults to the current one ran on the map the player was on: name it, so the routine does not depend on where it starts.
                        if (!step.Args.ContainsKey("location") && e.Map.Length > 0 && step.Job is "collect_forage" or "store_in_chest" or "take_from_chest")
                            step.Args["location"] = e.Map;
                        // A chest job names its chest by tile: it becomes the chest's id when the routine is saved.
                        if (step.Job is "store_in_chest" or "take_from_chest" && step.Args.TryGetValue("x", out object? x) && x is int cx && step.Args.TryGetValue("y", out object? y) && y is int cy)
                        {
                            string onMap = Str(step, "location") ?? e.Map;
                            step.Args.Remove("x");
                            step.Args.Remove("y");
                            step.Args.Remove("location");
                            step.Args["chest"] = ChestTile(onMap, $"{cx},{cy}");
                        }
                    }
                    else
                        Leave($"{e.Job} (you asked for it)", "routines cannot include it");
                    break;
                case LessonAct.Left when e.Left != null:
                    p.LeftReady[e.Map] = e.Left.ToList();
                    break;
                default:
                    if (NotAStep(e.Act) is { } why)
                        Leave(Act.Word(e.Act), why);
                    break;
            }
            if (step == null)
                continue;

            // Neighbouring forage steps on one map become one; any step the same as an earlier one adds nothing.
            if (step.Job == "collect_forage" && p.Steps.LastOrDefault() is { Job: "collect_forage" } last && Str(last, "location") == e.Map)
            {
                var items = (List<object?>)last.Args["items"]!;
                if (!items.Contains(e.Item))
                    items.Add(e.Item);
                continue;
            }
            if (step.Job is not ("store_in_chest" or "take_from_chest") && p.Steps.Any(s => s.Key == step.Key))
                continue;
            p.Steps.Add(step);
        }
        p.LeftToYou = left.Select(l => $"{l.act}: {l.why}").ToList();
        return p;
    }

    private static RoutineStep Step(string job, params (string key, object? value)[] args)
    {
        var s = new RoutineStep { Job = job };
        foreach (var (k, v) in args)
            s.Args[k] = v;
        return s;
    }

    public static string? Str(RoutineStep s, string key) => s.Args.TryGetValue(key, out object? v) ? v as string : null;

    // ------------------------------------------------------------ words

    /// <summary>A step in plain words, for the playback, the save question and the run report.</summary>
    public static string Words(RoutineStep s, Names names, Dictionary<string, List<string>>? leftReady = null)
    {
        string Map(string key) => On(names.Map(Str(s, key) ?? "Farm"));
        string Item(string? id) => id == null ? "?" : id.StartsWith("(") ? Names.Clean(names.Item(id)) : Names.Clean(id);
        string ItemList(string key) => s.Args.TryGetValue(key, out object? v) && v is List<object?> l ? I18n.List(l.Select(o => Item(o as string))) : "?";
        string Left(string kind, string? except)
        {
            string map = Str(s, "location") ?? "";
            if (leftReady == null || !leftReady.TryGetValue(map, out var kinds))
                return "";
            var others = kinds.Where(k => k.StartsWith(kind + ":") && k.Substring(kind.Length + 1) != except).Select(k => Item(k.Substring(kind.Length + 1))).Distinct().ToList();
            return others.Count > 0 ? I18n.Get("step.you-left", new { items = I18n.List(others) }) : "";
        }
        string Chest() => Str(s, "chest") is { } c ? ChestWords(c, names) : I18n.Get("step.a-chest");
        string Npc() => Names.Clean(names.Npc(Str(s, "npc") ?? "?"));
        string Count(string key) => s.Args.TryGetValue(key, out object? n) && n is int c ? c.ToString() : I18n.Get("step.all");

        return s.Job switch
        {
            "water_crops" => I18n.Get("step.water", new { where = Map("location") }),
            "harvest_crops" => Str(s, "crop") is { } crop
                ? I18n.Get("step.harvest-crop", new { crop = Item(crop), where = Map("location"), left = Left("crop", crop) })
                : I18n.Get("step.harvest", new { where = Map("location") }),
            "collect_forage" => s.Args.ContainsKey("items")
                ? I18n.Get("step.forage-items", new { items = ItemList("items"), where = Map("location"), left = Left("forage", null) })
                : I18n.Get("step.forage", new { where = Map("location") }),
            "tend_animals" => Str(s, "building") is { } b ? I18n.Get("step.animals-in", new { where = On(names.Map(b)) }) : I18n.Get("step.animals"),
            "tend_machines" => (Str(s, "machine") is { } m
                    ? I18n.Get("step.machine", new { machine = Item(m), where = Map("location") })
                    : I18n.Get("step.machines", new { where = Map("location") }))
                + (Str(s, "refillWith") is { } r ? I18n.Get("step.refill", new { item = Item(r) }) : ""),
            "store_in_chest" => I18n.Get("step.store", new { items = ItemList("items"), chest = Chest() }),
            "take_from_chest" => I18n.Get("step.take", new { count = Count("count"), item = Item(Str(s, "item")), chest = Chest() }),
            "talk_to" => I18n.Get("step.talk", new { npc = Npc() }),
            "put_things_away" => I18n.Get("step.put-away"),
            "buy_items" => I18n.Get("step.buy", new { count = s.Args.TryGetValue("count", out object? bc) ? bc : "?", item = Item(Str(s, "item")) }),
            "ship_items" => I18n.Get("step.ship", new { items = ShipWords(s, Item) }),
            "give_gift" => I18n.Get("step.gift", new { item = Item(Str(s, "item")), npc = Npc() }),
            _ => $"{s.Job} {Plain.Text(s.Args)}",
        };
    }

    private static string ShipWords(RoutineStep s, Func<string?, string> item) =>
        s.Args.TryGetValue("items", out object? v) && v is List<object?> l
            ? I18n.List(l.OfType<Dictionary<string, object?>>().Select(o => I18n.Get("step.count-item", new
            {
                count = o.TryGetValue("count", out object? c) && c is int n ? n.ToString() : I18n.Get("step.all"),
                item = item(o.TryGetValue("item", out object? i) ? i as string : null),
            })))
            : "?";

    /// <summary>"on the farm", "in the Shed".</summary>
    public static string On(string mapName) => I18n.Get(mapName == I18n.Get("map.farm") ? "step.on-farm" : "step.in", new { place = mapName });

    /// <summary>"the chest at (6,12) in the shed", from an id's remembered place or a tile placeholder.</summary>
    public static string ChestWords(string chest, Names names)
    {
        // Saved routines keep the chest's place for wording next to its id: "id@Map:x,y".
        string place = chest.StartsWith("tile:") ? chest.Substring(5) : chest.Contains('@') ? chest.Substring(chest.IndexOf('@') + 1) : "";
        int colon = place.LastIndexOf(':');
        if (colon <= 0)
            return I18n.Get("step.its-chest");
        return I18n.Get("step.chest-at", new { tile = place.Substring(colon + 1), where = On(names.Map(place.Substring(0, colon))) });
    }
}

/// <summary>A job's argument schema, without JSON: what a routine step's arguments are checked against.</summary>
public sealed class ArgSpec
{
    public Dictionary<string, ArgType> Props = new();
    public HashSet<string> Required = new();
}

public sealed class ArgType
{
    /// <summary>"string", "integer", "boolean", "array" or "object".</summary>
    public string Type = "string";
    public List<string>? Enum;
    public ArgType? Items;
    public ArgSpec? Object;
}

/// <summary>The checks every routine passes when saved and every time it runs.</summary>
public static class RoutineRules
{
    public const int MaxSteps = 12;
    public const int MaxRoutines = 20;
    public const int MaxName = 40;

    /// <summary>Jobs a routine may run.</summary>
    public static readonly HashSet<string> Allowed = new()
    {
        "water_crops", "harvest_crops", "collect_forage", "tend_animals", "tend_machines", "store_in_chest", "take_from_chest", "talk_to",
        "put_things_away", "buy_items", "ship_items", "give_gift",
    };

    /// <summary>Steps that cost something: they always ask, and self-play never runs a routine with one.</summary>
    public static readonly HashSet<string> Asks = new() { "buy_items", "ship_items", "give_gift" };

    /// <summary>Arguments that widen a job too far for an unwatched routine.</summary>
    public static readonly HashSet<string> Forbidden = new() { "includeBundleItems", "allButTools", "includeToolbar" };

    /// <summary>Letters, digits and single spaces, at most 40 characters; lower case so names match however they are typed.</summary>
    public static string CleanName(string? name)
    {
        var chars = (name ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        string clean = string.Join(" ", new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length > MaxName ? clean.Substring(0, MaxName).TrimEnd() : clean;
    }

    public static bool NeedsAsking(Routine r) => r.Steps.Any(s => Asks.Contains(s.Job));

    /// <summary>Problems with a routine, empty when it may be saved or run.</summary>
    public static List<string> Check(Routine r, Func<string, ArgSpec?> specOf, Func<string, bool> isOthersHome)
    {
        var problems = new List<string>();
        if (CleanName(r.Name).Length == 0)
            problems.Add("The routine has no name.");
        if (r.Steps.Count == 0)
            problems.Add("The routine has no steps.");
        if (r.Steps.Count > MaxSteps)
            problems.Add($"A routine has at most {MaxSteps} steps; this one has {r.Steps.Count}.");
        for (int i = 0; i < r.Steps.Count; i++)
        {
            RoutineStep s = r.Steps[i];
            string at = $"Step {i + 1} ({s.Job})";
            if (!Allowed.Contains(s.Job))
            {
                problems.Add($"{at}: routines cannot run {s.Job}.");
                continue;
            }
            if (specOf(s.Job) is not { } spec)
            {
                problems.Add($"{at}: unknown job.");
                continue;
            }
            foreach (string key in s.Args.Keys.Where(Forbidden.Contains))
                problems.Add($"{at}: {key} is not allowed in a routine.");
            foreach (string p in ArgProblems(s.Args, spec))
                problems.Add($"{at}: {p}");
            if (s.Args.TryGetValue("location", out object? loc) && loc is string map && isOthersHome(map))
                problems.Add($"{at}: that is another farmer's home.");
            if (s.Args.TryGetValue("chest", out object? chest) && chest is string c && c.StartsWith("tile:"))
                problems.Add($"{at}: the chest has no id yet.");
        }
        return problems;
    }

    /// <summary>Arguments against a schema: known keys, types, enums, required keys.</summary>
    public static List<string> ArgProblems(Dictionary<string, object?> args, ArgSpec spec)
    {
        var problems = new List<string>();
        foreach (string req in spec.Required.Where(r => !args.TryGetValue(r, out object? v) || v == null))
            problems.Add($"{req} is missing.");
        foreach (var (key, value) in args)
        {
            if (!spec.Props.TryGetValue(key, out ArgType? type))
                problems.Add($"unknown argument {key}.");
            else if (value != null && TypeProblem(value, type) is { } why)
                problems.Add($"{key} {why}.");
        }
        return problems;
    }

    private static string? TypeProblem(object value, ArgType type)
    {
        switch (type.Type)
        {
            case "string":
                if (value is not string s)
                    return "must be text";
                return type.Enum != null && !type.Enum.Contains(s) ? $"must be one of {string.Join(", ", type.Enum)}" : null;
            case "integer":
                return value is int or long ? null : "must be a whole number";
            case "boolean":
                return value is bool ? null : "must be true or false";
            case "array":
                if (value is not List<object?> list)
                    return "must be a list";
                if (type.Items != null)
                    foreach (object? item in list)
                    {
                        if (item == null)
                            return "has an empty item";
                        if (TypeProblem(item, type.Items) is { } why)
                            return "has an item that " + why;
                    }
                return null;
            case "object":
                if (value is not Dictionary<string, object?> obj)
                    return "must be an object";
                return type.Object != null && ArgProblems(obj, type.Object) is { Count: > 0 } inner ? "has " + string.Join(" ", inner) : null;
            default:
                return null;
        }
    }
}

/// <summary>Changes the player asks for before saving, passed on by the agent. The only way the model shapes a routine.</summary>
public static class RoutineEdits
{
    /// <summary>Apply edits: "drop" N, "count" N value, "widen" N. Returns the problems; the steps are changed in place.</summary>
    public static List<string> Apply(List<RoutineStep> steps, IEnumerable<(string kind, int step, int? value)> edits)
    {
        var problems = new List<string>();
        var drop = new HashSet<int>();
        foreach (var (kind, n, value) in edits)
        {
            if (n < 1 || n > steps.Count)
            {
                problems.Add($"There is no step {n}.");
                continue;
            }
            RoutineStep s = steps[n - 1];
            switch (kind)
            {
                case "drop":
                    drop.Add(n - 1);
                    break;
                case "count" when value is int c && c > 0 && s.Job is "take_from_chest" or "buy_items":
                    s.Args["count"] = c;
                    break;
                case "count":
                    problems.Add($"Step {n}: only taking from a chest and buying have a count (a positive whole number).");
                    break;
                case "widen":
                    string? filter = s.Job switch
                    {
                        "harvest_crops" => "crop",
                        "collect_forage" => "items",
                        "tend_animals" => "building",
                        "tend_machines" when !s.Args.ContainsKey("refillWith") => "machine",
                        _ => null,
                    };
                    if (filter == null)
                        problems.Add($"Step {n}: this step cannot be widened.");
                    else
                        s.Args.Remove(filter);
                    break;
                default:
                    problems.Add($"Unknown change '{kind}'.");
                    break;
            }
        }
        for (int i = steps.Count - 1; i >= 0; i--)
            if (drop.Contains(i))
                steps.RemoveAt(i);
        return problems;
    }
}

/// <summary>Plain argument values: copying and a stable text form.</summary>
public static class Plain
{
    public static Dictionary<string, object?> CopyArgs(Dictionary<string, object?> args) => args.ToDictionary(kv => kv.Key, kv => Copy(kv.Value));

    private static object? Copy(object? v) => v switch
    {
        List<object?> l => l.Select(Copy).ToList(),
        Dictionary<string, object?> d => CopyArgs(d),
        _ => v,
    };

    public static string Text(object? v) => v switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        bool b => b ? "true" : "false",
        List<object?> l => "[" + string.Join(",", l.Select(Text)) + "]",
        Dictionary<string, object?> d => "{" + string.Join(",", d.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + ":" + Text(kv.Value))) + "}",
        _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}
