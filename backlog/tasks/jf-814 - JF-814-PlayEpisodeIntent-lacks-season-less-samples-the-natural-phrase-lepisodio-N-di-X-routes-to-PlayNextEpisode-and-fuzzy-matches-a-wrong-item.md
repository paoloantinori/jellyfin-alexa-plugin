---
id: JF-814
title: >-
  JF-814 - PlayEpisodeIntent lacks season-less samples: the natural phrase
  "l'episodio N di X" routes to PlayNextEpisode and fuzzy-matches a wrong item
status: Done
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
- [x] #1 dotnet build passes with 0 errors (both TFMs; solution build clean)
- [x] #2 dotnet test passes (5529/5529 net9.0; net10.0 5529/5529 on rerun; the one-run VideoAudioController flake is pre-existing and filed as JF-842)
- [x] #3 No new compiler warnings introduced (build output grep for error|warn clean; NoWarn set unchanged)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (n/a + verified: the change adds no session attributes; the elicit passes no session state)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (n/a + verified: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (it-IT incident phrase one-shot + two-step + PlayNextEpisode competition guard; en-US skipped as not clean, its own fixture documents the PlaySong free-text theft; dry-run 8 passed / 1145 skipped)
- [x] #7 E2E test added for new intent or handler logic (handler logic pinned at unit level: elicit-shape, slotValues echo, series-only NextUp preserved, season-without-episode fall-through; live NLU/E2E verification is the orchestrator's post-deploy battery per the round-2 dispatch, no SMAPI access in this round)
- [x] #8 Locale response strings added to all 17 locales (DidNotCatchSeasonNumber in all 17 + ResponseStringsTests ledger)
- [x] #9 /simplify passed (4 angles clean, no blocking cleanups)
- [x] #10 /code-review high passed (round-2: F2/F3 applied, F1 rejected-then-corrected per F2 below, F4/F5 filed same-turn; gate-marker round: F1/F4/F6/F7 applied, F2/F3/F5 filed as record/task amendments, all same-turn)
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
3. That behavior was PINNED green by the JF-324-era
   `HandleAsync_MissingSeasonNumber_FallsBackToNextUp` (then at
   PlayEpisodeIntentHandlerTests.cs:206; superseded 2026-10-09 by the JF-814
   re-decision as `HandleAsync_EpisodeNumberWithoutSeason_ElicitsSeasonNumber`,
   with the preserved JF-324 case split out as
   `HandleAsync_SeriesOnly_StillFallsBackToNextUp`): it built exactly
   `seriesName: "The Office", episodeNumber: "10"` (season absent) and asserted
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
`HandleAsync_MissingSeasonNumber_FallsBackToNextUp` (done 2026-10-09: flipped
to `HandleAsync_EpisodeNumberWithoutSeason_ElicitsSeasonNumber`, red recorded
against the unmodified handler on both TFMs before the fix) with red-green
evidence:
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

## Round 2 record (2026-10-09, worktree agent-af77920eafddbeb3a)

Landed as commit cd6b1226 (handler + 17 locales + 17 templates/models +
fixtures + mirrors) plus the tail commit after the orchestrator gate-marker.

### Red-green evidence summary

- JF-324 pin re-decision: `HandleAsync_MissingSeasonNumber_FallsBackToNextUp`
  flipped to `HandleAsync_EpisodeNumberWithoutSeason_ElicitsSeasonNumber` and
  run against the UNMODIFIED handler first: RED on both TFMs ("a question must
  keep the session open or the mic never listens", 15 passed / 1 failed each).
  Handler change applied, then green. A second pin
  (`HandleAsync_SeriesOnly_StillFallsBackToNextUp`) locks the preserved
  JF-324 series-only case; a code-review pin
  (`HandleAsync_SeasonNumberWithoutEpisode_FallsBackToNextUp`) locks the
  season-without-episode fall-through (JF-841's shape).
- Gate-marker F1 (JF-614 slotValues echo): both elicit pins flipped to assert
  the echoed slotValues and run against the value-less shape first: RED on
  both TFMs (2 failed / 15 passed; Expected "Sailor Moon"/"4", Actual null).
  Echo fix applied, green 17/17 both TFMs.
- Full suite: 5529/5529 net9.0; net10.0 5529/5529 on rerun (one run hit the
  pre-existing VideoAudioController flake, filed as JF-842).

### Gate-marker dispositions (seven findings)

- F1 (JF-614 elicit contract): APPLIED. Both PlayEpisode elicits now echo
  already-captured slots as slotValues via BuildElicitSlotResponse (the
  series_name elicit echoes season/episode raw values; the season elicit
  echoes series_name + episode_number), mirroring the
  AddSongToPlaylistIntentHandler precedent. Red-green as above.
- F2 (disposition-record correction): APPLIED, see the correction below.
- F3 (season-only gate extension scope): APPLIED as a JF-841 amendment (the
  unparseable-but-filled episode_number shape joins the filing).
- F4 (validator enforcement): APPLIED. Phase 10 error check
  `check_play_episode_season_without_episode` in
  scripts/validate_interaction_models.py fails on any PlayEpisode sample with
  {season_number} but no {episode_number} in any locale; validator PASSes with
  0 errors at the unchanged 294-warning baseline; self-test confirmed the
  check fires on a synthetic season-only sample.
- F5 (absolute-vs-per-season numbering): APPLIED, filed as JF-843.
- F6 (wrapper-pin rationale): APPLIED, the it-IT fixture comment now names the
  JF-684 catalog NO_SELECTION dependency (the battery's safety rests on the
  live catalog resolving the series).
- F7 (finalize): this section and the pin-name corrections above.

### F2: the round-2 worker's F1-rejection rationale was FALSE (record correction)

The worker's /code-review disposition rejected the elicit-loop finding citing
"AMAZON.NUMBER elicitation is validated/coerced Amazon-side". That is FALSE
for this model: season_number is registered with `elicitationRequired: false`
(manual dialog control) in all 17 models, and under manual dialog control
Amazon performs NO validation of elicit replies, so a non-number answer can
leave the slot unfilled and the handler re-elicts until the user cancels.
WARNING for future rounds: never cite "validated Amazon-side" for AMAZON.*
elicit replies under elicitationRequired:false; the sound grounds for the
no-cap re-elicit shape are the JF-549 series_name precedent plus the
BuildCancelDuringOpenElicit escape hatch ONLY. No code change: the no-cap
re-elicit stays the JF-549 house shape.
<!-- SECTION:NOTES:END -->
