using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.GameData.Machines;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>
/// get_machine_info: what a machine accepts (raw, Data/Machines) and what it would make from a given item (asked of the
/// game: the same output-rule lookup and output builder it runs when a player loads a machine, with the builder's
/// "probe" flag, which promises not to change anything).
/// </summary>
internal static class MachineKnowledge
{
    private static readonly HashSet<string> RandomKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "RANDOM", "SYNCED_RANDOM", "SYNCED_CHOICE", "SYNCED_SUMMER_RAIN_RANDOM",
    };

    public static ToolDef Tool() => new()
    {
        Name = "get_machine_info",
        Description =
            "What a machine (Keg, Preserves Jar, Furnace, Cask, Seed Maker ...) accepts and produces, from the game's data. Give 'item' to ask " +
            "what the machine would make from that item and how long it takes; that answer comes from the game's own machine code, so machines " +
            "whose output is decided in code are covered. Where a machine can give one of several outputs, every possible one is listed.",
        InputSchema = Schema.Object(
            ("machine", Schema.String("Machine name or id, for example 'Keg' or '(BC)12'."), true),
            ("item", Schema.String("An item to put in, for example 'Hops' or 'Copper Ore'."), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string name = args.Str("machine") ?? throw new ToolError("machine is required.");
        string machineId = FindMachine(name) ?? throw new ToolError($"'{name}' is not a machine in the game data.");
        MachineData data = DataLoader.Machines(Game1.content)[machineId];
        string? itemName = args.Str("item");
        return itemName == null ? Describe(machineId, data) : Probe(machineId, data, itemName);
    }

    private static string? FindMachine(string query)
    {
        var machines = DataLoader.Machines(Game1.content);
        if (machines.ContainsKey(query))
            return query;
        // Machines are big craftables mostly; look those up by name first.
        foreach (string prefix in new[] { "(BC)", "(O)" })
        {
            string? match = machines.Keys.FirstOrDefault(k => k.StartsWith(prefix) && ItemRegistry.GetData(k) is { } d
                && (d.DisplayName.Equals(query, StringComparison.OrdinalIgnoreCase) || d.InternalName.Equals(query, StringComparison.OrdinalIgnoreCase)));
            if (match != null)
                return match;
        }
        return null;
    }

    private static string ItemName(string? id) =>
        id == null ? "" : ItemRegistry.GetData(id) is { } d ? d.DisplayName : id;

    private static object Describe(string machineId, MachineData data)
    {
        var rules = new List<object>();
        foreach (MachineOutputRule rule in data.OutputRules ?? new())
        {
            foreach (MachineOutputTriggerRule trigger in rule.Triggers ?? new())
            {
                rules.Add(new
                {
                    when = trigger.Trigger.ToString(),
                    input = trigger.RequiredItemId != null ? ItemName(trigger.RequiredItemId) : null,
                    inputTags = trigger.RequiredTags?.Count > 0 ? trigger.RequiredTags : null,
                    count = trigger.Trigger.HasFlag(MachineOutputTrigger.ItemPlacedInMachine) && trigger.RequiredCount != 1 ? trigger.RequiredCount : (int?)null,
                    condition = string.IsNullOrWhiteSpace(trigger.Condition) ? null : trigger.Condition,
                    outputs = (rule.OutputItem ?? new()).Select(o => o.OutputMethod != null
                        ? $"decided by the game's code ({o.OutputMethod.Split(':').Last().Trim()})"
                        : ItemRegistry.GetData(o.ItemId) is { } d ? d.DisplayName : o.ItemId).Distinct().ToList(),
                    time = TimeText(rule.MinutesUntilReady, rule.DaysUntilReady),
                });
            }
        }
        return new
        {
            machine = ItemName(machineId),
            id = machineId,
            rules,
            alsoConsumes = data.AdditionalConsumedItems?.Select(a => new { item = ItemName(a.ItemId), count = a.RequiredCount }).ToList(),
            how = "Raw game data (Data/Machines). Tags such as category_fruits mean any item with that tag. Ask with 'item' for the exact output.",
        };
    }

    private static object Probe(string machineId, MachineData data, string itemName)
    {
        string qid = StateTools.FindItemId(itemName) ?? throw new ToolError($"No item named '{itemName}'.");
        Item input = ItemRegistry.Create(qid, 999); // enough for any required count
        Farmer who = Game1.player;

        // Use a machine the player has placed if there is one (some outputs depend on where it stands); otherwise a loose one.
        SObject? placed = FindPlaced(machineId);
        SObject machine = placed ?? ItemRegistry.Create<SObject>(machineId);
        GameLocation location = placed?.Location ?? Game1.getFarm();
        if (placed == null)
            machine.Location = location;

        try
        {
            if (!MachineDataUtility.TryGetMachineOutputRule(machine, data, MachineOutputTrigger.ItemPlacedInMachine, input, who, location,
                    out MachineOutputRule? rule, out MachineOutputTriggerRule? trigger, out MachineOutputRule? ruleIgnoringCount, out MachineOutputTriggerRule? triggerIgnoringCount))
            {
                if (ruleIgnoringCount != null)
                    return new { machine = ItemName(machineId), item = ItemName(qid), accepts = true, needsCount = triggerIgnoringCount?.RequiredCount, how = "Asked of the game's machine code." };
                return new { machine = ItemName(machineId), item = ItemName(qid), accepts = false, how = "Asked of the game's machine code." };
            }

            var outputs = new List<object>();
            foreach (MachineItemOutput output in rule.OutputItem ?? new())
            {
                if (!GameStateQuery.CheckConditions(output.Condition, location, who, null, input, null, RandomKeys))
                    continue;
                Item? made = MachineDataUtility.GetOutputItem(machine, output, input, who, probe: true, out int? overrideMinutes);
                if (made == null)
                    continue;
                outputs.Add(new
                {
                    name = made.DisplayName,
                    id = made.QualifiedItemId,
                    count = made.Stack,
                    sellPrice = made is SObject o ? o.sellToStorePrice() : (int?)null,
                    time = overrideMinutes.HasValue ? TimeText(overrideMinutes.Value, -1) : null,
                    randomPick = output.ItemId != null && (output.ItemId.Contains("RANDOM") || output.RandomItemId?.Count > 0) ? true : (bool?)null,
                    decidedByCode = output.OutputMethod != null
                        ? "One example from the game's code; the count, quality or even the item can differ each time (for example the Seed Maker gives 1-3 seeds and rarely something else)."
                        : null,
                });
                if (rule.UseFirstValidOutput)
                    break;
            }
            return new
            {
                machine = ItemName(machineId),
                item = ItemName(qid),
                accepts = true,
                itemsUsed = trigger?.RequiredCount,
                alsoConsumes = data.AdditionalConsumedItems?.Select(a => new { item = ItemName(a.ItemId), count = a.RequiredCount }).ToList(),
                outputs,
                oneOf = !rule.UseFirstValidOutput && outputs.Count > 1 ? "The machine picks one of these at random." : null,
                time = TimeText(rule.MinutesUntilReady, rule.DaysUntilReady),
                timeChangesWithProfessions = data.ReadyTimeModifiers?.Count > 0 ? true : (bool?)null,
                usedPlacedMachine = placed != null,
                how = "Asked of the game's machine code (probe mode, nothing is changed). Base quality input.",
            };
        }
        catch (Exception ex)
        {
            return new
            {
                machine = ItemName(machineId),
                item = ItemName(qid),
                error = $"The game could not answer for a machine that is not placed ({ex.GetType().Name}). Place one and ask again, or use the raw rules (call without 'item').",
            };
        }
    }

    private static SObject? FindPlaced(string machineId)
    {
        SObject? found = null;
        Utility.ForEachLocation(loc =>
        {
            foreach (SObject obj in loc.objects.Values)
            {
                if (obj.QualifiedItemId == machineId)
                {
                    found = obj;
                    return false;
                }
            }
            return true;
        }, includeInteriors: true, includeGenerated: false);
        return found;
    }

    private static string? TimeText(int minutes, int days)
    {
        if (days > 0)
            return days == 1 ? "1 day (ready next morning)" : $"{days} days";
        if (minutes > 0)
            return minutes >= 60 ? $"{minutes / 60}h{(minutes % 60 > 0 ? $" {minutes % 60}m" : "")} of game time" : $"{minutes}m of game time";
        return null;
    }
}
