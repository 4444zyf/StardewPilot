using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;

namespace StardewPilot.Autopilot;

/// <summary>
/// What stands on a tile, for failure messages ("a tree at (53,23)"), for use_tool's report (before and after a swing),
/// and for clear_way (what it may cut). Words for the player come from the i18n files; the English report is for the agent.
/// </summary>
internal static class Blockers
{
    /// <summary>A thing on a tile: an i18n key under "blocker.", an English word for the agent, and its health when it has one.</summary>
    public sealed record Thing(string Key, string English, string? Name = null, float? Health = null)
    {
        public string Word => I18n.Get("blocker." + this.Key, new { name = this.Name ?? "" });

        public string Report => this.Health is { } h ? $"{this.English} (health {h:0.#})" : this.English;
    }

    public static Thing? At(GameLocation loc, Point t)
    {
        var v = new Vector2(t.X, t.Y);
        if (loc.isCharacterAtTile(v) is { } npc)
            return new Thing("villager", npc.Name, npc.displayName);
        foreach (FarmAnimal a in loc.animals.Values)
            if (a.TilePoint == t)
                return new Thing("animal", $"an animal ({a.displayName})", a.displayName);
        if (loc.terrainFeatures.TryGetValue(v, out TerrainFeature? tf))
        {
            switch (tf)
            {
                case FruitTree:
                    return new Thing("fruit-tree", "a fruit tree");
                case Tree tree:
                    return tree.stump.Value
                        ? new Thing("stump", "a tree stump", Health: tree.health.Value)
                        : new Thing("tree", tree.tapped.Value ? "a tapped tree" : "a tree", Health: tree.health.Value);
                case HoeDirt { crop: not null }:
                    return new Thing("crop", "a crop");
            }
        }
        foreach (LargeTerrainFeature f in loc.largeTerrainFeatures)
            if (f.getBoundingBox().Contains(t.X * 64 + 32, t.Y * 64 + 32))
                return new Thing("bush", "a bush");
        foreach (ResourceClump c in loc.resourceClumps)
            if (c.occupiesTile(t.X, t.Y))
                return c.parentSheetIndex.Value switch
                {
                    ResourceClump.stumpIndex => new Thing("large-stump", "a large stump (needs a copper axe)", Health: c.health.Value),
                    ResourceClump.hollowLogIndex => new Thing("log", "a large log (needs a steel axe)", Health: c.health.Value),
                    _ => new Thing("boulder", "a boulder (needs a better pickaxe)", Health: c.health.Value),
                };
        if (loc.objects.TryGetValue(v, out StardewValley.Object? obj))
        {
            if (obj is Fence fence)
                return fence.isGate.Value ? new Thing("gate", "a closed gate") : new Thing("fence", "a fence");
            if (obj.IsWeeds())
                return new Thing("weeds", "weeds");
            if (obj.IsBreakableStone())
                return new Thing("stone", "a stone", Health: obj.MinutesUntilReady);
            if (obj.IsTwig())
                return new Thing("twig", "a twig");
            return new Thing("object", $"an object ({obj.Name})", obj.DisplayName);
        }
        foreach (Building b in loc.buildings)
            if (b.occupiesTile(v))
                return new Thing("building", $"a building ({b.buildingType.Value})", StardewPilot.Tools.AnimalTools.BuildingName(b));
        foreach (StardewValley.Objects.Furniture f in loc.furniture)
            if (f.GetBoundingBox().Contains(t.X * 64 + 32, t.Y * 64 + 32))
                return new Thing("object", $"furniture ({f.Name})", f.DisplayName);
        return Navigation.IsWalkable(loc, t) ? null : new Thing("map", "part of the map (a wall, water or a cliff)");
    }

    /// <summary>
    /// The things walling the farmer off from a target: blocked tiles at the edge of the area the farmer can reach,
    /// nearest to the target first. The target itself first when it is blocked.
    /// </summary>
    /// <param name="besideTarget">The walk was to stand next to the target (a bin, a door): the target itself is not in the way.</param>
    public static List<(Point Tile, Thing What)> Between(GameLocation loc, Point target, int max = 3, bool besideTarget = false)
    {
        var found = new List<(Point, Thing)>();
        if (!besideTarget && At(loc, target) is { } onTarget && !Navigation.IsWalkable(loc, target))
            found.Add((target, onTarget));
        HashSet<Point> reach = Navigation.ReachableTiles(loc);
        var edge = new HashSet<Point>();
        foreach (Point p in reach)
            foreach (Point d in new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) })
            {
                var n = new Point(p.X + d.X, p.Y + d.Y);
                if (!reach.Contains(n) && n != target)
                    edge.Add(n);
            }
        // A villager or animal standing in a gap closes it until they move: name them wherever they are.
        foreach (Point n in edge)
            if (found.Count < max && At(loc, n) is { Key: "villager" or "animal" } mover)
                found.Add((n, mover));
        foreach (Point n in edge.OrderBy(n => Math.Abs(n.X - target.X) + Math.Abs(n.Y - target.Y)))
        {
            if (found.Any(f => f.Item1 == n))
                continue;
            if (found.Count >= max)
                break;
            if (At(loc, n) is { Key: not ("map" or "building") } thing)
                found.Add((n, thing));
        }
        return found;
    }

    /// <summary>"a tree at (53,23), a stone at (52,24)" in the player's language, or null when nothing but the map is in the way.</summary>
    public static string? Words(List<(Point Tile, Thing What)> things) =>
        things.Count == 0 ? null : I18n.List(things.Select(x => I18n.Get("blocker.at", new { what = x.What.Word, tile = $"{x.Tile.X},{x.Tile.Y}" })));

    /// <summary>The same for the agent's report, in English.</summary>
    public static List<string> Report(List<(Point Tile, Thing What)> things) =>
        things.Select(x => $"{x.What.Report} at ({x.Tile.X},{x.Tile.Y})").ToList();

    /// <summary>Things clear_way may cut or break with the farmer's tools: debris, wild trees and their stumps (never fruit or tapped trees).</summary>
    public static bool Clearable(GameLocation loc, Point t) =>
        Navigation.IsClearableDebris(loc, t)
        || (loc.terrainFeatures.TryGetValue(new Vector2(t.X, t.Y), out TerrainFeature? tf) && tf is Tree tree && !tree.tapped.Value);
}
