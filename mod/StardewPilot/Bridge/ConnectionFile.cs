using System;
using System.IO;
using System.Runtime.InteropServices;
using Newtonsoft.Json;

namespace StardewPilot.Bridge;

/// <summary>
/// Writes the port and token to a per-user file so the agent can find the bridge
/// (~/.config/StardewPilot/bridge.json on Linux, %APPDATA%\StardewPilot\bridge.json on Windows).
/// </summary>
internal static class ConnectionFile
{
    public static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StardewPilot");

    public static string FilePath => Path.Combine(DirectoryPath, "bridge.json");

    public static void Write(int port, string token, string gameVersion, string modVersion)
    {
        Directory.CreateDirectory(DirectoryPath);
        string json = JsonConvert.SerializeObject(new
        {
            url = $"ws://127.0.0.1:{port}/",
            port,
            token,
            pid = Environment.ProcessId,
            gameVersion,
            modVersion,
        }, Formatting.Indented);

        // Create the file empty, restrict it to the owner, then write the secret.
        File.WriteAllText(FilePath, "");
        RestrictToOwner(FilePath);
        File.WriteAllText(FilePath, json);
    }

    public static void Delete()
    {
        try { File.Delete(FilePath); } catch { /* best effort */ }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chmod(string path, uint mode);

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try { chmod(path, 0b110_000_000); } catch { /* best effort */ }
        }
        // On Windows, %APPDATA% is already private to the user.
    }
}
