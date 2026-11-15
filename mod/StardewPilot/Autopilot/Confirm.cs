using System;
using System.Collections;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace StardewPilot.Autopilot;

/// <summary>Kinds of irreversible actions that need the player's yes.</summary>
internal enum Permission
{
    Ship,
    Sell,
    Spend,
    Gift,
    Trash,
    OthersProperty,
}

/// <summary>Asks the player in an in-game question box before irreversible actions.</summary>
internal static class Confirm
{
    public static bool HasStanding(ModConfig cfg, Permission kind)
    {
        if (cfg.Autopilot.FullSelfPlay && kind != Permission.OthersProperty)
            return true; // full self-play switches confirmations off, except other players' things in co-op
        return kind switch
        {
            Permission.Ship => cfg.Autopilot.AlwaysAllow.Ship,
            Permission.Sell => cfg.Autopilot.AlwaysAllow.Sell,
            Permission.Spend => cfg.Autopilot.AlwaysAllow.Spend,
            Permission.Gift => cfg.Autopilot.AlwaysAllow.Gift,
            Permission.Trash => cfg.Autopilot.AlwaysAllow.Trash,
            _ => false,
        };
    }

    private static void Grant(ModConfig cfg, Permission kind)
    {
        switch (kind)
        {
            case Permission.Ship: cfg.Autopilot.AlwaysAllow.Ship = true; break;
            case Permission.Sell: cfg.Autopilot.AlwaysAllow.Sell = true; break;
            case Permission.Spend: cfg.Autopilot.AlwaysAllow.Spend = true; break;
            case Permission.Gift: cfg.Autopilot.AlwaysAllow.Gift = true; break;
            case Permission.Trash: cfg.Autopilot.AlwaysAllow.Trash = true; break;
        }
    }

    /// <summary>Ask the player. Calls <paramref name="answer"/> with true for yes.</summary>
    public static IEnumerable Ask(JobContext ctx, ModConfig cfg, IModHelper helper, Permission kind, string question, Action<bool> answer)
    {
        if (HasStanding(cfg, kind))
        {
            answer(true);
            yield break;
        }

        string? choice = null;
        ctx.ExpectingMenu = true;
        ctx.Progress("waiting for the player to confirm");
        var responses = kind == Permission.OthersProperty
            ? new[] { new Response("yes", "Yes"), new Response("no", "No") }
            : new[] { new Response("yes", "Yes"), new Response("always", "Yes, and don't ask again"), new Response("no", "No") };
        Game1.currentLocation.createQuestionDialogue($"StardewPilot: {question}", responses, (_, key) => choice = key);

        // Up to two minutes; closing the box counts as no.
        for (int i = 0; i < 60 * 120 && choice == null; i++)
        {
            ctx.Touch();
            if (i > 5 && Game1.activeClickableMenu is not DialogueBox)
                break;
            yield return null;
        }
        // The question box takes a moment to close after the answer.
        for (int i = 0; i < 120 && Game1.activeClickableMenu is DialogueBox; i++)
        {
            ctx.Touch();
            yield return null;
        }
        if (Game1.activeClickableMenu is DialogueBox)
            Game1.exitActiveMenu();
        ctx.ExpectingMenu = false;
        if (choice == "always")
        {
            Grant(cfg, kind);
            helper.WriteConfig(cfg);
        }
        answer(choice is "yes" or "always");
    }
}
