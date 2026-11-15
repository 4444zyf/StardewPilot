using System;
using System.Diagnostics;
using System.IO;
using StardewModdingAPI;

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
        if (!File.Exists(extension))
        {
            this.monitor.Log($"Launcher: the pi extension was not found at {extension}. Set Launcher.ExtensionPath in config.json.", LogLevel.Error);
            return;
        }

        // With the wrapper: node <agent>/src/wrapper/main.ts run. It runs pi and serves the settings page.
        string wrapper = Path.Combine(Path.GetDirectoryName(extension)!, "wrapper", "main.ts");
        bool useWrapper = cfg.UseWrapper && File.Exists(wrapper);
        if (cfg.UseWrapper && !useWrapper)
            this.monitor.Log($"Launcher: the wrapper was not found at {wrapper}; starting pi directly.", LogLevel.Warn);
        if (!useWrapper && OperatingSystem.IsWindows())
        {
            // On Windows `pi` is a .cmd script, which cannot be started directly. The wrapper starts pi through node instead.
            this.monitor.Log("Launcher: starting pi without the wrapper is not supported on Windows. Set Launcher.UseWrapper to true, or start pi yourself in a terminal.", LogLevel.Error);
            return;
        }
        this.command = useWrapper ? cfg.NodeCommand : cfg.PiCommand;

        var info = new ProcessStartInfo(this.command)
        {
            RedirectStandardInput = true, // the child exits when stdin closes; keeping it open keeps it running
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(extension)!,
        };
        string[] args = useWrapper
            ? new[] { wrapper, "run", "--pi", cfg.PiCommand, "--extension", extension }
            : new[] { "--mode", "rpc", "--no-builtin-tools", "-e", extension };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);
        if (cfg.Model.Length > 0)
        {
            info.ArgumentList.Add("--model");
            info.ArgumentList.Add(cfg.Model);
        }

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
