---
id: JF-940
title: >-
  Catalog-backed song slots: anchor PlaySongIntent routing with the user's
  library song titles (issue #46 architectural fix)
status: Done
assignee: []
created_date: '2026-11-21 19:37'
updated_date: '2026-11-27 05:58'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/46'
  - docs/nlu-lab.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
REVISED PLAN after Fable design review (2026-11-22, grounded with live library data: 12,766 tracks → 9,875 distinct titles ≈ 20% of the 50k cap). CORRECTIONS: variants ≠ values (a song with 5 phonetic variants is ONE catalog entry, not 5); the 140-char limit is per-string and already handled. DESIGN DECISIONS: (1) cardinality: automatic, no gate/wizard, show "N of M synced" on config page, warn on cut only; (2) growth past cap: order by play count then date added (current code sorts alphabetically, losing end of alphabet); dedup BEFORE cap (60k tracks with 30k distinct titles fits); (3) short titles: skip phonetics for titles with no word ≥4 letters, exclude junk (track numbers, "no title" appears 37x); (4) long titles: strip parenthetical suffixes ("(G Corp Mix)") and " - " suffixes, use stripped form as value + dedup key (7.4% of titles carry suffixes nobody speaks); (5) partial-name synonyms: NO for songs (1,942 single-word titles); (6) chunking: not applicable. RISKS: collateral steal (songs titled "Stop"/"Repeat" could steal from transport intents: the new intent must use noun carriers ONLY, never bare); the lab cannot verify this feature (no catalog wiring: spike on the dev skill or build catalog mirroring); sync gap (song added after weekly sync stays broken: consider debounced sync); privacy (full tracklist goes to Amazon: opt-in). WIRING: new slot name (not {song}), limit v1 to PlaySong only, ar-SA needs static seed. THE SPIKE (first unit): hand-build a 200-title catalog, wire to the lab skill, answer: (1) known title + noun carrier picks the new intent? (2) unknown title still reaches PlaySongIntent? (3) fixture battery unchanged (series/album/transport)? If Q3 fails, design changes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 PREREQUISITE: /pa:research --depth exhaustive completed and the findings document committed, covering at minimum: SMAPI catalog slot type value limits (how many values can a valueCatalog carry), how other high-volume skills handle large catalogs, the interaction between catalog-backed types and AMAZON.MusicRecording (can they coexist? is there a hybrid?), the JF-684 selection-gating implications for songs NOT in the catalog, and the sync-time/memory cost of uploading potentially 10k+ song titles
- [ ] #2 The design decision documented: catalog-only (reject unknown) vs hybrid (catalog + free-text fallback) vs some other mechanism, with the evidence
- [ ] #3 Implementation: SongTitle added to the catalog sync family, PlaySongIntent's {song} slot updated in ALL 17 locale templates, catalog sync extended to upload song titles with phonetic synonyms
- [ ] #4 NLU fixtures with non-famous titles in every locale, verified against the lab
- [ ] #5 Full gates (build, suite, /simplify, code-review, validators)
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED (2026-11-27 closure round; the architectural fix for issue #46 is DELIVERED AND LIVE): (1) The twin-intent fold shipped (PlaySongIntentHandler.CanHandle claims BOTH PlaySongIntent and PlayLibrarySongIntent; the single song-slot read branches to Slots.LibrarySong for the twin arrival; no request substitution, ER resolutions survive) after the delegating-shim shape was deleted in the 2026-11-22 review rounds (the synthesis core drops Slot.Resolutions; refuted by the altitude round). PlaySongLibrarySongTwinTests (3) pin CanHandle both names, the librarySong fill driving the pipeline, and the empty-fill elicit. (2) Phase 2 = JF-946: the catalog-backed SongTitle shipped and is ARMED LIVE on the household skill since ~2026-11-23 (catalog-green fixture row 'spiele den song circles' live-verified; carrier flips recorded in memory). (3) The folded DLL has been live on minix through every deploy since (JF-966/JF-967/JF-974; the 2026-11-27 all-locale rebuild carries it), closing the 'redeploy pending' note from 2026-11-22. (4) AC#1 research: claudedocs/research_catalog-backed-song-slots_2026-11-21.md committed, superseded operationally by the Fable-reviewed revised plan recorded in the task. (5) AC#2: hybrid shape chosen and shipped (catalog anchor + free-text PlaySong coexistence; famous titles on carriers stay on PlaySong per the lab matrix). (6) AC#3: SongTitle in the catalog family + the twin slot in all 17 locale templates (the fold required no per-locale dialog entries; verified by the shipped models). (7) AC#4 PARTIAL BY DESIGN: non-famous twin pins live in the two archetype locales (de-DE 5 rows, it-IT 7 rows, lab-verified 2026-11-22); the 15-locale rollout requires per-locale live verification before any row commits (the phantom-red rule) and is filed as JF-977 (batched onto future locale batteries, not 15 dedicated runs). Today's corroborating live evidence: the JF-933 baseline (run 20261127-064247) shows the twin absorbing all three de article carriers with the title filled on the CURRENT model. gate-exempt: no code changed in this closure round; the implementation went through its own 2026-11-22 review rounds (documented in the prior final summary: Fable design review, altitude/refutation rounds, lab matrices jf940-de-*, stock counterfactual, live batteries).
<!-- SECTION:FINAL_SUMMARY:END -->

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
