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
    private static string Key(Exit e) => $"{e.From}@{e.Tile.X},{e.Tile.Y}";

    /// <summary>
    /// An exit that failed, and the exits beside it to the same map (a map edge is a strip of warp tiles: when one is
    /// unreachable, its neighbours usually are too). Remembered only for the walk that found them, so a way cleared
    /// later in the day is tried again.
    /// </summary>
    private static IEnumerable<string> BlockWithNeighbours(Exit failed) =>
        ExitsOf(Game1.getLocationFromName(failed.From) ?? Game1.currentLocation)
            .Where(e => e.To.Equals(failed.To, StringComparison.OrdinalIgnoreCase) && e.IsDoor == failed.IsDoor
                && Math.Abs(e.Tile.X - failed.Tile.X) + Math.Abs(e.Tile.Y - failed.Tile.Y) <= 3)
            .Select(Key)
            .Append(Key(failed));

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

    /// <summary>A map's name for the player: its display name when it is loaded, else its id.</summary>
    private static string PlaceName(string id)
    {
        GameLocation? loc = Game1.getLocationFromName(id);
        if (loc?.ParentBuilding is { } building)
            return StardewPilot.Tools.AnimalTools.BuildingName(building);
        return loc?.DisplayName ?? id;
    }

    private static bool IsBuildingInterior(string id) => Game1.getLocationFromName(id)?.ParentBuilding != null;

    /// <summary>A reason for a failed route, for the job's message.</summary>
    private static string WhyNoRoute(string target, HashSet<string> blocked)
    {
        GameLocation? loc = Game1.getLocationFromName(target);
        if (loc == null)
            return I18n.Get("route.not-loaded");
        Building? b = loc.ParentBuilding;
        Utility.ForEachBuilding(x =>
        {
            if (x.GetIndoorsName()?.Equals(target, StringComparison.OrdinalIgnoreCase) == true)
                b ??= x;
            return b == null;
        });
        if (b != null)
        {
            if (b.isUnderConstruction())
                return I18n.Get("route.under-construction");
            if (b.humanDoor.Value.X < 0)
                return I18n.Get("route.no-door");
            GameLocation? outside = b.GetParentLocation();
            if (outside != null && ExitsOf(outside).FirstOrDefault(e => e.To.Equals(target, StringComparison.OrdinalIgnoreCase)) is { } door
                && blocked.Contains(Key(door)))
            {
                string? what = Blockers.Words(Blockers.Between(outside, DoorStand(outside, door.Tile), 2));
                return I18n.Get("route.door-blocked", new { tile = $"{door.Tile.X},{door.Tile.Y}" }) + (what != null ? " " + I18n.Get("route.blocked-by", new { what }) : "");
            }
        }
        return I18n.Get("route.locked");
    }

    /// <summary>Cheapest route (by estimated walking distance) from the farmer to a map, as a list of exits to use.</summary>
    public static List<Exit>? PlanRoute(string targetLocation, HashSet<string>? blocked = null)
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
                if (blocked?.Contains(Key(exit)) == true || Game1.getLocationFromName(exit.To) == null)
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
        var blocked = new HashSet<string>();
        int failures = 0;
        string? firstWhy = null;
        for (int hops = 0; hops < 16 && failures < 4; hops++)
        {
            string here = Game1.currentLocation.NameOrUniqueName;
            if (here.Equals(location, StringComparison.OrdinalIgnoreCase))
                break;
            List<Exit> route = PlanRoute(location, blocked) ?? throw JobContext.Fail(I18n.Get("fail.no-route", new
            {
                from = Game1.currentLocation.DisplayName,
                to = PlaceName(location),
                // Every way was tried and failed here: say why the first one did, not "it may be locked".
                why = firstWhy != null && !IsBuildingInterior(location) ? I18n.Get("route.ways-blocked") + " " + firstWhy : WhyNoRoute(location, blocked),
            }));
            Exit hop = route[0];
            ctx.Progress($"walking to {location} (via {hop.To})");
            bool ok = false;
            string? why = null;
            IEnumerator exit = UseExit(ctx, hop, r => ok = r).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = exit.MoveNext(); }
                catch (JobStop stop) when (stop.NoPath)
                {
                    why = stop.Message;
                    break;
                }
                if (!more) break;
                yield return exit.Current;
            }
            if (!ok)
            {
                firstWhy ??= why;
                failures++;
                foreach (string key in BlockWithNeighbours(hop))
                    blocked.Add(key); // try another way
                ctx.Log($"exit to {hop.To} blocked: {why}");
            }
        }
        if (!Game1.currentLocation.NameOrUniqueName.Equals(location, StringComparison.OrdinalIgnoreCase))
            throw JobContext.Fail(I18n.Get("fail.all-routes-blocked", new { to = PlaceName(location) }));

        if (tile is { } target)
        {
            foreach (object? step in WalkWithinMap(ctx, target, adjacent))
                yield return step;
        }
    }

    /// <summary>Walk inside the current map. Retries with a fresh path if something blocks the way.</summary>
    /// <param name="clearTrees">Also cut down wild trees and stumps in the way (clear_way), not only weeds, stones and twigs.</param>
    public static IEnumerable WalkWithinMap(JobContext ctx, Point target, bool adjacent, bool clearTrees = false)
    {
        GameLocation loc = Game1.currentLocation;
        bool stalled = false;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (IsAt(target, adjacent))
                yield break;

            PathFindController.isAtEnd endTest = adjacent
                ? (node, end, _, _) => Math.Abs(node.x - end.X) + Math.Abs(node.y - end.Y) == 1
                : PathFindController.isAtEndPoint;
            Stack<Point>? path = clearTrees ? null : PathFindController.findPath(Game1.player.TilePoint, target, endTest, loc, Game1.player, 30000);

            // The game's pathfinder walks over beds, and stepping on a bed makes the game ask "Go to sleep for the night?",
            // which stops the job. Go around beds unless the bed is where the walk ends (going to bed).
            // A villager standing still on the path makes the game's walker give up after a few seconds: go around them.
            HashSet<Point> avoid = BedTiles(loc);
            avoid.Remove(target);
            if (stalled)
                foreach (Point t in OccupiedTiles(loc))
                    avoid.Add(t);
            if (path != null && avoid.Count > 0 && (stalled || path.Any(avoid.Contains)))
            {
                List<Point>? around = PathThroughDebris(loc, Game1.player.TilePoint, target, adjacent, avoid);
                if (around != null)
                {
                    ctx.Log($"path to ({target.X},{target.Y}) crossed {(stalled ? "a villager or animal" : "a bed")}; going around");
                    foreach (object? step in WalkClearing(ctx, loc, around, clearTrees))
                        yield return step;
                    if (Game1.currentLocation != loc)
                        yield break;
                    continue;
                }
            }

            if (path == null)
            {
                // Blocked by weeds, stones or twigs (or trees, for clear_way)? Walk through them, clearing them with tools like a player would.
                List<Point>? clearing = PathThroughDebris(loc, Game1.player.TilePoint, target, adjacent, avoid, clearTrees);
                if (clearing == null && attempt < 2 && (loc.animals.Length > 0 || loc.characters.Count > 0))
                {
                    // An animal or villager may be standing in a gap; they move on every few seconds, so wait (up to
                    // ten seconds) and look again as soon as a way opens.
                    ctx.Log($"no path to ({target.X},{target.Y}); waiting for animals or villagers to move");
                    for (int i = 0; i < 600; i++)
                    {
                        ctx.Touch();
                        yield return null;
                        if (i % 30 == 29 && PathFindController.findPath(Game1.player.TilePoint, target, endTest, loc, Game1.player, 30000) != null)
                            break;
                    }
                    stalled = false;
                    continue;
                }
                if (clearing == null)
                    throw NoPathTo(ctx, loc, target);
                foreach (object? step in WalkClearing(ctx, loc, clearing, clearTrees))
                    yield return step;
                if (Game1.currentLocation != loc)
                    yield break;
                continue;
            }

            Point end = path.Last();
            Point from = Game1.player.TilePoint;
            ctx.Log($"walk start: {path.Count} steps to ({target.X},{target.Y})");
            Game1.player.controller = new PathFindController(path, loc, Game1.player, end);
            Point lastTile = from;
            ctx.Progress($"walking to ({target.X}, {target.Y}) in {loc.DisplayName}");
            while (Game1.player.controller != null && Game1.currentLocation == loc && !WarpRequested())
            {
                if (Game1.player.TilePoint != lastTile)
                {
                    lastTile = Game1.player.TilePoint;
                    ctx.Progress();
                }
                yield return null;
            }
            ctx.Log($"walk end at ({Game1.player.TilePoint.X},{Game1.player.TilePoint.Y})");
            foreach (object? step in FinishWarp(ctx, loc))
                yield return step;
            if (Game1.currentLocation != loc)
                yield break; // walked through an exit; the caller decides what next
            if (Game1.player.TilePoint == from && !IsAt(target, adjacent))
            {
                stalled = true; // the game's walker gave up without a step: something stands in the way
                if (NextStepBlocker(loc, path) is { } who)
                    ctx.Log($"walk stalled: {who.Report} in the way");
            }
        }
        if (!IsAt(target, adjacent))
            throw NoPathTo(ctx, loc, target, adjacent);
    }

    /// <summary>A no-path failure that says what is in the way, and reports the blockers to the agent.</summary>
    private static JobStop NoPathTo(JobContext ctx, GameLocation loc, Point target, bool? adjacent = null)
    {
        var things = Blockers.Between(loc, target, besideTarget: adjacent == true);
        if (things.Count > 0)
            ctx.Report["blockedBy"] = Blockers.Report(things);
        string? what = Blockers.Words(things);
        string key = adjacent is null ? "fail.no-path" : adjacent.Value ? "fail.blocked-next-to" : "fail.blocked-to";
        string text = I18n.Get(key, new { tile = $"{target.X},{target.Y}", place = loc.DisplayName });
        return JobContext.NoPath(what != null ? text + " " + I18n.Get("route.blocked-by", new { what }) : text);
    }

    /// <summary>Tiles villagers and farm animals stand on.</summary>
    private static IEnumerable<Point> OccupiedTiles(GameLocation loc) =>
        loc.characters.Select(c => c.TilePoint).Concat(loc.animals.Values.Select(a => a.TilePoint));

    /// <summary>What stands on the first tiles of a path, if anything.</summary>
    private static Blockers.Thing? NextStepBlocker(GameLocation loc, Stack<Point> path) =>
        path.Take(3).Select(t => Blockers.At(loc, t)).FirstOrDefault(t => t != null);

    /// <summary>The game started a warp (the farmer stepped on a warp tile or used a door).</summary>
    private static bool WarpRequested() => Game1.isWarping || Game1.locationRequest != null;

    /// <summary>
    /// After a walk that may have stepped onto a warp: stop the game's path walker at once (left running, it keeps
    /// stepping onto the warp tile, asking for the warp again and again, and after the warp walks the farmer on in the
    /// new map using the old map's tiles), then wait for the warp and fade to finish.
    /// </summary>
    private static IEnumerable FinishWarp(JobContext ctx, GameLocation loc)
    {
        if (WarpRequested() || Game1.currentLocation != loc)
            Game1.player.controller = null;
        for (int i = 0; i < 600 && Game1.currentLocation == loc && WarpRequested(); i++)
        {
            ctx.Touch();
            yield return null;
        }
        if (Game1.currentLocation != loc)
            Game1.player.controller = null;
    }

    /// <summary>The tiles beds cover on a map: walking onto one makes the game offer to sleep.</summary>
    public static HashSet<Point> BedTiles(GameLocation loc)
    {
        var tiles = new HashSet<Point>();
        foreach (StardewValley.Objects.BedFurniture bed in loc.furniture.OfType<StardewValley.Objects.BedFurniture>())
        {
            Rectangle box = bed.GetBoundingBox();
            for (int x = box.Left / 64; x <= (box.Right - 1) / 64; x++)
                for (int y = box.Top / 64; y <= (box.Bottom - 1) / 64; y++)
                    tiles.Add(new Point(x, y));
        }
        return tiles;
    }

    /// <summary>Debris a player can clear with starting tools: weeds (scythe), stones (pickaxe), twigs (axe).</summary>
    public static bool IsClearableDebris(GameLocation loc, Point tile) =>
        loc.objects.TryGetValue(new Vector2(tile.X, tile.Y), out StardewValley.Object obj)
        && (obj.IsWeeds() || obj.IsBreakableStone() || obj.IsTwig());

    public static bool IsWalkable(GameLocation loc, Point t) =>
        !loc.isCollidingPosition(new Rectangle(t.X * 64 + 1, t.Y * 64 + 1, 62, 62), Game1.viewport, isFarmer: true, 0, glider: false, Game1.player, pathfinding: true);

    /// <summary>Every tile the farmer can walk to from where they stand (flood fill).</summary>
    public static HashSet<Point> ReachableTiles(GameLocation loc)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        var seen = new HashSet<Point> { Game1.player.TilePoint };
        var queue = new Queue<Point>(seen);
        while (queue.Count > 0)
        {
            Point p = queue.Dequeue();
            foreach (var d in new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) })
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (n.X < 0 || n.Y < 0 || n.X >= w || n.Y >= h || seen.Contains(n) || !IsWalkable(loc, n))
                    continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return seen;
    }

    /// <summary>A* where clearable debris is passable at extra cost. Returns the tiles to walk, start excluded.</summary>
    public static List<Point>? PathThroughDebris(GameLocation loc, Point start, Point target, bool adjacent, HashSet<Point>? avoid = null, bool trees = false)
    {
        int w = loc.Map.Layers[0].LayerWidth, h = loc.Map.Layers[0].LayerHeight;
        const int DebrisCost = 8, TreeCost = 30;
        var cost = new Dictionary<Point, int> { [start] = 0 };
        var from = new Dictionary<Point, Point>();
        var open = new PriorityQueue<Point, int>();
        open.Enqueue(start, 0);
        int expanded = 0;
        var dirs = new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) };
        while (open.TryDequeue(out Point cur, out _) && expanded++ < 40000)
        {
            int dist = Math.Abs(cur.X - target.X) + Math.Abs(cur.Y - target.Y);
            if (adjacent ? dist == 1 : dist == 0)
            {
                var path = new List<Point>();
                for (Point p = cur; p != start; p = from[p])
                    path.Add(p);
                path.Reverse();
                return path;
            }
            foreach (Point d in dirs)
            {
                var next = new Point(cur.X + d.X, cur.Y + d.Y);
                bool isTarget = next == target;
                bool inBounds = next.X >= 0 && next.Y >= 0 && next.X < w && next.Y < h;
                if (!inBounds && !isTarget)
                    continue;
                if (avoid != null && !isTarget && avoid.Contains(next))
                    continue;
                int step;
                if (!inBounds || IsWalkable(loc, next)) step = 1;
                else if (IsClearableDebris(loc, next)) step = DebrisCost;
                else if (trees && Blockers.Clearable(loc, next)) step = TreeCost;
                else continue;
                int c = cost[cur] + step;
                if (cost.TryGetValue(next, out int known) && known <= c)
                    continue;
                cost[next] = c;
                from[next] = cur;
                open.Enqueue(next, c + Math.Abs(next.X - target.X) + Math.Abs(next.Y - target.Y));
            }
        }
        return null;
    }

    /// <summary>Walk a precomputed path, clearing debris (and, for clear_way, trees) on it with the matching tool.</summary>
    private static IEnumerable WalkClearing(JobContext ctx, GameLocation loc, List<Point> path, bool trees = false)
    {
        bool InTheWay(Point t) => IsClearableDebris(loc, t) || (trees && !IsWalkable(loc, t) && Blockers.Clearable(loc, t));
        int i = 0;
        while (i < path.Count && Game1.currentLocation == loc)
        {
            // Walk the clear stretch up to the next thing to clear.
            int j = i;
            while (j < path.Count && !InTheWay(path[j]))
                j++;
            if (j > i)
            {
                var segment = new Stack<Point>(path.GetRange(i, j - i).AsEnumerable().Reverse());
                Game1.player.controller = new PathFindController(segment, loc, Game1.player, path[j - 1]);
                Point last = Game1.player.TilePoint;
                ctx.Progress("walking (clearing a way)");
                while (Game1.player.controller != null && Game1.currentLocation == loc && !WarpRequested())
                {
                    if (Game1.player.TilePoint != last) { last = Game1.player.TilePoint; ctx.Progress(); }
                    yield return null;
                }
                foreach (object? step in FinishWarp(ctx, loc))
                    yield return step;
                if (Game1.currentLocation != loc || Game1.player.TilePoint != path[j - 1])
                    yield break; // warped, or knocked off the path: the caller re-plans
            }
            if (j >= path.Count)
                yield break;

            // Clear what is in front: weeds with the scythe, stones with the pickaxe, twigs, trees and stumps with the axe.
            Point spot = path[j];
            Blockers.Thing what = Blockers.At(loc, spot) ?? new Blockers.Thing("object", "something");
            loc.objects.TryGetValue(new Vector2(spot.X, spot.Y), out StardewValley.Object? obj);
            StardewValley.Tool? tool = obj?.IsWeeds() == true ? (StardewValley.Tool?)Actions.FindScythe() ?? Actions.FindTool<StardewValley.Tools.Pickaxe>()
                : obj?.IsBreakableStone() == true ? Actions.FindTool<StardewValley.Tools.Pickaxe>()
                : Actions.FindTool<StardewValley.Tools.Axe>();
            if (tool == null)
                throw JobContext.Fail(I18n.Get("fail.debris-no-tool", new { thing = what.Word }));
            int hits = what.Key is "tree" or "stump" ? 40 : 6; // a tree takes about ten swings, then its stump about five
            for (int k = 0; k < hits && InTheWay(spot); k++)
            {
                foreach (object? s in Actions.UseTool(ctx, Config(), tool, spot))
                    yield return s;
            }
            if (InTheWay(spot))
                throw JobContext.Fail(I18n.Get("fail.debris-stuck", new { thing = what.Word, tile = $"{spot.X},{spot.Y}" }));
            if (what.Key is "tree" or "stump")
                ctx.Report["treesCleared"] = (ctx.Report.TryGetValue("treesCleared", out object? n) && n is int c ? c : 0) + 1;
            i = j;
        }
    }

    /// <summary>Set by the mod entry so walking can read the config (energy reserve).</summary>
    public static Func<ModConfig> Config = () => new ModConfig();

    public static bool WarpInProgress() =>
        Game1.isWarping || Game1.locationRequest != null || Game1.fadeToBlack || Game1.globalFade;

    private static bool IsAt(Point target, bool adjacent)
    {
        Point p = Game1.player.TilePoint;
        int d = Math.Abs(p.X - target.X) + Math.Abs(p.Y - target.Y);
        return adjacent ? d == 1 : d == 0;
    }

    /// <summary>Where to stand to use a door: below it if that tile is free, else a free tile beside or diagonally below it.</summary>
    private static Point DoorStand(GameLocation loc, Point door)
    {
        Point[] choices =
        {
            new(door.X, door.Y + 1), new(door.X - 1, door.Y + 1), new(door.X + 1, door.Y + 1), new(door.X - 1, door.Y), new(door.X + 1, door.Y),
        };
        foreach (Point p in choices)
            if (IsWalkable(loc, p))
                return p;
        return choices[0];
    }

    /// <summary>Use one exit: walk into a warp, or open a door.</summary>
    private static IEnumerable UseExit(JobContext ctx, Exit exit, Action<bool> result)
    {
        GameLocation from = Game1.currentLocation;
        if (exit.IsDoor)
        {
            // Stand next to the door, face it, and press the action button through the game's own code. Below the
            // door is usual; some doors (a cabin's) have their porch tile blocked, and the game also accepts a door
            // within one tile, diagonals included.
            var stand = DoorStand(from, exit.Tile);
            foreach (object? step in WalkWithinMap(ctx, stand, adjacent: false))
                yield return step;
            if (Game1.currentLocation != from)
            {
                result(true);
                yield break;
            }
            Game1.player.faceDirection(stand.Y > exit.Tile.Y ? Game1.up : stand.X < exit.Tile.X ? Game1.right : Game1.left);
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
                throw JobContext.Fail(I18n.Get("fail.cannot-enter", new { place = PlaceName(exit.To), why }));
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
        if (Game1.currentLocation != from)
            Game1.player.controller = null;
        bool arrived = Game1.currentLocation != from;
        if (arrived)
            ctx.Progress($"arrived in {Game1.currentLocation.DisplayName}");
        result(arrived);
    }
}
