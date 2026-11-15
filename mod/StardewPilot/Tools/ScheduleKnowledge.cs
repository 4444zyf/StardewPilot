using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace StardewPilot.Tools;

/// <summary>
/// get_npc_schedule: where a villager is now and where they will go later today, read from the schedule the game has
/// built for today (NPC.Schedule). In co-op the host builds schedules; a guest's game may not have them, and then the
/// tool reports only where the villager is now.
/// </summary>
internal static class ScheduleKnowledge
{
    public static ToolDef Tool() => new()
    {
        Name = "get_npc_schedule",
        Description =
            "Where a villager is now and the rest of today's plan (times, maps and spots), read from the schedule the game has loaded for today. " +
            "Plans can change during the day (rain, festivals, events). Useful before walking to someone to talk or give a gift.",
        InputSchema = Schema.Object(("npc", Schema.String("Villager name, for example 'Abigail'."), true)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string name = args.Str("npc") ?? throw new ToolError("npc is required.");
        NPC npc = Find(name) ?? throw new ToolError($"No villager called '{name}'.");

        var now = new
        {
            map = npc.currentLocation?.Name,
            mapName = npc.currentLocation?.DisplayName,
            tile = npc.currentLocation == null ? null : StateTools.Tile(npc.TilePoint.X, npc.TilePoint.Y),
            moving = npc.isMoving() || npc.controller != null,
        };

        Dictionary<int, SchedulePathDescription>? schedule = npc.Schedule;
        bool met = Game1.player.friendshipData.ContainsKey(npc.Name);
        if (schedule == null || schedule.Count == 0)
        {
            return new
            {
                npc = npc.displayName,
                met,
                now,
                plan = (object?)null,
                note = Context.IsMainPlayer
                    ? "No moving plan today: the villager stays where they are, or the game has not given them one (festival, event, or a mod)."
                    : "This is a co-op guest's game, which does not have the villagers' plans; only where they are now is known.",
                how = "Raw: the villager's position now.",
            };
        }

        var stops = schedule.OrderBy(p => p.Key).Select(p => new
        {
            leaves = StateTools.FormatTime(p.Key),
            to = p.Value.targetLocationName,
            toName = Game1.getLocationFromName(p.Value.targetLocationName)?.DisplayName ?? p.Value.targetLocationName,
            tile = StateTools.Tile(p.Value.targetTile.X, p.Value.targetTile.Y),
            then = string.IsNullOrWhiteSpace(p.Value.endOfRouteBehavior) ? null : p.Value.endOfRouteBehavior,
            past = p.Key <= Game1.timeOfDay ? true : (bool?)null,
        }).ToList();

        return new
        {
            npc = npc.displayName,
            met,
            now,
            plan = stops,
            scheduleKey = npc.ScheduleKey,
            note = "Times are when the villager sets off; walking takes a while. 'past' marks stops already started.",
            how = "Raw: the plan the game has built for today.",
        };
    }

    private static NPC? Find(string name)
    {
        NPC? found = Game1.getCharacterFromName(name);
        if (found != null)
            return found;
        Utility.ForEachVillager(n =>
        {
            if (n.displayName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                found = n;
                return false;
            }
            return true;
        });
        return found;
    }
}
