using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewValley.Locations;

namespace StardewPilot.Guide;

/// <summary>Answers "is this item still needed for a Community Center bundle the farmer can see?"</summary>
internal static class BundleHelper
{
    /// <summary>Whether the farmer can see bundles at all (and has not chosen Joja).</summary>
    public static bool BundlesVisible()
    {
        Farmer p = Game1.player;
        return !p.mailReceived.Contains("JojaMember")
            && p.hasOrWillReceiveMail("canReadJunimoText")
            && !p.mailReceived.Contains("ccIsComplete");
    }

    /// <summary>The display name of an unfinished bundle that still needs this item, or null.</summary>
    public static string? BundleNeeding(Item item)
    {
        if (!BundlesVisible() || item.TypeDefinitionId != "(O)")
            return null;

        var cc = Game1.RequireLocation<CommunityCenter>("CommunityCenter");
        var state = Game1.netWorldState.Value.Bundles;
        foreach (var pair in Game1.netWorldState.Value.BundleData)
        {
            string[] keyParts = pair.Key.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int index) || cc.isBundleComplete(index))
                continue;
            string[] fields = pair.Value.Split('/');
            if (fields.Length < 3)
                continue;
            string[] items = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            bool[] done = state.TryGetValue(index, out bool[]? arr) ? arr : Array.Empty<bool>();
            for (int i = 0; i + 2 < items.Length; i += 3)
            {
                int slot = i / 3;
                if (slot < done.Length && done[slot])
                    continue;
                int minQuality = int.TryParse(items[i + 2], out int q) ? q : 0;
                if (items[i] == item.ItemId && item.Quality >= minQuality)
                    return fields.Length > 6 && fields[6].Length > 0 ? fields[6] : fields[0];
            }
        }
        return null;
    }
}
