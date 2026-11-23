using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewPilot.Guide;
using StardewValley;
using StardewValley.Menus;

namespace StardewPilot.Autopilot;

/// <summary>How a job ended. Matches the design: done / partly done with reason / failed with reason.</summary>
internal enum JobOutcome
{
    Done,
    Partial,
    Failed,
}

/// <summary>Why a job ended: by itself, or stopped from outside it.</summary>
internal enum StopCause
{
    /// <summary>The job ended by itself: done, or it threw (failed, partly done, internal error).</summary>
    Job,

    /// <summary>The player took control or switched the autopilot off.</summary>
    Player,

    /// <summary>A menu, cutscene or festival took the screen, or the save closed.</summary>
    Screen,

    /// <summary>A new job replaced it, or the agent stopped it.</summary>
    Replaced,

    /// <summary>The work stop time.</summary>
    EndOfDay,

    /// <summary>No progress for too long.</summary>
    Watchdog,
}

/// <summary>Thrown inside a routine to end the job early.</summary>
internal sealed class JobStop : Exception
{
    public JobStop(JobOutcome outcome, string reason) : base(reason) => this.Outcome = outcome;
    public JobOutcome Outcome { get; }

    /// <summary>No way through to a tile: the route planner tries another way instead of failing.</summary>
    public bool NoPath { get; init; }
}

/// <summary>Wait a number of game ticks (60 per second).</summary>
internal sealed record WaitTicks(int Ticks);

/// <summary>What a running job can report and ask for.</summary>
internal sealed class JobContext
{
    public JobContext(string name, JobContext? parent = null)
    {
        this.Name = name;
        this.parent = parent;
    }

    /// <summary>The routine a step runs in. Progress, menus and the always-ask flag belong to the routine, so the runner sees them.</summary>
    private readonly JobContext? parent;

    public string Name { get; }

    /// <summary>The arguments the job was called with, when it came from a tool call.</summary>
    public JObject? Args { get; init; }

    /// <summary>Facts for the agent: counts, items bought, and so on.</summary>
    public Dictionary<string, object?> Report { get; } = new();

    /// <summary>Notes on what could not be done, for a partial result.</summary>
    public List<string> Problems { get; } = new();

    private bool expectingMenu;

    /// <summary>Set while the job itself has a menu or dialogue open (shop, confirmation, sleep question).</summary>
    public bool ExpectingMenu
    {
        get => this.parent?.ExpectingMenu ?? this.expectingMenu;
        set { if (this.parent != null) this.parent.ExpectingMenu = value; else this.expectingMenu = value; }
    }

    private bool alwaysAsk;

    private bool noQuestions;

    /// <summary>Started by its schedule with nobody asking: a question would open unasked, so a step that would ask fails instead.</summary>
    public bool NoQuestions
    {
        get => this.parent?.NoQuestions ?? this.noQuestions;
        set { if (this.parent != null) this.parent.NoQuestions = value; else this.noQuestions = value; }
    }

    /// <summary>Ask before shipping, spending and gifting whatever the standing permissions say (routines run unwatched).</summary>
    public bool AlwaysAsk
    {
        get => this.parent?.AlwaysAsk ?? this.alwaysAsk;
        set { if (this.parent != null) this.parent.AlwaysAsk = value; else this.alwaysAsk = value; }
    }

    private DateTime lastProgress = DateTime.UtcNow;

    /// <summary>Last real-time moment the job made progress. The watchdog stops jobs that stall.</summary>
    public DateTime LastProgress => this.parent?.LastProgress ?? this.lastProgress;

    private string activity = "";

    public string Activity
    {
        get => this.parent?.Activity ?? this.activity;
        set { if (this.parent != null) this.parent.Activity = value; else this.activity = value; }
    }

    public void Progress(string? activity = null)
    {
        this.Touch();
        if (activity != null)
            this.Activity = activity;
    }

    /// <summary>Debug trace with a timestamp (SMAPI log, trace level).</summary>
    public static Action<string>? Trace;

    public void Log(string message) => Trace?.Invoke($"[{this.Name}] {message}");

