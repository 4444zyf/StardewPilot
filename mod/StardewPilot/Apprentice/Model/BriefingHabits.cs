using System.Collections.Generic;
using System.Linq;

namespace StardewPilot.Apprentice.Model;

/// <summary>
/// What the morning briefing says about habits: the habit for today's kind of day, and a one-time offer to do the usual
/// morning. Pure, so it is tested without the game.
/// </summary>
public static class BriefingHabits
{
    /// <summary>The phrase the offer tells the player to say.</summary>
    public const string OfferPhrase = "do my usual morning";

    /// <summary>The day-type habit for today: rain first, then the weekday. Null when none applies.</summary>
    public static Habit? UsualDay(IEnumerable<Habit> habits, bool rain, string weekday)
    {
        var dayTypes = habits.Where(h => h.Kind == "day-type").ToList();
        return (rain ? dayTypes.FirstOrDefault(h => h.Condition == "rain") : null)
            ?? dayTypes.FirstOrDefault(h => h.Condition == weekday);
    }

    /// <summary>The usual morning's steps that a job can do, in order. Empty when there is no morning habit.</summary>
    public static List<MorningStep> OfferSteps(IEnumerable<Habit> habits) =>
        habits.FirstOrDefault(h => h.Kind == "morning")?.Steps?.Where(s => s.Job != null).ToList() ?? new();

    /// <summary>The key stored once the offer was shown: the jobs in order. A different order or a new step is a new offer.</summary>
    public static string OfferKey(List<MorningStep> steps) => string.Join(",", steps.Select(s => s.Job));

    /// <summary>The steps to offer today, or null when there is nothing new to offer.</summary>
    public static List<MorningStep>? Offer(IEnumerable<Habit> habits, string? offeredKey)
    {
        var steps = OfferSteps(habits);
        return steps.Count > 0 && OfferKey(steps) != offeredKey ? steps : null;
    }

    /// <summary>"water, then harvest" from the steps' activities, in the player's language.</summary>
    public static string StepWords(List<MorningStep> steps) => string.Join(I18n.Get("habit.then"), steps.Select(s => Act.Say(s.Activity)));

    public static string OfferLine(List<MorningStep> steps) =>
        I18n.Get("habit.offer", new { steps = StepWords(steps), phrase = I18n.Get("habit.offer-phrase") });

    /// <summary>The habit's text, already worded as "mostly ... (n of m)". The diary's words, so English.</summary>
    public static string UsualDayLine(Habit h) => (h.Condition == "rain" ? I18n.Get("habit.rain-today") + " " : "") + h.Text + ".";
}
