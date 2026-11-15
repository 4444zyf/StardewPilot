using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;

namespace StardewPilot.Autopilot;

/// <summary>
/// Holds the tool button for the fishing minigame. The minigame reads the previous frame's keyboard
/// state (Game1.oldKBState) to decide whether the button is held, so setting it before the game's update
/// is the same as a player holding the key. Nothing else in the game is affected.
/// </summary>
internal static class InputSim
{
    /// <summary>Whether the tool button should read as held during the next update.</summary>
    public static bool ToolHeld { get; set; }

    private static Keys ToolKey =>
        Game1.options.useToolButton.Length > 0 && Game1.options.useToolButton[0].key != Keys.None ? Game1.options.useToolButton[0].key : Keys.C;

    /// <summary>Called on UpdateTicking, before the game's update.</summary>
    public static void BeforeGameUpdate()
    {
        if (ToolHeld)
            Game1.oldKBState = new KeyboardState(ToolKey);
    }

    public static void ReleaseAll() => ToolHeld = false;

    /// <summary>Presses we simulate never reach SMAPI's input events, so nothing is mistaken for the player.</summary>
    public static bool IsSimulated(SButton button) => false;
}
