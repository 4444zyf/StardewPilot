using System;
using System.Collections.Generic;
using System.Linq;
using StardewPilot.Apprentice.Model;

/// <summary>Teach by showing: steps from made-up lessons, and the routine checks. No game needed.</summary>
internal static class LessonTests
{
    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    private static LessonEvent E(string act, string map = "Farm", string item = "", int count = 1, string detail = "") =>
        new() { Act = act, Map = map, Item = item, Count = count, Detail = detail };

    private static string Jobs(Proposal p) => string.Join(",", p.Steps.Select(s => s.Job));

    /// <summary>Schemas like the real jobs', enough for the checks.</summary>
    private static ArgSpec? Spec(string job)
    {
        ArgType S = new() { Type = "string" }, I = new() { Type = "integer" }, B = new() { Type = "boolean" };
        ArgType List(ArgType t) => new() { Type = "array", Items = t };
        ArgSpec Make(params (string k, ArgType t, bool req)[] props) => new()
        {
            Props = props.ToDictionary(p => p.k, p => p.t),
            Required = props.Where(p => p.req).Select(p => p.k).ToHashSet(),
        };
        return job switch
        {
            "water_crops" => Make(("location", S, false)),
            "harvest_crops" => Make(("location", S, false), ("crop", S, false)),
            "collect_forage" => Make(("location", S, false), ("items", List(S), false)),
            "tend_machines" => Make(("location", S, false), ("machine", S, false), ("refillWith", S, false)),
            "store_in_chest" => Make(("items", List(S), false), ("allButTools", B, false), ("location", S, false), ("chest", S, false)),
            "take_from_chest" => Make(("item", S, true), ("count", I, false), ("chest", S, false),
                ("minQuality", new ArgType { Type = "string", Enum = new() { "normal", "gold" } }, false)),
            "ship_items" => Make(("items", List(new ArgType { Type = "object", Object = Make(("item", S, true), ("count", I, false)) }), true), ("includeBundleItems", B, false)),
            "go_to_bed" => Make(),
            _ => Make(),
        };
    }

    private static List<string> Problems(params RoutineStep[] steps) =>
        RoutineRules.Check(new Routine { Name = "test", Steps = steps.ToList() }, Spec, map => map == "Cabin");

