using System;
using StardewModdingAPI;
using StardewPilot.Platform;

namespace StardewPilot.Bridge;

/// <summary>
/// The agent wrapper's status, from its status file (agent-status.json, next to bridge.json). The wrapper writes it,
/// so the game can show why the copilot is not connected even when pi is not running.
/// The file holds the settings page link with its access key: never log it.
/// </summary>
internal static class WrapperStatus
{
    private static string FilePath => ConfigFolder.File("agent-status.json");

    private static DateTime lastCheck = DateTime.MinValue;
    private static WrapperStatusInfo current = WrapperStatusInfo.None;

    /// <summary>The wrapper's latest status. Re-reads the file at most once a second.</summary>
    public static WrapperStatusInfo Current
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < 1)
                return current;
            lastCheck = now;
            WrapperStatusInfo? read = WrapperStatusInfo.Read(FilePath);
            if (read == null)
                return current; // half-written: keep the last good value
            if (read.State != "stopped" && read != WrapperStatusInfo.None && !ProcessCheck.IsAlive(read.Pid))
                read = read with { State = "stopped", SettingsUrl = "" }; // the wrapper died without saying so
            if (read.State == "stopped")
                read = read with { SettingsUrl = "" };
            return current = read;
        }
    }

    /// <summary>What to tell the player when the agent is not connected.</summary>
    public static string NotConnectedHint()
    {
        WrapperStatusInfo s = Current;
        return s.State switch
        {
            "needs_setup" => "The copilot needs setting up: type /settings to open the settings page.",
            "starting" => "The copilot is starting. Try again in a moment.",
            "running" => "The copilot is connecting. Try again in a moment.",
            "error" => $"The copilot stopped: {s.Message} Type /settings to check its settings.",
            _ => "The copilot is not running. Set Launcher.Enabled to true in the mod's config.json, or start pi with the StardewPilot extension.",
        };
    }

    /// <summary>Open the settings page in the player's browser. Returns a message for the chat.</summary>
    public static string OpenSettings(IMonitor monitor)
    {
        lastCheck = DateTime.MinValue; // read fresh
        WrapperStatusInfo s = Current;
        if (s.SettingsUrl.Length == 0)
            return "The settings page is served by the copilot wrapper, which is not running. Set Launcher.Enabled to true in config.json and restart the game, or run: node <mod folder>/agent/src/wrapper/main.ts setup";
        string? error = Browser.Open(s.SettingsUrl);
        if (error == null)
            return "Opened the copilot settings in your browser.";
        monitor.Log($"Could not open the browser: {error}", LogLevel.Warn);
        return $"Could not open a browser. The link is in {FilePath} (keep it private).";
    }
}
