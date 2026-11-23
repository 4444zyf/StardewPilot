using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewPilot.Apprentice.Model;
using StardewPilot.Autopilot;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewPilot.Apprentice;

/// <summary>
/// Routines that start themselves: when a scheduled routine is due, an offer on the screen and a key that starts it; a 6am routine set
/// to "by itself" starts after a short count if the player has touched nothing since waking. All the mod's own code, no model request.
/// </summary>
internal sealed class RoutineScheduler
{
    /// <summary>The by-itself count, in ticks (five seconds).</summary>
    private const int CountTicks = 300;

    /// <summary>No count in the first moments after the day begins or a save loads.</summary>
    private const int GraceTicks = 120;

    private readonly RoutineService routines;
    private readonly MemoryStore memory;
    private readonly Func<ModConfig> config;
    private readonly IModHelper helper;
    private readonly IMonitor monitor;
    private readonly JobRunner runner;
    private readonly SelfPlay selfPlay;
    private readonly Watcher watcher;
    private readonly Action<object> notifyAgent;

    private List<Routine> cache = new();
    private HashSet<string> inEffect = new();
    private DateTime? cacheTime;
    private bool cacheLoaded;

    /// <summary>The player has given input since waking (or since the save loaded): nothing starts by itself today.</summary>
    private bool touched;
    private int dayStartTick;
    private Point lastCursor;
    private int lastScroll;
    private int lastJobEndTick;

    private int count = CountTicks;

    /// <summary>The routine on offer now, and whether it is counting down to start by itself.</summary>
    private Routine? offered;
    private bool counting;

    /// <summary>The run the scheduler started, until it ends.</summary>
    private (string name, bool byItself)? started;

    /// <summary>A by-itself run ended done: the next by-itself routine due now starts at once (one count for the chain).</summary>
    private bool chain;

    public RoutineScheduler(RoutineService routines, MemoryStore memory, Func<ModConfig> config, IModHelper helper, IMonitor monitor, JobRunner runner, SelfPlay selfPlay, Watcher watcher, Action<object> notifyAgent)
    {
        this.routines = routines;
        this.memory = memory;
        this.config = config;
        this.helper = helper;
        this.monitor = monitor;
        this.runner = runner;
        this.selfPlay = selfPlay;
        this.watcher = watcher;
        this.notifyAgent = notifyAgent;
        routines.Changed += () => this.cacheLoaded = false;
    }

    /// <summary>A run the scheduler started is running now.</summary>
    public bool ScheduledRunning => this.started != null && this.runner.CurrentJob == "run_routine";

    // ------------------------------------------------------------ day and file

    /// <summary>A save loaded or a new day started: read the routines and the record again, and nothing is touched yet.</summary>
    public void OnDayStarted()
    {
        this.routines.LoadRecord();
        this.cacheLoaded = false;
        this.touched = false;
        this.chain = false;
        this.started = null;
        this.offered = null;
        this.count = CountTicks;
        this.dayStartTick = Game1.ticks;
        this.lastCursor = this.Cursor();
        this.lastScroll = Game1.input.GetMouseState().ScrollWheelValue;
    }

    /// <summary>Every ten game minutes: read the file again if it was changed by hand.</summary>
    public void OnTimeChanged()
    {
        if (this.memory.RoutinesWriteTime != this.cacheTime)
            this.cacheLoaded = false;
    }

    private void Refresh()
    {
        this.cacheLoaded = true;
        this.cacheTime = this.memory.RoutinesWriteTime;
        var (all, problem) = this.routines.Load();
        if (problem != null)
            this.monitor.Log($"Routines: {problem}", LogLevel.Warn);
        this.cache = all.Where(r => r.When != null).ToList();
        this.inEffect = this.cache.Where(r => this.routines.ScheduleProblems(r).Count == 0).Select(r => r.Name).ToHashSet();
    }

    /// <summary>The scheduled routines for today, for the briefing: name, time, and how it starts.</summary>
    public List<(string name, int at, string start)> Today()
    {
        if (!this.cacheLoaded)
            this.Refresh();
        if (this.selfPlay.Enabled || !this.config().Routines.StartThemselves)
            return new();
        string date = MemoryStore.DateKey();
        bool rain = Game1.getFarm().IsRainingHere();
        string weekday = HabitFinder.Weekdays[(Game1.dayOfMonth - 1) % 7];
        return this.cache
            .Where(r => this.inEffect.Contains(r.Name) && RoutineSchedule.Today(r.When!, date, weekday, rain))
            .OrderBy(r => r.When!.At).ThenBy(r => r.Name, StringComparer.Ordinal)
            .Select(r => (r.Name, r.When!.At, r.When!.Start))
            .ToList();
    }

