# Habits in the morning briefing

Date: 2026-11-16. Status: implemented on 2026-11-16; not yet released.

Apprentice mode ([design](2026-11-16-apprentice-mode-design.md)) learns habits, but the player only meets them by asking. This round puts them where the player already looks each morning, the briefing. Two items from its "Later rounds" list:

1. **A personal line**: when today is a kind of day the player has a habit for, the briefing says so. "Rainy day: you usually spend these at the beach, fishing (4 of 5)."
2. **A one-time offer**: when a morning habit with jobs first appears, the briefing offers to do it. "I've noticed your usual morning: water the crops, then harvest the crops. Say "do my usual morning" and I'll do it for you."

## The personal line

- **Which habit:** the day-type habits from `HabitFinder.DayTypes`. Rain wins over the weekday, because the watcher's rainy days are the stronger pattern and both lines would crowd the briefing. At most one line.
- **Rain** is the same test the watcher logs (`farm.IsRainingHere()`), so a habit learnt from rainy days matches rainy days. The weekday is the watcher's (`Weekdays[(day - 1) % 7]`).
- **Wording:** the fallback line is the habit's own text ("Rain today. Rainy days: mostly at the mountain, fishing (4 of 5)."); the agent is told to word it as "you usually ...", never as advice or as something the player said.
- **Facts:** `usualDay = { condition, text, days, ofDays }` in `get_day_briefing` and the seed's briefing facts, so the agent words it like the other points.
- **Not in self-play:** the agent already plans from the habits there, and the line is for the player.

## The one-time offer

- **When:** the morning habit has at least one step with a job (`MorningStep.Job`), and that list of jobs, in order, was not offered before.
- **Once per list.** The offered list is stored in `apprentice-offer.txt` beside the notice marker, written at once (like the briefing record), so quitting without sleeping does not repeat it. When the habit changes (a new step, a different order), the new list is offered once more. A habit that fades and comes back unchanged is not offered again.
- **Only jobs.** Steps without a job (fishing, crafting...) are left out of the offer; round one's morning activities are all reversible, so the offer never names shipping, selling, gifting or spending.
- **Not a question.** The briefing rule stays "no question back"; the offer tells the player what to say. Nothing runs until they say it.
- **Facts:** `habitOffer = { steps: [words...], say: "do my usual morning" }`.
- **Shown only when the briefing is shown.** The marker is written where the briefing record is written (fallback text, the agent's wording, or self-play's plan). In self-play the offer is left out and not marked, so the player still sees it on a day they play.

## Off switches

- `Apprentice.Enabled = false`: neither part (no habits are read).
- `MorningBriefing = false`: neither part; the offer waits for a day with a briefing.
- `/forget habits` also deletes `apprentice-offer.txt`, so a habit learnt again is offered again.

## Work items

| # | Item | State |
|---|---|---|
| B1 | Pure `BriefingHabits` (usual-day pick, offer list and its key) with tests | Done |
| B2 | Briefing facts and fallback lines; offer marker in `MemoryStore`; marked where the briefing is marked; `/forget` deletes it | Done |
| B3 | Agent: the briefing prompt keeps the offer's exact phrase; system prompt line | Done |
| B4 | In-game check: fallback text and agent wording on a rainy day with made-up logs; the offer shown once | Done |

## Checked in the game (2026-11-16)

- 14 made-up summer logs (water, harvest, tend the animals each morning; three rainy days fishing at the mountain), tomorrow set to rain, slept.
- Sunny Friday before: `get_day_briefing` had no `usualDay` (no Friday habit).
- Rainy Saturday: the agent's briefing said "on rainy days you usually fish at the Mountains" and kept the offer line word for word, with "do my usual morning" quoted; `apprentice-offer.txt` held `water_crops,harvest_crops,tend_animals`.
- Rainy Sunday: the rain line again, no offer.
