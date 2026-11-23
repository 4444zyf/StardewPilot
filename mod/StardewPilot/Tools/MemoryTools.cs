using System;
using System.Collections.Generic;
using System.Linq;
using StardewPilot.Guide;
using StardewPilot.Memory;
using StardewValley;

namespace StardewPilot.Tools;

/// <summary>Tools for the agent's memory and for self-play.</summary>
internal static class MemoryTools
{
    public static void Register(ToolRegistry registry, MemoryStore memory, SelfPlay selfPlay, Func<ModConfig> config, Action<bool, string?> setSelfPlay, Func<List<string>> habits, Func<List<string>> routines)
    {
        registry.Add(new ToolDef
        {
            Name = "read_memory",
            Description = "What you remember about this farm: the standing goal, your notes (goals, the player's preferences, what you learned), today's plan, summaries of recent days, and the player's habits as the mod observed them. Also self-play state and today's model budget.",
            Handler = _ => Snapshot(memory, selfPlay, config(), habits(), routines()),
        });

        registry.Add(new ToolDef
        {
            Name = "write_notes",
            Description = "Replace your notes for this farm (plain text the player can read and edit). Keep what is still true; add what you learned. Saved when the game saves at night.",
            InputSchema = Schema.Object(("text", Schema.String("The full new notes."), true)),
            ReadOnly = false,
            Handler = a =>
            {
                memory.WriteNotes(a.Str("text") ?? "");
                return new { saved = "pending until the game saves tonight" };
            },
        });

        registry.Add(new ToolDef
        {
            Name = "write_day_plan",
            Description = "Write today's plan: a short ordered list of what to do today and why. The player sees it in the chat and can change it by telling you.",
            InputSchema = Schema.Object(("plan", Schema.String("Short ordered list, one task per line."), true)),
            ReadOnly = false,
            Handler = a =>
            {
                string plan = a.Str("plan") ?? "";
                memory.WritePlan(plan);
                ChatLog.Add(ChatSender.System, I18n.Get("chat.todays-plan") + "\n" + plan.Trim());
                return new { saved = "pending until the game saves tonight", shownToPlayer = true };
            },
        });

        registry.Add(new ToolDef
        {
            Name = "write_day_summary",
            Description = "Write the summary of today: what got done, money, problems, and what tomorrow should start with. Call it before going to bed.",
            InputSchema = Schema.Object(("summary", Schema.String("A few short lines."), true)),
            ReadOnly = false,
            Handler = a =>
            {
                memory.WriteSummary(a.Str("summary") ?? "");
                return new { saved = "pending until the game saves tonight" };
            },
        });

        registry.Add(new ToolDef
        {
            Name = "set_self_play",
            Description = "Start or stop self-play, where you play by yourself toward a standing goal. Only when the player asks. Optionally set the goal in plain language; without one, follow the default first-year checklist.",
            InputSchema = Schema.Object(
                ("enabled", Schema.Boolean("true to start, false to stop."), true),
                ("goal", Schema.String("Standing goal, for example 'earn 10,000g by summer' or 'finish the spring bundles'."), false)),
            ReadOnly = false,
            Handler = a =>
            {
                setSelfPlay(a.Bool("enabled") ?? false, a.Str("goal"));
                return Snapshot(memory, selfPlay, config(), habits(), routines());
            },
        });
    }

    public static object Snapshot(MemoryStore memory, SelfPlay selfPlay, ModConfig cfg, List<string> habits, List<string> routines) => new
    {
        date = MemoryStore.DateKey(),
        goal = memory.Goal.Length > 0 ? memory.Goal : null,
        usingDefaultChecklist = memory.Goal.Length == 0,
        notes = memory.Notes.Length > 0 ? memory.Notes : null,
        todayPlan = memory.TodayPlan.Length > 0 ? memory.TodayPlan : null,
        recentDays = memory.RecentSummaries(3).Select(d => new { d.date, summary = d.text }),
        habits = habits.Count > 0 ? habits : null,
        routines = routines.Count > 0 ? new { names = routines, note = "The player's routines (run_routine). Run one only when the player asks for it." } : null,
        selfPlay = new
        {
            enabled = selfPlay.Enabled,
            stoppedBecause = selfPlay.Enabled || selfPlay.StopReason.Length == 0 ? null : selfPlay.StopReason,
            requestsToday = selfPlay.RequestsToday,
            requestBudget = cfg.SelfPlay.DailyRequestBudget > 0 ? cfg.SelfPlay.DailyRequestBudget : (int?)null,
            followHabits = cfg.SelfPlay.FollowMyHabits && habits.Count > 0,
        },
    };
}
