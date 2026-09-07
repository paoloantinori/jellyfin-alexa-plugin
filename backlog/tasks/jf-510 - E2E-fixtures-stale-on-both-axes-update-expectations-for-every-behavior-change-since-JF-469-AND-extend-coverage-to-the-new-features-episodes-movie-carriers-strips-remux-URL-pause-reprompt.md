---
id: JF-510
title: >-
  E2E fixtures stale on both axes: update expectations for every behavior change
  since JF-469 AND extend coverage to the new features (episodes, movie
  carriers, strips, remux URL, pause reprompt)
status: Done
assignee: []
created_date: '2026-09-06 19:10'
updated_date: '2026-09-07 00:10'
labels:
  - e2e
  - tests
  - debt
dependencies: []
references:
  - JF-469 (last fixture touch)
  - JF-487
  - JF-488
  - JF-489
  - JF-490
  - JF-492
  - JF-493
  - JF-498
  - JF-504
  - JF-505
  - JF-509
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-09-06 direction: the E2E fixtures (tests/integration/fixtures/e2e_it-IT.yaml, e2e_en-US.yaml, e2e_reliability_it-IT.yaml) are stale at JF-469 (2026-09-03) on BOTH axes: they neither track the behavior changes landed since NOR cover the new features at all. The suite is runnable autonomously (./scripts/run_e2e_tests.sh with ASK_SKILL_ID + JELLYFIN_URL/API_KEY/USER; orchestrator-proven 2026-09-06) so this debt is pure fixture work, no tooling needed.

AXIS 1 - UPDATE EXISTING EXPECTATIONS for landed behavior changes (verify each against the live suite, then update the fixture):
- JF-488: pause with PauseKeepsSession (now default ON) speaks 'Pausa.' + reprompt with shouldEndSession=false; the 'pausa' fixture's loose response_type:any should be tightened to pin the reprompt presence.
- JF-487: FindSong single-candidate auto-play ('Riproduco X di Y', no 'Quale?'), singular count grammar, ArtistName from item metadata ('di Soul Coughing', never 'di Unknown'); welcome SSML separators.
- JF-492/JF-509: the calling-word/musician-strip and media-noun-strip recoveries change not-found shapes ('cerca un album chiamato X' now plays; 'film ada' heals to 'ada').
- JF-498: video items with incompatible audio route to the HLS remux URL (/alexaskill/api/video-audio/episode/{id}/stream.m3u8) in the stream directive instead of /Videos/static; movies (compatible) keep static.
- JF-493: PlayNextEpisode ('il prossimo/ultimo episodio di {series}', 'continua a guardare {series}') with the catalog-backed series fill; PlayEpisode without explicit numbers falls to NextUp.
- JF-504: movie-by-title carriers ('il film {title}') route; the swallowed-noun shape heals.
- JF-505: the VideoApp gate is fail-open on absent interface maps (the simulator context) so video fixtures keep working, but a screenless-context pin belongs in the unit suite only - do NOT try to e2e it.

AXIS 2 - EXTEND with new-feature coverage (all absent today):
- Next/latest episode plays (JF-324/JF-493): 'metti il prossimo episodio di <series>', 'l'ultimo episodio di', 'continua a guardare', and the explicit 'la stagione N episodio M di' numbers-first shape.
- Movie by title (JF-504): 'voglio guardare il film <title>' and the JF-509 swallowed-carrier shape (assert the play directive or the VideoApp source URL).
- Album calling-word family (JF-489/JF-492): 'un album chiamato <title>' and 'cerca un album chiamato <title>'.
- che-si-chiama carriers (JF-490 + follow-up): 'un album che si chiama <title>' and the verb-ful form.
- Series catalog fill (JF-493): a real-library series name resolves (ER_SUCCESS_MATCH shape is NLU-level; e2e asserts the launched item name).
- The JF-498 remux source-URL assertion on one h264+eac3 episode vs the static URL on one compatible movie.