    // ------------------------------------------------------------ input

    private Point Cursor()
    {
        Vector2 px = this.helper.Input.GetCursorPosition().ScreenPixels;
        return new Point((int)px.X, (int)px.Y);
    }

    /// <summary>Any button, from keyboard, mouse or controller.</summary>
    public void OnButtonPressed(SButton button)
    {
        if (!this.touched)
            this.monitor.Log($"Routines: input ({button}); nothing starts by itself today.", LogLevel.Trace);
        this.touched = true;
    }

    /// <summary>Held keys, the cursor, the scroll wheel, the sticks, and the farmer moving by hand.</summary>
    private void ReadInput()
    {
        if (this.touched)
            return;
        Point cursor = this.Cursor();
        int scroll = Game1.input.GetMouseState().ScrollWheelValue;
        string? why = null;
        if (Math.Abs(cursor.X - this.lastCursor.X) + Math.Abs(cursor.Y - this.lastCursor.Y) > 4)
            why = "the cursor moved";
        else if (scroll != this.lastScroll)
            why = "the scroll wheel";
        else if (Game1.game1.IsActive && Game1.input.GetKeyboardState().GetPressedKeys().Length > 0)
            why = "a key is held";
        else if (Game1.input.GetGamePadState() is { IsConnected: true } pad
            && (pad.ThumbSticks.Left.Length() > 0.25f || pad.ThumbSticks.Right.Length() > 0.25f || pad.Triggers.Left > 0.25f || pad.Triggers.Right > 0.25f))
            why = "the controller";
        else if (!this.runner.IsRunning && Game1.ticks - this.lastJobEndTick > 30 && Game1.player.isMoving())
            why = "the farmer moved";
        this.lastCursor = cursor;
        this.lastScroll = scroll;
        if (why != null)
        {
            this.monitor.Log($"Routines: input ({why}); nothing starts by itself today.", LogLevel.Trace);
            this.touched = true;
        }
    }

    // ------------------------------------------------------------ each tick

    public void Tick()
    {
        if (!Context.IsWorldReady)
            return;
        // Input counts while the window is the player's; the mod's own job moves the farmer, not the player.
        if (Game1.game1.IsActive)
            this.ReadInput();
        else
            this.lastCursor = this.Cursor();

        this.offered = null;
        this.counting = false;
        if (!this.ShowConditions())
        {
            this.count = CountTicks;
            return;
        }
        if (!this.cacheLoaded)
            this.Refresh();
        if (this.cache.Count == 0)
            return;

        bool rain = Game1.getFarm().IsRainingHere();
        string weekday = HabitFinder.Weekdays[(Game1.dayOfMonth - 1) % 7];
        bool fullSelfPlay = this.config().Autopilot.FullSelfPlay;
        List<Routine> due = RoutineSchedule.Due(this.cache, MemoryStore.DateKey(), weekday, rain, Game1.timeOfDay, this.routines.Record,
            r => this.inEffect.Contains(r.Name) && !(fullSelfPlay && RoutineRules.NeedsAsking(r)));
        if (due.Count == 0)
        {
            this.count = CountTicks;
            return;
        }
        // A by-itself routine goes first while it may start; otherwise the earliest offer.
        bool byItself = this.ByItselfConditions();
        this.offered = (byItself ? due.FirstOrDefault(r => r.When!.Start == RoutineSchedule.Self) : null) ?? due[0];
        if (this.offered.When!.Start != RoutineSchedule.Self || !byItself)
        {
            this.count = CountTicks;
            return;
        }
        this.counting = true;
        if (this.chain)
            this.count = 0;
        if (--this.count > 0)
            return;
        this.count = CountTicks;
        this.Start(this.offered, byItself: true);
    }

    /// <summary>When an offer may show at all.</summary>
    private bool ShowConditions()
    {
        if (!this.config().Routines.StartThemselves || !this.runner.Enabled || this.runner.IsRunning || AgentState.Busy)
            return false;
        if (!Context.CanPlayerMove || Game1.eventUp || Game1.isFestival() || Game1.currentMinigame != null || Game1.activeClickableMenu != null)
            return false;
        if (this.watcher.Lesson.Active || this.selfPlay.Enabled)
            return false;
        if (Game1.currentLocation is MineShaft or VolcanoDungeon)
            return false;
        // run_routine's own refusal: not in the hour before work stops.
        return Game1.timeOfDay < this.config().Autopilot.StopWorkAt - 100;
    }

