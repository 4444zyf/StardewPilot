using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewPilot.Bridge;
using StardewPilot.Guide;
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
    private bool autoLoadDone;

    /// <summary>Tips sent to the agent for wording, with the time their fallback text is due.</summary>
    private readonly Queue<(TipTrigger tip, double deadline)> pendingTips = new();
    private const double TipWaitSeconds = 45;

    public override void Entry(IModHelper helper)
    {
        this.config = helper.ReadConfig<ModConfig>();
        if (string.IsNullOrWhiteSpace(this.config.Token))
        {
            this.config.Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
            helper.WriteConfig(this.config);
        }

        this.tools = new ToolRegistry(this.Monitor);
        StateTools.Register(this.tools, () => this.ModManifest.Version.ToString());
        this.tips = new TipEngine(() => this.config, this.DeliverTip, this.ShowWarning);

        ChatLog.Added += entry =>
        {
            if (entry.Sender is ChatSender.Agent or ChatSender.Tip or ChatSender.Warning)
                Toast.Show(entry);
        };

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += (_, _) => this.Notify("save_loaded");
        helper.Events.GameLoop.DayStarted += (_, _) =>
        {
            this.tips.OnDayStarted();
            this.Notify("day_started", new { season = Game1.currentSeason, day = Game1.dayOfMonth, year = Game1.year });
        };
        helper.Events.GameLoop.TimeChanged += (_, e) =>
        {
            this.Monitor.Log($"Time changed to {e.NewTime}.", LogLevel.Trace);
            this.tips.OnTimeChanged(e.NewTime);
        };
        helper.Events.GameLoop.Saved += (_, _) => this.Notify("saved");
        helper.Events.GameLoop.ReturnedToTitle += (_, _) =>
        {
            ChatLog.Clear();
            this.pendingTips.Clear();
            this.Notify("returned_to_title");
        };
        helper.Events.Player.InventoryChanged += (_, e) => this.tips.OnInventoryChanged(e);
        helper.Events.Input.ButtonPressed += this.OnButtonPressed;
        helper.Events.Display.RenderedHud += (_, e) => Toast.Draw(e.SpriteBatch);
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
            this.bridge.Register("agent/status", (p, _) =>
            {
                AgentState.SetBusy(p?.Value<bool?>("busy") ?? false, p?.Value<string>("activity"));
                return Task.FromResult<object?>(new { });
            });
            this.bridge.Start();

            ConnectionFile.Write(this.config.Port, this.config.Token, Game1.version, this.ModManifest.Version.ToString());
            this.Monitor.Log($"Bridge listening on 127.0.0.1:{this.config.Port}. Connection details: {ConnectionFile.FilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Could not start the bridge on port {this.config.Port}: {ex.Message}. Change Port in config.json if another program uses it.", LogLevel.Error);
        }
    }

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        MainThread.Drain();

        if (Context.IsWorldReady)
        {
            // Single-player: hold the clock while the agent thinks. Co-op cannot pause freely.
            bool hold = this.config.Dev.FreezeTime || (this.config.PauseWhileThinking && AgentState.Busy && !Context.IsMultiplayer);
            if (hold)
                Game1.gameTimeInterval = 0;

            if (e.IsMultipleOf(20))
            {
                this.tips!.OnUpdate();
                this.FlushOverdueTips();
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

    private void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (e.Button != this.config.ChatKey || !Context.IsWorldReady)
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
        ChatLog.Add(ChatSender.Player, text);
        if (!AgentState.Connected)
        {
            ChatLog.Add(ChatSender.System, "The agent is not connected. Start pi with the StardewPilot extension, then try again.");
            return;
        }
        this.bridge?.Broadcast("chat/user_message", new { text });
    }

    private void ReceiveAgentMessage(string text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        if (kind == "tip")
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

    /// <summary>Tell connected agents that something happened in the game.</summary>
    private void Notify(string type, object? data = null) =>
        this.bridge?.Broadcast("notifications/game_event", new { type, data });

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            this.bridge?.Dispose();
            ConnectionFile.Delete();
        }
        base.Dispose(disposing);
    }
}
