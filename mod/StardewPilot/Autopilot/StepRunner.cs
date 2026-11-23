using System;
using System.Collections;
using System.Collections.Generic;
using StardewPilot.Guide;
using StardewPilot.Tools;
using StardewValley;
using StardewValley.Menus;

namespace StardewPilot.Autopilot;

/// <summary>
/// Runs one job as a step inside another job (a routine): with its own context, so its report and problems stay apart, and
/// with every stop it throws caught and turned into its result. A stop from outside (the player moving, F9, a menu, the
/// watchdog) is the runner's <see cref="JobRunner.Stop"/>, which ends the routine itself and never reaches this code.
/// </summary>
internal static class StepRunner
{
    public static IEnumerable Run(JobContext routine, string name, Func<JobContext, IEnumerable> build, Action<JobResult> done)
    {
        var ctx = new JobContext(name, routine);
        int start = Game1.timeOfDay;
        var stack = new Stack<IEnumerator>();
        JobResult? result = null;
        JobResult Stopped(JobOutcome outcome, string why) => new(outcome, why, ctx.Report, ctx.Problems);

        try { stack.Push(build(ctx).GetEnumerator()); }
        catch (JobStop stop) { result = Stopped(stop.Outcome, stop.Message); }
        catch (ToolError err) { result = Stopped(JobOutcome.Failed, err.Message); }

        // The step's own nested routines are run here rather than by the runner, so their stops are caught here too.
        while (result == null && stack.Count > 0)
        {
            bool more;
            try { more = stack.Peek().MoveNext(); }
            catch (JobStop stop) { result = Stopped(stop.Outcome, stop.Message); break; }
            catch (ToolError err) { result = Stopped(JobOutcome.Failed, err.Message); break; }
            catch (Exception ex) { result = Stopped(JobOutcome.Failed, $"Internal error: {ex.Message}"); break; }
            if (!more)
            {
                stack.Pop();
                continue;
            }
            object? current = stack.Peek().Current;
            if (current is IEnumerable nested and not string)
            {
                stack.Push(nested.GetEnumerator());
                continue;
            }
            yield return current;
        }
        result ??= new JobResult(ctx.Problems.Count > 0 ? JobOutcome.Partial : JobOutcome.Done,
            ctx.Problems.Count > 0 ? $"{name} finished with problems." : $"{name} finished.", ctx.Report, ctx.Problems);

        // The clean-up the runner does when a job ends.
        Game1.player.controller = null;
        Game1.player.Halt();
        if (ctx.ExpectingMenu && Game1.activeClickableMenu is not null and not ChatOverlayMenu)
            Game1.exitActiveMenu();
        ctx.ExpectingMenu = false;
        InputSim.ReleaseAll();
        Actions.ReleaseFarmer();
        ctx.Report["gameMinutesUsed"] = Utility.CalculateMinutesBetweenTimes(start, Game1.timeOfDay);
        done(result);
    }
}
