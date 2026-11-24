using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewPilot.Apprentice.Model;
using StardewPilot.Autopilot;
using StardewPilot.Bridge;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Objects;
using Proposal = StardewPilot.Apprentice.Model.Proposal;

namespace StardewPilot.Apprentice;

/// <summary>
/// Teach by showing: "watch me" records a lesson, the mod proposes a routine from it, the player approves it in the game,
/// and "do my ..." runs it as one job. The model never writes a step: it passes on named edits the mod applies and checks.
/// </summary>
internal sealed class RoutineService
{
    private readonly MemoryStore memory;
    private readonly Func<ModConfig> config;
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly JobRunner runner;
    private readonly SelfPlay selfPlay;
    private readonly Watcher watcher;

    /// <summary>The last proposal end_lesson made, waiting for save_routine.</summary>
    private (string id, Proposal proposal)? proposal;

    /// <summary>Today's runs and how scheduled runs last ended. Loaded with the save, written at once.</summary>
    private RunRecord record = new();

    /// <summary>Raised after the mod writes routines.json, so the scheduler reads it again.</summary>
    public event Action? Changed;

    public RoutineService(MemoryStore memory, Func<ModConfig> config, IModHelper helper, IMonitor monitor, JobRunner runner, SelfPlay selfPlay, Watcher watcher)
    {
        this.memory = memory;
        this.config = config;
        this.helper = helper;
        this.monitor = monitor;
        this.runner = runner;
        this.selfPlay = selfPlay;
        this.watcher = watcher;
    }

    private bool SelfPlaying => this.selfPlay.Enabled || this.config().Autopilot.FullSelfPlay;

    // ------------------------------------------------------------ file

