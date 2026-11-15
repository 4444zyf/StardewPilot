using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
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

/// <summary>Thrown inside a routine to end the job early.</summary>
internal sealed class JobStop : Exception
{
    public JobStop(JobOutcome outcome, string reason) : base(reason) => this.Outcome = outcome;
    public JobOutcome Outcome { get; }
}

/// <summary>Wait a number of game ticks (60 per second).</summary>
internal sealed record WaitTicks(int Ticks);

/// <summary>What a running job can report and ask for.</summary>
internal sealed class JobContext
{
    public JobContext(string name) => this.Name = name;

    public string Name { get; }

    /// <summary>Facts for the agent: counts, items bought, and so on.</summary>
    public Dictionary<string, object?> Report { get; } = new();

    /// <summary>Notes on what could not be done, for a partial result.</summary>
    public List<string> Problems { get; } = new();

    /// <summary>Set while the job itself has a menu or dialogue open (shop, confirmation, sleep question).</summary>
    public bool ExpectingMenu { get; set; }

    /// <summary>Last real-time moment the job made progress. The watchdog stops jobs that stall.</summary>
    public DateTime LastProgress { get; private set; } = DateTime.UtcNow;

    public string Activity { get; set; } = "";

    public void Progress(string? activity = null)
    {
        this.LastProgress = DateTime.UtcNow;
        if (activity != null)
            this.Activity = activity;
    }

    /// <summary>Debug trace with a timestamp (SMAPI log, trace level).</summary>
    public static Action<string>? Trace;

    public void Log(string message) => Trace?.Invoke($"[{this.Name}] {message}");

    /// <summary>Hold the watchdog off, for waits the job expects (confirmations, menus).</summary>
    public void Touch() => this.LastProgress = DateTime.UtcNow;

    public static JobStop Fail(string reason) => new(JobOutcome.Failed, reason);

    public static JobStop Partial(string reason) => new(JobOutcome.Partial, reason);
}

internal sealed record JobResult(JobOutcome Outcome, string Summary, Dictionary<string, object?> Report, List<string> Problems)
{
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

    /// <summary>Start a job. Must be called on the main thread.</summary>
    public Task<JobResult> Start(string name, Func<JobContext, IEnumerable> routine)
    {
        if (!this.Enabled)
            return Task.FromResult(new JobResult(JobOutcome.Failed, "The player has switched the autopilot off (hotkey). Ask them to switch it on, or tell them what to do instead.", new(), new()));
        if (this.ctx != null)
            this.Stop(JobOutcome.Failed, $"Replaced by a new job: {name}.");

        this.ctx = new JobContext(name);
        this.completion = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        this.stack.Clear();
        this.stack.Push(routine(this.ctx).GetEnumerator());
        this.waitTicks = 0;
        this.startTime = Game1.timeOfDay;
        this.monitor.Log($"Autopilot: starting {name}.", LogLevel.Debug);
        return this.completion.Task;
    }

    /// <summary>End the running job, for example because the player took control.</summary>
    public void Stop(JobOutcome outcome, string reason)
    {
        if (this.ctx == null)
            return;
        Game1.player.controller = null;
        Game1.player.Halt();
        if (this.ctx.ExpectingMenu && Game1.activeClickableMenu is not null and not ChatOverlayMenu)
            Game1.exitActiveMenu();
        this.Finish(outcome, reason);
    }

    /// <summary>Called every update tick.</summary>
    public void Update()
    {
        if (this.ctx == null)
            return;
        if (!Context.IsWorldReady)
        {
            this.Finish(JobOutcome.Failed, "The save was closed.");
            return;
        }

        // Hand control back if something the job did not start takes over the screen.
        if (Game1.eventUp || Game1.isFestival())
        {
            this.Stop(JobOutcome.Partial, "A cutscene or festival started, so the autopilot handed control back to the player.");
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
            this.Stop(JobOutcome.Partial, $"A menu or dialogue opened ({menu.GetType().Name}), so the autopilot handed control back to the player.");
            return;
        }

        // Watchdog: a job that makes no progress for a while is stuck.
        if ((DateTime.UtcNow - this.ctx.LastProgress).TotalSeconds > this.config().Autopilot.WatchdogSeconds)
        {
            this.Stop(JobOutcome.Failed, $"No progress for {this.config().Autopilot.WatchdogSeconds} seconds while {this.ctx.Activity}. The farmer may be stuck.");
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
            this.Stop(JobOutcome.Failed, $"Internal error: {ex.Message}");
        }
    }

    private void Finish(JobOutcome outcome, string reason)
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
            : outcome == JobOutcome.Done ? $"{done.Name} finished." : $"{done.Name} finished with problems.";
        var result = new JobResult(outcome, summary, done.Report, done.Problems);
        this.monitor.Log($"Autopilot: {done.Name} -> {outcome}: {summary}", LogLevel.Debug);
        this.Finished?.Invoke(done.Name, result);
        tcs?.TrySetResult(result);
    }
}
