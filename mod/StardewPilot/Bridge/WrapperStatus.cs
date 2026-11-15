using System;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;

namespace StardewPilot.Bridge;

/// <summary>
/// Reads the agent wrapper's status file (agent-status.json, next to bridge.json). The wrapper writes it,
/// so the game can show why the copilot is not connected even when pi is not running.
/// The file holds the settings page link with its access key: never log it.
/// </summary>
internal static class WrapperStatus
{
    private static string FilePath => Path.Combine(ConnectionFile.DirectoryPath, "agent-status.json");

    private static DateTime lastCheck = DateTime.MinValue;
    private static DateTime lastWrite = DateTime.MinValue;
    private static Snapshot current = Snapshot.None;

    internal sealed record Snapshot(string State, string Message, string Model, string SettingsUrl)
    {
        public static readonly Snapshot None = new("none", "", "", "");
    }

    /// <summary>The wrapper's latest status. Re-reads the file at most once a second.</summary>
    public static Snapshot Current
    {
        get
        {
            DateTime now = DateTime.UtcNow;
            if ((now - lastCheck).TotalSeconds < 1)
                return current;
            lastCheck = now;
            try
            {
                if (!File.Exists(FilePath))
                    return current = Snapshot.None;
                DateTime write = File.GetLastWriteTimeUtc(FilePath);
                if (write == lastWrite && current != Snapshot.None)
                    return current;
                lastWrite = write;
                // Open so that the wrapper can replace the file while we read (Windows blocks that by default).
                string text;
                using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream))
                    text = reader.ReadToEnd();
                JObject json = JObject.Parse(text);
                string state = json.Value<string>("state") ?? "none";
                int pid = json.Value<int?>("pid") ?? 0;
                if (state != "stopped" && !IsAlive(pid))
                    state = "stopped"; // the wrapper died without saying so
                current = new Snapshot(state, json.Value<string>("message") ?? "", json.Value<string>("model") ?? "", state == "stopped" ? "" : json.Value<string>("settingsUrl") ?? "");
            }
            catch
            {
                // half-written or unreadable: keep the last good value
            }
            return current;
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid <= 0)
            return false;
        try
        {
            using Process p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>What to tell the player when the agent is not connected.</summary>
    public static string NotConnectedHint()
    {
        Snapshot s = Current;
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
        Snapshot s = Current;
        if (s.SettingsUrl.Length == 0 || !s.SettingsUrl.StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
            return "The settings page is served by the copilot wrapper, which is not running. Set Launcher.Enabled to true in config.json and restart the game, or run: node <mod folder>/agent/src/wrapper/main.ts setup";
        try
        {
            ProcessStartInfo info = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(s.SettingsUrl) { UseShellExecute = true }
                : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
            if (!OperatingSystem.IsWindows())
                info.ArgumentList.Add(s.SettingsUrl);
            Process.Start(info)?.Dispose();
            return "Opened the copilot settings in your browser.";
        }
        catch (Exception ex)
        {
            monitor.Log($"Could not open the browser: {ex.Message}", LogLevel.Warn);
            return $"Could not open a browser. The link is in {FilePath} (keep it private).";
        }
    }
}
