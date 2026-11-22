---
id: JF-940
title: >-
  Catalog-backed song slots: anchor PlaySongIntent routing with the user's
  library song titles (issue #46 architectural fix)
status: In Progress
assignee: []
created_date: '2026-11-21 19:37'
updated_date: '2026-11-22 18:04'
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
2026-11-22 verification record (implementation session): (1) HANDLER live-verified on minix: simulator corr 32031eac shows the delegation chain complete and BYTE-EQUIVALENT to the direct PlaySong call for the same query (identical speech, reprompt, session attrs, DynamicEntities); the earlier 'NO RESPONSE' reading was a misparse (speech lives in outputSpeech.ssml). (2) Unit tests added (PlayLibrarySongIntentHandlerTests, 3 tests): CanHandle selectivity, slot rename + same-object delegation, null-slots safety; the rename test caught a real defect (Slot.Name stayed 'librarySong' after the key rename) now fixed in the handler. (3) it-IT live battery GREEN: 'suona il brano rapsodia' -> PlayLibrarySongIntent; famous bare 'riprodurre bohemian rhapsody' -> PlaySongIntent; non-famous bare -> None is the PRE-EXISTING JF-298 class (stock-model lab counterfactual run 20261122-172731-stock-it-bare: identical None on stock). (4) de-DE: the static-seed premise is REFUTED for 3 of 4 carriers (lab matrix jf940-de-final/va/vb/vc/vd/ve: only v-c, whose seed contains the exact test title, greens all four; v-e removing just the test title breaks them: exact-match anchoring, not span training). 'das Lied' + unknown title DOES select on the shipped live model. Phase 2 = JF-946 (catalog-backed SongTitle). (5) de-DE live model rebuilt via endpoint (SUCCEEDED) and fixtures re-anchored to measured reality (famous-title carrier rows stay on PlaySongIntent; 'den song' row is RED-UNTIL-CATALOG per JF-946). (6) Tool bug found and fixed: nlu_wrapper_battery.default_skill_id first-match discriminator silently probed the LAB skill after a lab it-IT deploy (both declare JellyfinArtist); now prefers the skill NAMED 'Jellyfin' in nameByLocale; nlu-lab _discover_live_skill_id delegates to it. (7) de-DE two-step simulate slot verification blocked by a per-locale simulate outage (it-IT evergreen green); JF-919 gate handles it.

SHAPE CHANGE (2026-11-22 evening, review rounds): the delegating shim handler was DELETED. The shipped shape is the JF-874 twin-intent fold: PlaySongIntentHandler.CanHandle claims BOTH PlaySongIntent and PlayLibrarySongIntent, and the single song-slot read branches to Slots.LibrarySong for the twin arrival (no request substitution at all: the musician slot's catalog ER resolutions survive intact; the reuse agent's BuildSongDelegation suggestion was refuted by the altitude round because the synthesis core drops Slot.Resolutions). The twin elicits through the PlaySong dialog registration exactly like PlayVideo/PlayMovie (JF-874 comment: no twin entry in any locale's dialog.intents; the it-IT/de-DE dialog registrations added earlier were REMOVED). Tests rewritten as PlaySongLibrarySongTwinTests (3 tests: CanHandle both names, librarySong fill drives the pipeline, empty fill elicits). Live redeploy of the folded DLL still pending at this writing.
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
