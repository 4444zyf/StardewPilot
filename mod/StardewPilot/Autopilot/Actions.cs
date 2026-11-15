using System;
using System.Collections;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewPilot.Autopilot;

/// <summary>Single farmer actions, each done the way the game does it for a player.</summary>
internal static class Actions
{
    /// <summary>Select the first inventory slot whose item matches.</summary>
    public static bool Select(Func<Item, bool> match)
    {
        Farmer p = Game1.player;
        for (int i = 0; i < p.Items.Count; i++)
        {
            if (p.Items[i] is { } item && match(item))
            {
                p.CurrentToolIndex = i;
                return true;
            }
        }
        return false;
    }

    /// <summary>Select a slot that is empty or holds a tool, so the action button does not gift or place anything.</summary>
    public static void SelectSafeHand()
    {
        Farmer p = Game1.player;
        if (p.CurrentItem == null || p.CurrentItem is Tool)
            return;
        for (int i = 0; i < p.Items.Count; i++)
        {
            if (p.Items[i] == null || p.Items[i] is Tool)
            {
                p.CurrentToolIndex = i;
                return;
            }
        }
    }

    public static T? FindTool<T>() where T : Tool => Game1.player.Items.OfType<T>().FirstOrDefault();

    public static MeleeWeapon? FindScythe() => Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w => w.isScythe());

    /// <summary>Face a neighbouring tile.</summary>
    public static void Face(Point tile)
    {
        Point p = Game1.player.TilePoint;
        int dx = tile.X - p.X, dy = tile.Y - p.Y;
        int dir = Math.Abs(dx) > Math.Abs(dy) ? (dx > 0 ? Game1.right : Game1.left) : (dy > 0 ? Game1.down : Game1.up);
        if (dx != 0 || dy != 0)
            Game1.player.faceDirection(dir);
    }

    /// <summary>Energy the farmer keeps in reserve; tool use stops before going below it.</summary>
    public static float EnergyReserve(ModConfig config) =>
        Math.Max(config.Autopilot.MinEnergy, Game1.player.MaxStamina * 0.05f);

    /// <summary>Use a tool on a neighbouring tile, with the game's normal animation, energy and time cost.</summary>
    public static IEnumerable UseTool(JobContext ctx, ModConfig config, Tool tool, Point tile)
    {
        Farmer p = Game1.player;
        if (p.Stamina <= EnergyReserve(config) && tool is not MeleeWeapon)
            throw JobContext.Partial($"Stopped to keep energy in reserve (energy {(int)p.Stamina}/{p.MaxStamina}). Eat something or rest.");

        int slot = p.Items.IndexOf(tool);
        if (slot < 0)
            throw JobContext.Fail($"The {tool.DisplayName} is not in the backpack.");
        p.CurrentToolIndex = slot;
        Face(tile);
        // Same as clicking the tile: the game aims tools at lastClick when it is within reach.
        p.lastClick = new Vector2(tile.X * 64 + 32, tile.Y * 64 + 32);

        // Wait until the farmer is free to act (previous animation finished).
        ctx.Log($"tool {tool.Name} wait-ready (using={p.UsingTool} canMove={p.CanMove})");
        for (int i = 0; i < 120 && (p.UsingTool || !p.CanMove); i++)
            yield return null;
        ctx.Log($"tool {tool.Name} begin");

        if (tool is MeleeWeapon weapon)
        {
            weapon.leftClick(p);
            p.BeginUsingTool();
        }
        else
        {
            p.BeginUsingTool();
        }
        yield return new WaitTicks(2);
        for (int i = 0; i < 180 && p.UsingTool; i++)
        {
            ctx.Touch();
            yield return null;
        }
        // The game frees the farmer when the player lets go of the tool button. Nobody holds it here,
        // so do the same release step it does (Game1 update: useToolButtonReleased).
        if (!p.UsingTool && !p.CanMove)
        {
            p.canReleaseTool = true;
            p.CanMove = true;
        }
        ctx.Log($"tool {tool.Name} done (using={p.UsingTool} canMove={p.CanMove})");
        ctx.Progress();
    }

    /// <summary>Press the action button on a neighbouring tile (talk, open, harvest, pick up).</summary>
    public static bool Interact(Point tile)
    {
        Face(tile);
        Game1.player.lastClick = new Vector2(tile.X * 64 + 32, tile.Y * 64 + 32);
        return Game1.tryToCheckAt(new Vector2(tile.X, tile.Y), Game1.player);
    }

    /// <summary>Place the held object (seed, fertilizer) on a neighbouring tile.</summary>
    public static bool PlaceHeld(Point tile)
    {
        if (Game1.player.ActiveObject is not SObject obj)
            return false;
        Face(tile);
        return Utility.tryToPlaceItem(Game1.currentLocation, obj, tile.X * 64 + 32, tile.Y * 64 + 32);
    }

    /// <summary>Whether the farmer is in the middle of an animation (using a tool, holding up an item, frozen).</summary>
    public static bool FarmerBusy()
    {
        Farmer p = Game1.player;
        return !p.CanMove || p.UsingTool || p.freezePause > 0 || p.FarmerSprite.PauseForSingleAnimation || Navigation.WarpInProgress();
    }

    /// <summary>
    /// Wait for the result of an action to settle: until a menu opens, or the farmer has been idle for half a second.
    /// Some messages only appear after an animation (a gift box shows its message about three seconds later).
    /// </summary>
    public static IEnumerable Settle(JobContext ctx, int maxTicks = 600)
    {
        int idle = 0;
        for (int i = 0; i < maxTicks && Game1.activeClickableMenu == null; i++)
        {
            idle = FarmerBusy() ? 0 : idle + 1;
            if (i > 10 && idle > 30)
                yield break;
            ctx.Touch();
            yield return null;
        }
    }

    /// <summary>Close any dialogue box the last action opened, returning its text.</summary>
    public static string? CloseDialogue()
    {
        if (Game1.activeClickableMenu is DialogueBox box)
        {
            string text = box.getCurrentString();
            box.closeDialogue();
            if (Game1.activeClickableMenu is DialogueBox)
                Game1.exitActiveMenu();
            return text;
        }
        return null;
    }

    /// <summary>Read out every page of an NPC dialogue, then close it.</summary>
    public static IEnumerable ReadAndCloseDialogue(JobContext ctx, Action<string> collect)
    {
        for (int pages = 0; pages < 12 && Game1.activeClickableMenu is DialogueBox box; pages++)
        {
            collect(box.getCurrentString());
            box.receiveLeftClick(0, 0); // advances like a click; finishes the typing animation first
            yield return new WaitTicks(2);
            if (Game1.activeClickableMenu is DialogueBox again && again == box && again.getCurrentString() == null)
                break;
            ctx.Touch();
        }
        if (Game1.activeClickableMenu is DialogueBox)
            Game1.exitActiveMenu();
    }
}
