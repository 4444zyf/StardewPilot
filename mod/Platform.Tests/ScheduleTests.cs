using System;
using System.Collections.Generic;
using System.Linq;
using StardewPilot.Apprentice.Model;

/// <summary>Routines that start themselves: schedule checks, due tests, the run record and the words. No game needed.</summary>
internal static class ScheduleTests
{
    private static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception(what);
    }

    private static Routine R(string name, RoutineWhen? when, params string[] jobs) => new()
    {
        Name = name,
        When = when,
        Steps = jobs.Select(j => new RoutineStep { Job = j }).ToList(),
    };

    private static RoutineWhen W(int at, string start = RoutineSchedule.Offer, string? weather = null, params string[] days) =>
        new() { At = at, Start = start, Weather = weather, Days = days.Length > 0 ? days.ToList() : null };

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("schedule: times parse", () =>
        {
            Check(RoutineSchedule.ParseTime("6am") == 600, "6am");
            Check(RoutineSchedule.ParseTime("4:30 pm") == 1630, "4:30 pm");
            Check(RoutineSchedule.ParseTime("12pm") == 1200, "12pm");
            Check(RoutineSchedule.ParseTime("12am") == 0, "12am");
            Check(RoutineSchedule.ParseTime("16:00") == 1600, "16:00");
            Check(RoutineSchedule.ParseTime("1600") == 1600, "1600");
            Check(RoutineSchedule.ParseTime("morning") == 600, "morning");
            Check(RoutineSchedule.ParseTime("noonish") == null, "noonish");
            Check(RoutineSchedule.ParseTime("13pm") == null, "13pm");
            Check(RoutineSchedule.Day("Fridays") == "Fri" && RoutineSchedule.Day("mon") == "Mon" && RoutineSchedule.Day("x") == null, "days");
        });

        yield return ("schedule: checks", () =>
        {
            Routine plain = R("truffle run", null, "collect_forage", "tend_machines");
            Routine gift = R("town run", null, "take_from_chest", "give_gift");
            Check(RoutineSchedule.Check(W(600, RoutineSchedule.Self), plain, 2330).Count == 0, "6am self");
            Check(RoutineSchedule.Check(W(1600), gift, 2330).Count == 0, "4pm offer with a gift");
            Check(RoutineSchedule.Check(W(605), plain, 2330).Count == 1, "off the grid");
            Check(RoutineSchedule.Check(W(530), plain, 2330).Count == 1, "before 6am");
            Check(RoutineSchedule.Check(W(2030), plain, 2330).Count == 0, "8:30pm is three hours before 11:30pm");
            Check(RoutineSchedule.Check(W(2040), plain, 2330).Count == 1, "8:40pm is too late");
            Check(RoutineSchedule.Check(W(1600, RoutineSchedule.Self), plain, 2330).Count == 1, "self at 4pm");
            Check(RoutineSchedule.Check(W(600, RoutineSchedule.Self), gift, 2330).Count == 1, "self with a gift");
            Check(RoutineSchedule.Check(W(600, days: "Funday"), plain, 2330).Count == 1, "bad day");
            Check(RoutineSchedule.Check(new RoutineWhen { At = 600, Weather = "snow" }, plain, 2330).Count == 1, "bad weather");
        });

        yield return ("schedule: due or not", () =>
        {
            var record = new RunRecord { Date = "Y1-spring-05" };
            var routines = new List<Routine>
            {
                R("b morning", W(600, RoutineSchedule.Self), "water_crops"),
                R("a morning", W(600), "water_crops"),
                R("friday", W(1600, days: "Fri"), "talk_to"),
                R("rainy", W(900, weather: "rain"), "tend_animals"),
                R("dry", W(900, weather: "dry"), "tend_animals"),
                R("none", null, "water_crops"),
            };
            List<string> Due(string day, bool rain, int now) =>
                RoutineSchedule.Due(routines, "Y1-spring-05", day, rain, now, record, _ => true).Select(r => r.Name).ToList();

            Check(string.Join(",", Due("Thu", false, 600)) == "a morning,b morning", "order: " + string.Join(",", Due("Thu", false, 600)));
            Check(Due("Thu", false, 750).Count == 2, "inside the window");
            Check(Due("Thu", false, 800).Count == 0, "after the window");
            Check(!Due("Thu", false, 1600).Contains("friday"), "not on Thursday");
            Check(Due("Fri", false, 1600).Contains("friday"), "on Friday");
            Check(Due("Fri", false, 1550).Count == 0, "before the time");
            Check(Due("Thu", true, 900).SequenceEqual(new[] { "rainy" }), "rain");
            Check(Due("Thu", false, 900).SequenceEqual(new[] { "dry" }), "dry");

            record.Runs["a morning"] = new RoutineRun { Time = 610, Done = true };
            record.Runs["b morning"] = new RoutineRun { Time = 610, Done = false };
            Check(Due("Thu", false, 700).SequenceEqual(new[] { "b morning" }), "a ran today; b was stopped by the player");

            routines[0].When!.SetOn = "Y1-spring-05";
            Check(!Due("Thu", false, 700).Contains("b morning"), "set today: takes effect tomorrow");
        });

        yield return ("schedule: the run record", () =>
        {
            var record = new RunRecord { Date = "Y1-spring-05" };
            record.Runs["truffle run"] = new RoutineRun { Time = 600, Done = true };
            record.Runs["town run"] = new RoutineRun { Time = 1600, Done = false };
            record.LastStop["truffle run"] = "step 2: no truffles";

            // Kept on the farmer as text, and read back.
            string text = record.RunsText();
            Check(text == "Y1-spring-05|town run=1600:0|truffle run=600:1", text);
            var back = new RunRecord();
            back.ReadRuns(text);
            Check(back.Date == "Y1-spring-05" && back.RanToday("truffle run") && !back.RanToday("town run") && back.Runs["town run"].Time == 1600, "read back");
            back.ReadRuns("Y1-spring-05|bad|x=y:1|ok=700:1");
            Check(back.Runs.Count == 1 && back.RanToday("ok"), "bad parts skipped");
            back.ReadRuns(null);
            Check(back.Date == "" && back.Runs.Count == 0, "nothing kept");

            // The same date keeps the runs; a new date clears them, but not the last stops.
            Check(!record.Reconcile("Y1-spring-05") && record.Runs.Count == 2, "same day");
            Check(record.Reconcile("Y1-spring-06") && record.Runs.Count == 0 && record.Date == "Y1-spring-06", "new day");
            Check(record.LastStop.ContainsKey("truffle run"), "last stop kept across days");
        });

        yield return ("schedule: words", () =>
        {
            Check(RoutineSchedule.Words(W(600)) == "every morning", RoutineSchedule.Words(W(600)));
            Check(RoutineSchedule.Words(W(1600, days: "Fri")) == "at 4pm on Fridays", RoutineSchedule.Words(W(1600, days: "Fri")));
            Check(RoutineSchedule.Words(W(600, weather: "rain")) == "every morning when it rains", RoutineSchedule.Words(W(600, weather: "rain")));
            string two = RoutineSchedule.Words(W(930, days: new[] { "Thu", "Mon" }));
            Check(two == "at 9:30am on Mondays and Thursdays", two);
            Check(RoutineSchedule.Sentence(W(600, RoutineSchedule.Self)) == "starts by itself every morning", "sentence");
            Check(RoutineSchedule.Sentence(W(1300)) == "offered at 1pm every day", RoutineSchedule.Sentence(W(1300)));
        });
    }
}
