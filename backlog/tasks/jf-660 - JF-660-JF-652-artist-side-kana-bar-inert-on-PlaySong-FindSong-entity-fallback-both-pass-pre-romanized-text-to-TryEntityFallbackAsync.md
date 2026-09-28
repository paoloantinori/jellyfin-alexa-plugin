---
id: JF-660
title: >-
  JF-660 - JF-652 artist-side kana bar inert on PlaySong/FindSong entity
  fallback: both pass pre-romanized text to TryEntityFallbackAsync
status: Done
assignee: []
created_date: '2026-09-28 11:25'
updated_date: '2026-09-28 16:54'
labels:
  - search
  - i18n
  - ja-JP
  - acceptance
  - device-found
dependencies: []
references:
  - >-
    backlog/tasks/jf-652 -
    JF-652-kana-derived-queries-are-wrongly-accepted-by-Latin-calibrated-thresholds-クイーン-plays-Keane-silent-91-tie-and-ビートルズ-plays-Sator-plain-fuzzy-false-positive-the-JF-643-completion-blocker.md
  - >-
    backlog/tasks/jf-654 -
    JF-654-the-song-side-kana-bar-is-missing-ビートルズ-refuses-the-wrong-artist-but-TrySongFallback-then-auto-plays-a-wrong-song-Bitters-Absolut-for-the-kana-derived-query.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-28 from the JF-654 /simplify altitude review (out-of-scope observation, verified against the code the same turn).

THE GAP: CrossMediaFallback.TryEntityFallbackAsync computes its artist-side kana-origin flag from its own slotText param pre-romanization (CrossMediaFallback.cs, the `bool kanaOrigin = Util.ArtistSearch.IsKanaOriginQuery(null, slotText);` line, JF-652/JF-659), and its doc claims "the callers pass raw slot text". But TWO callers hand it ALREADY-ROMANIZED text:
- PlaySongIntentHandler.cs:339 passes `songQuery`, romanized at entry since JF-643 (PlaySongIntentHandler.cs:204).
- FindSongIntentHandler.cs:590 passes `keywords`, the romanized local (FindSongIntentHandler.cs:471).

IsKanaOriginQuery on a romaji string returns false, so the JF-652 artist-side kana bar is INERT on those two paths: a kana-origin song/keywords miss ('ビートルズ' -> 'bitoruzu') falls into the plain cross-media artist gates. With the default JF-363 Confirm mode the live class ('bitoruzu' vs 'Sator' ~60) surfaces as a spurious "did you mean Sator?" OFFER rather than a play; a >= 85 plain-fuzzy artist collision would auto-play. Same wrong-accept family JF-652 killed, surviving through two caller paths it never saw.

THE FIX SHAPE (mirror JF-654's song-side threading): thread a kanaOrigin flag into TryEntityFallbackAsync (default false), computed by the callers on the RAW slot pre-romanization via ArtistSearch.IsKanaOriginQuery (PlaySong and FindSong already hold those raws; PlaySong computes a song-slot kanaOrigin local since JF-654 that can be reused, FindSong's is on the stored raw Keywords). Alternatively hoist a RomanizeWithOrigin composite on KatakanaRomanizer so the capture-then-romanize couplet (now at ~8 sites) enforces its ordering by signature (the JF-654 /simplify pass noted the couplet but skipped it as outside that diff).

VERIFICATION BAR: a kana-origin PlaySong title miss takes the honest song not-found (never an artist offer/play without collision evidence), same for a FindSong keywords miss; the Latin cross-media matrix unchanged.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; handler logic pinned by 8 unit tests, E2E re-probe is the orchestrator's post-merge step)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new response strings)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-28 LIVE EVIDENCE (deployed a7d42b09, simulator, log-corroborated): PlaySong song=ビートルズ -> the JF-654 song bar correctly refuses ('no song or album named bitoruzu'), then the JF-363 cross-media ARTIST SUGGESTION fires offering Sator ('アーティストの Sator さんのことでしょうか?'): the suggestion band accepted the plain-fuzzy Sator match because the entity-fallback path passed the pre-romanized text and the JF-652 artist flag was inert - exactly this task's leak, now observed live. Fix priority note: the suggestion (not-found follow-up) leaks the wrong artist NAME into a yes/no prompt; the yes plays Sator. The same flag threading this task specifies closes it.

