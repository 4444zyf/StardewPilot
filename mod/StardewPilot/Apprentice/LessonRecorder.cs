using System;
using System.Collections.Generic;
using System.Linq;
using StardewPilot.Apprentice.Model;
using StardewValley;

namespace StardewPilot.Apprentice;

/// <summary>
/// "Watch me": an ordered list of what the local player does by hand, kept apart from the day log. The watcher feeds it.
/// A lesson is asked for by the player, so it records even when apprentice mode is off; then it reaches no day log.
/// </summary>
internal sealed class LessonRecorder
{
    /// <summary>A lesson ends by itself after this many game minutes.</summary>
    public const int MaxMinutes = 6 * 60;

    public bool Active { get; private set; }

    /// <summary>Why the lesson ended by itself, or null while it runs or when the player ended it.</summary>
    public string? EndedBecause { get; private set; }

    public int StartedAt { get; private set; }
    public string Day { get; private set; } = "";

    /// <summary>True from start until end_lesson read it (a lesson that ended by itself still waits to be offered).</summary>
    public bool HasLesson { get; private set; }

    public List<LessonEvent> Events { get; } = new();

    public void Start(string day)
    {
        this.Events.Clear();
        this.Active = true;
        this.HasLesson = true;
        this.EndedBecause = null;
        this.StartedAt = Game1.timeOfDay;
        this.Day = day;
    }

    /// <summary>Stop recording; what was seen is kept until it is read.</summary>
    public void Stop(string? because)
    {
        if (!this.Active)
            return;
        this.Active = false;
        this.EndedBecause = because;
    }

    /// <summary>Forget the lesson (the day ended, or the player dropped it).</summary>
    public void Drop()
    {
        this.Active = false;
        this.HasLesson = false;
        this.EndedBecause = null;
        this.Events.Clear();
    }

    /// <summary>The events, once; the lesson is then over.</summary>
    public List<LessonEvent> Take()
    {
        var events = this.Events.ToList();
        this.Drop();
        return events;
    }

    /// <summary>Add an event; one of the same kind, map, item and place just before it only grows.</summary>
    public void Add(LessonEvent e)
    {
        if (!this.Active)
            return;
        e.Time = Game1.timeOfDay;
        if (e.Act != LessonAct.Job && e.Act != LessonAct.Left && this.Events.LastOrDefault() is { } last
            && last.Act == e.Act && last.Map == e.Map && last.Item == e.Item && last.Detail == e.Detail)
        {
            last.Count += e.Count;
            return;
        }
        // A "left" note replaces an earlier one for the same map.
        if (e.Act == LessonAct.Left)
            this.Events.RemoveAll(x => x.Act == LessonAct.Left && x.Map == e.Map);
        this.Events.Add(e);
        if (this.Events.Count >= LessonSteps.MaxEvents)
            this.Stop(I18n.Get("lesson.because-max", new { count = LessonSteps.MaxEvents }));
    }
}
