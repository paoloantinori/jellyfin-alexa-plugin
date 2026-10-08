---
id: JF-814
title: >-
  JF-814 - PlayEpisodeIntent lacks season-less samples: the natural phrase
  "l'episodio N di X" routes to PlayNextEpisode and fuzzy-matches a wrong item
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - nlu
  - bug
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the 2026-10-08 device-round incident (live evidence, profile-nlu
confirmed): the natural one-shot "chiedi a mia collezione di mettere l'episodio
54 di Sailor Moon" selects **PlayNextEpisodeIntent** (series_name="54 di sailor
moon"), not PlayEpisodeIntent - EVERY PlayEpisode sample in the it-IT model (and
per the template family, all 17 locales) requires the season number. The
PlayNextEpisode handler then fuzzy-resolves the polluted slot and matched
"Sailor Moon R - The Movie" (the wrong item), minting a VideoApp launch for it.

Fix shape: add season-less PlayEpisode samples to the it-IT template (and the
16 mirrors): "metti l'episodio {episode_number} di {series_name}" plus the
imperative/infinitive/one-shot-wrapper family. The handler already elicits the
season when the slot is missing (verify: season_number elicit path exists -
the elicitation flow must catch the season-less match and ask "quale stagione?"
before querying). MIND the NLU competition rules (anti-pattern #3): the new
samples must not steal from PlayNextEpisodeIntent's legitimate phrases
("l'episodio successivo/prossimo di X") - run the nlu-verify battery over both
intents' phrases in the 17 locales before deploying.

Evidence: profile-nlu 2026-10-08 selectedIntent=PlayNextEpisodeIntent for the
exact user utterance; the handler log shows the wrong-item fuzzy match and the
VideoApp launch for 'Sailor Moon R - The Movie'.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Part-2 worker round 2026-10-08 (worktree agent-a83017f0a5e671049 off main tip
436b35b9). STOPPED at the step-1 elicit-prerequisite gate, per the dispatch
instruction: the handler code must be able to ask for the season before the
season-less samples ship, and it cannot. No templates, models, or fixtures were
touched. The task needs re-scoping: handler code + locale string first, then
the template family.

## The verification verdict (code read, all four checks)

1. `PlayEpisodeIntentHandler.HandleAsync` has exactly ONE elicit path: the
   `series_name` one (`DidNotCatchSeriesName`, lines 82-97). There is NO
   season elicit branch anywhere in the handler.
2. What a season-less match actually does today: series present + episode
   parseable + season empty makes `ItalianNumberWords.TryParse(seasonRaw)`
   return false (the helper returns false on null/whitespace,
   ItalianNumberWords.cs:54-57), so `hasExplicitNumbers` is false and the
   `!hasExplicitNumbers` branch (lines 136-139) calls
   `TvNextUp.PlayNextUpEpisodeAsync`: the handler SILENTLY launches the
   series' next-up episode. It never asks "quale stagione?" and never plays
   the requested episode number.
3. That behavior is PINNED green by
   `HandleAsync_MissingSeasonNumber_FallsBackToNextUp`
   (PlayEpisodeIntentHandlerTests.cs:206): it builds exactly
   `seriesName: "The Office", episodeNumber: "10"` (season absent) and asserts
   a VideoAppLaunchDirective, with the comment "partial or missing numbers
   fall back to the NextUp core" (the JF-324 fallback). The handler change is
   therefore a pinned-behavior re-decision, not an additive branch.
4. No "which season" prompt string exists in any locale: grep for
   `DidNotCatchSeason|quale stagione|WhichSeason|DidNotCatchEpisode` over the
   plugin (cs + all 17 locale json) returns zero hits. The fix needs a NEW
   key (DidNotCatchSeasonNumber shape) in all 17 `<locale>.json` files
   (DoD #8 of this task).

Why templates-alone would be a half-ship (the dispatch's own ground): the
incident phrase would then correctly SELECT PlayEpisodeIntent, but the handler
would silently launch the series' next-up episode instead of episode 54: a
wrong-item launch from the same utterance, trading the wrong-series fuzzy
match for a wrong-episode silent substitution.

## What is already in place (the re-scope is small)

- `ElicitSlots` already carries the full PlayEpisode dialog set including
  `season_number` (ElicitSlots.cs:29), so the elicit's allSlotNames payload
  is ready with no table change.
- All 17 models' `dialog.intents` already declare PlayEpisodeIntent WITH
  `season_number` (verified per locale across the 17 generated models), so
  anti-pattern #9 (silent elicit drop) cannot bite.
- The cancel-word escape hatch (`BuildCancelDuringOpenElicit`) already runs
  in this handler before any elicit, so the JF-549 dead-mic rules are wired.
- Part 1 (AMAZON.NUMBER season/episode slots in it-IT) landed: the elicited
  season answer arrives as digits and `ItalianNumberWords.TryParse` parses
  digits in every locale.

## The handler fix shape (for the re-scoped round)

In `HandleAsync`, after `seasonRaw`/`episodeRaw` are read and BEFORE the
`SearchingMedia` progressive response: when
`TryParse(episodeRaw)` succeeds AND `TryParse(seasonRaw)` fails, return
`BuildDialogElicitResponse("DidNotCatchSeasonNumber", locale,
"season_number", IntentNames.PlayEpisode, Util.ElicitSlots.For(IntentNames.PlayEpisode))`.
Add the key to all 17 locale json files. Re-decide the JF-324 pin
`HandleAsync_MissingSeasonNumber_FallsBackToNextUp` with red-green evidence:
under the new design, "episode present + season missing" elicits the season;
only the fully-numberless series-only request (JF-324's original case) keeps
the next-up fallback. Note the pin's current shape covers the partial-number
case beyond JF-324's stated intent ("a series-only request"), which is why
the re-decision is legitimate rather than a regression. Then the template
family (the original part-2 steps) ships unchanged on top.

Alternative considered and rejected for the record: query the series'
episodes with `IndexNumber == N` across all seasons and play the unique match
without asking. Rejected because episode N exists in multiple seasons with
different content for any multi-season series, so it is ambiguous; silently
picking one is exactly the wrong-item class this task closes.
<!-- SECTION:NOTES:END -->