2026-09-28 DONE (worktree branch, worker session). FIX SHAPED AS THE TASK SPECIFIED, plus a PREMISE CORRECTION the mandated caller-map verification forced: THREE callers hand the gate pre-romanized text, not two. THREADING MAP (TryEntityFallbackAsync gained `bool? kanaOrigin = null`, the TrySongFallback shape, null = self-compute from the slot text pre-romanization):
- PlaySongIntentHandler: THREADS `kanaOrigin` (reuses the JF-654 local captured on the raw song slot, line 203; the gate call passes the romanized local).
- FindSongIntentHandler: THREADS `kanaOrigin` (reuses the JF-654 local captured on the raw stored Keywords, line 474).
- PlayAlbumIntentHandler: THREADS `albumKanaOrigin`, a NEW capture (the task's caller map asserted PlayAlbum passes raw text; it does not: the album slot is romanized IN PLACE at entry, line 108, so the self-computed flag was inert there too, same leak class through the JF-363 album band). Capture follows the file's own musician precedent: `IsKanaOriginQuery(canonicalAlbum, album)` with a NEW `canonicalAlbum` extraction via SlotValueHelper (the JF-659 invariant, an ER-resolved album keeps the flag false). The flag's validity at the fallback rests on a load-bearing invariant: the later `album` reassignments (JF-489/JF-492 retries, JF-411 resolution) all guarantee a non-empty album result downstream, so the fallback is never reached with their values (documented at the capture).
- PlayByGenre (raw genreSlot) and PlayMoodMusic (raw mood): UNCHANGED, self-compute default (documented fail-safe edge: the self-compute reads no ER canonical, so an ER-matched kana genre keeps the bar live where a canonical-aware flag would inert it; the stricter direction).
ROMANIZEWITHORIGIN DECISION: NOT adopted (the task's alternative, item 3). The ~8 capture-then-romanize couplets are heterogeneous (canonical-bearing vs not, null-guarded vs not, plus two self-computing sinks that take flags pinned by their callers, which the composite does not eliminate); converting them is a cross-cutting refactor with zero behavior change, outside this task's contained diff. The composite remains the structural follow-up; JF-661's description already cites it as the alternative fix shape for the same class.
GATES: /simplify (4 parallel review agents: reuse/simplification/efficiency/altitude) - production diff judged clean at all four angles; applied the test-file dedup (shared `FakeArtistIndex.CodesFromArtistNames`, probe-call wrapper, collision-test setup via Moq last-setup-wins) and two comment corrections the altitude agent found (the "raw-text callers carry no ER canonical" claim was false for PlayByGenre's genre slot; a stale raw-value triage sentence). Note: the hoisted helper first sat at namespace level in TestHelpers.cs and deterministically broke the compile (CS1519 at the preceding class brace + CS1513 at the helper line, a Roslyn parser state this file does not tolerate); as a static member of FakeArtistIndex it compiles clean. SKIPPED by constraint: folding the two pre-existing private CodesFromNames copies (KanaOriginAcceptanceTests, MusicianErCanonicalTests) would modify existing test files, which the dispatch forbids; the shared helper's doc records the fold-in on their next edit (tracked HERE as the same-turn deferral landing).
/code-review high (forked, 5 findings, all verified against the current state): (1) PlayAlbum's own JF-336 fuzzy-album arm (FindBestNonEmbeddedMatch, bare 60-bar, in-handler, one gate BEFORE the fixed fallback) remains kana-ungated - a real adjacent gap, NOT covered by JF-661 (which tracks the TryAlbumFallbackAsync cascade only): FILED as JF-662 the same turn (in this branch's backlog). (2) the deferred private-copy fold-in - tracked above. (3) the RomanizeWithOrigin structural enforcement - the documented follow-up above. (4) the kanaOrigin param doc must carry the raw-capture rule itself - APPLIED. (5) the in-band FindSong test is a contract pin, not regression power (FindSong wires no JF-363 band, so the in-band shape resolves null with and without the fix) - kept, labeled in its comment; the discriminating FindSong pin is the strict-arm 'サト' test.
VERIFICATION TAIL: build 0 errors 0 warnings both TFMs. Full suite 4697/4697 green BOTH TFMs (net9.0 + net10.0, 4689 baseline + 8 new pins in KanaOriginEntityFallbackThreadingTests). Fixture scores dumped from the production matcher, not assumed: bitoruzu/Sator 60 (JF-363 band, codes PTRS vs STR no collision), sato/Sator 90 (strict, ST vs STR no collision), satoru/Sator STR/STR collision at 100 (phonetic boost; 90 plain). The code-review fork independently reproduced all three score pairs and the romanization outputs. SENSITIVITY CHECK: with the PlaySong pin swapped to `kanaOrigin: null` (the pre-fix shape) the live-shape pin fails on both TFMs (the Confirm offer appears), so the pins detect the leak. No model/locale/config changes, no threshold changes.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-09-28 as merge ff482799 (pushed; deployed with the full checklist, config intact; live re-probe green: PlaySong song=ビートルズ -> the honest song not-found with NO Sator offer; the Latin near-miss control unchanged; the SearchMedia and クイーン regressions hold): the entity-fallback kana threading. TryEntityFallbackAsync gained the pinned-flag shape (the TrySongFallback precedent); the worker's mandated caller-map verification found a THIRD leaking caller beyond the task's two (PlayAlbum romanizes its album slot in place - fixed with a new albumKanaOrigin capture, the validity invariant across the JF-489/JF-492/JF-411 reassignments verified structurally); with the flag true the strict arm requires the real DM collision and the JF-363 suggestion band returns null. Gates: /simplify in-worker (clean production), the code-review skill marker in the orchestrator transcript (no correctness bug; the pins' config-independence and a garbled comment applied in the marker pass; the doc-only contract and the deferred copies tracked), suites 4697/4697 both TFMs (orchestrator-verified; the file battery 8/8 after the marker edits). The RomanizeWithOrigin composite evaluated and deferred (heterogeneous couplets, zero behavior change). Follow-up filed: JF-662 (PlayAlbum's own JF-336 in-handler fuzzy arm, the fourth ungated surface).
<!-- SECTION:FINAL_SUMMARY:END -->