    /// <summary>When a by-itself routine may count down: untouched since waking, at home or on the farm, no festival today, the game running.</summary>
    private bool ByItselfConditions()
    {
        if (this.touched || Game1.ticks - this.dayStartTick < GraceTicks)
            return false;
        if (!Game1.game1.IsActive || Game1.paused || Game1.HostPaused || Game1.globalFade)
            return false;
        if (Utility.isFestivalDay())
            return false;
        GameLocation here = Game1.currentLocation;
        return here is Farm || here == Utility.getHomeOfFarmer(Game1.player);
    }

    // ------------------------------------------------------------ starting

    /// <summary>The start key: start the routine on offer. True when the key was used.</summary>
    public bool OnStartKey()
    {
        if (this.offered == null)
            return false;
        this.Start(this.offered, byItself: false);
        return true;
    }

    private void Start(Routine r, bool byItself)
    {
        string name = r.Name;
        this.chain = false;
        this.started = (name, byItself);
        this.monitor.Log($"Routines: starting \"{name}\" ({(byItself ? "by itself" : "from the offer")}).", LogLevel.Debug);
        if (byItself)
            this.Say(I18n.Get("routine.started-by-itself", new { name }), toast: true);
        _ = this.runner.Start("run_routine", ctx => this.routines.Run(ctx, name, byItself), new JObject { ["name"] = name });
    }

    /// <summary>Any job ended (also the steps' parent): note when, and report a run the scheduler started.</summary>
    public void OnJobFinished(string job, JobResult result)
    {
        this.lastJobEndTick = Game1.ticks;
        if (job != "run_routine" || this.started is not { } run)
            return;
        this.started = null;
        Routine? r = this.cache.FirstOrDefault(x => x.Name == run.name);
        string head = r?.When != null
            ? I18n.Get("routine.head-when", new { name = run.name, when = RoutineSchedule.Words(r.When) })
            : I18n.Get("routine.head", new { name = run.name });
        string? stoppedAt = result.Report.TryGetValue("stoppedAt", out object? at) ? at as string : null;
        string text;
        bool toast = true;

        switch (result.Cause)
        {
            case StopCause.Job when result.Outcome == JobOutcome.Done:
                int steps = r?.Steps.Count ?? 0;
                text = I18n.Get(steps == 1 ? "routine.done-one" : "routine.done", new { head, count = steps });
                this.routines.Record.LastStop.Remove(run.name);
                this.chain = run.byItself;
                break;
            case StopCause.Job:
                string what = stoppedAt ?? result.Summary;
                text = I18n.Get("routine.stopped-at", new { head, what });
                // The same failure as last time: only in the chat, so a daily routine that stops at the same step does not nag.
                if (this.routines.Record.LastStop.TryGetValue(run.name, out string? last) && last == what)
                    toast = false;
                this.routines.Record.LastStop[run.name] = what;
                break;
            case StopCause.EndOfDay:
                text = I18n.Get("routine.stopped-late", new { head, time = Time.Say(this.config().Autopilot.StopWorkAt) });
                break;
            case StopCause.Watchdog:
                text = I18n.Get("routine.stopped", new { head, why = result.Summary });
                break;
            default:
                // The player took over, a menu opened, or a job the player asked for replaced it: the offer comes back, and nothing starts by itself again today.
                this.touched = true;
                text = I18n.Get("routine.stopped-again", new { head, why = result.Summary, key = this.config().Routines.StartKey });
                toast = false;
                break;
        }
        this.routines.SaveRecord();
        this.Say(text, toast);
        this.notifyAgent(new { name = run.name, byItself = run.byItself, outcome = result.Outcome.ToString().ToLowerInvariant(), text });
    }

    private void Say(string text, bool toast)
    {
        ChatLog.Add(ChatSender.System, text);
        if (toast)
            Toast.Show(new ChatEntry(ChatSender.System, text, DateTime.Now));
    }

    // ------------------------------------------------------------ screen

    /// <summary>The offer line, under the lesson line's place.</summary>
    public void Draw(SpriteBatch b)
    {
        if (this.offered == null || Game1.activeClickableMenu != null || Game1.eventUp)
            return;
        string key = this.config().Routines.StartKey.ToString();
        string text = this.counting
            ? I18n.Get("routine.countdown", new { name = this.offered.Name, seconds = Math.Max(1, (this.count + 59) / 60), key })
            : I18n.Get("routine.offer", new { name = this.offered.Name, key });
        text = Guide.Toast.WrapClearOfClock(text);
        Vector2 size = Game1.smallFont.MeasureString(text);
        int w = (int)size.X + 32, h = (int)size.Y + 24;
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), 8, 8, w, h, Color.White, drawShadow: false);
        Utility.drawTextWithShadow(b, text, Game1.smallFont, new Vector2(24, 20), Game1.textColor);
    }
}
