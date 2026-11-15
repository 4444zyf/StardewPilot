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

    /// <summary>Autopilot rules.</summary>
    public AutopilotConfig Autopilot { get; set; } = new();

    /// <summary>Developer options. Leave at defaults for normal play.</summary>
    public DevConfig Dev { get; set; } = new();
}

public sealed class AutopilotConfig
{
    /// <summary>Key that switches the autopilot on or off (and stops a running job).</summary>
    public SButton ToggleKey { get; set; } = SButton.F9;

    /// <summary>Game time when work stops and the farmer heads to bed (2am is 2600).</summary>
    public int StopWorkAt { get; set; } = 2330;

    /// <summary>Tool use stops before energy drops below this (or 5% of max, whichever is higher).</summary>
    public int MinEnergy { get; set; } = 10;

    /// <summary>A job that makes no progress for this long is stopped.</summary>
    public int WatchdogSeconds { get; set; } = 15;

    /// <summary>Purchases above this total ask the player first.</summary>
    public int ConfirmSpendAbove { get; set; } = 500;

    /// <summary>Full self-play: no confirmations at all (except other players' things in co-op).</summary>
    public bool FullSelfPlay { get; set; } = false;

    /// <summary>Standing permissions, set here or by answering "don't ask again".</summary>
    public StandingPermissions AlwaysAllow { get; set; } = new();
}

public sealed class StandingPermissions
{
    public bool Ship { get; set; }
    public bool Sell { get; set; }
    public bool Spend { get; set; }
    public bool Gift { get; set; }
    public bool Trash { get; set; }
}

public sealed class DevConfig
{
    /// <summary>Save folder name to load automatically at the title screen (for example Test_454395324). Empty to disable.</summary>
    public string AutoLoadSave { get; set; } = "";

    /// <summary>Freeze the game clock after a save loads, so test state does not drift.</summary>
    public bool FreezeTime { get; set; } = false;
}
