using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Menus;
using StardewValley.Pathfinding;

namespace StardewPilot.Autopilot;

/// <summary>A way out of a map: walk into a warp, or use a door with the action button.</summary>
internal sealed record Exit(string From, Point Tile, string To, Point Arrival, bool IsDoor);

/// <summary>
/// Walking. Within a map the game's own pathfinder (PathFindController) moves the farmer.
/// Between maps we plan a route over the map exits, because the game's pathfinder only covers one map;
/// walking into a warp triggers the game's own warp code, and doors are opened with the game's action code.
/// </summary>
internal static class Navigation
{
    private static readonly HashSet<string> BlockedExits = new();

    /// <summary>Forget exits that failed earlier today (called at day start).</summary>
    public static void ResetBlockedExits() => BlockedExits.Clear();

    private static string Key(Exit e) => $"{e.From}@{e.Tile.X},{e.Tile.Y}";

    /// <summary>Every exit the farmer can use out of a map.</summary>
    public static List<Exit> ExitsOf(GameLocation loc)
    {
        var exits = new List<Exit>();
        string from = loc.NameOrUniqueName;
        foreach (Warp w in loc.warps)
        {
            if (w.npcOnly.Value)
                continue;
            exits.Add(new Exit(from, new Point(w.X, w.Y), w.TargetName, new Point(w.TargetX, w.TargetY), IsDoor: false));
        }
        foreach (Point door in loc.doors.Keys)
        {
            Warp? w = loc.getWarpFromDoor(door, Game1.player);
            if (w != null)
                exits.Add(new Exit(from, door, w.TargetName, new Point(w.TargetX, w.TargetY), IsDoor: true));
        }
        foreach (Building b in loc.buildings)
        {
            GameLocation? indoors = b.GetIndoors();
            if (indoors == null || b.humanDoor.Value.X < 0 || b.isUnderConstruction())
                continue;
            var door = new Point(b.tileX.Value + b.humanDoor.Value.X, b.tileY.Value + b.humanDoor.Value.Y);
            Warp? first = indoors.warps.FirstOrDefault();
            exits.Add(new Exit(from, door, indoors.NameOrUniqueName, first != null ? new Point(first.X, first.Y - 1) : Point.Zero, IsDoor: true));
        }
        return exits;
    }

    /// <summary>Cheapest route (by estimated walking distance) from the farmer to a map, as a list of exits to use.</summary>
    public static List<Exit>? PlanRoute(string targetLocation)
    {
        GameLocation start = Game1.currentLocation;
        if (start.NameOrUniqueName.Equals(targetLocation, StringComparison.OrdinalIgnoreCase))
            return new List<Exit>();

        // Dijkstra over (map, arrival tile). Edge cost: Manhattan distance to the exit, plus one hop.
        var best = new Dictionary<string, int>();
        var queue = new PriorityQueue<(string loc, Point at, List<Exit> path), int>();
        queue.Enqueue((start.NameOrUniqueName, Game1.player.TilePoint, new List<Exit>()), 0);
        best[start.NameOrUniqueName] = 0;
        int expanded = 0;
        while (queue.TryDequeue(out var node, out int cost) && expanded++ < 2000)
        {
            if (node.loc.Equals(targetLocation, StringComparison.OrdinalIgnoreCase))
                return node.path;
            GameLocation? loc = Game1.getLocationFromName(node.loc);
            if (loc == null)
                continue;
            foreach (Exit exit in ExitsOf(loc))
            {
                if (BlockedExits.Contains(Key(exit)) || Game1.getLocationFromName(exit.To) == null)
                    continue;
                int next = cost + Math.Abs(exit.Tile.X - node.at.X) + Math.Abs(exit.Tile.Y - node.at.Y) + 5;
                if (best.TryGetValue(exit.To, out int known) && known <= next)
                    continue;
                best[exit.To] = next;
                queue.Enqueue((exit.To, exit.Arrival, new List<Exit>(node.path) { exit }), next);
            }
        }
        return null;
    }

