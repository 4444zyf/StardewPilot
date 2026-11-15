using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace StardewPilot.Autopilot;

/// <summary>
/// Presses buttons the way a player would, through SMAPI's input layer, so the game runs its normal input code
/// (used for fishing, where casting, hooking and the minigame all read the tool button).
/// SMAPI applies a pressed override for one tick, so held buttons are re-pressed every tick.
/// </summary>
internal static class InputSim
{
    private static readonly HashSet<SButton> Held = new();
    private static IReflectedMethod? overrideButton;

    public static void Init(IModHelper helper)
    {
        overrideButton = null;
        try { overrideButton = helper.Reflection.GetMethod(Game1.input, "OverrideButton"); }
        catch { /* not available: fishing reports it cannot run */ }
    }

    public static bool Available => overrideButton != null;

    /// <summary>The player's tool button (the first keyboard binding).</summary>
    public static SButton ToolButton => Game1.options.useToolButton.Length > 0 ? Game1.options.useToolButton[0].ToSButton() : SButton.C;

    public static void Hold(SButton button) => Held.Add(button);

    public static void Release(SButton button) => Held.Remove(button);

    public static void ReleaseAll() => Held.Clear();

    /// <summary>Whether a button press came from us (so it does not count as the player taking control).</summary>
    public static bool IsSimulated(SButton button) => Held.Contains(button) || recentlyReleased.Contains(button);

    private static readonly HashSet<SButton> recentlyReleased = new();

    /// <summary>Called every update tick: keep held buttons down for the next tick.</summary>
    public static void Tick()
    {
        recentlyReleased.Clear();
        if (overrideButton == null)
            return;
        foreach (SButton b in Held)
        {
            overrideButton.Invoke(b, true);
            recentlyReleased.Add(b);
        }
    }
}