CONSTRAINTS: e2e en-US competes with built-in skills (known flakiness) - keep new coverage on it-IT where possible; respect SMAPI_DELAY=1.5 (suite wall-clock scales with test count: keep additions ~20-25 tests, not double the suite); every new fixture must assert something specific (intent + the NEW behavior marker: reprompt presence, m3u8 source, played item name), never response_type:any, or the stale-fixture failure mode returns. Workflow: run the live suite FIRST, triage each failure into 'fixture stale' vs 'real regression' (file real ones), then update+extend in one pass, re-run to green, commit with gates.
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
WORKER EXECUTION (2026-09-06/07, no-subagent session; status stays In Progress for the orchestrator's commit+gates). Skill amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83 (re-discovered via list-skills-for-vendor, matches the orchestrator context).

TRIAGE of the 8 orchestrator-run failures (re-run with full output + profile-nlu CLEAN-string probes, no session context): (1) e2e:en-US 'play random rock songs' FIXTURE STALE on the slot axis: the intent routes PlayRandomIntent, the genre slot fills EMPTY exactly as NLU fixture en-US.yaml documents for this literal sample; the e2e entry wrongly pinned genre+media_type -> slots dropped, comment cites the NLU fixture. (2) 'suona i pink floyd' REAL MODEL REGRESSION (JF-508 family): profile-nlu selects PlayAlbumIntent album='i P!nk floyd' (the AlbumName catalog phonetic anchor steals the nominative-article artist query; 'suona i radiohead'/'suona i beatles' select NO intent at all while 'metti i led zep' still routes) -> skip_reason + replacement coverage added. (3) 'metti una canzone dei beatles' and (4) '...xyzzyfoo' REAL REGRESSION (JF-508): PlaySongIntent 'Metti {song_noun} {song}' absorbs the phrase wholesale for OUT-OF-CATALOG artists (song='1 canzone dei X'); in-catalog names still win via the JellyfinArtist anchor -> skip_reason on both + skip mark on the fast-mode xyzzyfoo param (Fast-mode path unchanged, covered by the in-catalog entry).

(5) 'suona la band radiohead' REAL REGRESSION (JF-508 part A confirmed live): NO intent selected in profile-nlu; simulate-skill fails to resolve entirely -> skip_reason. (6) 'riproduci album thriller' LANDSCAPE DRIFT, not a plugin regression: the JF-470-documented tie became NO selection ('riproduci l'album thriller' also None; 'metti il disco thriller' steals to PlaySongIntent) -> skip_reason; PlayAlbum routing pinned by the chiamato/che-si-chiama entries which fill the library-synced catalog deterministically. (7) reliability 'pausa' 2/3 JF-488-CAUSED but NOT the reprompt shape: iter 2 routed AMAZON.FallbackIntent because the pause response now KEEPS THE SESSION OPEN and the next prefixed simulation is captured into the open session (live probe: prefixed pausa x2 without reset alternates Pause/Fallback; bare 'stop' resets) -> harness inter-iteration reset, CONDITIONAL on the response body's shouldEndSession (an unconditional reset pushed the 5-iteration tests past the 120s per-test alarm; live 2026-09-07 'riproduci i miei preferiti' hit _TimeoutError); reliability pausa now 3/3. (8) fast-mode xyzzyfoo same as (4) -> pytest.param skip mark. All model-regression evidence appended to JF-508 the same turn (catalog versions JellyfinArtist v637 / AlbumName v643 / SeriesName v73; the JF-504 movie-carrier additions + repeated catalog re-injections shifted the statistics).

HARNESS (tests/integration/test_e2e.py). BUG FIX (load-bearing): _extract_skill_response read result.alexaExecutionInfo.skillExecutionInfo, but skillExecutionInfo is a SIBLING of alexaExecutionInfo under result; the response-shape assertions NEVER saw the body, which is why every historical fixture used expected_response_type:any and why a fixture comment claimed simulate-skill does not expose directives/outputSpeech (that stale false rationale was corrected in the playlist fixture comment). Verified against raw SMAPI JSON dumps. New fixture keys: expected_speech_contains, expected_reprompt (asserts reprompt present AND shouldEndSession not true), expected_end_session, expected_stream_url_contains (AudioPlayer.Play url / VideoApp.Launch source), skip_reason (skip only with an evidence-naming reason; validated in dry-run). _parse_skill_response/_extract_ssml_outputs/_extract_apl_directives now walk the current invocations[] shape via a shared _iter_response_bodies (the SSML-validity check was silently vacuous before). Bounded 3-attempt retry when a body-expecting fixture gets a no-invocation simulation (intent resolves, skillExecutionInfo empty): an Amazon-side throttle artifact under sustained SMAPI traffic (~1-2 tests per full run, windows ~35s, observed twice live); a genuinely broken endpoint fails all attempts, so this smooths infra flake only.

AXIS 2 (+19 it-IT fixtures, every one asserting a specific marker; all live-verified via profile-nlu + simulate-skill before landing): 9 episode fixtures (prossimo/ultimo/guarda/continua a guardare/continua la serie + numbers-first imperative/infinitive + series-first, on The Bear and Murderbot via the JF-493 series catalog); 5 movie fixtures ('voglio guardare'/'riproduci'/'cerca il film ada' on the compatible h264+aac movie, 'metti il film inside out 2' on the h264+eac3 remux class, plus the SKIPPED 'riprodurre il film ada' documenting the JF-504 open infinitive one-shot item); 4 album chiamato/che-si-chiama fixtures (cerca-chiamato, bare che-si-chiama, verb-ful che-si-chiama, disco variant; each pins the PLAY: 'In riproduzione' speech + AudioPlayer.Play + /Audio/ static URL); 1 pink floyd artist replacement ('metti una canzone dei pink floyd' -> PlayArtistSongsIntent musician='P!nk floyd', plays).

VIDEO MARKER CAVEAT (corrects this task's own AXIS-1 assumption): the JF-505 gate is NOT fail-open in simulate-skill; the simulated device reports SupportedInterfaces WITHOUT VideoApp, so every video launch is refused with the localized VideoRequiresScreen Tell. That Tell is the assertable marker that NLU -> catalog fill -> episode resolution -> launch decision ran end to end; the JF-498 remux-vs-static SOURCE URL is decided inside GetVideoAppLaunchUrl BEFORE the gate and is NOT observable in the response (the gate suppresses VideoApp.Launch). The URL routing stays pinned by the PlayVideoIntentHandlerTests unit wiring; Murderbot S1E3 (h264+eac3) and Inside Out 2 (h264+eac3) were chosen so the remux CLASS is exercised end to end even though the URL itself is not assertable here. JF-487 note: the FindSong single-candidate auto-play shape is not e2e-reachable with the current library (every artist has many tracks; live: both FindSong fixtures elicit); it stays unit-pinned. JF-488 pause pinned in e2e: speech 'Pausa.' + reprompt 'Dimmi pure quando vuoi riprendere.' + shouldEndSession=false + AudioPlayer.Stop directive, all four on one fixture.

GATES: /simplify pass applied (shared _output_speech_text helper, marker early-return, single-element loop removed, error-message clarity); review-local methodology run directly (no sub-agents per session rules): 1 finding at 85 (the playlist fixture's now-false 'simulate-skill does not expose directives' rationale, invalidated by the path fix) applied immediately; below-80 items noted (skipped tests still burn one reset simulation; reset wall-clock cost, since made conditional). VERIFICATION: full suite on the FINAL code (/tmp/jf510_full7.log) = 70 passed, 7 skipped (5 JF-508-family regressions + the JF-504 infinitive open item + fast-mode xyzzyfoo, each skip carrying its evidence), 0 failed, 22:01 wall clock; earlier runs on intermediate code: full3 green, full4/full5 exposed the timeout-then-throttle artifacts that drove the conditional reset and the 3-attempt retry; NLU dry-run 860 skipped clean; e2e dry-run 77 collected clean. dotnet build/test not applicable (no C# change). No commits made (worker rule).

Formal review dispositions (2026-09-07, orchestrator): no findings at threshold. Sub-threshold landed same-turn: (1) the stale 'every e2e fixture is it-IT' comment in test_e2e.py fixed (false since May; en-US runs with the benign it-IT reset); (2) JF-508's note wording corrected (4 skip_reason entries point at JF-508; the thriller skip points at JF-470 landscape drift, which JF-508 sub-finding 4 already carries); (3) latent vacuity RECORDED: expected_end_session:false passes vacuously on an absent body - harmless today (the only user, the pause fixture, carries three other body-dependent markers) but any future false-end-session fixture must pair with a body marker; (4) conftest's e2e_ glob double-matches e2e_reliability_it-IT.yaml (its 3 utterances also run as full_chain cases: the pausa0/pausa1 log ids), wasting ~2 SMAPI calls per run - pre-existing, fix opportunistically when conftest is next touched (an exclusion must NOT drop the dedicated reliability suite).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-07 (commit a47d6777). Both axes done: Axis 1 - expectations updated for every behavior change since JF-469 (pause pinned to the full JF-488 reprompt shape with all four markers; stale en-US slot pins dropped); Axis 2 - +19 it-IT fixtures with specific markers covering next/latest/continua episodes (numbers-first and series-first), movie carriers incl. the h264+eac3 remux class, the album chiamato/che-si-chiama families, and a pink floyd artist replacement. Harness load-bearing fix: the response-body extraction path was structurally wrong (skillExecutionInfo is a sibling of alexaExecutionInfo), which is why every historical fixture used response_type:any; the fix unlocked real body assertions (speech/reprompt/end_session/stream_url markers), a conditional reliability reset (the pause session now stays open), and a bounded retry for the observed Amazon-side no-invocation throttle artifact. Final live suite: 70 passed, 7 skipped-with-evidence, 0 failed (22 min). The triage surfaced 5 REAL model regressions (the JF-508 family, now carrying the live catalog-shift evidence: JellyfinArtist v637/AlbumName v643/SeriesName v73) and corrected this task's own AXIS-1 assumption (the JF-505 gate is NOT fail-open in simulate-skill; the localized refusal Tell is the end-to-end marker, the remux URL stays unit-pinned). The review gate's 4 sub-threshold items landed same-turn.
<!-- SECTION:FINAL_SUMMARY:END -->
