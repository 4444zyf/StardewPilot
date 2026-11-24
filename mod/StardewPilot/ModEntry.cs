using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewPilot.Apprentice;
using StardewPilot.Autopilot;
using StardewPilot.Bridge;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewPilot.Tools;
using StardewValley;

namespace StardewPilot;

/// <summary>Mod entry point: owns the bridge and wires game events to it.</summary>
public sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private BridgeServer? bridge;
    private ToolRegistry? tools;
    private TipEngine? tips;
    private JobRunner? runner;
    private MemoryStore? memory;
    private AgentLauncher? launcher;
    private ApprenticeService? apprentice;
    private RoutineService? routines;
    private RoutineScheduler? scheduler;
    private readonly SelfPlay selfPlay = new();
    private bool autoLoadDone;
    private int lastBusyScreenTick;

    /// <summary>The copilot moved the farmer last (a job ended) and the player has not moved since.</summary>
    private bool copilotHasFarmer;

    /// <summary>Today's end-of-day walk to bed was started already (it is started once a day at most).</summary>
    private bool endOfDayStarted;

    /// <summary>Tips sent to the agent for wording, with the time their fallback text is due.</summary>
    private readonly Queue<(TipTrigger tip, double deadline)> pendingTips = new();
    private const double TipWaitSeconds = 45;

    /// <summary>Today's morning briefing is waiting for the agent's wording, until this game-time second.</summary>
    private bool briefingDue;
    private double briefingDeadline;
    private const double BriefingWaitSeconds = 90;

    public override void Entry(IModHelper helper)
    {
        I18n.Lookup = (key, tokens) => helper.Translation.Get(key, tokens).ToString();
        this.config = helper.ReadConfig<ModConfig>();
        if (string.IsNullOrWhiteSpace(this.config.Token))
        {
            this.config.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            helper.WriteConfig(this.config);
        }

        this.tools = new ToolRegistry(this.Monitor);
        StateTools.Register(this.tools, () => this.ModManifest.Version.ToString());
        this.tools.Add(FishKnowledge.Tool());
        this.tools.Add(CropKnowledge.Tool());
        this.tools.Add(RecipeKnowledge.Tool());
        this.tools.Add(MachineKnowledge.Tool());
        this.tools.Add(CollectionKnowledge.Tool());
        this.tools.Add(ScheduleKnowledge.Tool());
        this.tools.Add(ItemFinder.Tool());
        this.tools.Add(GiftIdeas.Tool());
        this.tools.Add(BundlePlan.Tool());
        this.tools.Add(AnimalTools.Tool());
        this.runner = new JobRunner(this.Monitor, () => this.config);
        Navigation.Config = () => this.config;
        ActionTools.Register(this.tools, this.runner, new Macros(() => this.config, helper));
        this.memory = new MemoryStore(helper.DirectoryPath, this.Monitor);
        this.apprentice = new ApprenticeService(this.memory, () => this.config, this.Monitor, this.runner, this.selfPlay);
        this.routines = new RoutineService(this.memory, () => this.config, helper, this.Monitor, this.runner, this.selfPlay, this.apprentice.Watcher);
        this.scheduler = new RoutineScheduler(this.routines, this.memory, () => this.config, helper, this.Monitor, this.runner, this.selfPlay, this.apprentice.Watcher,
            ran => this.bridge?.Broadcast("routines/ran", ran));
        DayBriefing.RoutinesToday = this.scheduler.Today;
        MemoryTools.Register(this.tools, this.memory, this.selfPlay, () => this.config, (on, goal) => this.SetSelfPlay(on, goal), this.apprentice.SeedLines, this.routines.SeedNames);
        this.tools.Add(DayBriefing.Tool(this.memory, () => this.config, this.apprentice));
        this.apprentice.Register(this.tools);
        this.routines.Register(this.tools);
        this.runner.Started += _ => this.apprentice.Watcher.OnJobStarted();
        this.apprentice.Watcher.ScheduledRunning = () => this.scheduler!.ScheduledRunning;
        this.apprentice.Watcher.CopilotHasFarmer = () => this.copilotHasFarmer;
        // Each step of a routine counts as its own job for the self-play log and the watcher.
        this.runner.StepFinished += (name, result) =>
        {
            this.selfPlay.LogJob(name, result);
            this.apprentice.Watcher.OnJobFinished(name, result);
        };
        this.runner.Finished += (name, result) =>
        {
            this.copilotHasFarmer = true;
            this.selfPlay.LogJob(name, result);
            this.apprentice.Watcher.OnJobFinished(name, result);
            this.routines.OnJobFinished(name, result);
            this.scheduler.OnJobFinished(name, result);
            if (name == "go_to_bed (end of day)")
                ChatLog.Add(ChatSender.System, result.Outcome == JobOutcome.Done ? I18n.Get("bed.done") : I18n.Get("bed.failed", new { why = result.Summary }));
        };
        this.tips = new TipEngine(() => this.config, this.DeliverTip, this.ShowWarning);

        ChatLog.Added += entry =>
        {
            if (entry.Sender is ChatSender.Agent or ChatSender.Tip or ChatSender.Warning)
                Toast.Show(entry);
        };

        helper.Events.GameLoop.UpdateTicking += (_, _) => InputSim.BeforeGameUpdate();
        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += (_, _) =>
        {
            this.Notify("save_loaded");
            if (!AgentState.Connected && WrapperStatus.Current.State is "needs_setup" or "error")
                Toast.Show(new ChatEntry(ChatSender.System, I18n.Get("copilot.hint-with-key", new { hint = WrapperStatus.NotConnectedHint(), key = this.config.ChatKey }), DateTime.Now));
        };
        helper.ConsoleCommands.Add("pilot_diagnose", "Print a self-check of StardewPilot (versions, Node, pi, wrapper, bridge) to paste into a bug report. Contains no keys, tokens or links.", (_, _) =>
            this.Monitor.Log("\n" + Platform.Diagnostics.Build(new Platform.DiagnoseInfo(
                this.ModManifest.Version.ToString(),
                Game1.version,
                Constants.ApiVersion.ToString(),
                this.config.Launcher.Enabled,
                this.config.Launcher.UseWrapper,
                this.config.Launcher.NodeCommand,
                this.config.Launcher.PiCommand,
                this.config.Launcher.ExtensionPath.Length > 0 ? this.config.Launcher.ExtensionPath : System.IO.Path.Combine(helper.DirectoryPath, "agent", "src", "index.ts"),
                this.bridge?.Url ?? "(not started)",
                this.bridge?.ClientCount ?? 0,
                new[] { this.config.Token })), LogLevel.Info));
        helper.ConsoleCommands.Add("pilot_settings", "Open the StardewPilot copilot settings page in the browser.", (_, _) =>
            this.Monitor.Log(WrapperStatus.OpenSettings(this.Monitor), LogLevel.Info));
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            this.tips.OnDayStarted();
            this.copilotHasFarmer = false;
            this.endOfDayStarted = false;
            AgentState.SetWaiting(false);
            this.selfPlay.OnDayStarted();
            this.scheduler!.OnDayStarted();
            this.PrepareBriefing();
            this.apprentice!.StartDay();
            this.Notify("day_started", new { season = Game1.currentSeason, day = Game1.dayOfMonth, year = Game1.year, selfPlay = this.selfPlay.Enabled });
        };
        helper.Events.GameLoop.TimeChanged += (_, e) =>
        {
            this.Monitor.Log($"Time changed to {e.NewTime}.", LogLevel.Trace);
            this.tips.OnTimeChanged(e.NewTime);
            this.scheduler!.OnTimeChanged();
        };
        helper.Events.GameLoop.DayEnding += (_, _) => this.WriteFallbackSummary(this.apprentice!.EndDay());
        helper.Events.GameLoop.Saved += (_, _) =>
        {
            this.memory.Commit();
            this.Notify("saved");
        };
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            this.runner.Stop(JobOutcome.Failed, I18n.Get("stop.title-screen"), StopCause.Screen);
            this.apprentice!.Watcher.Reset();
            this.memory.Discard();
            this.selfPlay.Stop(I18n.Get("stop.title-screen"));
            ChatLog.Clear();
            this.pendingTips.Clear();
            this.briefingDue = false;
            this.Notify("returned_to_title");
        };
        helper.Events.Player.InventoryChanged += (_, e) =>
        {
            this.tips.OnInventoryChanged(e);
            this.apprentice!.Watcher.OnInventoryChanged(e);
        };
        helper.Events.Player.Warped += (_, e) => this.apprentice!.Watcher.OnWarped(e);
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Display.RenderedHud += (_, e) =>
        {
            Toast.Draw(e.SpriteBatch);
            this.routines?.DrawLessonLine(e.SpriteBatch);
            this.scheduler?.Draw(e.SpriteBatch);
        };
        helper.Events.Content.LocaleChanged += (_, _) => StateTools.ResetNameIndex();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        try
        {
            this.bridge = new BridgeServer(this.config.Port, this.config.Token, this.Monitor);
            this.bridge.ClientCountChanged += AgentState.SetClients;
            this.bridge.Register("initialize", (_, _) => MainThread.Run<object?>(() => new
            {
                protocolVersion = "2025-06-18",
                serverInfo = new { name = "stardewpilot-bridge", version = this.ModManifest.Version.ToString() },
                capabilities = new { tools = new { } },
                gameVersion = Game1.version,
                settings = new
                {
                    gameLanguage = LocalizedContentManager.CurrentLanguageCode.ToString(),
                    tellMeEverything = this.config.TellMeEverything,
                    chattiness = this.config.Chattiness.ToString(),
                    selfPlay = this.selfPlay.Enabled,
                },
            }));
            this.bridge.Register("ping", (_, _) => Task.FromResult<object?>(new { }));
            this.bridge.Register("tools/list", (_, _) => Task.FromResult<object?>(this.tools!.List()));
            this.bridge.Register("tools/call", (p, _) => this.tools!.Call(p));
            this.bridge.Register("chat/agent_message", (p, _) => MainThread.Run<object?>(() =>
            {
                this.ReceiveAgentMessage(p?.Value<string>("text") ?? "", p?.Value<string>("kind") ?? "reply");
                return new { };
            }));
            this.bridge.Register("agent/usage", (p, _) => MainThread.Run<object?>(() =>
            {
                this.selfPlay.AddUsage(p?.Value<int?>("requests") ?? 0, p?.Value<long?>("tokens") ?? 0);
                return new { };
            }));
            this.bridge.Register("selfplay/next", (_, _) => MainThread.Run<object?>(this.SelfPlayNext));
            this.bridge.Register("memory/seed", (_, _) => MainThread.Run<object?>(() => Context.IsWorldReady ? this.DaySeed() : new { }));
            this.bridge.Register("agent/status", (p, _) =>
            {
                AgentState.SetBusy(p?.Value<bool?>("busy") ?? false, p?.Value<string>("activity"));
                return Task.FromResult<object?>(new { });
            });
            this.bridge.Register("selfplay/waiting", (p, _) => MainThread.Run<object?>(() =>
            {
                bool waiting = p?.Value<bool?>("waiting") ?? false;
                AgentState.SetWaiting(waiting && this.selfPlay.Enabled);
                if (AgentState.Waiting)
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("selfplay.waiting", new { key = this.config.ChatKey })) { noIcon = true });
                return new { waiting = AgentState.Waiting };
            }));
            this.bridge.Start();

            ConnectionFile.Write(this.bridge.Url, this.config.Port, this.config.Token, Game1.version, this.ModManifest.Version.ToString());
            this.Monitor.Log($"Bridge listening on {this.bridge.Url.Replace("ws://", "").TrimEnd('/')}. Connection details: {ConnectionFile.FilePath}", LogLevel.Info);
            if (this.config.Launcher.Enabled)
            {
                this.launcher = new AgentLauncher(this.Monitor);
                this.launcher.Start(this.config.Launcher, this.Helper.DirectoryPath);
            }
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Could not start the bridge on port {this.config.Port}: {ex.Message}. Change Port in config.json if another program uses it.", LogLevel.Error);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        MainThread.Drain();
        if (Game1.eventUp || Game1.activeClickableMenu != null)
            this.lastBusyScreenTick = Game1.ticks;

        if (Context.IsWorldReady)
        {
            this.runner!.Update();
            this.EnforceEndOfDay();
            this.apprentice!.Watcher.Tick();
            this.scheduler!.Tick();

            // Single-player: hold the clock while the agent thinks (not while autopilot jobs run). Co-op cannot pause freely.
            // Also while self-play waits for the player's answer: the day should not run out while the question is unread.
            bool hold = this.config.Dev.FreezeTime
                || (this.config.PauseWhileThinking && (AgentState.Busy || (AgentState.Waiting && this.selfPlay.Enabled)) && !this.runner.IsRunning && !Context.IsMultiplayer);
            if (hold)
                Game1.gameTimeInterval = 0;

            if (e.IsMultipleOf(20))
            {
                this.tips!.OnUpdate();
                this.FlushOverdueTips();
                if (this.briefingDue && Game1.currentGameTime.TotalGameTime.TotalSeconds > this.briefingDeadline)
                    this.ShowBriefingFallback();
            }
        }

        // Dev: load a save automatically once the title screen is up.
        if (!this.autoLoadDone && this.config.Dev.AutoLoadSave.Length > 0 && Game1.activeClickableMenu is StardewValley.Menus.TitleMenu && e.Ticks > 120)
        {
            this.autoLoadDone = true;
            this.Monitor.Log($"Dev: auto-loading save {this.config.Dev.AutoLoadSave}.", LogLevel.Info);
            SaveGame.Load(this.config.Dev.AutoLoadSave);
            Game1.exitActiveMenu();
        }
    }

    /// <summary>Late at night, stop work and go to bed, whatever the plan says. Passing out costs gold.</summary>
    private void EnforceEndOfDay()
    {
        if (Game1.timeOfDay < this.config.Autopilot.StopWorkAt || this.runner!.CurrentJob is "go_to_bed" or "go_to_bed (end of day)")
            return;
        if (!this.runner.IsRunning)
        {
            // Nothing running, but the copilot has the farmer (self-play, or a job left the farmer and the player has not
            // moved since): the farmer still has to get to bed. Started once; the player can always take over.
            if (this.endOfDayStarted || !(this.selfPlay.Enabled || this.copilotHasFarmer) || !Context.IsPlayerFree || Game1.player.isInBed.Value)
                return;
            this.endOfDayStarted = true;
            this.StartEndOfDayBed();
            return;
        }
        // A routine started by its schedule only stops: nobody asked for it just then, so nobody asked to end the day.
        if (this.scheduler!.ScheduledRunning)
        {
            this.runner.Stop(JobOutcome.Partial, I18n.Get("stop.late"), StopCause.EndOfDay);
            return;
        }
        this.runner.Stop(JobOutcome.Partial, I18n.Get("stop.late-bed"), StopCause.EndOfDay);
        this.endOfDayStarted = true;
        this.StartEndOfDayBed();
    }

    /// <summary>Walk to bed; if no way home is found, tell the player at once while there is still time before 2am.</summary>
    private void StartEndOfDayBed()
    {
        var macros = new Macros(() => this.config, this.Helper);
        this.runner!.Start("go_to_bed (end of day)", ctx => macros.GoToBed(ctx)).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result.Outcome != JobOutcome.Done)
                _ = MainThread.Run<object?>(() =>
                {
                    string text = I18n.Get("warn.no-way-home", new { why = t.Result.Summary });
                    Game1.addHUDMessage(new HUDMessage(text, HUDMessage.error_type));
                    ChatLog.Add(ChatSender.System, text);
                    return null;
                });
        });
    }

    /// <summary>Movement or tool input from the player cancels the autopilot at once.</summary>
    private bool IsTakeControlInput(SButton button)
    {
        if (Game1.activeClickableMenu != null || Game1.eventUp)
            return false; // typing in the chat, answering a confirmation, or clicking through a cutscene does not count
        if (Game1.ticks - this.lastBusyScreenTick < 90 && button is SButton.MouseLeft or SButton.MouseRight)
            return false; // the last click of a cutscene or dialogue often lands just after it closes
        var o = Game1.options;
        return button is SButton.MouseLeft or SButton.MouseRight
            || o.moveUpButton.Any(k => k.ToSButton() == button) || o.moveDownButton.Any(k => k.ToSButton() == button)
            || o.moveLeftButton.Any(k => k.ToSButton() == button) || o.moveRightButton.Any(k => k.ToSButton() == button)
            || o.useToolButton.Any(k => k.ToSButton() == button) || o.actionButton.Any(k => k.ToSButton() == button)
            || button.IsUseToolButton() || button.IsActionButton()
            || button is SButton.LeftThumbstickUp or SButton.LeftThumbstickDown or SButton.LeftThumbstickLeft or SButton.LeftThumbstickRight
                or SButton.DPadUp or SButton.DPadDown or SButton.DPadLeft or SButton.DPadRight;
    }

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!Context.IsWorldReady)
            return;
        this.apprentice!.Watcher.OnButtonPressed(e);
        this.scheduler!.OnButtonPressed(e.Button);

        if (e.Button == this.config.Routines.StartKey && Game1.activeClickableMenu == null && this.scheduler.OnStartKey())
        {
            this.Helper.Input.Suppress(e.Button);
            return;
        }

        if (e.Button == this.config.SelfPlay.ToggleKey)
        {
            this.SetSelfPlay(!this.selfPlay.Enabled, null);
            this.Helper.Input.Suppress(e.Button);
            return;
        }
        if (e.Button == this.config.Autopilot.ToggleKey)
        {
            this.runner!.Enabled = !this.runner.Enabled;
            if (!this.runner.Enabled)
            {
                this.runner.Stop(JobOutcome.Partial, I18n.Get("stop.autopilot-off"), StopCause.Player);
                if (this.selfPlay.Enabled)
                    this.SetSelfPlay(false, null, I18n.Get("stop.autopilot-off"));
            }
            Game1.addHUDMessage(new HUDMessage(I18n.Get(this.runner.Enabled ? "hud.autopilot-on" : "hud.autopilot-off")));
            this.Helper.Input.Suppress(e.Button);
            return;
        }
        bool takeControl = IsTakeControlInput(e.Button);
        if (takeControl)
        {
            this.copilotHasFarmer = false;
            AgentState.SetWaiting(false);
        }
        if ((this.runner!.IsRunning || this.selfPlay.Enabled) && takeControl)
        {
            this.runner.Stop(JobOutcome.Partial, I18n.Get("stop.player"), StopCause.Player);
            if (this.selfPlay.Enabled)
                this.SetSelfPlay(false, null, I18n.Get("selfplay.player-took-over", new { key = this.config.SelfPlay.ToggleKey }));
            return;
        }

        if (e.Button != this.config.ChatKey)
            return;
        if (Game1.activeClickableMenu is ChatOverlayMenu open)
        {
            open.exitThisMenu();
            this.Helper.Input.Suppress(e.Button);
        }
        else if (Game1.activeClickableMenu == null && Context.IsPlayerFree)
        {
            Game1.activeClickableMenu = new ChatOverlayMenu(this.SendPlayerMessage);
            this.Helper.Input.Suppress(e.Button);
        }
    }

    /// <summary>The player typed in the overlay.</summary>
    private void SendPlayerMessage(string text)
    {
        if (text.Trim().Equals("/settings", StringComparison.OrdinalIgnoreCase))
        {
            ChatLog.Add(ChatSender.System, WrapperStatus.OpenSettings(this.Monitor));
            return;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(text.Trim(), @"^/forget\s+habits$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            int removed = this.apprentice!.Forget();
            ChatLog.Add(ChatSender.System, I18n.Get(this.config.Apprentice.Enabled ? "apprentice.forgot" : "apprentice.forgot-off", new { days = removed }));
            return;
        }
        ChatLog.Add(ChatSender.Player, text);
        if (!AgentState.Connected)
        {
            ChatLog.Add(ChatSender.System, WrapperStatus.NotConnectedHint());
            return;
        }
        this.bridge?.Broadcast("chat/user_message", new { text });
    }

    private void ReceiveAgentMessage(string text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        text = PlainText(text);
        if (kind == "briefing")
        {
            this.Monitor.Log($"Morning briefing from the agent: {text}", LogLevel.Debug);
            if (this.briefingDue)
            {
                this.briefingDue = false;
                this.memory?.MarkBriefingShown();
                this.apprentice?.BriefingShown(this.selfPlay.Enabled);
            }
            ChatLog.Add(ChatSender.Tip, text);
        }
        else if (kind == "tip")
        {
            if (this.pendingTips.Count > 0)
                this.pendingTips.Dequeue();
            ChatLog.Add(ChatSender.Tip, text);
        }
        else
        {
            ChatLog.Add(ChatSender.Agent, text);
        }
    }

    /// <summary>The game's fonts have no bold or headings: drop markdown markers the model may use.</summary>
    private static string PlainText(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(\*\*|__|`)", "");
        // The game's fonts lack typographic dashes and quotes; they would show as other glyphs.
        text = text.Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"').Replace('\u201D', '"').Replace('\u2026', '.');
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^#{1,6}\s*", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        return text;
    }

    private void DeliverTip(TipTrigger tip)
    {
        this.Monitor.Log($"Tip '{tip.Kind}' (agent connected: {AgentState.Connected}).", LogLevel.Debug);
        if (AgentState.Connected && this.bridge != null)
        {
            this.pendingTips.Enqueue((tip, Game1.currentGameTime.TotalGameTime.TotalSeconds + TipWaitSeconds));
            this.bridge.Broadcast("tips/trigger", new
            {
                kind = tip.Kind,
                important = tip.Important,
                facts = tip.Facts,
                fallback = tip.Fallback,
                gameLanguage = LocalizedContentManager.CurrentLanguageCode.ToString(),
            });
        }
        else
        {
            ChatLog.Add(ChatSender.Tip, tip.Fallback);
        }
    }

    /// <summary>If the agent did not word a tip in time, show the fixed text.</summary>
    private void FlushOverdueTips()
    {
        double now = Game1.currentGameTime.TotalGameTime.TotalSeconds;
        while (this.pendingTips.Count > 0 && this.pendingTips.Peek().deadline < now)
            ChatLog.Add(ChatSender.Tip, this.pendingTips.Dequeue().tip.Fallback);
    }

    private void ShowWarning(string kind, string text)
    {
        this.Monitor.Log($"Warning '{kind}': {text}", LogLevel.Debug);
        ChatLog.Add(ChatSender.Warning, text);
        Game1.playSound("cancel");
        this.bridge?.Broadcast("tips/warning", new { kind, text });
    }

    /// <summary>At day start: decide whether today's briefing is due, and show the fixed text at once if no agent can word it.</summary>
    private void PrepareBriefing()
    {
        this.briefingDue = false;
        if (this.memory == null || !TipEngine.BriefingOn(this.config) || this.memory.BriefingShownToday())
            return;
        if (!AgentState.Connected)
        {
            this.ShowBriefingFallback();
            return;
        }
        // The agent starts a fresh session for the new day and asks for the day's seed; the briefing travels with it.
        this.briefingDue = true;
        this.briefingDeadline = Game1.currentGameTime.TotalGameTime.TotalSeconds + BriefingWaitSeconds;
    }

    /// <summary>The briefing as the mod's own fixed text, when no agent words it in time.</summary>
    private void ShowBriefingFallback()
    {
        this.briefingDue = false;
        if (this.memory == null || !Context.IsWorldReady)
            return;
        var (_, lines) = DayBriefing.Build(this.memory, this.config, this.apprentice!, morning: true, selfPlay: this.selfPlay.Enabled);
        this.memory.MarkBriefingShown();
        this.apprentice?.BriefingShown(this.selfPlay.Enabled);
        this.Monitor.Log($"Morning briefing shown as fixed text: {string.Join(" | ", lines)}", LogLevel.Debug);
        ChatLog.Add(ChatSender.Tip, I18n.Get("briefing.good-morning") + " " + string.Join("\n", lines));
    }

    /// <summary>The first message of the agent's day session: memory, and today's briefing when it is due.</summary>
    private object DaySeed()
    {
        JObject seed = JObject.FromObject(MemoryTools.Snapshot(this.memory!, this.selfPlay, this.config, this.apprentice!.SeedLines(), this.routines!.SeedNames()));
        if (this.briefingDue)
        {
            var (facts, lines) = DayBriefing.Build(this.memory!, this.config, this.apprentice!, morning: true, selfPlay: this.selfPlay.Enabled);
            seed["briefing"] = JObject.FromObject(new
            {
                facts,
                fallback = string.Join("\n", lines),
                gameLanguage = LocalizedContentManager.CurrentLanguageCode.ToString(),
            });
            // In self-play the agent plans the day from these facts instead of writing a chat message.
            if (this.selfPlay.Enabled)
            {
                this.briefingDue = false;
                this.memory!.MarkBriefingShown();
            }
            else
            {
                this.briefingDeadline = Game1.currentGameTime.TotalGameTime.TotalSeconds + BriefingWaitSeconds;
            }
        }
        return seed;
    }

    /// <summary>Start or stop self-play, from the hotkey or the agent.</summary>
    private void SetSelfPlay(bool enabled, string? goal, string? reason = null)
    {
        if (goal is { Length: > 0 })
            this.memory!.WriteGoal(goal);
        if (enabled)
        {
            if (!this.runner!.Enabled)
            {
                ChatLog.Add(ChatSender.System, I18n.Get("selfplay.needs-autopilot", new { key = this.config.Autopilot.ToggleKey }));
                return;
            }
            if (!AgentState.Connected)
            {
                ChatLog.Add(ChatSender.System, I18n.Get("selfplay.needs-copilot", new { hint = WrapperStatus.NotConnectedHint() }));
                return;
            }
            this.selfPlay.Start();
            string g = this.memory!.Goal;
            ChatLog.Add(ChatSender.System, I18n.Get("selfplay.on", new { goal = g.Length > 0 ? g : I18n.Get("selfplay.default-goal"), key = this.config.SelfPlay.ToggleKey }));
        }
        else if (this.selfPlay.Enabled)
        {
            this.selfPlay.Stop(reason ?? I18n.Get("selfplay.stopped"));
            ChatLog.Add(ChatSender.System, (I18n.Get("selfplay.off") + " " + reason).Trim());
        }
        this.bridge?.Broadcast("selfplay/changed", new { enabled = this.selfPlay.Enabled, goal = this.memory!.Goal, reason });
    }

    /// <summary>Asked by the agent before each self-play step: may it continue now?</summary>
    private object SelfPlayNext()
    {
        if (!Context.IsWorldReady)
            return new { go = false, reason = "No save is loaded." };
        if (!this.selfPlay.Enabled)
            return new { go = false, reason = this.selfPlay.StopReason };
        if (this.selfPlay.BudgetExhausted(this.config))
        {
            string why = I18n.Get("selfplay.budget", new { count = this.selfPlay.RequestsToday });
            this.SetSelfPlay(false, null, why + " " + I18n.Get("selfplay.resume", new { key = this.config.SelfPlay.ToggleKey }));
            if (Game1.timeOfDay >= 2000 && !this.runner!.IsRunning)
            {
                var macros = new Macros(() => this.config, this.Helper);
                _ = this.runner.Start("go_to_bed (end of day)", ctx => macros.GoToBed(ctx));
            }
            return new { go = false, reason = why };
        }
        if (this.runner!.IsRunning || !Context.IsPlayerFree || Game1.activeClickableMenu != null || Game1.timeOfDay >= this.config.Autopilot.StopWorkAt)
            return new { go = false, wait = true, waitSeconds = 3 };
        Farmer p = Game1.player;
        return new
        {
            go = true,
            brief = $"{StateTools.FormatTime(Game1.timeOfDay)}, {Game1.currentSeason} {Game1.dayOfMonth}, energy {(int)p.Stamina}/{p.MaxStamina}, {p.Money}g, at {p.currentLocation.DisplayName}.",
            requestsLeft = this.config.SelfPlay.DailyRequestBudget > 0 ? this.config.SelfPlay.DailyRequestBudget - this.selfPlay.RequestsToday : (int?)null,
        };
    }

    /// <summary>
    /// If the agent did not summarise the day, keep a plain record so tomorrow (and the morning briefing) still has one.
    /// Written on every day, also when the player played the whole day by hand.
    /// </summary>
    private void WriteFallbackSummary(List<string> activity)
    {
        if (this.memory == null)
            return;
        if (this.memory.TodaySummary.Length > 0)
        {
            // The agent wrote the summary: add what the watcher saw.
            if (activity.Count > 0)
                this.memory.AppendToSummary(string.Join("\n", activity));
            return;
        }
        int earned = Game1.player.Money - this.selfPlay.MoneyAtDayStart;
        int shipped = ShippingBinValue();
        string text = "(Written automatically: the agent did not write a summary.)\n"
            + $"Money: {Game1.player.Money}g ({(earned >= 0 ? "+" : "")}{earned}g today, before shipping).\n"
            + $"In the shipping bin tonight: {shipped}g.\n"
            + (this.selfPlay.JobLog.Count > 0
                ? $"Autopilot jobs:\n- {string.Join("\n- ", this.selfPlay.JobLog)}"
                : "No autopilot jobs: the player played the day by hand.")
            + (activity.Count > 0 ? "\n" + string.Join("\n", activity) : "");
        this.memory.WriteSummary(text);
    }

    /// <summary>What the local player's shipping bin and mini shipping bins will pay tonight, at the game's sell prices.</summary>
    private static int ShippingBinValue()
    {
        int total = Game1.getFarm().getShippingBin(Game1.player).Where(i => i != null).Sum(i => Utility.getSellToStorePriceOfItem(i));
        Utility.ForEachLocation(loc =>
        {
            foreach (StardewValley.Object obj in loc.objects.Values)
            {
                if (obj is StardewValley.Objects.Chest { SpecialChestType: StardewValley.Objects.Chest.SpecialChestTypes.MiniShippingBin } bin)
                    total += bin.GetItemsForPlayer(Game1.player.UniqueMultiplayerID).Where(i => i != null).Sum(i => Utility.getSellToStorePriceOfItem(i));
            }
            return true;
        });
        return total;
    }

    /// <summary>Tell connected agents that something happened in the game.</summary>
    private void Notify(string type, object? data = null) =>
        this.bridge?.Broadcast("notifications/game_event", new { type, data });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.launcher?.Dispose();
            this.bridge?.Dispose();
            ConnectionFile.Delete();
        }
        base.Dispose(disposing);
    }
}
