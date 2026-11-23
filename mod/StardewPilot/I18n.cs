using System;
using System.Collections.Generic;
using System.Linq;

namespace StardewPilot;

/// <summary>
/// The text the mod shows the player, from the i18n folder (default.json is English; zh, ru and fr follow the game's
/// language). Kept free of the game so the pure model code and its tests can use it: the mod sets <see cref="Lookup"/>
/// to SMAPI's translations, and the tests to a reader of default.json.
/// Text only the agent reads (tool results, errors, notes, the diary) stays English and does not come through here.
/// </summary>
public static class I18n
{
    /// <summary>The translation of a key with its {{tokens}} filled in.</summary>
    public static Func<string, object?, string> Lookup = (key, _) => key;

    public static string Get(string key, object? tokens = null) => Lookup(key, tokens);

    /// <summary>"a", "a and b", "a, b and c" in the player's language.</summary>
    public static string List(IEnumerable<string> items)
    {
        var all = items.ToList();
        if (all.Count == 0)
            return "";
        if (all.Count == 1)
            return all[0];
        string head = string.Join(Get("list.separator"), all.Take(all.Count - 1));
        return Get("list.and", new { first = head, last = all[^1] });
    }
}
