using Newtonsoft.Json;
using StardewPilot.Platform;

namespace StardewPilot.Bridge;

/// <summary>
/// Writes the bridge URL and token to a per-user file so the agent can find the bridge
/// (~/.config/StardewPilot/bridge.json on Linux, %APPDATA%\StardewPilot\bridge.json on Windows).
/// </summary>
internal static class ConnectionFile
{
    public static string DirectoryPath => ConfigFolder.Path;

    public static string FilePath => ConfigFolder.File("bridge.json");

    public static void Write(string url, int port, string token, string gameVersion, string modVersion)
    {
        string json = JsonConvert.SerializeObject(new
        {
            url,
            port,
            token,
            pid = System.Environment.ProcessId,
            gameVersion,
            modVersion,
        }, Formatting.Indented);
        PrivateFile.Write(FilePath, json);
    }

    public static void Delete()
    {
        try { System.IO.File.Delete(FilePath); } catch { /* best effort */ }
    }
}
