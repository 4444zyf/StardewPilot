using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;

namespace StardewPilot.Bridge;

/// <summary>Runs work on the game's main thread. The game state is not thread-safe, so every bridge request goes through here.</summary>
internal static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();

    /// <summary>Queue a function to run on the next update tick and get its result.</summary>
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>Called from the game's update loop.</summary>
    public static void Drain()
    {
        // Bound the work per tick so a flood of requests cannot freeze a frame for long.
        for (int i = 0; i < 32 && Queue.TryDequeue(out Action? action); i++)
            action();
    }
}
