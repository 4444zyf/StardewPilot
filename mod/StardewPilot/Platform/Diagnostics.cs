using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StardewPilot.Platform;

/// <summary>Find a program on PATH the way the operating system would (with PATHEXT on Windows).</summary>
internal static class ExecutableFinder
{
    public static string? Find(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        if (Path.IsPathRooted(command) || command.Contains('/') || command.Contains('\\'))
            return File.Exists(command) ? command : null;
        IEnumerable<string> exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Prepend("")
            : new[] { "" };
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim('"'), command + ext);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // a malformed PATH entry
                }
            }
        }
        return null;
    }
}

/// <summary>What pilot_diagnose reports. Filled by the mod; secrets are passed only so they can be removed.</summary>
internal sealed record DiagnoseInfo(
    string ModVersion,
    string GameVersion,
    string SmapiVersion,
    bool LauncherEnabled,
    bool UseWrapper,
    string NodeCommand,
    string PiCommand,
    string ExtensionPath,
    string BridgeUrl,
    int AgentsConnected,
    IReadOnlyCollection<string> Secrets);

/// <summary>Builds the pilot_diagnose report: a self-check a tester can paste into an issue. Never contains secrets.</summary>
internal static class Diagnostics
{
    public static string Build(DiagnoseInfo info)
    {
        var sb = new StringBuilder();
        void Line(string label, object? value) => sb.Append(label).Append(": ").Append(value).Append('\n');

        Line("StardewPilot", info.ModVersion);
        Line("Game / SMAPI", $"{info.GameVersion} / {info.SmapiVersion}");
        Line("System", $"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        Line("Bridge", $"{info.BridgeUrl} (agents connected: {info.AgentsConnected})");
        Line("Launcher", info.LauncherEnabled ? (info.UseWrapper ? "on, through the wrapper" : "on, starting pi directly") : "off");

        string? node = ExecutableFinder.Find(info.NodeCommand);
        string? pi = ExecutableFinder.Find(info.PiCommand);
        Line("Node", node ?? $"NOT FOUND ('{info.NodeCommand}'); set Launcher.NodeCommand to its full path");
        Line("pi", pi ?? $"NOT FOUND ('{info.PiCommand}'); install it with npm install -g @earendil-works/pi-coding-agent");
        Line("Extension", File.Exists(info.ExtensionPath) ? info.ExtensionPath : $"MISSING at {info.ExtensionPath}");

        WrapperStatusInfo? status = WrapperStatusInfo.Read(ConfigFolder.File("agent-status.json"));
        if (status == null)
            Line("Wrapper", "status file unreadable");
        else if (status == WrapperStatusInfo.None)
            Line("Wrapper", "has not run");
        else
        {
            bool alive = ProcessCheck.IsAlive(status.Pid);
            Line("Wrapper", $"{status.State}{(status.State != "stopped" && !alive ? " (but its process is gone)" : "")}; {status.Message}");
            Line("Model", status.Model.Length > 0 ? status.Model : "(none chosen)");
            if (status.PiPid is int piPid)
                Line("pi process", ProcessCheck.IsAlive(piPid) ? "running" : "not running");
        }
        Line("Files", $"{ConfigFolder.Path} (bridge.json {(File.Exists(ConfigFolder.File("bridge.json")) ? "present" : "missing")}, agent-status.json {(File.Exists(ConfigFolder.File("agent-status.json")) ? "present" : "missing")})");
        sb.Append("This report contains no keys, tokens or links. Paste it into a GitHub issue if you need help.");
        return Scrub(sb.ToString(), info.Secrets);
    }

    /// <summary>Remove anything secret, whatever the report picked up on the way (for example a wrapper message).</summary>
    public static string Scrub(string text, IEnumerable<string> secrets)
    {
        foreach (string secret in secrets.Where(s => !string.IsNullOrEmpty(s) && s.Length >= 8))
            text = text.Replace(secret, "[removed]");
        // The settings link carries its key after #t=.
        return System.Text.RegularExpressions.Regex.Replace(text, @"#t=[A-Za-z0-9_\-]+", "#t=[removed]");
    }
}
