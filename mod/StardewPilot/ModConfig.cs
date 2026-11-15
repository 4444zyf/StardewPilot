namespace StardewPilot;

/// <summary>Settings stored in SMAPI's standard config.json.</summary>
public sealed class ModConfig
{
    /// <summary>TCP port for the bridge WebSocket. It listens on 127.0.0.1 only.</summary>
    public int Port { get; set; } = 45781;

    /// <summary>Random secret the agent must present. Generated on first run when empty.</summary>
    public string Token { get; set; } = "";

    /// <summary>Developer options. Leave at defaults for normal play.</summary>
    public DevConfig Dev { get; set; } = new();
}

public sealed class DevConfig
{
    /// <summary>Save folder name to load automatically at the title screen (for example Test_454395324). Empty to disable.</summary>
    public string AutoLoadSave { get; set; } = "";

    /// <summary>Freeze the game clock after a save loads, so test state does not drift.</summary>
    public bool FreezeTime { get; set; } = false;
}
