using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StardewValley;
using StardewValley.Extensions;
using StardewValley.GameData.Objects;
using StardewValley.ItemTypeDefinitions;
using StardewValley.Locations;
using StardewValley.Menus;

namespace StardewPilot.Tools;

/// <summary>
/// get_collections: what is still missing, as the game's own Collections page lists it (the page is built off-screen and read),
/// and what can go to the museum (the museum's own "can this be donated" check, LibraryMuseum.IsItemSuitableForDonation).
/// </summary>
internal static class CollectionKnowledge
{
    private static readonly string[] Tabs = { "shipped", "fish", "artifacts", "minerals", "cooking" };

    public static ToolDef Tool() => new()
    {
        Name = "get_collections",
        Description =
            "Progress in the Collections tab (items shipped, fish caught, artifacts and minerals donated, dishes cooked) and the museum: " +
            "how many are done and missing, and which found items can still be donated. Names of missing items are spoilers, so they are only " +
            "listed with names=true (use it when the player asks what they are missing).",
        InputSchema = Schema.Object(
            ("tab", Schema.Enum("One tab. Default all.", Tabs), false),
            ("names", Schema.Boolean("List the names of missing items. Default false."), false)),
        Handler = Query,
    };

    private static object Query(JObject args)
    {
        string? only = args.Str("tab");
        bool names = args.Bool("names") ?? false;

        // Ask the game's own Collections page (built off-screen, never shown) which items each tab holds and which are done.
        var done = Tabs.ToDictionary(t => t, _ => 0);
        var missing = Tabs.ToDictionary(t => t, _ => new List<string>());
        int unreadable = 0;
        var page = new CollectionsPage(0, 0, 1280, 720);
        for (int tab = 0; tab < Tabs.Length; tab++)
        {
            if (!page.collections.TryGetValue(tab, out var pages))
                continue;
            foreach (ClickableTextureComponent entry in pages.SelectMany(p => p))
            {
                string[] parts = entry.name.Split(' ');
                if (parts.Length < 2 || ItemRegistry.GetData("(O)" + parts[0]) is not { } data)
                {
                    unreadable++;
                    continue;
                }
                if (parts[1] == "True") done[Tabs[tab]]++;
                else missing[Tabs[tab]].Add(data.DisplayName);
            }
        }

        var tabs = Tabs.Where(t => only == null || t == only).Select(t => new
        {
            tab = t,
            done = done[t],
            total = done[t] + missing[t].Count,
            missing = missing[t].Count,
            missingNames = names ? missing[t].OrderBy(n => n).ToList() : null,
        }).ToList();

        return new
        {
            tabs,
            museum = Museum(),
            unreadableEntries = unreadable > 0 ? unreadable : (int?)null,
            how = "Asked of the game: its own Collections page and the museum's own donation check.",
        };
    }

    private static object Museum()
    {
        int donated = 0, total = 0;
        var readyToDonate = new SortedSet<string>();
        var carried = Game1.player.Items.Where(i => i != null).Select(i => i.QualifiedItemId).ToHashSet();
        foreach (ParsedItemData item in ItemRegistry.GetObjectTypeDefinition().GetAllData())
        {
            if (item.IsErrorItem || !LibraryMuseum.IsItemSuitableForDonation(item.QualifiedItemId, checkDonatedItems: false))
                continue;
            total++;
            if (LibraryMuseum.HasDonatedArtifact(item.QualifiedItemId))
            {
                donated++;
                continue;
            }
            bool found = Game1.player.archaeologyFound.ContainsKey(item.ItemId) || Game1.player.mineralsFound.ContainsKey(item.ItemId);
            if (found || carried.Contains(item.QualifiedItemId))
                readyToDonate.Add(item.DisplayName + (carried.Contains(item.QualifiedItemId) ? " (in backpack)" : ""));
        }
        return new
        {
            donated,
            donatable = total,
            foundButNotDonated = readyToDonate.ToList(),
        };
    }
}
