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

        var info = new ProcessStartInfo(cfg.PiCommand)
        {
            RedirectStandardInput = true, // RPC mode reads commands from stdin; keeping it open keeps pi running
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(extension)!,
        };
        foreach (string arg in new[] { "--mode", "rpc", "--no-builtin-tools", "-e", extension })
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
            this.monitor.Log($"Launcher: could not start '{cfg.PiCommand}': {ex.Message}. Install pi or set Launcher.PiCommand to its full path.", LogLevel.Error);
            return;
        }
        if (this.process == null)
            return;
        // Drain output so pi never blocks on a full pipe. RPC events are not needed here; errors go to the SMAPI log.
        this.process.OutputDataReceived += (_, _) => { };
        this.process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) this.monitor.Log($"pi: {e.Data}", LogLevel.Trace); };
        this.process.BeginOutputReadLine();
        this.process.BeginErrorReadLine();
        this.process.EnableRaisingEvents = true;
        this.process.Exited += (_, _) => this.monitor.Log($"Launcher: pi exited (code {this.process?.ExitCode}).", LogLevel.Info);
        this.monitor.Log($"Launcher: started pi (pid {this.process.Id}).", LogLevel.Info);
    }

    public void Dispose()
    {
        try
        {
            if (this.IsRunning)
            {
                this.process!.StandardInput.Close(); // pi exits when stdin closes
                if (!this.process.WaitForExit(3000))
                    this.process.Kill(entireProcessTree: true);
            }
        }
        catch { /* shutting down */ }
        this.process?.Dispose();
    }
}
