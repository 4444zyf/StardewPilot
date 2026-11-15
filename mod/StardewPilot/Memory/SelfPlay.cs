using System.Collections.Generic;
using StardewPilot.Autopilot;
using StardewValley;

namespace StardewPilot.Memory;

/// <summary>Self-play state for the current game day. Lives in the mod because the agent's session is replaced every day.</summary>
internal sealed class SelfPlay
{
    public bool Enabled { get; private set; }

    /// <summary>Why self-play last stopped, for the agent and the player.</summary>
    public string StopReason { get; private set; } = "";

    public int RequestsToday { get; private set; }
    public long TokensToday { get; private set; }

    /// <summary>Jobs finished today, for the fallback day summary.</summary>
    public List<string> JobLog { get; } = new();

    public int MoneyAtDayStart { get; private set; }

    public void Start() { this.Enabled = true; this.StopReason = ""; }

    public void Stop(string reason) { this.Enabled = false; this.StopReason = reason; }

    public void OnDayStarted()
    {
        this.RequestsToday = 0;
        this.TokensToday = 0;
        this.JobLog.Clear();
        this.MoneyAtDayStart = Game1.player.Money;
    }

    public void AddUsage(int requests, long tokens)
    {
        this.RequestsToday += requests;
        this.TokensToday += tokens;
    }

    /// <summary>True when today's model budget is used up. A limit of 0 means no limit.</summary>
    public bool BudgetExhausted(ModConfig cfg) =>
        (cfg.SelfPlay.DailyRequestBudget > 0 && this.RequestsToday >= cfg.SelfPlay.DailyRequestBudget)
        || (cfg.SelfPlay.DailyTokenBudget > 0 && this.TokensToday >= cfg.SelfPlay.DailyTokenBudget);

    public void LogJob(string name, JobResult result)
    {
        if (this.JobLog.Count < 60)
            this.JobLog.Add($"{StardewPilot.Tools.StateTools.FormatTime(Game1.timeOfDay)} {name}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Summary.EndsWith("finished.") ? "" : " - " + result.Summary)}");
    }
}
