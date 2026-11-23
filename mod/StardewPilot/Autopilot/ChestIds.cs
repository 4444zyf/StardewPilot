using System;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;

namespace StardewPilot.Autopilot;

/// <summary>
/// Chests a routine names, known by an id in the chest's modData. Checked in the game (2026-11-16): the id survives a save, a
/// kick (which moves a chest up to three tiles) and an upgrade to a big chest; it is lost when the chest is picked up.
/// </summary>
internal static class ChestIds
{
    public const string Key = "StardewPilot/ChestId";

    public static string? Of(Chest box) => box.modData.TryGetValue(Key, out string? id) && id.Length > 0 ? id : null;

    /// <summary>The chest's id, stamping a new one when it has none.</summary>
    public static string Stamp(Chest box)
    {
        if (Of(box) is { } id)
            return id;
        id = Guid.NewGuid().ToString("N").Substring(0, 12);
        box.modData[Key] = id;
        return id;
    }

    /// <summary>The chest or fridge with this id, anywhere.</summary>
    public static (GameLocation loc, Point tile, Chest box)? Find(string id)
    {
        (GameLocation, Point, Chest)? hit = null;
        Utility.ForEachLocation(loc =>
        {
            foreach (var (tile, o) in loc.objects.Pairs)
            {
                if (o is Chest c && Of(c) == id)
                {
                    hit = (loc, new Point((int)tile.X, (int)tile.Y), c);
                    return false;
                }
            }
            if (loc.GetFridge(onlyUnlocked: false) is { } fridge && Of(fridge) == id && loc.GetFridgePosition() is { } at)
            {
                hit = (loc, at, fridge);
                return false;
            }
            return true;
        });
        return hit;
    }

    /// <summary>The chest or fridge on a tile of a map.</summary>
    public static Chest? At(GameLocation loc, Point tile)
    {
        if (loc.objects.TryGetValue(new Vector2(tile.X, tile.Y), out StardewValley.Object? o) && o is Chest c)
            return c;
        if (loc.GetFridgePosition() is { } at && at == tile)
            return loc.GetFridge(onlyUnlocked: false);
        return null;
    }

    /// <summary>Where a chest whose menu is open stands: its own tile, or the fridge's.</summary>
    public static Point? TileOf(GameLocation loc, Chest box)
    {
        if (ReferenceEquals(loc.GetFridge(onlyUnlocked: false), box))
            return loc.GetFridgePosition();
        foreach (var (tile, o) in loc.objects.Pairs)
            if (ReferenceEquals(o, box))
                return new Point((int)tile.X, (int)tile.Y);
        return null;
    }

    /// <summary>Another farmer's house or cabin.</summary>
    public static bool IsOthersHome(GameLocation loc) =>
        loc is StardewValley.Locations.FarmHouse home && home != Utility.getHomeOfFarmer(Game1.player);
}
