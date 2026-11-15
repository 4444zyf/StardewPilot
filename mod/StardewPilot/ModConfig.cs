using StardewModdingAPI;
using StardewPilot.Guide;

namespace StardewPilot;

/// <summary>Settings stored in SMAPI's standard config.json.</summary>
public sealed class ModConfig
{
    /// <summary>TCP port for the bridge WebSocket. It listens on 127.0.0.1 only.</summary>
    public int Port { get; set; } = 45781;

    /// <summary>Random secret the agent must present. Generated on first run when empty.</summary>
    public string Token { get; set; } = "";

    /// <summary>Key that opens the private copilot chat.</summary>
    public SButton ChatKey { get; set; } = SButton.F8;

    /// <summary>How often the copilot speaks up on its own: Off, Important, or Talkative.</summary>
    public Chattiness Chattiness { get; set; } = Chattiness.Important;

    /// <summary>Most proactive tips per game day. Warnings do not count.</summary>
    public int MaxTipsPerDay { get; set; } = 6;

    /// <summary>Instant fixed-text warnings (selling a bundle item, about to pass out).</summary>
    public bool Warnings { get; set; } = true;

    /// <summary>Allow the copilot to talk about content this save has not unlocked yet.</summary>
    public bool TellMeEverything { get; set; } = false;

    /// <summary>In single-player, stop the clock while the agent is thinking.</summary>
    public bool PauseWhileThinking { get; set; } = true;

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
