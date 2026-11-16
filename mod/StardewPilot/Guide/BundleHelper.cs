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

    /// <summary>Why the farmer has no bundles to work on, or null when bundles are visible.</summary>
    public static string? HiddenReason()
    {
        Farmer p = Game1.player;
        if (p.mailReceived.Contains("JojaMember"))
            return "The farm joined Joja; the Community Center is being converted, so there are no bundles.";
        if (p.mailReceived.Contains("ccIsComplete"))
            return "The Community Center is finished; there are no bundles left.";
        if (!p.hasOrWillReceiveMail("canReadJunimoText"))
            return "The farmer cannot read the Community Center's bundles yet.";
        return null;
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

    /// <summary>One item slot of an unfinished visible bundle that is still empty.</summary>
    public sealed record Need(string QualifiedId, int Quantity, int MinQuality, string Bundle, string Room, int BundleIndex, int SlotsStillRequired);

    /// <summary>
    /// Every empty item slot of the unfinished bundles the farmer can see. Gold slots (id -1) are left out.
    /// SlotsStillRequired is how many more slots the bundle needs, which can be fewer than its empty slots.
    /// </summary>
    public static List<Need> MissingNeeds()
    {
        var needs = new List<Need>();
        if (!BundlesVisible())
            return needs;
        var cc = Game1.RequireLocation<CommunityCenter>("CommunityCenter");
        var state = Game1.netWorldState.Value.Bundles;
        foreach (var pair in Game1.netWorldState.Value.BundleData)
        {
            string[] keyParts = pair.Key.Split('/');
            if (keyParts.Length < 2 || !int.TryParse(keyParts[1], out int index) || cc.isBundleComplete(index))
                continue;
            // The Missing Bundle in the abandoned JojaMart only opens after the Community Center is finished.
            if (keyParts[0] == "Abandoned Joja Mart")
                continue;
            string[] fields = pair.Value.Split('/');
            if (fields.Length < 3)
                continue;
            string[] items = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int slots = items.Length / 3;
            int required = fields.Length > 4 && int.TryParse(fields[4], out int r) && r > 0 ? r : slots;
            bool[] done = state.TryGetValue(index, out bool[]? arr) ? arr : Array.Empty<bool>();
            int filled = Enumerable.Range(0, slots).Count(i => i < done.Length && done[i]);
            int stillRequired = Math.Max(0, required - filled);
            if (stillRequired == 0)
                continue;
            string name = fields.Length > 6 && fields[6].Length > 0 ? fields[6] : fields[0];
            for (int slot = 0; slot < slots; slot++)
            {
                if (slot < done.Length && done[slot])
                    continue;
                string id = items[slot * 3];
                if (id == "-1")
                    continue;
                int qty = int.TryParse(items[slot * 3 + 1], out int q) ? q : 1;
                int quality = int.TryParse(items[slot * 3 + 2], out int ql) ? ql : 0;
                string qid = ItemRegistry.QualifyItemId(id) ?? "(O)" + id;
                needs.Add(new Need(qid, qty, quality, name, keyParts[0], index, stillRequired));
            }
        }
        return needs;
    }

    /// <summary>
    /// How many of an item are spare after the visible bundles take what they still need. Bundle slots that need the
    /// best quality are filled first, each with the lowest quality that is good enough, so nothing is held back twice.
    /// </summary>
    public static int Spare(List<Need> needs, string qualifiedId, IEnumerable<(int quality, int count)> owned)
    {
        var left = owned.GroupBy(o => o.quality).ToDictionary(g => g.Key, g => g.Sum(o => o.count));
        foreach (Need need in needs.Where(n => n.QualifiedId == qualifiedId).OrderByDescending(n => n.MinQuality))
        {
            int want = need.Quantity;
            foreach (int quality in left.Keys.Where(q => q >= need.MinQuality).OrderBy(q => q).ToList())
            {
                int take = Math.Min(want, left[quality]);
                left[quality] -= take;
                want -= take;
                if (want == 0)
                    break;
            }
        }
        return left.Values.Sum();
    }
}