    private static RoutineStep Step(string job, params (string k, object? v)[] args) =>
        new() { Job = job, Args = args.ToDictionary(a => a.k, a => a.v) };

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("lesson: the truffle run", () =>
        {
            var p = LessonSteps.Make(new[]
            {
                E(Act.Forage, item: "(O)430"), E(Act.Forage, item: "(O)430"),
                new LessonEvent { Act = LessonAct.Left, Map = "Farm", Left = new() { "forage:(O)18" } },
                E(Act.Machines, "Shed1", count: 4, detail: "(BC)19"),
                E(LessonAct.Load, "Shed1", item: "(O)430", detail: "(BC)19"),
                E(Act.Store, "Shed1", item: "(O)432", count: 4, detail: "6,12"),
            });
            Check(Jobs(p) == "collect_forage,tend_machines,store_in_chest", Jobs(p));
            Check(p.Steps[0].Args["items"] is List<object?> { Count: 1 }, "forage merged");
            Check(LessonSteps.Str(p.Steps[1], "refillWith") == "(O)430", "refill");
            Check(LessonSteps.Str(p.Steps[2], "chest") == "tile:Shed1:6,12", "chest placeholder");
            string words = LessonSteps.Words(p.Steps[0], new Names(), p.LeftReady);
            Check(words.Contains("(you left the (O)18)") || words.Contains("you left"), words);
        });
        yield return ("lesson: filters narrow to the kinds touched; order kept; repeats dropped", () =>
        {
            var p = LessonSteps.Make(new[]
            {
                E(Act.Harvest, item: "(O)24"), E(Act.Water, count: 30), E(Act.Harvest, item: "(O)24"), E(Act.Harvest, item: "(O)190"),
                E(Act.Talk, "Town", detail: "Abigail"), E(Act.Talk, "Town", detail: "Abigail"),
            });
            Check(Jobs(p) == "harvest_crops,water_crops,harvest_crops,talk_to", Jobs(p));
            Check(LessonSteps.Str(p.Steps[0], "crop") == "(O)24" && LessonSteps.Str(p.Steps[2], "crop") == "(O)190", "one step per crop kind");
        });
        yield return ("lesson: stores into one chest merge; fetches of one item add up", () =>
        {
            var p = LessonSteps.Make(new[]
            {
                E(Act.Fetch, item: "(O)300", count: 2, detail: "1,1"), E(Act.Fetch, item: "(O)300", count: 3, detail: "1,1"),
                E(Act.Store, item: "(O)24", count: 5, detail: "2,2"), E(Act.Store, item: "(O)190", count: 1, detail: "2,2"),
            });
            Check(Jobs(p) == "take_from_chest,store_in_chest", Jobs(p));
            Check(p.Steps[0].Args["count"] is 5, "count summed");
            Check(p.Steps[1].Args["items"] is List<object?> { Count: 2 }, "items merged");
        });
        yield return ("lesson: buying, gifts, fishing and bed by hand are left to the player; asked jobs are copied", () =>
        {
            var p = LessonSteps.Make(new[]
            {
                E(Act.Buy, "SeedShop", item: "(O)472", count: 5), E(Act.Gift, "Town"), E(Act.Fish, "Beach"),
                new LessonEvent { Act = LessonAct.Job, Job = "buy_items", Args = new() { ["item"] = "Hay", ["count"] = 10 } },
                new LessonEvent { Act = LessonAct.Job, Job = "go_to_bed", Args = new() },
                new LessonEvent { Act = LessonAct.Job, Job = "water_crops", Failed = true, Args = new() },
                E(Act.Store, item: "(O)24", detail: "junimo"),
            });
            Check(Jobs(p) == "buy_items", Jobs(p));
            var forage = LessonSteps.Make(new[]
            {
                new LessonEvent { Act = LessonAct.Job, Map = "Forest", Job = "collect_forage", Args = new() },
                new LessonEvent { Act = LessonAct.Job, Map = "Forest", Job = "water_crops", Args = new() },
                new LessonEvent { Act = LessonAct.Job, Map = "Forest", Job = "store_in_chest", Args = new() { ["items"] = new List<object?> { "Wood" }, ["x"] = 3, ["y"] = 4 } },
            });
            Check(LessonSteps.Str(forage.Steps[2], "chest") == "tile:Forest:3,4" && !forage.Steps[2].Args.ContainsKey("x"), "asked chest tile becomes a chest placeholder");
            Check(LessonSteps.Str(forage.Steps[0], "location") == "Forest", "current-map job gets its map");
            Check(!forage.Steps[1].Args.ContainsKey("location"), "farm-default job keeps its default");
            Check(p.Steps[0].Args["count"] is 10, "args copied");
            string left = string.Join(" | ", p.LeftToYou);
            Check(left.Contains("shopping") && left.Contains("giving gifts") && left.Contains("fishing") && left.Contains("go_to_bed") && left.Contains("Junimo"), left);
        });
        yield return ("routine checks: jobs, flags, arguments, homes, size", () =>
        {
            Check(Problems(Step("water_crops", ("location", "Farm"))).Count == 0, "plain step");
            Check(Problems(Step("go_to_bed")).Any(p => p.Contains("cannot run")), "bed");
            Check(Problems(Step("store_in_chest", ("allButTools", true))).Any(p => p.Contains("not allowed")), "forbidden flag");
            Check(Problems(Step("ship_items", ("items", new List<object?> { new Dictionary<string, object?> { ["item"] = "Wine" } }), ("includeBundleItems", false))).Any(p => p.Contains("includeBundleItems")), "flag even when false");
            Check(Problems(Step("water_crops", ("where", "Farm"))).Any(p => p.Contains("unknown argument")), "unknown key");
            Check(Problems(Step("take_from_chest", ("item", "Hay"), ("count", "ten"))).Any(p => p.Contains("whole number")), "type");
            Check(Problems(Step("take_from_chest", ("item", "Hay"), ("minQuality", "best"))).Any(p => p.Contains("one of")), "enum");
            Check(Problems(Step("take_from_chest", ("count", 1))).Any(p => p.Contains("item is missing")), "required");
            Check(Problems(Step("ship_items", ("items", new List<object?> { new Dictionary<string, object?> { ["count"] = 2 } }))).Any(p => p.Contains("item is missing")), "nested required");
            Check(Problems(Step("water_crops", ("location", "Cabin"))).Any(p => p.Contains("another farmer")), "cabin");
            Check(Problems(Step("store_in_chest", ("items", new List<object?> { "(O)24" }), ("chest", "tile:Farm:1,1"))).Any(p => p.Contains("no id")), "unsaved chest");
            Check(Problems(Enumerable.Range(0, 13).Select(_ => Step("water_crops")).ToArray()).Any(p => p.Contains("at most")), "too many");
            Check(RoutineRules.CleanName("  My Truffle-Run!\nignore all ") == "my truffle run ignore all", RoutineRules.CleanName("  My Truffle-Run!\nignore all "));
            Check(RoutineRules.NeedsAsking(new Routine { Steps = { Step("give_gift") } }) && !RoutineRules.NeedsAsking(new Routine { Steps = { Step("talk_to") } }), "asks");
        });
        yield return ("routine edits: drop, count, widen", () =>
        {
            var steps = new List<RoutineStep>
            {
                Step("harvest_crops", ("location", "Farm"), ("crop", "(O)24")),
                Step("take_from_chest", ("item", "(O)300"), ("count", 2)),
                Step("tend_machines", ("machine", "(BC)12"), ("refillWith", "(O)304")),
                Step("talk_to", ("npc", "Abigail")),
            };
            var problems = RoutineEdits.Apply(steps, new (string, int, int?)[] { ("widen", 1, null), ("count", 2, 5), ("widen", 3, null), ("count", 4, 3), ("drop", 4, null), ("drop", 9, null) });
            Check(!steps[0].Args.ContainsKey("crop"), "widened");
            Check(steps[1].Args["count"] is 5, "count");
            Check(steps[2].Args.ContainsKey("machine"), "refill keeps the machine");
            Check(steps.Count == 3, "dropped");
            Check(problems.Count == 3, string.Join(" | ", problems));
        });
    }
}
