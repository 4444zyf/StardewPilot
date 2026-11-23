using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Tools;

namespace StardewPilot.Autopilot;

/// <summary>
/// Fishing controller, using the game's own rod functions (the same calls a player's clicks make):
/// hold to charge the cast, release to cast, press when a fish nibbles, then play the minigame by
/// holding the button whenever the fish is above the green bar.
/// </summary>
internal static class Fishing
{
    public static IEnumerable Fish(JobContext ctx, ModConfig cfg, string? location, int catches)
    {
        FishingRod rod = Actions.FindTool<FishingRod>() ?? throw JobContext.Fail(I18n.Get("fail.no-rod"));
        GameLocation loc = StateTools.ResolveLocation(location);
        foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
            yield return s;
        if (!loc.canFishHere())
            throw JobContext.Fail(I18n.Get("fail.cannot-fish", new { place = loc.DisplayName }));

        List<(Point stand, int dir)> spots = FindSpots(loc);
        if (spots.Count == 0)
            throw JobContext.Fail(I18n.Get("fail.no-water", new { place = loc.DisplayName }) + $" ({DebugInfo})");
        ctx.Log($"fishing spots: {string.Join(", ", spots.Take(5).Select(sp => $"({sp.stand.X},{sp.stand.Y}) dir {sp.dir}"))}");

        var caught = new List<string>();
        int spotIndex = 0, misses = 0;
        bool standing = false;
        while (caught.Count < catches)
        {
            if (Game1.player.Stamina <= Actions.EnergyReserve(cfg) + 8)
                throw JobContext.Partial($"Stopped fishing to keep energy in reserve after {caught.Count} catches.");
            if (Game1.player.isInventoryFull())
                throw JobContext.Partial($"The backpack is full after {caught.Count} catches.");
            if (spotIndex >= spots.Count || misses >= 4)
                break;

            var (stand, dir) = spots[spotIndex];
            if (!standing)
            {
                bool reached = true;
                IEnumerator walk = Navigation.WalkWithinMap(ctx, stand, adjacent: false).GetEnumerator();
                while (true)
                {
                    bool more;
                    try { more = walk.MoveNext(); }
                    catch (JobStop) { reached = false; break; }
                    if (!more) break;
                    yield return walk.Current;
                }
                if (!reached || Game1.player.TilePoint != stand) { spotIndex++; continue; }
                standing = true;
            }
            Game1.player.faceDirection(dir);
            Game1.player.CurrentToolIndex = Game1.player.Items.IndexOf(rod);
            var before = Game1.player.Items.Where(i => i != null).GroupBy(i => i.QualifiedItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));

            // Cast the way the game does when the tool button is pressed and let go at once (a short cast).
            ctx.ExpectingMenu = true;
            ctx.Progress("casting");
            Game1.player.lastClick = Vector2.Zero;
            Game1.player.BeginUsingTool();
            // Release at full charge (about four tiles out), so it lands past the shallow edge.
            // The game casts on the next tick (no button is held), so the power is set in this same tick.
            rod.castingPower = 1f;
            ctx.Log($"cast started: timing={rod.isTimingCast}");
            for (int i = 0; i < 300 && !rod.isFishing; i++) { ctx.Touch(); yield return null; }
            ctx.Log($"cast from ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y}) facing {Game1.player.FacingDirection}: fishing={rod.isFishing} timing={rod.isTimingCast} casting={rod.isCasting} inAir={rod.castedButBobberStillInAir} usingTool={Game1.player.UsingTool} tool={Game1.player.CurrentTool?.Name}");
            if (!rod.isFishing)
            {
                // The bobber did not land in fishable water: try the next spot.
                foreach (object? s in WaitForFarmerFree(ctx)) yield return s;
                spotIndex++; standing = false; misses++;
                continue;
            }

            // Wait for a bite (up to about a minute), then strike.
            ctx.Progress("waiting for a bite");
            for (int i = 0; i < 60 * 60 && !rod.isNibbling && rod.isFishing; i++) { ctx.Touch(); yield return null; }
            if (!rod.isNibbling)
            {
                Reel(rod); // reel in and try again
                foreach (object? s in WaitForFarmerFree(ctx)) yield return s;
                misses++;
                continue;
            }
            Reel(rod); // strike: the same call a click makes while a fish nibbles
            // The strike animation runs before the minigame (or the catch, for trash) appears.
            for (int i = 0; i < 180 && !(Game1.activeClickableMenu is BobberBar || rod.isReeling || rod.fishCaught || rod.pullingOutOfWater || rod.showingTreasure); i++)
            {
                ctx.Touch();
                yield return null;
            }

            // The minigame (no minigame for trash: it comes straight out).
            float lastDistance = -1;
            bool hadMinigame = false;
            for (int i = 0; i < 60 * 60 && (Game1.activeClickableMenu is BobberBar || rod.isReeling); i++)
            {
                ctx.Touch();
                if (Game1.activeClickableMenu is BobberBar bar)
                {
                    hadMinigame = true;
                    lastDistance = bar.distanceFromCatching;
                    float fish = bar.bobberPosition + 16f;
                    float barCenter = bar.bobberBarPos + bar.bobberBarHeight / 2f;
                    float predicted = barCenter + bar.bobberBarSpeed * 8f;
                    InputSim.ToolHeld = fish < predicted;
                }
                yield return null;
            }
            InputSim.ToolHeld = false;
            ctx.Log($"bite: minigame={hadMinigame} lastProgress={lastDistance:0.00} caught={rod.fishCaught}");

            // After the minigame the fish is pulled out and held up; a click puts it away. Treasure opens a chest menu.
            int holding = 0;
            for (int i = 0; i < 600; i++)
            {
                ctx.Touch();
                if (Game1.activeClickableMenu is ItemGrabMenu grab)
                {
                    foreach (Item item in grab.ItemsToGrabMenu.actualInventory.Where(x => x != null).ToList())
                    {
                        if (Game1.player.addItemToInventory(item) == null)
                            grab.ItemsToGrabMenu.actualInventory.Remove(item);
                    }
                    grab.exitThisMenu();
                }
                else if (rod.fishCaught)
                {
                    if (++holding == 40)
                        rod.doneHoldingFish(Game1.player);
                }
                else if (i > 90 && !rod.pullingOutOfWater && !rod.showingTreasure && !rod.isReeling && Game1.activeClickableMenu == null)
                {
                    break;
                }
                yield return null;
            }
            foreach (object? s in WaitForFarmerFree(ctx)) yield return s;
            ctx.ExpectingMenu = false;

            var after = Game1.player.Items.Where(i => i != null).GroupBy(i => i.QualifiedItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));
            var gained = after.Where(kv => kv.Value > before.GetValueOrDefault(kv.Key)).Select(kv => ItemRegistry.GetDataOrErrorItem(kv.Key).DisplayName).ToList();
            if (gained.Count > 0)
            {
                caught.AddRange(gained);
                misses = 0;
                ctx.Progress($"fishing ({caught.Count} caught)");
            }
            else
            {
                misses++;
            }
        }
        ctx.ExpectingMenu = false;
        InputSim.ReleaseAll();
        ctx.Report["caught"] = caught.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}");
        ctx.Report["energy"] = (int)Game1.player.Stamina;
        if (caught.Count < catches)
            ctx.Problems.Add($"Caught {caught.Count} of {catches}; the fish got away or no spot worked.");
    }

    /// <summary>What the game does when the player clicks while the line is out: strike if a fish nibbles, else reel in.</summary>
    private static void Reel(FishingRod rod)
    {
        Farmer p = Game1.player;
        rod.DoFunction(p.currentLocation, (int)p.lastClick.X, (int)p.lastClick.Y, 1, p);
    }

    private static IEnumerable WaitForFarmerFree(JobContext ctx)
    {
        for (int i = 0; i < 300 && (Game1.player.UsingTool || !Game1.player.CanMove); i++)
        {
            ctx.Touch();
            yield return null;
        }
        Actions.ReleaseFarmer();
    }

    public static string DebugInfo = "";

    /// <summary>Land tiles next to open water, with the direction to face. Open water: four water tiles in a row, fishable from the third (a full cast lands about four tiles out).</summary>
    private static List<(Point stand, int dir)> FindSpots(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var dirs = new (int dx, int dy, int dir)[] { (0, -1, Game1.up), (1, 0, Game1.right), (0, 1, Game1.down), (-1, 0, Game1.left) };
        Point me = Game1.player.TilePoint;
        var spots = new List<(Point, int)>();
        for (int x = 1; x < w - 1; x++)
        {
            for (int y = 1; y < h - 1; y++)
            {
                if (loc.isWaterTile(x, y) || !Navigation.IsWalkable(loc, new Point(x, y)))
                    continue;
                foreach (var (dx, dy, dir) in dirs)
                {
                    bool open = true;
                    for (int k = 1; k <= 4 && open; k++)
                        open = loc.isWaterTile(x + dx * k, y + dy * k) && (k < 3 || loc.isTileFishable(x + dx * k, y + dy * k));
                    if (open) { spots.Add((new Point(x, y), dir)); break; }
                }
            }
        }
        // Keep the nearest spots the farmer can actually walk to (rivers have two banks).
        HashSet<Point> reachable = Navigation.ReachableTiles(loc);
        DebugInfo = $"{spots.Count} shore spots, none reachable from ({me.X},{me.Y})";
        return spots.Where(s => reachable.Contains(s.Item1))
            .OrderBy(s => Math.Abs(s.Item1.X - me.X) + Math.Abs(s.Item1.Y - me.Y)).Take(6).ToList();
    }
}