    /// <summary>Walk to a map, and optionally to a tile in it (or next to it when <paramref name="adjacent"/>).</summary>
    public static IEnumerable WalkTo(JobContext ctx, string location, Point? tile, bool adjacent = false)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            string here = Game1.currentLocation.NameOrUniqueName;
            if (here.Equals(location, StringComparison.OrdinalIgnoreCase))
                break;
            List<Exit> route = PlanRoute(location) ?? throw JobContext.Fail($"No known way from {Game1.currentLocation.DisplayName} to {location}.");
            Exit hop = route[0];
            ctx.Progress($"walking to {location} (via {hop.To})");
            bool ok = false;
            foreach (object? step in UseExit(ctx, hop, r => ok = r))
                yield return step;
            if (!ok)
                BlockedExits.Add(Key(hop)); // try another way
        }
        if (!Game1.currentLocation.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase))
            throw JobContext.Fail($"Could not reach {location}: every route was blocked.");

        if (tile is { } target)
        {
            foreach (object? step in WalkWithinMap(ctx, target, adjacent))
                yield return step;
        }
    }

    /// <summary>Walk inside the current map. Retries with a fresh path if something blocks the way.</summary>
    public static IEnumerable WalkWithinMap(JobContext ctx, Point target, bool adjacent)
    {
        GameLocation loc = Game1.currentLocation;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (IsAt(target, adjacent))
                yield break;

            PathFindController.isAtEnd endTest = adjacent
                ? (node, end, _, _) => Math.Abs(node.x - end.X) + Math.Abs(node.y - end.Y) == 1
                : PathFindController.isAtEndPoint;
            Stack<Point>? path = PathFindController.findPath(Game1.player.TilePoint, target, endTest, loc, Game1.player, 30000);
            if (path == null)
                throw JobContext.Fail($"No path to tile ({target.X}, {target.Y}) in {loc.DisplayName}.");

            Point end = path.Last();
            ctx.Log($"walk start: {path.Count} steps to ({target.X},{target.Y})");
            Game1.player.controller = new PathFindController(path, loc, Game1.player, end);
            Point lastTile = Game1.player.TilePoint;
            ctx.Progress($"walking to ({target.X}, {target.Y}) in {loc.DisplayName}");
            while (Game1.player.controller != null && Game1.currentLocation == loc)
            {
                if (Game1.player.TilePoint != lastTile)
                {
                    lastTile = Game1.player.TilePoint;
                    ctx.Progress();
                }
                yield return null;
            }
            ctx.Log($"walk end at ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y})");
            // Walking into an exit starts a warp; the map only changes after the screen fades.
            for (int i = 0; i < 600 && Game1.currentLocation == loc && (Game1.isWarping || Game1.locationRequest != null); i++)
            {
                ctx.Touch();
                yield return null;
            }
            if (Game1.currentLocation != loc)
                yield break; // walked through an exit; the caller decides what next
        }
        if (!IsAt(target, adjacent))
            throw JobContext.Fail($"Could not get {(adjacent ? "next to" : "to")} tile ({target.X}, {target.Y}) in {loc.DisplayName}: the way is blocked.");
    }

    public static bool WarpInProgress() =>
        Game1.isWarping || Game1.locationRequest != null || Game1.fadeToBlack || Game1.globalFade;

    private static bool IsAt(Point target, bool adjacent)
    {
        Point p = Game1.player.TilePoint;
        int d = Math.Abs(p.X - target.X) + Math.Abs(p.Y - target.Y);
        return adjacent ? d == 1 : d == 0;
    }

    /// <summary>Use one exit: walk into a warp, or open a door.</summary>
    private static IEnumerable UseExit(JobContext ctx, Exit exit, Action<bool> result)
    {
        GameLocation from = Game1.currentLocation;
        if (exit.IsDoor)
        {
            // Stand below the door, face it, and press the action button through the game's own code.
            var below = new Point(exit.Tile.X, exit.Tile.Y + 1);
            foreach (object? step in WalkWithinMap(ctx, below, adjacent: false))
                yield return step;
            if (Game1.currentLocation != from)
            {
                result(true);
                yield break;
            }
            Game1.player.faceDirection(Game1.up);
            ctx.ExpectingMenu = true;
            Game1.tryToCheckAt(new Vector2(exit.Tile.X, exit.Tile.Y), Game1.player);
            yield return new WaitTicks(2);
            if (Game1.activeClickableMenu is DialogueBox box)
            {
                // The game refused (for example a shop that is closed) and said why.
                string why = box.getCurrentString();
                box.closeDialogue();
                Game1.exitActiveMenu();
                ctx.ExpectingMenu = false;
                throw JobContext.Fail($"Could not enter {exit.To}: {why}");
            }
            ctx.ExpectingMenu = false;
        }
        else
        {
            // The warp tile is usually just off the map edge; walking into it makes the game warp the farmer.
            foreach (object? step in WalkWithinMap(ctx, exit.Tile, adjacent: false))
                yield return step;
        }

        // Wait for the warp and fade to finish.
        for (int i = 0; i < 600 && (Game1.currentLocation == from || WarpInProgress()); i++)
        {
            ctx.Touch();
            yield return null;
        }
        bool arrived = Game1.currentLocation != from;
        if (arrived)
            ctx.Progress($"arrived in {Game1.currentLocation.DisplayName}");
        result(arrived);
    }
}
