using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewPilot.Bridge;
using StardewPilot.Tools;
using StardewValley;

namespace StardewPilot;

/// <summary>Mod entry point: owns the bridge and wires game events to it.</summary>
public sealed class ModEntry : Mod
{
    private ModConfig config = new();
    private BridgeServer? bridge;
    private ToolRegistry? tools;

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

        helper.Events.GameLoop.GameLaunched += this.OnGameLaunched;
        helper.Events.GameLoop.UpdateTicked += this.OnUpdateTicked;
        helper.Events.GameLoop.SaveLoaded += this.OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += (_, _) => this.Notify("day_started");
        helper.Events.GameLoop.Saved += (_, _) => this.Notify("saved");
        helper.Events.GameLoop.ReturnedToTitle += (_, _) => this.Notify("returned_to_title");
        helper.Events.Content.LocaleChanged += (_, _) => StateTools.ResetNameIndex();
    }

    private void OnGameLaunched(object? sender, GameLaunchedEventArgs e)
    {
        try
        {
            this.bridge = new BridgeServer(this.config.Port, this.config.Token, this.Monitor);
            this.bridge.Register("initialize", (_, _) => Task.FromResult<object?>(new
            {
                protocolVersion = "2025-06-18",
                serverInfo = new { name = "stardewpilot-bridge", version = this.ModManifest.Version.ToString() },
                capabilities = new { tools = new { } },
                gameVersion = Game1.version,
            }));
            this.bridge.Register("ping", (_, _) => Task.FromResult<object?>(new { }));
            this.bridge.Register("tools/list", (_, _) => Task.FromResult<object?>(this.tools!.List()));
            this.bridge.Register("tools/call", (p, _) => this.tools!.Call(p));
            this.bridge.Start();

            ConnectionFile.Write(this.config.Port, this.config.Token, Game1.version, this.ModManifest.Version.ToString());
            this.Monitor.Log($"Bridge listening on 127.0.0.1:{this.config.Port}. Connection details: {ConnectionFile.FilePath}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            this.Monitor.Log($"Could not start the bridge on port {this.config.Port}: {ex.Message}. Change Port in config.json if another program uses it.", LogLevel.Error);
        }
    }

    private bool autoLoadDone;

    private void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        MainThread.Drain();

        // Dev: hold the clock still (same technique as SMAPI's world_freezetime).
        if (this.config.Dev.FreezeTime && Context.IsWorldReady)
            Game1.gameTimeInterval = 0;

        // Dev: load a save automatically once the title screen is up.
        if (!this.autoLoadDone && this.config.Dev.AutoLoadSave.Length > 0 && Game1.activeClickableMenu is StardewValley.Menus.TitleMenu && e.Ticks > 120)
        {
            this.autoLoadDone = true;
            this.Monitor.Log($"Dev: auto-loading save {this.config.Dev.AutoLoadSave}.", LogLevel.Info);
            StardewValley.SaveGame.Load(this.config.Dev.AutoLoadSave);
            Game1.exitActiveMenu();
        }
    }

    private void OnSaveLoaded(object? sender, SaveLoadedEventArgs e)
    {
        this.Notify("save_loaded");
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
