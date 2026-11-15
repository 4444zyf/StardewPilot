using System;
using System.Collections.Generic;

namespace StardewPilot.Guide;

internal enum ChatSender
{
    Player,
    Agent,
    Tip,
    Warning,
    System,
}

internal sealed record ChatEntry(ChatSender Sender, string Text, DateTime At);

/// <summary>The private conversation shown in the overlay. Only touched on the main thread.</summary>
internal static class ChatLog
{
    private const int MaxEntries = 200;
    private static readonly List<ChatEntry> Entries = new();

    public static IReadOnlyList<ChatEntry> All => Entries;

    /// <summary>Raised after an entry is added.</summary>
    public static event Action<ChatEntry>? Added;

    public static void Add(ChatSender sender, string text)
    {
        var entry = new ChatEntry(sender, text.Trim(), DateTime.Now);
        Entries.Add(entry);
        if (Entries.Count > MaxEntries)
            Entries.RemoveRange(0, Entries.Count - MaxEntries);
        Added?.Invoke(entry);
    }

    public static void Clear() => Entries.Clear();
}
