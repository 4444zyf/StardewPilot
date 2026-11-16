using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.GameData.FarmAnimals;
using StardewValley.GameData.Machines;
using StardewValley.Objects;
using SObject = StardewValley.Object;

namespace StardewPilot.Autopilot;

/// <summary>
/// Daily farm chores done the way a player does them: walk to the animal or machine and use the action button or a tool.
/// </summary>
internal sealed class Chores
{
    private readonly Func<ModConfig> config;

    public Chores(Func<ModConfig> config) => this.config = config;

    // ---------------------------------------------------------------- animals

    /// <summary>Pet, collect produce, milk or shear, and put out hay, in one building or all of them.</summary>
    public IEnumerable TendAnimals(JobContext ctx, string? buildingFilter)
    {
        // From 7pm a stationary animal answers petting with "trying to sleep" instead (FarmAnimal.pet).
        if (Game1.timeOfDay >= 1900)
            throw JobContext.Fail("It's after 7pm: the animals are trying to sleep and cannot be petted. Tend them earlier tomorrow.");

        var buildings = AnimalTools.AnimalBuildings()
            .Where(b => buildingFilter == null
                || AnimalTools.BuildingName(b).Contains(buildingFilter, StringComparison.OrdinalIgnoreCase)
                || b.buildingType.Value.Contains(buildingFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (buildings.Count == 0)
            throw JobContext.Fail(buildingFilter == null ? "The farm has no animal buildings." : $"No animal building matches '{buildingFilter}'.");

        var report = new TendReport();
        foreach (Building b in buildings)
        {
            var house = (AnimalHouse)b.GetIndoors()!;
            foreach (object? s in Navigation.WalkTo(ctx, house.NameOrUniqueName, null))
                yield return s;
            ctx.Progress($"tending the {AnimalTools.BuildingName(b)}");

            foreach (object? s in this.PickUpFloorProduce(ctx, house, report))
                yield return s;
            foreach (object? s in this.EmptyAutoGrabbers(ctx, house, report))
                yield return s;
            foreach (object? s in this.PutOutHay(ctx, house, report))
                yield return s;
            foreach (FarmAnimal animal in house.animals.Values.ToList())
            {
                foreach (object? s in this.TendAnimal(ctx, animal, report))
                    yield return s;
            }
        }

        // Animals out on the farm whose home is one of these buildings.
        Farm farm = Game1.getFarm();
        var outside = farm.animals.Values.Where(a => a.home != null && buildings.Contains(a.home) && NeedsTending(a)).ToList();
        if (outside.Count > 0)
        {
            foreach (object? s in Navigation.WalkTo(ctx, farm.NameOrUniqueName, null))
                yield return s;
            foreach (FarmAnimal animal in outside)
            {
                foreach (object? s in this.TendAnimal(ctx, animal, report))
                    yield return s;
            }
        }

        // Pets on the farm or in the player's own house, when all buildings are tended.
        if (buildingFilter == null)
        {
            GameLocation home = Utility.getHomeOfFarmer(Game1.player);
            foreach (Pet pet in AnimalTools.Pets().Where(p => !AnimalTools.PettedToday(p) && (p.currentLocation == farm || p.currentLocation == home)))
            {
                foreach (object? s in this.PetPet(ctx, pet, report))
                    yield return s;
            }
        }

        ctx.Report["petted"] = report.Petted;
        ctx.Report["collected"] = report.Collected.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (report.HayPutOut > 0)
            ctx.Report["hayPutOut"] = report.HayPutOut;
        ctx.Problems.AddRange(report.Problems.Distinct());
    }

    private sealed class TendReport
    {
        public int Petted;
        public int HayPutOut;
        public List<string> Collected = new();
        public List<string> Problems = new();
    }

    private static bool NeedsTending(FarmAnimal a) =>
        !a.wasPet.Value || (a.currentProduce.Value != null && a.isAdult() && a.GetHarvestType() == FarmAnimalHarvestType.HarvestWithTool);

    private IEnumerable TendAnimal(JobContext ctx, FarmAnimal animal, TendReport report)
    {
        if (!NeedsTending(animal))
            yield break;
        if (Game1.timeOfDay >= 1900)
        {
            report.Problems.Add("Stopped petting at 7pm: the animals are going to sleep.");
            yield break;
        }
        bool reached = false;
        foreach (object? s in ApproachAnimal(ctx, animal, ok => reached = ok))
            yield return s;
        if (!reached)
        {
            report.Problems.Add($"Could not get next to {animal.displayName}.");
            yield break;
        }

        if (!animal.wasPet.Value)
        {
            // Empty hand or a tool: petting while holding a Golden Animal Cracker feeds it to the animal instead.
            Actions.SelectSafeHand();
            if (Game1.player.ActiveObject != null)
            {
                report.Problems.Add("Could not free the farmer's hands to pet the animals.");
                yield break;
            }
            Actions.Face(BodyTile(animal));
            animal.pet(Game1.player); // what the action button does on an animal (GameLocation.CheckPetAnimal)
            yield return new WaitTicks(20);
            if (animal.wasPet.Value)
            {
                report.Petted++;
                ctx.Progress($"petted {animal.displayName}");
            }
            else if (Actions.CloseDialogue() is { } said)
            {
                report.Problems.Add($"{animal.displayName}: {said}");
            }
        }

        if (animal.currentProduce.Value is { } produce && animal.isAdult() && animal.GetHarvestType() == FarmAnimalHarvestType.HarvestWithTool)
        {
            string? toolName = animal.GetAnimalData()?.HarvestTool;
            Tool? tool = Game1.player.Items.OfType<Tool>().FirstOrDefault(t => animal.CanGetProduceWithTool(t));
            if (tool == null)
            {
                report.Problems.Add($"No {toolName ?? "tool"} in the backpack for {animal.displayName}'s produce.");
                yield break;
            }
            // The real item, with its quality: silver milk does not stack with normal milk and needs its own slot.
            Item made = ItemRegistry.Create("(O)" + produce, animal.hasEatenAnimalCracker.Value ? 2 : 1, animal.produceQuality.Value);
            if (!Game1.player.couldInventoryAcceptThisItem(made))
            {
                report.Problems.Add("The backpack is full.");
                yield break;
            }
            string name = ItemRegistry.GetDataOrErrorItem(produce).DisplayName;
            // The tool reaches the tile in front of the farmer; a wandering or wide animal can be missed, so try a few times.
            for (int attempt = 0; attempt < 3 && animal.currentProduce.Value != null; attempt++)
            {
                // The tool looks for the animal's harvest area, which is not where its body box is (FarmAnimal.GetHarvestBoundingBox).
                bool near = false;
                foreach (object? s in ApproachAnimal(ctx, animal, ok => near = ok, forTool: true))
                    yield return s;
                if (!near)
                    break;
                animal.Halt();
                animal.pauseTimer = Math.Max(animal.pauseTimer, 2000);
                foreach (object? s in Actions.UseTool(ctx, this.config(), tool, HarvestTile(animal)))
                    yield return s;
                foreach (object? s in Actions.Settle(ctx))
                    yield return s;
            }
            if (animal.currentProduce.Value == null)
            {
                report.Collected.Add(name);
                ctx.Progress($"collected {name}");
            }
            else
            {
                report.Problems.Add($"Could not collect {name} from {animal.displayName}.");
            }
        }
    }

    private IEnumerable PetPet(JobContext ctx, Pet pet, TendReport report)
    {
        GameLocation where = pet.currentLocation;
        if (Game1.currentLocation != where)
        {
            foreach (object? s in Navigation.WalkTo(ctx, where.NameOrUniqueName, null))
                yield return s;
        }
        for (int attempt = 0; attempt < 4 && !AnimalTools.PettedToday(pet); attempt++)
        {
            pet.Halt();
            bool walked = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, pet.TilePoint, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); }
                catch (JobStop) { walked = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            Point me = Game1.player.TilePoint, at = pet.TilePoint;
            if (!walked || Math.Abs(me.X - at.X) + Math.Abs(me.Y - at.Y) > 1)
            {
                yield return new WaitTicks(10);
                continue;
            }
            // Empty hand or a tool: a held hat would go on the pet, and Butterfly Powder asks a question.
            Actions.SelectSafeHand();
            Actions.Face(at);
            pet.checkAction(Game1.player, where); // what the action button does on a pet
            yield return new WaitTicks(20);
        }
        if (AnimalTools.PettedToday(pet))
        {
            report.Petted++;
            ctx.Progress($"petted {pet.displayName}");
        }
        else
        {
            report.Problems.Add($"Could not get next to {pet.displayName}.");
        }
    }

    /// <summary>The tile under the middle of the area a Milk Pail or Shears must reach.</summary>
    private static Point HarvestTile(FarmAnimal animal)
    {
        Rectangle box = animal.GetHarvestBoundingBox();
        return new Point(box.Center.X / 64, box.Center.Y / 64);
    }

    /// <summary>The tile under the middle of the animal's body (a cow is two tiles wide; its position is a corner).</summary>
    private static Point BodyTile(FarmAnimal animal)
    {
        Rectangle box = animal.GetBoundingBox();
        return new Point(box.Center.X / 64, box.Center.Y / 64);
    }

    /// <summary>Walk next to an animal. Animals wander, so stop it and try again a few times.</summary>
    private static IEnumerable ApproachAnimal(JobContext ctx, FarmAnimal animal, Action<bool> done, bool forTool = false)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            animal.Halt();
            animal.pauseTimer = Math.Max(animal.pauseTimer, 4000); // the game pauses animals the same way when you interact
            Point target = forTool ? HarvestTile(animal) : BodyTile(animal);
            bool walked = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, target, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); }
                catch (JobStop) { walked = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            Point me = Game1.player.TilePoint, at = forTool ? HarvestTile(animal) : BodyTile(animal);
            if (walked && Math.Abs(me.X - at.X) + Math.Abs(me.Y - at.Y) <= 1 && me != at)
            {
                done(true);
                yield break;
            }
            yield return new WaitTicks(10);
        }
        done(false);
    }

    private IEnumerable PickUpFloorProduce(JobContext ctx, AnimalHouse house, TendReport report)
    {
        var gaveUp = new HashSet<Vector2>();
        while (true)
        {
            var next = house.objects.Pairs
                .Where(p => p.Value.IsSpawnedObject && p.Value.QualifiedItemId != AnimalTools.HayId && !gaveUp.Contains(p.Key))
                .OrderBy(p => Vector2.Distance(p.Key, Game1.player.Tile))
                .Select(p => (KeyValuePair<Vector2, SObject>?)p)
                .FirstOrDefault();
            if (next is not { } pair)
                yield break;
            if (!Game1.player.couldInventoryAcceptThisItem(pair.Value))
            {
                report.Problems.Add("The backpack is full.");
                yield break;
            }
            Point tile = new((int)pair.Key.X, (int)pair.Key.Y);
            bool walked = true;
            IEnumerator walk = Navigation.WalkWithinMap(ctx, tile, adjacent: true).GetEnumerator();
            while (true)
            {
                bool more;
                try { more = walk.MoveNext(); }
                catch (JobStop) { walked = false; break; }
                if (!more) break;
                yield return walk.Current;
            }
            string name = pair.Value.DisplayName;
            if (walked)
            {
                Actions.SelectSafeHand();
                Actions.Interact(tile);
                foreach (object? s in Actions.Settle(ctx))
                    yield return s;
            }
            if (house.objects.ContainsKey(pair.Key))
                gaveUp.Add(pair.Key);
            else
            {
                report.Collected.Add(name);
                ctx.Progress($"picked up {name}");
            }
        }
    }

    private IEnumerable EmptyAutoGrabbers(JobContext ctx, AnimalHouse house, TendReport report)
    {
        foreach (var pair in house.objects.Pairs.Where(p => p.Value.QualifiedItemId == AnimalTools.AutoGrabberId && p.Value.heldObject.Value is Chest c && c.Items.Any(i => i != null)).ToList())
        {
            Point tile = new((int)pair.Key.X, (int)pair.Key.Y);
            foreach (object? s in Navigation.WalkWithinMap(ctx, tile, adjacent: true))
                yield return s;
            Actions.Face(tile);
            var box = (Chest)pair.Value.heldObject.Value;
            foreach (Item item in box.Items.Where(i => i != null).ToList())
            {
                if (!Game1.player.couldInventoryAcceptThisItem(item))
                {
                    report.Problems.Add("The backpack is full.");
                    break;
                }
                // Count first: adding to an existing stack merges this one into it.
                string name = item.DisplayName;
                int count = item.Stack;
                box.Items.Remove(item);
                Game1.player.addItemToInventory(item);
                for (int i = 0; i < count; i++)
                    report.Collected.Add(name);
            }
            box.clearNulls();
            pair.Value.showNextIndex.Value = box.Items.Any(i => i != null); // the grabber's "full" look, as when emptied by hand
            ctx.Progress("emptied the auto-grabber");
        }
    }

    private IEnumerable PutOutHay(JobContext ctx, AnimalHouse house, TendReport report)
    {
        if (AnimalTools.IsAutoFeed(house))
            yield break;
        var empty = AnimalTools.TroughTiles(house).Where(t => !house.objects.ContainsKey(new Vector2(t.X, t.Y))).ToList();
        // The hopper never hands out more than one portion per animal living here.
        int wanted = Math.Min(empty.Count, Math.Max(0, house.animalsThatLiveHere.Count - house.numberOfObjectsWithName("Hay")));
        if (wanted == 0)
            yield break;

        int Held() => Game1.player.Items.Where(i => i?.QualifiedItemId == AnimalTools.HayId).Sum(i => i.Stack);
        if (Held() < wanted)
        {
            if (house.GetRootLocation().piecesOfHay.Value <= 0)
            {
                report.Problems.Add("No hay left in the silos. Buy hay at Marnie's or cut grass with a scythe.");
                if (Held() == 0)
                    yield break;
            }
            else if (house.objects.Pairs.FirstOrDefault(p => p.Value.QualifiedItemId == AnimalTools.HopperId) is { Value: not null } hopper)
            {
                Point at = new((int)hopper.Key.X, (int)hopper.Key.Y);
                foreach (object? s in Navigation.WalkWithinMap(ctx, at, adjacent: true))
                    yield return s;
                Actions.SelectSafeHand(); // the hopper only gives hay to an empty hand
                if (Game1.player.ActiveObject == null)
                {
                    Actions.Interact(at);
                    foreach (object? s in Actions.Settle(ctx))
                        yield return s;
                }
            }
            else
            {
                report.Problems.Add("This building has no hay hopper.");
            }
        }

        foreach (Point trough in empty.OrderBy(t => Math.Abs(t.X - Game1.player.TilePoint.X) + Math.Abs(t.Y - Game1.player.TilePoint.Y)))
        {
            if (Held() == 0 || house.numberOfObjectsWithName("Hay") >= house.animalsThatLiveHere.Count)
                break;
            foreach (object? s in Navigation.WalkWithinMap(ctx, trough, adjacent: true))
                yield return s;
            Actions.Select(i => i.QualifiedItemId == AnimalTools.HayId);
            Actions.Interact(trough); // AnimalHouse.checkAction puts the held hay on a Trough tile
            yield return new WaitTicks(6);
            if (house.objects.ContainsKey(new Vector2(trough.X, trough.Y)))
            {
                report.HayPutOut++;
                ctx.Progress("putting out hay");
            }
        }
        Actions.SelectSafeHand();
    }

    // ---------------------------------------------------------------- machines

    /// <summary>Collect ready output on a map, optionally only one kind of machine, then optionally refill empty ones with an item.</summary>
    public IEnumerable TendMachines(JobContext ctx, string? location, string? machineFilter, string? refillItem)
    {
        GameLocation loc = StateTools.ResolveLocation(location ?? "Farm");
        bool Kind(SObject o) => machineFilter == null || o.DisplayName.Contains(machineFilter, StringComparison.OrdinalIgnoreCase) || o.Name.Contains(machineFilter, StringComparison.OrdinalIgnoreCase);
        bool IsMachine(SObject o) => o.GetMachineData() != null && o is not Chest;

        // Refill check first, before any walking: does this kind of machine take the item at all, and is there enough of it and of the fuel?
        string? refillQid = null;
        if (refillItem != null)
        {
            if (machineFilter == null)
                throw JobContext.Fail("Say which kind of machine to refill (for example Keg).");
            refillQid = StateTools.FindItemId(refillItem) ?? throw JobContext.Fail($"No item called '{refillItem}'.");
            SObject? sample = loc.objects.Values.FirstOrDefault(o => IsMachine(o) && Kind(o));
            if (sample == null)
                throw JobContext.Fail($"There is no {machineFilter} in {loc.DisplayName}.");
            Item? held = Game1.player.Items.FirstOrDefault(i => i?.QualifiedItemId == refillQid);
            if (held == null)
                throw JobContext.Fail($"There is no {ItemRegistry.GetDataOrErrorItem(refillQid).DisplayName} in the backpack.");
            if (RefillProblem(sample, held) is { } why)
                throw JobContext.Fail(why);
        }

        var collected = new List<string>();
        var skipped = new HashSet<Vector2>();
        while (true)
        {
            var ready = loc.objects.Pairs.Where(p => IsMachine(p.Value) && Kind(p.Value) && p.Value.readyForHarvest.Value && p.Value.heldObject.Value != null && !skipped.Contains(p.Key)).Select(p => p.Key).ToList();
            if (ready.Count == 0)
                break;
            if (collected.Count == 0)
            {
                foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
                    yield return s;
            }
            Vector2 tile = ready.OrderBy(t => Vector2.Distance(t, Game1.player.Tile)).First();
            SObject machine = loc.objects[tile];
            Item output = machine.heldObject.Value;
            if (!Game1.player.couldInventoryAcceptThisItem(output))
            {
                ctx.Problems.Add("The backpack is full.");
                break;
            }
            Point at = new((int)tile.X, (int)tile.Y);
            foreach (object? s in Navigation.WalkWithinMap(ctx, at, adjacent: true))
                yield return s;
            string name = $"{output.DisplayName}";
            Actions.SelectSafeHand(); // an empty hand takes the output; a held item could be put in instead
            Actions.Interact(at);
            foreach (object? s in Actions.Settle(ctx))
                yield return s;
            if (machine.readyForHarvest.Value)
                skipped.Add(tile);
            else
            {
                collected.Add(name);
                ctx.Progress($"collected {name}");
            }
        }
        ctx.Report["collected"] = collected.GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}").ToList();
        if (skipped.Count > 0)
            ctx.Problems.Add($"{skipped.Count} machines could not be emptied.");

        if (refillQid == null)
            yield break;

        int filled = 0;
        while (true)
        {
            Item? held = Game1.player.Items.FirstOrDefault(i => i?.QualifiedItemId == refillQid);
            var empty = loc.objects.Pairs.Where(p => IsMachine(p.Value) && Kind(p.Value) && p.Value.heldObject.Value == null && !p.Value.readyForHarvest.Value && !skipped.Contains(p.Key)).Select(p => p.Key).ToList();
            if (held == null || empty.Count == 0)
                break;
            Vector2 tile = empty.OrderBy(t => Vector2.Distance(t, Game1.player.Tile)).First();
            SObject machine = loc.objects[tile];
            if (RefillProblem(machine, held) is { } why)
            {
                ctx.Problems.Add(why);
                break;
            }
            if (Game1.currentLocation != loc)
            {
                foreach (object? s in Navigation.WalkTo(ctx, loc.NameOrUniqueName, null))
                    yield return s;
            }
            Point at = new((int)tile.X, (int)tile.Y);
            foreach (object? s in Navigation.WalkWithinMap(ctx, at, adjacent: true))
                yield return s;
            Actions.Select(i => i == held);
            Actions.Interact(at); // the game's drop-in, as when a player clicks the machine holding the item
            foreach (object? s in Actions.Settle(ctx))
                yield return s;
            if (machine.heldObject.Value != null || machine.MinutesUntilReady > 0)
            {
                filled++;
                ctx.Progress($"filled {filled} {machine.DisplayName}");
            }
            else
            {
                skipped.Add(tile);
            }
        }
        Actions.SelectSafeHand();
        ctx.Report["refilled"] = filled;
        if (filled == 0 && ctx.Problems.Count == 0)
            ctx.Problems.Add($"No empty {machineFilter} was refilled.");
    }

    /// <summary>Why this machine will not take the held stack, or null if it will. Asks the game's machine code without changing anything.</summary>
    private static string? RefillProblem(SObject machine, Item held)
    {
        string machineName = machine.DisplayName, itemName = held.DisplayName;
        SObject? saved = machine.heldObject.Value;
        machine.heldObject.Value = null; // as the game does when it probes a busy machine
        bool accepts;
        try
        {
            accepts = machine.performObjectDropInAction(held, probe: true, Game1.player);
        }
        finally
        {
            machine.heldObject.Value = saved;
        }
        if (accepts)
            return null;

        MachineData? data = machine.GetMachineData();
        if (data == null)
            return $"{machineName} is not a machine that takes items.";
        foreach (MachineItemAdditionalConsumedItems extra in data.AdditionalConsumedItems ?? new())
        {
            int have = Game1.player.Items.Where(i => i?.QualifiedItemId == ItemRegistry.QualifyItemId(extra.ItemId)).Sum(i => i.Stack);
            if (have < extra.RequiredCount)
                return $"{machineName} also needs {extra.RequiredCount} {ItemRegistry.GetDataOrErrorItem(extra.ItemId).DisplayName} in the backpack (there {(have == 1 ? "is" : "are")} {have}).";
        }
        Item many = held.getOne();
        many.Stack = 999;
        if (MachineDataUtility.TryGetMachineOutputRule(machine, data, MachineOutputTrigger.ItemPlacedInMachine, many, Game1.player, machine.Location ?? Game1.getFarm(), out _, out MachineOutputTriggerRule? trigger, out _, out _)
            && trigger != null && trigger.RequiredCount > held.Stack)
            return $"{machineName} needs {trigger.RequiredCount} {itemName} at a time; the backpack has {held.Stack}.";
        return $"{machineName} does not take {itemName}.";
    }
}
