using System;
using System.Diagnostics;
using System.IO;
using System.Net;

namespace StardewPilot.Platform;

/// <summary>Whether a process is still running.</summary>
internal static class ProcessCheck
{
    public static bool IsAlive(int pid)
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
}

/// <summary>Opening a link in the player's browser.</summary>
internal static class Browser
{
    /// <summary>How the link is opened on this system. Separate from Open so tests can check it without opening anything.</summary>
    public static ProcessStartInfo StartInfoFor(string url)
    {
        if (OperatingSystem.IsWindows())
            return new ProcessStartInfo(url) { UseShellExecute = true }; // the shell opens the default browser
        var info = new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(url);
        return info;
    }

    /// <summary>Open a local page. Returns null on success, else the error.</summary>
    public static string? Open(string url)
    {
        if (!url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal) && !url.StartsWith("http://localhost:", StringComparison.Ordinal))
            return "Only local pages are opened.";
        try
        {
            Process.Start(StartInfoFor(url))?.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}

/// <summary>What the launcher needs to start the agent.</summary>
internal sealed record AgentLaunchOptions(bool UseWrapper, string NodeCommand, string PiCommand, string Extension, string Model);

/// <summary>How the mod starts the agent: the wrapper with node (all systems), or pi directly (development, not on Windows).</summary>
internal static class AgentStart
{
    /// <summary>The start info, or an error message when the agent cannot be started this way.</summary>
    public static (ProcessStartInfo? Info, string? Error, bool UsesWrapper) Build(AgentLaunchOptions o)
    {
        if (!File.Exists(o.Extension))
            return (null, $"the pi extension was not found at {o.Extension}. Set Launcher.ExtensionPath in config.json.", false);
        string wrapper = Path.Combine(Path.GetDirectoryName(o.Extension)!, "wrapper", "main.ts");
        bool useWrapper = o.UseWrapper && File.Exists(wrapper);
        if (!useWrapper && OperatingSystem.IsWindows())
            return (null, "starting pi without the wrapper is not supported on Windows (pi is a .cmd script there). Set Launcher.UseWrapper to true, or start pi yourself in a terminal.", false);

        var info = new ProcessStartInfo(useWrapper ? o.NodeCommand : o.PiCommand)
        {
            RedirectStandardInput = true, // the child exits when stdin closes; keeping it open keeps it running
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(o.Extension)!,
        };
        string[] args = useWrapper
            ? new[] { wrapper, "run", "--pi", o.PiCommand, "--extension", o.Extension }
            : new[] { "--mode", "rpc", "--no-builtin-tools", "-e", o.Extension };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        if (o.Model.Length > 0)
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(o.Model);
        }
        return (info, null, useWrapper);
    }
}

/// <summary>The bridge's HTTP listener on the loopback interface.</summary>
internal static class Loopback
{
    /// <summary>
    /// Start listening on 127.0.0.1. On Windows the HTTP service may refuse that prefix to a non-admin user; then
    /// "localhost" is used, which it allows. Returns the listener and the WebSocket URL clients must use (the
    /// Host header has to match the prefix).
    /// </summary>
    public static (HttpListener Listener, string Url) Start(int port)
    {
        foreach (string host in new[] { "127.0.0.1", "localhost" })
        {
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://{host}:{port}/");
            try
            {
                listener.Start();
                return (listener, $"ws://{host}:{port}/");
            }
            catch (HttpListenerException) when (host == "127.0.0.1" && OperatingSystem.IsWindows())
            {
                listener.Close(); // try localhost
            }
        }
        throw new InvalidOperationException("unreachable");
    }
}