    /// <summary>Hold the watchdog off, for waits the job expects (confirmations, menus).</summary>
    public void Touch()
    {
        if (this.parent != null) this.parent.Touch();
        else this.lastProgress = DateTime.UtcNow;
    }

    public static JobStop Fail(string reason) => new(JobOutcome.Failed, reason);

    /// <summary>No way to a tile (see <see cref="JobStop.NoPath"/>).</summary>
    public static JobStop NoPath(string reason) => new(JobOutcome.Failed, reason) { NoPath = true };

    public static JobStop Partial(string reason) => new(JobOutcome.Partial, reason);
}

internal sealed record JobResult(JobOutcome Outcome, string Summary, Dictionary<string, object?> Report, List<string> Problems)
{
    /// <summary>The arguments the job was called with, when known.</summary>
    public JObject? Args { get; init; }

    /// <summary>Why it ended.</summary>
    public StopCause Cause { get; init; } = StopCause.Job;

    public object ToJson() => new
    {
        result = this.Outcome switch { JobOutcome.Done => "done", JobOutcome.Partial => "partly done", _ => "failed" },
        summary = this.Summary,
        details = this.Report.Count > 0 ? this.Report : null,
        problems = this.Problems.Count > 0 ? this.Problems : null,
    };
}

/// <summary>
/// Runs one autopilot job at a time on the game's main thread. A job is a coroutine:
/// it yields null to wait a tick, a <see cref="WaitTicks"/>, or a nested routine to run first.
/// </summary>
internal sealed class JobRunner
{
    private readonly IMonitor monitor;
    private readonly Func<ModConfig> config;
    private readonly Stack<IEnumerator> stack = new();
    private JobContext? ctx;
    private TaskCompletionSource<JobResult>? completion;
    private int waitTicks;
    private int startTime;

    public JobRunner(IMonitor monitor, Func<ModConfig> config)
    {
        this.monitor = monitor;
        this.config = config;
        JobContext.Trace = msg => monitor.Log($"{DateTime.Now:HH:mm:ss.fff} {msg}", LogLevel.Trace);
    }

    /// <summary>The player can switch the autopilot off with a hotkey.</summary>
    public bool Enabled { get; set; } = true;

    public bool IsRunning => this.ctx != null;

    public string? CurrentJob => this.ctx?.Name;

    public string? CurrentActivity => this.ctx?.Activity;

    /// <summary>Raised on the main thread when a job ends.</summary>
    public event Action<string, JobResult>? Finished;

    /// <summary>Raised when a job starts, before its first step runs (a job can start and finish within one tick).</summary>
    public event Action<string>? Started;

    /// <summary>Raised on the main thread when one step of a routine ends, so the watcher and the self-play log see each step's own job.</summary>
    public event Action<string, JobResult>? StepFinished;

    public void ReportStep(string name, JobResult result) => this.StepFinished?.Invoke(name, result);

    /// <summary>Start a job. Must be called on the main thread.</summary>
    public Task<JobResult> Start(string name, Func<JobContext, IEnumerable> routine, JObject? args = null)
    {
        if (!this.Enabled)
            return Task.FromResult(new JobResult(JobOutcome.Failed, "The player has switched the autopilot off (hotkey). Ask them to switch it on, or tell them what to do instead.", new(), new()));
        if (this.ctx != null)
            this.Stop(JobOutcome.Failed, I18n.Get("stop.replaced", new { job = name }), StopCause.Replaced);

        this.ctx = new JobContext(name) { Args = args };
        this.completion = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.stack.Clear();
        this.stack.Push(routine(this.ctx).GetEnumerator());
        this.waitTicks = 0;
        this.startTime = Game1.timeOfDay;
        this.monitor.Log($"Autopilot: starting {name}.", LogLevel.Debug);
        Task<JobResult> task = this.completion.Task;
        this.Started?.Invoke(name);
        return task;
    }

    /// <summary>End the running job, for example because the player took control.</summary>
    public void Stop(JobOutcome outcome, string reason, StopCause cause = StopCause.Job)
    {
        if (this.ctx == null)
            return;
        Game1.player.controller = null;
        Game1.player.Halt();
        if (this.ctx.ExpectingMenu && Game1.activeClickableMenu is not null and not ChatOverlayMenu)
            Game1.exitActiveMenu();
        this.Finish(outcome, reason, cause);
    }

