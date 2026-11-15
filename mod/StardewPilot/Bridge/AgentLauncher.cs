using System;
using System.Diagnostics;
using System.IO;
using StardewModdingAPI;
using StardewPilot.Platform;

namespace StardewPilot.Bridge;

/// <summary>
/// One-click start: the mod launches pi in RPC mode with the StardewPilot extension and stops it when the game exits.
/// pi finds the bridge through the connection file, so nothing is passed on the command line except the extension.
/// </summary>
internal sealed class AgentLauncher : IDisposable
{
    private readonly IMonitor monitor;
    private Process? process;
    private string command = "";

    public AgentLauncher(IMonitor monitor) => this.monitor = monitor;

    public bool IsRunning => this.process is { HasExited: false };

    public void Start(LauncherConfig cfg, string modFolder)
    {
        if (this.IsRunning)
            return;
        string extension = cfg.ExtensionPath.Length > 0 ? cfg.ExtensionPath : Path.Combine(modFolder, "agent", "src", "index.ts");
        var (info, error, useWrapper) = AgentStart.Build(new AgentLaunchOptions(cfg.UseWrapper, cfg.NodeCommand, cfg.PiCommand, extension, cfg.Model));
        if (info == null)
        {
            this.monitor.Log($"Launcher: {error}", LogLevel.Error);
            return;
        }
        if (cfg.UseWrapper && !useWrapper)
            this.monitor.Log("Launcher: the wrapper was not found next to the extension; starting pi directly.", LogLevel.Warn);
        this.command = info.FileName;

        try
        {
            this.process = Process.Start(info);
        }
        catch (Exception ex)
        {
            this.monitor.Log(useWrapper
                ? $"Launcher: could not start '{cfg.NodeCommand}': {ex.Message}. Install Node.js 22.19 or newer, or set Launcher.NodeCommand to its full path."
                : $"Launcher: could not start '{cfg.PiCommand}': {ex.Message}. Install pi or set Launcher.PiCommand to its full path.", LogLevel.Error);
            return;
        }
        if (this.process == null)
            return;
        // Drain output so pi never blocks on a full pipe. RPC events are not needed here; errors go to the SMAPI log.
        this.process.OutputDataReceived += (_, _) => { };
        this.process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && !e.Data.Contains("#t=")) this.monitor.Log($"agent: {e.Data}", LogLevel.Trace); };
        this.process.BeginOutputReadLine();
        this.process.BeginErrorReadLine();
        this.process.EnableRaisingEvents = true;
        this.process.Exited += (_, _) => this.monitor.Log($"Launcher: {this.command} exited (code {this.process?.ExitCode}).", LogLevel.Info);
        this.monitor.Log($"Launcher: started {(useWrapper ? "the agent wrapper" : "pi")} (pid {this.process.Id}).", LogLevel.Info);
    }

    public void Dispose()
    {
        try
        {
            if (this.IsRunning)
            {
                this.process!.StandardInput.Close(); // pi and the wrapper exit when stdin closes
                if (!this.process.WaitForExit(5000)) // the wrapper gives pi 3 s
                    this.process.Kill(entireProcessTree: true);
            }
        }
        catch { /* shutting down */ }
        this.process?.Dispose();
    }
}
