using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Menus;
using StardewValley.Monsters;
using StardewValley.Tools;
using SObject = StardewValley.Object;

namespace StardewPilot.Autopilot;

/// <summary>
/// Basic mines and combat: go down a number of floors. On each floor take the ladder if one is showing;
/// otherwise break the nearest rocks with the pickaxe until a ladder appears. Monsters that come next to
/// the farmer are fought with the weapon (real button presses). Leaves when health or energy runs low.
/// </summary>
internal static class Mining
{
    public static IEnumerable Descend(JobContext ctx, ModConfig cfg, int floors)
    {
        Pickaxe pick = Actions.FindTool<Pickaxe>() ?? throw JobContext.Fail(I18n.Get("fail.no-pickaxe"));
        MeleeWeapon? weapon = Game1.player.Items.OfType<MeleeWeapon>().FirstOrDefault(w => !w.isScythe());

        if (Game1.currentLocation is not MineShaft)
        {
            foreach (object? s in Navigation.WalkTo(ctx, "Mine", null))
                yield return s;
            Point? entrance = FindAction(Game1.currentLocation, a => a == "Mine" || a == "NextMineLevel");
            if (entrance is not { } door)
                throw JobContext.Fail(I18n.Get("fail.no-way-down"));
            foreach (object? s in Navigation.WalkWithinMap(ctx, door, adjacent: true))
                yield return s;
            ctx.ExpectingMenu = true;
            Actions.Interact(door);
            foreach (object? s in WaitForLevelChange(ctx, null))
                yield return s;
            if (Game1.activeClickableMenu is MineElevatorMenu)
            {
                // The elevator lets players pick a floor; take the deepest one reached, like a player would.
                Game1.exitActiveMenu();
                Game1.enterMine(Math.Max(1, Game1.player.deepestMineLevel / 5 * 5));
                foreach (object? s in WaitForLevelChange(ctx, null))
                    yield return s;
            }
            ctx.ExpectingMenu = false;
            if (Game1.currentLocation is not MineShaft)
                throw JobContext.Fail(I18n.Get("fail.cannot-enter-mines"));
        }

        int startLevel = ((MineShaft)Game1.currentLocation).mineLevel;
        int brokenRocks = 0, monstersHit = 0;
        while (Game1.currentLocation is MineShaft shaft && shaft.mineLevel < startLevel + floors)
        {
            Farmer p = Game1.player;
            if (p.health < p.maxHealth * 0.3f)
                throw JobContext.Partial($"Health is low ({p.health}/{p.maxHealth}); stopped on floor {shaft.mineLevel}. Eat something or leave the mines.");
            if (p.Stamina <= Actions.EnergyReserve(cfg) + 4)
                throw JobContext.Partial($"Energy is low; stopped on floor {shaft.mineLevel}.");

            // Fight anything next to us first.
            Monster? close = shaft.characters.OfType<Monster>().Where(m => !m.IsInvisible && m.Health > 0)
                .OrderBy(m => Vector2.Distance(m.Tile, p.Tile)).FirstOrDefault();
            if (close != null && weapon != null && Vector2.Distance(close.Tile, p.Tile) <= 1.6f)
            {
                p.CurrentToolIndex = p.Items.IndexOf(weapon);
                Actions.Face(close.TilePoint);
                foreach (object? st in Actions.UseTool(ctx, cfg, weapon, close.TilePoint))
                    yield return st;
                monstersHit++;
                ctx.Progress($"fighting on floor {shaft.mineLevel}");
                continue;
            }

            // Ladder showing? Take it.
            Point? ladder = FindLadder(shaft);
            if (ladder is { } down)
            {
                int level = shaft.mineLevel;
                bool reached = true;
                IEnumerator walk = Navigation.WalkWithinMap(ctx, down, adjacent: true).GetEnumerator();
                while (true)
                {
                    bool more;
                    try { more = walk.MoveNext(); } catch (JobStop) { reached = false; break; }
                    if (!more) break;
                    yield return walk.Current;
                }
                if (reached)
                {
                    Actions.Interact(down);
                    foreach (object? s in WaitForLevelChange(ctx, level))
                        yield return s;
                    ctx.Progress($"reached floor {(Game1.currentLocation as MineShaft)?.mineLevel}");
                    continue;
                }
            }

            // Otherwise break the nearest reachable rock.
            Point me = p.TilePoint;
            var rocks = shaft.objects.Pairs.Where(kv => kv.Value.IsBreakableStone() || kv.Value.Name == "Stone")
                .Select(kv => new Point((int)kv.Key.X, (int)kv.Key.Y))
                .OrderBy(t => Math.Abs(t.X - me.X) + Math.Abs(t.Y - me.Y)).Take(6).ToList();
            if (rocks.Count == 0)
                throw JobContext.Partial($"No rocks left and no ladder on floor {shaft.mineLevel}.");
            bool broke = false;
            foreach (Point rock in rocks)
            {
                bool reached = true;
                IEnumerator walk = Navigation.WalkWithinMap(ctx, rock, adjacent: true).GetEnumerator();
                while (true)
                {
                    bool more;
                    try { more = walk.MoveNext(); } catch (JobStop) { reached = false; break; }
                    if (!more) break;
                    yield return walk.Current;
                }
                if (!reached) continue;
                for (int i = 0; i < 6 && shaft.objects.ContainsKey(new Vector2(rock.X, rock.Y)); i++)
                {
                    foreach (object? s in Actions.UseTool(ctx, cfg, pick, rock))
                        yield return s;
                }
                brokenRocks++;
                broke = true;
                break;
            }
            if (!broke)
                throw JobContext.Partial($"Could not reach any rock on floor {shaft.mineLevel}.");
        }

        ctx.Report["floor"] = (Game1.currentLocation as MineShaft)?.mineLevel;
        ctx.Report["deepestFloorEver"] = Game1.player.deepestMineLevel;
        ctx.Report["rocksBroken"] = brokenRocks;
        ctx.Report["swingsAtMonsters"] = monstersHit;
        ctx.Report["health"] = $"{Game1.player.health}/{Game1.player.maxHealth}";
        ctx.Report["energy"] = (int)Game1.player.Stamina;
    }

    private static IEnumerable WaitForLevelChange(JobContext ctx, int? fromLevel)
    {
        GameLocation start = Game1.currentLocation;
        for (int i = 0; i < 600; i++)
        {
            bool moved = Game1.currentLocation != start && !Navigation.WarpInProgress();
            if (moved || Game1.activeClickableMenu is MineElevatorMenu)
                yield break;
            ctx.Touch();
            yield return null;
        }
    }

    private static Point? FindLadder(MineShaft shaft)
    {
        var layer = shaft.Map.GetLayer("Buildings");
        Point me = Game1.player.TilePoint;
        Point? best = null;
        int bestD = int.MaxValue;
        for (int x = 0; x < layer.LayerWidth; x++)
            for (int y = 0; y < layer.LayerHeight; y++)
                if (layer.Tiles[x, y]?.TileIndex == 173)
                {
                    int d = Math.Abs(x - me.X) + Math.Abs(y - me.Y);
                    if (d < bestD) { bestD = d; best = new Point(x, y); }
                }
        return best;
    }

    private static Point? FindAction(GameLocation loc, Func<string, bool> match)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                string[] a = loc.GetTilePropertySplitBySpaces("Action", "Buildings", x, y);
                if (a.Length > 0 && match(a[0]))
                    return new Point(x, y);
            }
        return null;
    }
}
