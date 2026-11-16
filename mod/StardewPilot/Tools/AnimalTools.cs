using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.GameData.FarmAnimals;
using StardewValley.Objects;
using StardewValley.TokenizableStrings;
using SObject = StardewValley.Object;

namespace StardewPilot.Tools;

/// <summary>get_animals: the farm's animals and their buildings, read from the game. Shared helpers for the animal chores.</summary>
internal static class AnimalTools
{
    public const string HayId = "(O)178";
    public const string HopperId = "(BC)99";
    public const string AutoGrabberId = "(BC)165";

    public static ToolDef Tool() => new()
    {
        Name = "get_animals",
        Description =
            "The farm's animals, pets and buildings: each animal's kind, name, building, whether it is inside or out on the farm, age, hearts, whether it was petted today, " +
            "and whether it has produce ready and which tool collects it. Each building: door open or closed, feeding spots filled and empty, whether it feeds itself, produce lying on the floor, " +
            "and what an auto-grabber holds. The farm's hay: stored and silo capacity (one shared count, not per hopper).",
        Handler = _ => Describe(),
    };

    private static object Describe()
    {
        Farm farm = Game1.getFarm();
        var buildings = new List<object>();
        var animals = new List<object>();
        foreach (Building b in AnimalBuildings())
        {
            var house = (AnimalHouse)b.GetIndoors()!;
            var troughs = TroughTiles(house);
            int filled = troughs.Count(t => house.objects.TryGetValue(new Vector2(t.X, t.Y), out SObject? o) && o.QualifiedItemId == HayId);
            var grabbers = house.objects.Values.Where(o => o.QualifiedItemId == AutoGrabberId && o.heldObject.Value is Chest).ToList();
            buildings.Add(new
            {
                building = BuildingName(b),
                map = house.NameOrUniqueName,
                animalsLivingHere = house.animalsThatLiveHere.Count,
                capacity = house.animalLimit.Value,
                doorOpen = b.animalDoorOpen.Value,
                feedsItself = IsAutoFeed(house) ? true : (bool?)null,
                feedingSpots = new { total = troughs.Count, withHay = filled, empty = troughs.Count - filled },
                hasHopper = house.objects.Values.Any(o => o.QualifiedItemId == HopperId),
                produceOnFloor = FloorProduce(house).Select(o => o.DisplayName).GroupBy(n => n).Select(g => $"{g.Key} x{g.Count()}").ToList(),
                autoGrabber = grabbers.Count > 0
                    ? grabbers.SelectMany(g => ((Chest)g.heldObject.Value).Items.Where(i => i != null)).GroupBy(i => i.DisplayName).Select(g => $"{g.Key} x{g.Sum(i => i.Stack)}").ToList()
                    : null,
            });
        }

        foreach (FarmAnimal a in AllAnimals())
        {
            FarmAnimalData? data = a.GetAnimalData();
            bool toolProduce = a.currentProduce.Value != null && a.isAdult() && a.GetHarvestType() == FarmAnimalHarvestType.HarvestWithTool;
            animals.Add(new
            {
                name = a.displayName,
                kind = a.displayType,
                building = a.home != null ? BuildingName(a.home) : null,
                where = a.currentLocation is AnimalHouse ? "inside" : a.currentLocation?.IsFarm == true ? "outside on the farm" : a.currentLocation?.DisplayName,
                ageDays = a.age.Value,
                adult = a.isAdult(),
                hearts = a.friendshipTowardFarmer.Value / 200,
                pettedToday = a.wasPet.Value || a.wasAutoPet.Value,
                produceReady = toolProduce ? ItemRegistry.GetDataOrErrorItem(a.currentProduce.Value).DisplayName : null,
                collectWith = toolProduce ? data?.HarvestTool : null,
                producesBy = a.GetHarvestType()?.ToString(),
            });
        }

        var pets = Pets().Select(p => new
        {
            name = p.displayName,
            kind = p.petType.Value,
            where = p.currentLocation?.DisplayName,
            hearts = p.friendshipTowardFarmer.Value / 200,
            pettedToday = PettedToday(p),
        }).ToList();

        return new
        {
            hay = new { stored = farm.piecesOfHay.Value, siloCapacity = farm.GetHayCapacity() },
            buildings,
            animals,
            pets = pets.Count > 0 ? pets : null,
            note = animals.Count == 0 ? "No farm animals yet." : "Dropped produce (eggs, wool from rabbits, duck feathers) lies on the building floor; milk and wool from sheep need the Milk Pail or Shears. Petting is possible until 7pm.",
            how = "Raw: read from the game's animals, buildings and the farm's hay count.",
        };
    }

    /// <summary>The farm's pets (cat, dog, turtle ...), as the game's animal page lists them.</summary>
    public static List<Pet> Pets()
    {
        var pets = new List<Pet>();
        Utility.ForEachLocation(loc =>
        {
            foreach (NPC npc in loc.characters)
                if (npc is Pet pet && !pet.hideFromAnimalSocialMenu.Value)
                    pets.Add(pet);
            return true;
        });
        return pets;
    }

    /// <summary>Whether the local player has petted this pet today.</summary>
    public static bool PettedToday(Pet pet) =>
        pet.lastPetDay.TryGetValue(Game1.player.UniqueMultiplayerID, out int day) && day == Game1.Date.TotalDays;

    public static IEnumerable<Building> AnimalBuildings() =>
        Game1.getFarm().buildings.Where(b => !b.isUnderConstruction() && b.GetIndoors() is AnimalHouse);

    public static IEnumerable<FarmAnimal> AllAnimals()
    {
        Farm farm = Game1.getFarm();
        foreach (FarmAnimal a in farm.animals.Values)
            yield return a;
        foreach (Building b in AnimalBuildings())
            foreach (FarmAnimal a in ((AnimalHouse)b.GetIndoors()!).animals.Values)
                yield return a;
    }

    public static string BuildingName(Building b) =>
        (b.GetData()?.Name is { } n ? TokenParser.ParseText(n) : null) ?? b.buildingType.Value;

    public static bool IsAutoFeed(AnimalHouse house) => house.HasMapPropertyWithValue("AutoFeed");

    /// <summary>Tiles where hay can be put out (the map's Trough tiles).</summary>
    public static List<Point> TroughTiles(AnimalHouse house)
    {
        var tiles = new List<Point>();
        var layer = house.map.Layers[0];
        for (int x = 0; x < layer.LayerWidth; x++)
            for (int y = 0; y < layer.LayerHeight; y++)
                if (house.doesTileHaveProperty(x, y, "Trough", "Back") != null)
                    tiles.Add(new Point(x, y));
        return tiles;
    }

    /// <summary>Produce the animals dropped on the floor (eggs, rabbit's wool ...): spawned objects in the building.</summary>
    public static List<SObject> FloorProduce(AnimalHouse house) =>
        house.objects.Values.Where(o => o.IsSpawnedObject && o.QualifiedItemId != HayId).ToList();
}
