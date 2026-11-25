---
id: JF-961
title: >-
  The live-only Fallback pair (it-IT brano wrapper, es-US free-text song) plus
  the unprobed ja-JP play-form row: catalog-wiring selection gates beyond the
  slot's own type
status: Done
assignee: []
created_date: '2026-11-25 10:56'
updated_date: '2026-11-25 14:15'
labels:
  - nlu
  - catalog-gate
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-959 re-verification census (claudedocs/census_jf959_reverify_2026-11-25.md, 2026-11-25, 602 probes with green evergreen controls): two still-red rows are PROVEN live-only (lab GREEN x2, live Fallback x2, identical template content verified on the live model), making catalog wiring the only structural difference. (1) it-IT 'chiedi a mia collezione di suonare il brano rapsodia': the armed SongTitle catalog gates intent SELECTION on the composed wrapper shape (the JF-684 catalog-gate x wrapper interaction); the JF-945 title-less opener fixed the podcast analog, so the candidate fix is 'Di suonare il brano' (and per the JF-844 lesson each verb needs its own literal row: Di suonare/Di riprodurre/Di mettere il brano) — with the row-count cost weighed against the wrapper's user value. (2) es-US 'Reproduce la cancion luciernagas del sur': Fallback live-only on a FREE-text MusicRecording slot with catalog wiring present on OTHER types (SeriesName/AudiobookTitle/JellyfinArtist verified wired); the mechanism by which other types' wiring gates a free-text slot's selection needs investigation (candidate: the JF-684 selection-discouragement is model-wide when any catalog-backed type carries no match for its slot in the utterance). Also fold the ja-JP note: the census row (kana play form) was NOT probed in the re-verification (the worker probed the shuffle form, green); the play-form row needs its own re-probe before any fixture decision.
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
gate-exempt: research-only (a live ablation report; no code, fixtures, templates or models changed; the live skill was restored byte-identical with readback and behavioral witnesses, documented in the report).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
ABLATION COMPLETE 2026-11-25, report committed at claudedocs/research_jf961_ablation_2026-11-25.md (9 wiring states x 2 identical rebuilds x 9-row battery, 165 profile-nlu probes, wiring readback after every rebuild, live model restored byte-identical with behavioral witnesses). VERDICT: the censused es-US red row is a two-factor CONJUNCTION: the JellyfinArtist wiring (PlaySongIntent's own musician slot, the per-intent vulnerability) PLUS at least one more catalog wiring (the model-wide cumulative pressure); neither factor alone reproduces the red (artist-only green, audio-only green, series-only green, audio+series green). A secondary cleaner finding: TOKEN COLLISION is CONFIRMED as the mechanism behind the NAKED-carrier row's breakage (real audiobook values break 'Reproduce luciernagas del sur', zero-overlap pseudowords keep it green), extending anti-pattern #11 to catalog-wired locales. VERB INVERSION discovered: the failure is reproduce-specific (escucha/pon/toca of the same phrase route PlaySongIntent even at full wiring; reproduce is the most contested carrier). FOR JF-960: the pruning arithmetic is exact (pruning artist fixes the row everywhere but costs the most functionally; pruning one cofactor while keeping artist leaves an artist-pair and the row stays red); the carrier-side alternative (sample reinforcement of the reproduce shape) addresses it without touching ER. The maintainer decision on which mitigation is pending. The it-IT brano-wrapper half of the task: the mechanism (catalog-gate x wrapper) is now better understood through the conjunction lens; the JF-945-class opener remains the candidate. The ja-JP play-form row remains unprobed. Restore proof: pre-experiment model redeployed, sha256 byte-identical, behavioral witnesses match. Residual: one inert synthetic catalog undeletable (CATALOG_VERSION_IN_USE), retry in a later session.
<!-- SECTION:FINAL_SUMMARY:END -->
