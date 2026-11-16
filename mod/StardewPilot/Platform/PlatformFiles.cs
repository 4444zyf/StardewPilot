using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace StardewPilot.Platform;

// Everything in this folder is the mod's operating-system-specific code. It must not use the game, SMAPI or
// Newtonsoft, so the test project in mod/Platform.Tests can compile it on machines without the game (Windows CI).

/// <summary>The per-user folder shared with the agent wrapper (bridge.json, agent-status.json).</summary>
internal static class ConfigFolder
{
    /// <summary>Tests point this elsewhere.</summary>
    public static string? Override { get; set; }

    /// <summary>
    /// %APPDATA%\StardewPilot on Windows; $XDG_CONFIG_HOME/StardewPilot or ~/.config/StardewPilot on Linux and macOS.
    /// Must match configDir() in agent/src/wrapper/files.ts.
    /// </summary>
    public static string Path => Override ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewPilot");

    public static string File(string name) => System.IO.Path.Combine(Path, name);
}

/// <summary>Files readable only by their owner.</summary>
internal static class PrivateFile
{
    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    /// <summary>Create the file empty, restrict it to the owner, then write the content (so the secret is never readable by others).</summary>
    public static void Write(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, "");
        RestrictToOwner(path);
        System.IO.File.WriteAllText(path, content);
    }

    public static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { chmod(path, 0b110_000_000); } catch { /* best effort */ }
        }
        // Windows: %APPDATA% is private to the user already.
    }
}

/// <summary>The wrapper's status file (agent-status.json), read while the wrapper may replace it.</summary>
internal sealed record WrapperStatusInfo(string State, string Message, string Model, string SettingsUrl, int Pid, int? PiPid)
{
    public static readonly WrapperStatusInfo None = new("none", "", "", "", 0, null);

    /// <summary>
    /// Open for reading while letting other processes write or delete the file. Windows still refuses a rename over
    /// a file that is open (checked in CI), so callers keep it open only for the read; the wrapper retries its rename.
    /// </summary>
    public static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Read the status file, holding it open only for the read so the wrapper's replace (with retries) gets through.
    /// Returns null when the file is missing or half-written.
    /// </summary>
    public static WrapperStatusInfo? Read(string path)
    {
        if (!System.IO.File.Exists(path))
            return None;
        string text;
        try
        {
            using FileStream stream = OpenShared(path);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(text);
            JsonElement root = doc.RootElement;
            string Str(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            int? Int(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            return new WrapperStatusInfo(Str("state") is { Length: > 0 } s ? s : "none", Str("message"), Str("model"), Str("settingsUrl"), Int("pid") ?? 0, Int("piPid"));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