    /// <summary>The saved routines, and a reason when the file could not be read.</summary>
    public (List<Routine> routines, string? problem) Load()
    {
        string? text;
        try { text = this.memory.RoutinesText; }
        catch (Exception ex) { return (new(), $"routines.json could not be read: {ex.Message}"); }
        if (string.IsNullOrWhiteSpace(text))
            return (new(), null);
        try
        {
            var file = JsonConvert.DeserializeObject<JObject>(text);
            var list = new List<Routine>();
            foreach (JObject r in (file?["routines"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
            {
                list.Add(new Routine
                {
                    Name = RoutineRules.CleanName(r.Value<string>("name")),
                    Saved = r.Value<string>("saved") ?? "",
                    Steps = (r["steps"] as JArray)?.OfType<JObject>().Select(s => new RoutineStep
                    {
                        Job = s.Value<string>("job") ?? "",
                        Args = s["args"] is JObject a ? RoutineJson.ToPlain(a) : new(),
                    }).ToList() ?? new(),
                    When = r["when"] is JObject w ? ReadWhen(w) : null,
                });
            }
            return (list, null);
        }
        catch (Exception ex)
        {
            return (new(), $"routines.json is not valid JSON ({ex.Message}). Fix or delete the file.");
        }
    }

    private static RoutineWhen ReadWhen(JObject w) => new()
    {
        At = w.Value<int?>("at") ?? 0,
        Days = (w["days"] as JArray)?.Select(d => RoutineSchedule.Day(d.ToString()) ?? d.ToString()).ToList(),
        Weather = w.Value<string>("weather"),
        Start = w.Value<string>("start") ?? RoutineSchedule.Offer,
        SetOn = w.Value<string>("setOn") ?? "",
    };

    private static JObject WriteWhen(RoutineWhen w)
    {
        var o = new JObject { ["at"] = w.At };
        if (w.Days is { Count: > 0 })
            o["days"] = new JArray(w.Days);
        if (w.Weather != null)
            o["weather"] = w.Weather;
        o["start"] = w.Start;
        if (w.SetOn.Length > 0)
            o["setOn"] = w.SetOn;
        return o;
    }

    private void Save(List<Routine> routines)
    {
        var file = new JObject
        {
            ["note"] = "StardewPilot routines (teach by showing). Hand edits are allowed; every routine is checked again each time it runs.",
            ["routines"] = new JArray(routines.Select(r =>
            {
                var o = new JObject
                {
                    ["name"] = r.Name,
                    ["saved"] = r.Saved,
                    ["steps"] = new JArray(r.Steps.Select(s => new JObject { ["job"] = s.Job, ["args"] = RoutineJson.ToJson(s.Args) })),
                };
                if (r.When != null)
                    o["when"] = WriteWhen(r.When);
                return o;
            })),
        };
        this.memory.WriteRoutines(file.ToString(Formatting.Indented));
        this.Changed?.Invoke();
    }

    private static ArgSpec? SpecOf(string job) => ActionTools.Jobs.TryGetValue(job, out var j) ? RoutineJson.Spec(j.Schema) : null;

    private static bool IsOthersHome(string map) => Game1.getLocationFromName(map) is { } loc && ChestIds.IsOthersHome(loc);

    private static List<string> Check(Routine r) => RoutineRules.Check(r, SpecOf, IsOthersHome);

    /// <summary>Why a routine's schedule is not in effect (empty when it is): the routine's own checks, then the schedule's.</summary>
    public List<string> ScheduleProblems(Routine r)
    {
        if (r.When == null)
            return new();
        List<string> problems = Check(r);
        problems.AddRange(RoutineSchedule.Check(r.When, r, this.config().Autopilot.StopWorkAt));
        return problems;
    }

    // ------------------------------------------------------------ run record

    public RunRecord Record => this.record;

    /// <summary>
    /// Where today's runs are kept: the modData of the player's home, under the player's id. The host holds the locations, so they follow
    /// the save (a day loaded again without sleeping has none) and survive a guest reconnecting. The farmer's own modData did not:
    /// checked in co-op, a reconnecting guest got it back empty.
    /// </summary>
    private static (StardewValley.Mods.ModDataDictionary data, string key) RunsStore()
    {
        GameLocation home = (GameLocation?)Utility.getHomeOfFarmer(Game1.player) ?? Game1.getFarm();
        return (home.modData, $"StardewPilot/RoutineRuns/{Game1.player.UniqueMultiplayerID}");
    }

    /// <summary>
    /// After a save loads or a day starts: today's runs from the player's home (so they follow the save: a day loaded again without sleeping
    /// has none, a guest who reconnects keeps them), and the last stops from routine-runs.json.
    /// </summary>
    public void LoadRecord()
    {
        var rec = new RunRecord();
        try
        {
            if (this.memory.RoutineRunsText is { Length: > 0 } text && JsonConvert.DeserializeObject<JObject>(text) is { } o)
                foreach (var (name, v) in (o["lastStop"] as JObject) ?? new JObject())
                    rec.LastStop[name] = v?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Routines: routine-runs.json could not be read ({ex.Message}); starting a new one.", LogLevel.Warn);
        }
        var (data, key) = RunsStore();
        rec.ReadRuns(data.TryGetValue(key, out string? runs) ? runs : null);
        this.record = rec;
        if (rec.Reconcile(MemoryStore.DateKey()))
            this.SaveRecord();
    }

    public void SaveRecord()
    {
        var (data, key) = RunsStore();
        data[key] = this.record.RunsText();
        var o = new JObject { ["lastStop"] = new JObject(this.record.LastStop.Select(kv => new JProperty(kv.Key, kv.Value))) };
        try { this.memory.WriteRoutineRuns(o.ToString(Formatting.Indented)); }
        catch (Exception ex) { this.monitor.Log($"Routines: could not write routine-runs.json: {ex.Message}", LogLevel.Warn); }
    }

    /// <summary>A routine run ended (from any source): a run that ended by itself is done for today; one stopped from outside can be offered again.</summary>
    public void OnJobFinished(string job, JobResult result)
    {
        if (job != "run_routine" || result.Report.TryGetValue("routine", out object? n) is false || n is not string name)
            return;
        if (this.record.Date != MemoryStore.DateKey())
            this.record.Reconcile(MemoryStore.DateKey());
        int time = this.record.Runs.TryGetValue(name, out RoutineRun? run) ? run.Time : Game1.timeOfDay;
        this.record.Runs[name] = new RoutineRun { Time = time, Done = result.Cause is StopCause.Job or StopCause.EndOfDay or StopCause.Watchdog };
        this.SaveRecord();
    }

    /// <summary>Today's state of a scheduled routine, in words.</summary>
    public string TodayState(Routine r, bool running)
    {
        RoutineWhen w = r.When!;
        string date = MemoryStore.DateKey();
        if (running)
            return "running now";
        if (this.record.RanToday(r.Name))
            return "ran today";
        if (w.SetOn == date)
            return "takes effect tomorrow";
        bool rain = Game1.getFarm().IsRainingHere();
        string weekday = HabitFinder.Weekdays[(Game1.dayOfMonth - 1) % 7];
        if (!RoutineSchedule.Today(w, date, weekday, rain))
            return "not today";
        if (RoutineSchedule.InWindow(w, Game1.timeOfDay))
            return "due now";
        return Time.ToMinutes(Game1.timeOfDay) < Time.ToMinutes(w.At) ? $"due at {Time.Clock(w.At)}" : "its time has passed today";
    }

    /// <summary>A copy whose chest steps name where each chest stands now (a kick moves a chest), for wording only.</summary>
    private static Routine Fresh(Routine r)
    {
        var copy = new Routine { Name = r.Name, Saved = r.Saved, Steps = r.Steps.Select(s => s.Copy()).ToList(), When = r.When?.Copy() };
        foreach (RoutineStep s in copy.Steps)
        {
            if (LessonSteps.Str(s, "chest") is not { } c || c.StartsWith("tile:"))
                continue;
            string id = c.Split('@')[0];
            if (ChestIds.Find(id) is { } hit)
                s.Args["chest"] = $"{id}@{hit.loc.NameOrUniqueName}:{hit.tile.X},{hit.tile.Y}";
        }
        return copy;
    }

    /// <summary>Routine names for the day seed: only those with no step that costs something.</summary>
    public List<string> SeedNames()
    {
        try { return this.Load().routines.Where(r => !RoutineRules.NeedsAsking(r)).Select(r => r.Name).ToList(); }
        catch { return new(); }
    }

    // ------------------------------------------------------------ tools

    public void Register(ToolRegistry registry)
    {
        registry.Add(new ToolDef
        {
            Name = "start_lesson",
            Description =
                "The player said \"watch me\" (or \"let me show you ...\"): start recording what they do by hand, to turn it into a routine. " +
                "Then wait: they play, and say \"done\" or \"that's my ...\" when finished. Jobs they ask for meanwhile become steps too. Refused while self-play is on.",
            InputSchema = Schema.Object(("dropCurrent", Schema.Boolean("Drop a lesson already running or waiting. Only when the player said so."), false)),
            Handler = args => this.StartLesson(args.Bool("dropCurrent") ?? false),
        });
        registry.Add(new ToolDef
        {
            Name = "end_lesson",
            Description =
                "The player finished showing (\"done\", \"that's my truffle run\"): stop recording and get what was seen and the routine the mod proposes. " +
                "Show the player the 'seen' lines and the numbered steps in your own words, say what was left out and why, and ask whether to save it and under what name. " +
                "Then call save_routine with the lessonId and any changes they asked for.",
            Handler = _ => this.EndLesson(),
        });
        registry.Add(new ToolDef
        {
            Name = "save_routine",
            Description =
                "Save the routine proposed by end_lesson under a name, after the player agreed. Changes the player asked for are passed as edits: " +
                "drop a step, change a step's count (taking from a chest, buying), or widen a step to all of its kind (all crops, all forage, all machines, all animal buildings). " +
                "Steps cannot be written or added. The game then shows the final steps and asks the player; it is saved only on their yes.",
            ReadOnly = false,
            InputSchema = Schema.Object(
                ("lessonId", Schema.String("The lessonId end_lesson returned."), true),
                ("name", Schema.String("The routine's name, as the player said it, for example 'truffle run'."), true),
                ("edits", Schema.Array(Schema.Object(
                    ("change", Schema.Enum("What to change.", "drop", "count", "widen"), true),
                    ("step", Schema.Integer("The step's number in the proposal."), true),
                    ("count", Schema.Integer("The new count, for change 'count'."), false)), "Changes the player asked for. Default none."), false)),
            AsyncHandler = async args =>
            {
                Task<JobResult>? job = null;
                object? refused = await MainThread.Run(() => this.PrepareSave(args, out job));
                if (refused != null)
                    return refused;
                JobResult result = await job!;
                return result.ToJson();
            },
        });
        registry.Add(new ToolDef
        {
            Name = "list_routines",
            Description = "The player's saved routines: names and steps in words, and whether each can run now.",
            Handler = _ => this.List(),
        });
        registry.Add(new ToolDef
        {
            Name = "forget_routine",
            Description = "Delete a saved routine, when the player asks.",
            ReadOnly = false,
            InputSchema = Schema.Object(("name", Schema.String("The routine's name."), true)),
            Handler = args => this.Forget(args.Str("name") ?? ""),
        });
        registry.Add(new ToolDef
        {
            Name = "schedule_routine",
            Description =
                "Give one of the player's routines a schedule, when they ask (\"do my truffle run every morning\", \"offer my town run at 4pm on Fridays\"). " +
                "When it is due, the game shows an offer and a key starts it; with start 'self', a 6am routine starts by itself if the player touches nothing after waking " +
                "(any key keeps the farmer). 'self' only for 6am and for routines that do not buy, ship or give gifts. The game asks the player to confirm; it takes effect the next day. " +
                "One schedule per routine: a new one replaces the old.",
            ReadOnly = false,
            InputSchema = Schema.Object(
                ("name", Schema.String("The routine's name."), true),
                ("at", Schema.String("Time of day as the player said it: '6am' (the morning), '4pm', '9:30am'."), true),
                ("days", Schema.Array(Schema.String("Mon, Tue, Wed, Thu, Fri, Sat or Sun."), "Only on these weekdays. Default every day."), false),
                ("weather", Schema.Enum("Only on rainy or dry days. Default any weather.", "rain", "dry"), false),
                ("start", Schema.Enum("'offer' (default): shown with a key to start. 'self': starts by itself at 6am when the player leaves the farmer alone. Only when the player asked for 'by itself'.", "offer", "self"), false)),
            AsyncHandler = async args =>
            {
                Task<JobResult>? job = null;
                await MainThread.Run(() => this.PrepareSchedule(args, out job));
                return (await job!).ToJson();
            },
        });
        registry.Add(new ToolDef
        {
            Name = "unschedule_routine",
            Description = "Remove a routine's schedule (\"stop doing my truffle run every morning\"). The routine stays and can still be run when asked.",
            ReadOnly = false,
            InputSchema = Schema.Object(("name", Schema.String("The routine's name."), true)),
            Handler = args => this.Unschedule(args.Str("name") ?? ""),
        });
        registry.Add(new ToolDef
        {
            Name = "run_routine",
            Description =
                "Run one of the player's routines (\"do my truffle run\") as one job: its steps one after another. The first step that fails, is only partly done, " +
                "or that the player says no to ends the routine; the report says which steps ran. Buying, shipping and gifts in a routine always ask the player. " +
                "Run a routine only when the player asks for it.",
            ReadOnly = false,
            InputSchema = Schema.Object(("name", Schema.String("The routine's name."), true)),
            AsyncHandler = async args =>
            {
                string name = args.Str("name") ?? "";
                Task<JobResult> job = await MainThread.Run(() => this.runner.Start("run_routine", ctx => this.Run(ctx, name), args));
                return (await job).ToJson();
            },
        });
    }

    // ------------------------------------------------------------ lessons

    private object StartLesson(bool dropCurrent)
    {
        if (this.selfPlay.Enabled)
            throw new ToolError("Self-play is on. A lesson records the player's own play: switch self-play off first.");
        LessonRecorder lesson = this.watcher.Lesson;
        if (lesson.HasLesson && !dropCurrent)
            throw new ToolError(lesson.Active
                ? "A lesson is already running. Ask the player whether to drop it; if so, call again with dropCurrent."
                : "A finished lesson is waiting to be shown (end_lesson). Ask the player whether to drop it; if so, call again with dropCurrent.");
        lesson.Drop();
        this.watcher.StartLesson();
        this.proposal = null;
        return new
        {
            started = true,
            note = "Recording. Tell the player to go ahead and say \"done\" when finished. It ends by itself at the end of the day, at a festival or cutscene, or after six game hours.",
        };
    }

    private object EndLesson()
    {
        LessonRecorder lesson = this.watcher.Lesson;
        if (!lesson.HasLesson)
            throw new ToolError("There is no lesson. Start one with start_lesson when the player says \"watch me\".");
        string? because = lesson.EndedBecause;
        List<LessonEvent> events = this.watcher.EndLesson();
        Proposal p = LessonSteps.Make(events);
        string id = Guid.NewGuid().ToString("N").Substring(0, 8);
        this.proposal = (id, p);
        Names names = ApprenticeService.GameNames();
        return new
        {
            lessonId = id,
            endedBecause = because,
            seen = events.Where(e => e.Act != LessonAct.Left).Select(e => SeenLine(e, names)).ToList(),
            steps = p.Steps.Select((s, i) => $"{i + 1}. {LessonSteps.Words(s, names, p.LeftReady)}").ToList(),
            leftToYou = p.LeftToYou.Count > 0 ? p.LeftToYou : null,
            note = p.Steps.Count == 0
                ? "Nothing seen became a step. Tell the player what was seen and why."
                : "Computed by the mod from what changed next to the farmer; harvests, machines, forage and chests can be misread, so the player should check the steps. " +
                  "Each step does all of the kinds the player touched; a step can be widened to all of its kind, dropped, or (taking, buying) given another count.",
        };
    }

    /// <summary>One thing seen, in words.</summary>
    private static string SeenLine(LessonEvent e, Names names)
    {
        string item = e.Item.Length > 0 ? Names.Clean(names.Item(e.Item)) : "";
        string where = names.Map(e.Map);
        string on = LessonSteps.On(where);
        string count = e.Count > 1 ? $" x{e.Count}" : "";
        return $"{Time.Format(e.Time)} " + e.Act switch
        {
            Act.Harvest => $"harvested {item}{count} {on}",
            Act.Forage => $"picked {item}{count} {on}",
            Act.Animals => e.Item.Length > 0 ? $"gathered {item}{count} {on}" : $"tended animals {on} ({e.Count})",
            Act.Machines => $"collected from {Names.Clean(names.Item(e.Detail))}{count} {on}",
            LessonAct.Load => $"loaded {Names.Clean(names.Item(e.Detail))} with {item}{count} {on}",
            Act.Store => $"stored {item}{count} in " + (e.Detail == "junimo" ? "the Junimo chest" : $"the chest at ({e.Detail}) {on}"),
            Act.Fetch => $"took {item}{count} from " + (e.Detail == "junimo" ? "the Junimo chest" : $"the chest at ({e.Detail}) {on}"),
            Act.Talk => $"talked to {Names.Clean(names.Npc(e.Detail))}",
            Act.Gift => $"gave {item} to {Names.Clean(names.Npc(e.Detail))}",
            Act.Water => $"watered {on} ({e.Count} swings)",
            Act.Buy => $"bought {item}{count}",
            Act.Sell => $"sold {item}{count}",
            LessonAct.Job => $"you asked for {e.Job}" + (e.Failed ? " (it failed)" : ""),
            _ => $"{Act.Word(e.Act)} {on}{(e.Count > 1 ? $" ({e.Count})" : "")}",
        };
    }

    // ------------------------------------------------------------ saving

    /// <summary>Apply the edits, give the chests ids, check, and start the job that asks the player. Returns a refusal, or null with the job started.</summary>
    private object? PrepareSave(JObject args, out Task<JobResult>? job)
    {
        job = null;
        string lessonId = args.Str("lessonId") ?? "";
        if (this.proposal is not { } p || p.id != lessonId)
            throw new ToolError("That lesson is not waiting to be saved (only the last end_lesson can be). Start a new lesson.");
        string name = RoutineRules.CleanName(args.Str("name"));
        if (name.Length == 0)
            throw new ToolError("The name must have letters or digits.");

        var steps = p.proposal.Steps.Select(s => s.Copy()).ToList();
        var edits = ((args["edits"] as JArray) ?? new JArray()).OfType<JObject>()
            .Select(e => (e.Str("change") ?? "", e.Int("step") ?? 0, e.Int("count"))).ToList();
        if (RoutineEdits.Apply(steps, edits) is { Count: > 0 } editProblems)
            throw new ToolError("Some changes could not be made: " + string.Join(" ", editProblems));

        // Chests are named by an id stamped into the chest, so a kicked chest is still found and a new chest on the tile is not.
        foreach (RoutineStep s in steps)
        {
            if (LessonSteps.Str(s, "chest") is not { } c || !c.StartsWith("tile:"))
                continue;
            string place = c.Substring(5);
            int colon = place.LastIndexOf(':');
            string map = place.Substring(0, colon);
            string[] xy = place.Substring(colon + 1).Split(',');
            Chest? box = Game1.getLocationFromName(map) is { } loc && int.TryParse(xy[0], out int x) && int.TryParse(xy[1], out int y)
                ? ChestIds.At(loc, new Point(x, y)) : null;
            if (box == null)
                throw new ToolError($"The chest at ({place.Substring(colon + 1)}) in {ApprenticeService.AgentNames().Map(map)} is gone. Drop that step or teach it again.");
            s.Args["chest"] = $"{ChestIds.Stamp(box)}@{place}";
        }

        var routine = new Routine { Name = name, Saved = MemoryStore.DateKey(), Steps = steps };
        if (Check(routine) is { Count: > 0 } problems)
            throw new ToolError("The routine cannot be saved: " + string.Join(" ", problems));
        var (all, loadProblem) = this.Load();
        if (loadProblem != null)
            throw new ToolError(loadProblem);
        bool replaces = all.Any(r => r.Name == name);

        // Teaching a routine again keeps its schedule; new steps that cost something can only be offered.
        if (all.FirstOrDefault(r => r.Name == name)?.When is { } oldWhen)
        {
            routine.When = oldWhen.Copy();
            if (routine.When.Start == RoutineSchedule.Self && RoutineRules.NeedsAsking(routine))
                routine.When.Start = RoutineSchedule.Offer;
        }
        if (!replaces && all.Count >= RoutineRules.MaxRoutines)
            throw new ToolError($"There are already {RoutineRules.MaxRoutines} routines. Ask the player which one to forget.");

        Names names = ApprenticeService.GameNames();
        var words = steps.Select((s, i) => $"{i + 1}. {LessonSteps.Words(s, names)}").ToList();
        string head = !replaces ? I18n.Get("routine.proposal", new { name })
            : routine.When != null ? I18n.Get("routine.proposal-replaces-kept", new { name, when = RoutineSchedule.Sentence(routine.When) })
            : I18n.Get("routine.proposal-replaces", new { name });
        ChatLog.Add(ChatSender.System, head + "\n" + string.Join("\n", words));
        job = this.runner.Start("save_routine", ctx => this.AskAndSave(ctx, routine, words, replaces));
        return null;
    }

    private IEnumerable AskAndSave(JobContext ctx, Routine routine, List<string> words, bool replaces)
    {
        ctx.AlwaysAsk = true;
        string list = string.Join("; ", words);
        if (list.Length > 300)
            list = list.Substring(0, 297) + "...";
        bool yes = false;
        string question = routine.When != null
            ? I18n.Get(replaces ? "ask.replace-routine-when" : "ask.save-routine-when", new { name = routine.Name, when = RoutineSchedule.Sentence(routine.When), steps = list })
            : I18n.Get(replaces ? "ask.replace-routine" : "ask.save-routine", new { name = routine.Name, steps = list });
        foreach (object? s in Confirm.Ask(ctx, this.config(), this.helper, Permission.SaveRoutine, question, a => yes = a))
            yield return s;
        if (!yes)
            throw JobContext.Fail(I18n.Get("fail.said-no-routine"));
        var (all, problem) = this.Load();
        if (problem != null)
            throw JobContext.Fail(problem);
        all.RemoveAll(r => r.Name == routine.Name);
        all.Add(routine);
        this.Save(all);
        this.proposal = null;
        ctx.Report["saved"] = routine.Name;
        ctx.Report["steps"] = words;
        ctx.Report["note"] = $"Saved at once (it does not wait for the night). The player can say \"do my {routine.Name}\".";
        this.monitor.Log($"Routines: saved \"{routine.Name}\" with {routine.Steps.Count} steps.", LogLevel.Debug);
    }

    // ------------------------------------------------------------ schedules

    /// <summary>Check the schedule and start the job that asks the player. Throws a refusal.</summary>
    private object? PrepareSchedule(JObject args, out Task<JobResult>? job)
    {
        job = null;
        var (all, problem) = this.Load();
        if (problem != null)
            throw new ToolError(problem);
        string name = RoutineRules.CleanName(args.Str("name"));
        Routine r = all.FirstOrDefault(x => x.Name == name)
            ?? throw new ToolError($"There is no routine called \"{name}\". Saved: {(all.Count > 0 ? string.Join(", ", all.Select(x => x.Name)) : "none")}.");
        int at = RoutineSchedule.ParseTime(args.Str("at")) ?? throw new ToolError($"'{args.Str("at")}' is not a time of day. Use for example '6am', '4pm' or '9:30am'.");
        var days = new List<string>();
        foreach (string d in ((args["days"] as JArray) ?? new JArray()).Select(t => t.ToString()))
            days.Add(RoutineSchedule.Day(d) ?? throw new ToolError($"'{d}' is not a day of the week."));
        var when = new RoutineWhen
        {
            At = at,
            Days = days.Count is > 0 and < 7 ? RoutineSchedule.Weekdays.Where(days.Contains).ToList() : null,
            Weather = args.Str("weather"),
            Start = args.Str("start") ?? RoutineSchedule.Offer,
            SetOn = MemoryStore.DateKey(),
        };
        if (RoutineSchedule.Check(when, r, this.config().Autopilot.StopWorkAt) is { Count: > 0 } wrong)
            throw new ToolError("That schedule cannot be set: " + string.Join(" ", wrong));
        if (Check(r) is { Count: > 0 } broken)
            throw new ToolError($"The routine \"{r.Name}\" cannot run, so it cannot have a schedule: {string.Join(" ", broken)}");

        string key = this.config().Routines.StartKey.ToString();
        string question = when.Start == RoutineSchedule.Self
            ? I18n.Get("ask.schedule-self", new { name = r.Name, when = RoutineSchedule.Words(when) })
            : I18n.Get("ask.schedule-offer", new { name = r.Name, when = RoutineSchedule.Words(when), key });
        if (r.When != null)
            question = I18n.Get("ask.schedule-change", new { now = RoutineSchedule.Sentence(r.When), question });
        job = this.runner.Start("schedule_routine", ctx => this.AskAndSchedule(ctx, r.Name, when, question));
        return null;
    }

    private IEnumerable AskAndSchedule(JobContext ctx, string name, RoutineWhen when, string question)
    {
        ctx.AlwaysAsk = true;
        bool yes = false;
        foreach (object? s in Confirm.Ask(ctx, this.config(), this.helper, Permission.SaveRoutine, question, a => yes = a))
            yield return s;
        if (!yes)
            throw JobContext.Fail(I18n.Get("fail.said-no-schedule"));
        var (all, problem) = this.Load();
        if (problem != null)
            throw JobContext.Fail(problem);
        Routine r = all.FirstOrDefault(x => x.Name == name) ?? throw JobContext.Fail(I18n.Get("fail.routine-gone", new { name }));
        r.When = when;
        this.Save(all);
        ctx.Report["scheduled"] = name;
        ctx.Report["when"] = RoutineSchedule.Sentence(when);
        ctx.Report["note"] = "Takes effect tomorrow. The player can say \"stop doing my " + name + " " + RoutineSchedule.Words(when) + "\" to remove it, or switch Routines.StartThemselves off in the config.";
        this.monitor.Log($"Routines: \"{name}\" {RoutineSchedule.Sentence(when)}.", LogLevel.Debug);
    }

    private object Unschedule(string name)
    {
        string clean = RoutineRules.CleanName(name);
        var (all, problem) = this.Load();
        if (problem != null)
            throw new ToolError(problem);
        Routine r = all.FirstOrDefault(x => x.Name == clean) ?? throw new ToolError($"There is no routine called \"{clean}\".");
        if (r.When == null)
            throw new ToolError($"The routine \"{clean}\" has no schedule.");
        string was = RoutineSchedule.Sentence(r.When);
        r.When = null;
        this.Save(all);
        return new { unscheduled = clean, was, note = "The routine stays; it runs only when the player asks." };
    }

    // ------------------------------------------------------------ listing

    private object List()
    {
        var (all, problem) = this.Load();
        Names names = ApprenticeService.GameNames();
        return new
        {
            problem,
            routines = all.Select(Fresh).Select(r => new
            {
                name = r.Name,
                saved = r.Saved,
                steps = r.Steps.Select((s, i) => $"{i + 1}. {LessonSteps.Words(s, names)}"),
                asksEachTime = RoutineRules.NeedsAsking(r),
                cannotRun = Check(r) is { Count: > 0 } p ? p : null,
                schedule = r.When == null ? null : this.ScheduleProblems(r) is { Count: > 0 } sp
                    ? new { when = RoutineSchedule.Sentence(r.When), today = "not in effect", notInEffect = (List<string>?)sp }
                    : new { when = RoutineSchedule.Sentence(r.When), today = this.TodayState(r, this.runner.CurrentJob == "run_routine" && this.Running == r.Name), notInEffect = (List<string>?)null },
            }),
            note = all.Count == 0 ? "No routines yet. The player teaches one by saying \"watch me\"." : null,
        };
    }

    private object Forget(string name)
    {
        string clean = RoutineRules.CleanName(name);
        var (all, problem) = this.Load();
        if (problem != null)
            throw new ToolError(problem);
        if (all.RemoveAll(r => r.Name == clean) == 0)
            throw new ToolError($"There is no routine called \"{clean}\".");
        this.Save(all);
        if (this.record.LastStop.Remove(clean))
            this.SaveRecord();
        return new { forgotten = clean, note = "Its schedule, if it had one, went with it." };
    }

    // ------------------------------------------------------------ running

    /// <summary>The routine running now, while run_routine runs.</summary>
    public string? Running { get; private set; }

    /// <summary>Run a routine as one job. <paramref name="byItself"/>: started by its schedule with nobody asking, so no step may ask.</summary>
    public IEnumerable Run(JobContext ctx, string name, bool byItself = false)
    {
        // Unwatched and repeated: shipping, buying and gifting ask every time, whatever the standing permissions say.
        ctx.AlwaysAsk = true;
        ctx.NoQuestions = byItself;
        this.Running = null;
        var (all, problem) = this.Load();
        if (problem != null)
            throw JobContext.Fail(problem);
        string clean = RoutineRules.CleanName(name);
        Routine r = all.FirstOrDefault(x => x.Name == clean)
            ?? throw JobContext.Fail(I18n.Get("fail.no-routine", new { name = clean, saved = all.Count > 0 ? string.Join(", ", all.Select(x => x.Name)) : "-" }));
        if (Check(r) is { Count: > 0 } problems)
            throw JobContext.Fail(I18n.Get("fail.routine-broken", new { name = r.Name, problems = string.Join(" ", problems) }));
        int stopAt = this.config().Autopilot.StopWorkAt;
        if (Game1.timeOfDay >= stopAt - 100)
            throw JobContext.Fail(I18n.Get("fail.routine-too-late", new { time = Time.Say(stopAt) }));
        if (this.SelfPlaying && RoutineRules.NeedsAsking(r))
            throw JobContext.Fail(I18n.Get("fail.routine-self-play"));

        Names names = ApprenticeService.GameNames();
        Routine words0 = Fresh(r);
        var lines = new List<object>();
        ctx.Report["routine"] = r.Name;
        this.Running = r.Name;

        // Any run counts for today, whoever started it: a scheduled routine is not offered again after "do my ..." in chat.
        if (this.record.Date != MemoryStore.DateKey())
            this.record.Reconcile(MemoryStore.DateKey());
        this.record.Runs[r.Name] = new RoutineRun { Time = Game1.timeOfDay, Done = false };
        this.SaveRecord();
        ctx.Report["steps"] = lines;
        for (int i = 0; i < r.Steps.Count; i++)
        {
            RoutineStep step = r.Steps[i];
            string words = LessonSteps.Words(words0.Steps[i], names);
            if (this.SelfPlaying && RoutineRules.Asks.Contains(step.Job))
                throw this.StopAt(ctx, r, i, words, I18n.Get("fail.step-self-play"), JobOutcome.Partial, names);
            var build = ActionTools.Jobs[step.Job].Build(RoutineJson.ToJson(step.Args));
            ctx.Progress($"routine {r.Name}, step {i + 1} of {r.Steps.Count}: {words}");
            JobResult? result = null;
            // Kept only while the step runs: when the routine is stopped from outside, the report says where.
            ctx.Report["stoppedDuring"] = $"step {i + 1}: {words}";
            foreach (object? s in StepRunner.Run(ctx, step.Job, build, res => result = res))
                yield return s;
            ctx.Report.Remove("stoppedDuring");
            this.runner.ReportStep(step.Job, result!);
            lines.Add(new
            {
                step = i + 1,
                does = words,
                outcome = result!.Outcome.ToString().ToLowerInvariant(),
                summary = result.Summary,
                report = result.Report,
                problems = result.Problems.Count > 0 ? result.Problems : null,
            });
            if (result.Outcome != JobOutcome.Done)
                throw this.StopAt(ctx, r, i, words, result.Summary, i == 0 ? JobOutcome.Failed : JobOutcome.Partial, names, ran: true);
        }
    }

    /// <summary>End the routine at a step: a later step would act on the wrong state (a failed fetch leaves nothing to load).</summary>
    private JobStop StopAt(JobContext ctx, Routine r, int i, string words, string why, JobOutcome outcome, Names names, bool ran = false)
    {
        int from = ran ? i + 1 : i;
        ctx.Report["stoppedAt"] = I18n.Get("routine.step-why", new { step = i + 1, words, why });
        if (from < r.Steps.Count)
            ctx.Report["notRun"] = Fresh(r).Steps.Skip(from).Select((s, k) => $"{from + k + 1}. {LessonSteps.Words(s, names)}").ToList();
        return new JobStop(outcome, I18n.Get("fail.routine-stopped-at", new { name = r.Name, step = i + 1, words, why }));
    }

    // ------------------------------------------------------------ screen

    /// <summary>"StardewPilot is watching (lesson)", while a lesson records.</summary>
    public void DrawLessonLine(SpriteBatch b)
    {
        if (!this.watcher.Lesson.Active || Game1.activeClickableMenu is ChatOverlayMenu || Game1.eventUp)
            return;
        string text = I18n.Get("lesson.watching");
        text = Guide.Toast.WrapClearOfClock(text);
        Vector2 size = Game1.smallFont.MeasureString(text);
        int w = (int)size.X + 32, h = (int)size.Y + 24;
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), 8, 8, w, h, Color.White, drawShadow: false);
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(24, 20), Game1.textColor);
    }
}