    /// <summary>Called every update tick.</summary>
    public void Update()
    {
        if (this.ctx == null)
            return;
        if (!Context.IsWorldReady)
        {
            this.Finish(JobOutcome.Failed, I18n.Get("stop.save-closed"), StopCause.Screen);
            return;
        }

        // Hand control back if something the job did not start takes over the screen.
        if (Game1.eventUp || Game1.isFestival())
        {
            this.Stop(JobOutcome.Partial, I18n.Get("stop.cutscene"), StopCause.Screen);
            return;
        }
        IClickableMenu? menu = Game1.activeClickableMenu;
        if (menu is ChatOverlayMenu)
        {
            this.ctx.Touch(); // the player is chatting with the copilot; wait without counting it as a stall
            return;
        }
        if (menu != null && !this.ctx.ExpectingMenu)
        {
            this.Stop(JobOutcome.Partial, I18n.Get("stop.menu"), StopCause.Screen);
            this.monitor.Log($"Autopilot: a {menu.GetType().Name} opened during {this.ctx?.Name}.", LogLevel.Debug);
            return;
        }

        // Watchdog: a job that makes no progress for a while is stuck.
        if ((DateTime.UtcNow - this.ctx.LastProgress).TotalSeconds > this.config().Autopilot.WatchdogSeconds)
        {
            this.monitor.Log($"Autopilot: no progress while {this.ctx.Activity}.", LogLevel.Debug);
            this.Stop(JobOutcome.Failed, I18n.Get("stop.stuck", new { seconds = this.config().Autopilot.WatchdogSeconds }), StopCause.Watchdog);
            return;
        }

        if (this.waitTicks > 0)
        {
            this.waitTicks--;
            return;
        }

        try
        {
            // Run until the routine waits. Bounded so a busy loop cannot freeze the game.
            for (int steps = 0; steps < 200 && this.ctx != null && this.stack.Count > 0; steps++)
            {
                IEnumerator top = this.stack.Peek();
                if (!top.MoveNext())
                {
                    this.stack.Pop();
                    if (this.stack.Count == 0)
                    {
                        this.Finish(this.ctx.Problems.Count > 0 ? JobOutcome.Partial : JobOutcome.Done, "");
                        return;
                    }
                    continue;
                }
                switch (top.Current)
                {
                    case null:
                        return; // wait one tick
                    case WaitTicks w:
                        this.waitTicks = Math.Max(0, w.Ticks - 1);
                        return;
                    case IEnumerable nested:
                        this.stack.Push(nested.GetEnumerator());
                        continue;
                    default:
                        return;
                }
            }
        }
        catch (JobStop stop)
        {
            this.Stop(stop.Outcome, stop.Message);
        }
        catch (Exception ex)
        {
            this.monitor.Log($"Autopilot job {this.ctx?.Name} crashed: {ex}", LogLevel.Error);
            this.Stop(JobOutcome.Failed, I18n.Get("stop.internal-error", new { error = ex.Message }));
        }
    }

    private void Finish(JobOutcome outcome, string reason, StopCause cause = StopCause.Job)
    {
        JobContext? done = this.ctx;
        TaskCompletionSource<JobResult>? tcs = this.completion;
        InputSim.ReleaseAll(); // never leave a simulated button held
        Actions.ReleaseFarmer(); // never leave the farmer frozen after a job
        this.ctx = null;
        this.completion = null;
        this.stack.Clear();
        if (done == null)
            return;

        int minutes = Utility.CalculateMinutesBetweenTimes(this.startTime, Game1.timeOfDay);
        done.Report["gameMinutesUsed"] = minutes;
        string summary = reason.Length > 0
            ? reason
            : outcome == JobOutcome.Done ? I18n.Get("job.finished", new { job = done.Name }) : I18n.Get("job.finished-problems", new { job = done.Name });
        var result = new JobResult(outcome, summary, done.Report, done.Problems) { Args = done.Args, Cause = cause };
        this.monitor.Log($"Autopilot: {done.Name} -> {outcome}: {summary}", LogLevel.Debug);
        this.Finished?.Invoke(done.Name, result);
        tcs?.TrySetResult(result);
    }
}
