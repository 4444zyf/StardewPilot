using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Objects;

namespace StardewPilot.Tools;

/// <summary>
/// get_recipes: cooking and crafting recipes. Ingredients and "known" are raw (recipe data, player records);
/// "how many can I make now" is asked of the game's own count; how a recipe is learned is read from the recipe
/// data where it says so (start, skill level, friendship) and otherwise reported as not in the data.
/// </summary>
internal static class RecipeKnowledge
{
    private const int MaxListed = 60;

    public static ToolDef Tool() => new()
    {
        Name = "get_recipes",
        Description =
            "Cooking and crafting recipes from the game's data: ingredients, what it makes, whether the farmer knows it, how it is learned, " +
            "and how many can be made right now (the game's own count; cooking also counts the kitchen fridge). By default only known recipes are " +
            "listed; filter 'all' includes unknown ones (spoilers: follow the spoiler rule). Give 'recipe' for one recipe.",
        InputSchema = Schema.Object(
            ("kind", Schema.Enum("cooking or crafting. Default both.", "cooking", "crafting"), false),
            ("recipe", Schema.String("One recipe by name, for example 'Fried Egg' or 'Sprinkler'."), false),
            ("filter", Schema.Enum("known (default), canMakeNow, or all.", "known", "canMakeNow", "all"), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string? kind = args.Str("kind");
        string filter = args.Str("filter") ?? "known";
        string? one = args.Str("recipe");

        List<Item> fridge = KitchenItems();
        var rows = new List<object>();
        int matched = 0;
        foreach (bool cooking in new[] { true, false })
        {
            if (kind == "cooking" && !cooking || kind == "crafting" && cooking)
                continue;
            Dictionary<string, string> data = cooking ? CraftingRecipe.cookingRecipes : CraftingRecipe.craftingRecipes;
            var known = cooking ? Game1.player.cookingRecipes : Game1.player.craftingRecipes;
            foreach ((string key, string raw) in data)
            {
                CraftingRecipe recipe;
                try { recipe = new CraftingRecipe(key, cooking); }
                catch { continue; } // broken entry from a mod
                if (one != null && !key.Equals(one, StringComparison.OrdinalIgnoreCase) && !recipe.DisplayName.Equals(one, StringComparison.OrdinalIgnoreCase))
                    continue;
                bool isKnown = known.ContainsKey(key);
                int canMake = recipe.getCraftableCount(cooking ? fridge : new List<Item>());
                if (one == null && (filter == "known" && !isKnown || filter == "canMakeNow" && (!isKnown || canMake <= 0)))
                    continue;
                matched++;
                if (rows.Count >= MaxListed)
                    continue;
                rows.Add(Describe(key, raw, recipe, cooking, isKnown, known.TryGetValue(key, out int made) ? made : 0, canMake));
            }
        }
        if (one != null && rows.Count == 0)
            throw new ToolError($"No recipe named '{one}'.");
        return new
        {
            recipes = rows,
            more = matched > rows.Count ? $"{matched - rows.Count} more not shown; ask for one kind or one recipe." : null,
            how = "Ingredients, known: raw game data and the farmer's records. canMakeNow: the game's own count " +
                  "(crafting: backpack only, as in the game menu; cooking: backpack plus the kitchen fridges). learnedBy: from the recipe data.",
        };
    }

    private static object Describe(string key, string raw, CraftingRecipe recipe, bool cooking, bool known, int made, int canMake)
    {
        Item? output = null;
        try { output = recipe.createItem(); } catch { /* broken output */ }
        return new
        {
            name = recipe.DisplayName,
            key,
            kind = cooking ? "cooking" : "crafting",
            known,
            timesMade = known && made > 0 ? made : (int?)null,
            makes = output == null ? null : new { name = output.DisplayName, id = output.QualifiedItemId, count = recipe.numberProducedPerCraft },
            ingredients = recipe.recipeList.Select(p => new { name = recipe.getNameFromIndex(p.Key), count = p.Value }).ToList(),
            canMakeNow = known ? canMake : (int?)null,
            learnedBy = LearnedBy(raw, cooking, key),
        };
    }

    /// <summary>How a recipe is learned, when the recipe data says (cooking field 3, crafting field 4).</summary>
    private static string LearnedBy(string raw, bool cooking, string key)
    {
        string field = ArgUtility.Get(raw.Split('/'), cooking ? 3 : 4) ?? "";
        string[] t = ArgUtility.SplitBySpace(field);
        if (field == "default")
            return "known from the start";
        if (CraftingRecipe.TryParseLevelRequirement(key, raw, cooking, out int skill, out int level, logErrors: false))
            return $"{Farmer.getSkillDisplayNameFromIndex(skill)} level {level}";
        if (t.Length >= 3 && t[0] == "f")
        {
            string npc = Game1.getCharacterFromName(t[1])?.displayName ?? t[1];
            return $"a letter from {npc} at {t[2]} hearts";
        }
        return "source not in recipe data (for example the TV, a shop, or a special letter)";
    }

    /// <summary>What the kitchen counts for cooking: the house fridge and mini-fridges in the house (GameLocation cooking menu).</summary>
    private static List<Item> KitchenItems()
    {
        var items = new List<Item>();
        if (Game1.getLocationFromName(Game1.player.homeLocation.Value) is not { } home)
            return items;
        if (home.GetFridge(onlyUnlocked: false) is { } fridge)
            items.AddRange(fridge.Items.Where(i => i != null));
        foreach (var obj in home.objects.Values)
            if (obj is Chest { fridge.Value: true } mini)
                items.AddRange(mini.Items.Where(i => i != null));
        return items;
    }
}
