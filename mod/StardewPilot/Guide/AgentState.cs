namespace StardewPilot.Guide;

/// <summary>What the mod knows about the agent process. Written from bridge threads, read on the main thread.</summary>
internal static class AgentState
{
    private static volatile int connectedClients;
    private static volatile bool busy;
    private static volatile string activity = "";

    public static bool Connected => connectedClients > 0;
    public static bool Busy => busy && Connected;
    public static string Activity => activity;

    public static void SetClients(int count)
    {
        connectedClients = count;
        if (count == 0)
            busy = false;
    }

    public static void SetBusy(bool isBusy, string? what)
    {
        busy = isBusy;
        activity = what ?? "";
    }
}
