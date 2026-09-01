---
id: JF-422
title: >-
  Empty-album elicit reads album titles as artist names and dead-ends the
  musician flow (JF-411 path unreachable from elicit)
status: Done
assignee:
  - zai
created_date: '2026-08-31 15:02'
updated_date: '2026-09-01 23:42'
labels:
  - code-review
  - dialog
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:134
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:176
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Code-review finding (2026-08-31, high effort, CONFIRMED by code reading). PlayAlbumIntentHandler.cs:134 (empty-album elicit branch), interacts with the JF-411 block at line 176.

DEFECT (two failure directions):
1. 'riproduci un album' (both slots empty) elicits the MUSICIAN slot; the user answers with the album title they wanted ('the dark side of the moon'), it is captured as an artist, ArtistSearch finds nothing, and they get terminal NotFoundAlbumByArtist for an album that exists.
2. For the motivating JF-411 case (musician present, album empty, e.g. 'un disco dei' after ASR swallowed the name), the musician answer returns with dialogState IN_PROGRESS and line 134 returns an album-title elicit BEFORE the JF-411 block at 176 can run: a user who wanted 'any album by Koop' is asked a question they cannot answer. The JF-411 play-without-a-title resolution the comment promises is unreachable from the elicit path.

FIX SHAPE: elicit the ALBUM slot first (title answers are the common case), and route the IN_PROGRESS musician answer into the album-by-artist resolution (the JF-411 block) instead of re-elicitating the title. Consider slot-presence-driven branching: album empty + musician filled = JF-411 path; both empty = elicit album first.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 'riproduci un album' (empty slots) elicits slots in an order where an album-title answer leads to an album search, not an artist search misread
- [ ] #2 The JF-411 motivating case works from the elicit path: musician answer during dialogState IN_PROGRESS reaches the album-by-artist resolution (play any album by that artist), not an album-title prompt
- [ ] #3 Unit tests: (a) empty-album elicit + title answer resolves the album; (b) musician elicit answer plays an album by that artist
- [ ] #4 No regression on the direct 'un disco dei X' one-shot forms (JF-411 originals)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-02 orchestrator finalization: the implementer died to an API server error at report time (work complete, diff final, gates run). Final tree verified by the orchestrator: build 0 errors, full suite 2828/2828 (incl. the concurrent JF-425 event-handler tests), Release 0 warnings per two independent agents. Committing as part of the overnight batch.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-422: the empty-album elicit asks for the ALBUM (the common answer) and the musician answer now reaches the JF-411 resolution instead of a dead-end title prompt.

WHAT CHANGED (from the implementer run; finalized by the orchestrator after an API-error termination at report time)
- PlayAlbumIntentHandler branching is now purely slot-presence driven: BOTH slots empty -> elicit the ALBUM slot (a title answer feeds the album-title search; the old artist-first order captured 'the dark side of the moon' into the musician slot and dead-ended in NotFoundAlbumByArtist for an album that exists). MUSICIAN filled (any dialog state) -> falls through to the JF-411 album-by-artist resolution (plays an album without a title; the old IN_PROGRESS re-elicit asked the 'any album by X' user a question they could not answer). The DialogState check was deleted from this branch.
- The 2026-08-28 on-device case that motivated artist-first ('un disco dei' after ASR swallowed 'Koop') degrades only partially: a short article-free artist answer in the album slot still plays via the cross-media fallback; hardening that path is FILED as JF-446.
- BuildSlotElicitResponse collapsed to BuildAlbumElicitResponse (one shape, album-only, constants inlined); the orphaned doc comment moved back to BuildAlbumQuery; the ElicitArtistName locale key removed from all 17 locale JSONs (validate_locales PASS); the manual-verification checklist section A rewritten for the new prompt.

VERIFICATION
- 4 new/updated tests: both-empty elicits the album slot (ElicitSlot SlotToElicit asserted); album-title answer resolves by title (no artist query); musician-known IN_PROGRESS plays an album by that artist with NO title prompt; DialogDelegationTests partial-slots case updated. The implementer's scoped runs: 52/52 across its affected classes; final tree 2828/2828; Release 0 warnings (verified by both the implementer and the concurrent JF-425 agent).
- Gates: /simplify run by the implementer (findings applied: helper collapse, doc repair, test fixture reuse); code-review high run via its fork (its findings applied or filed: JF-446; the dialogState question tracked as JF-445).
- NOTE: the implementer agent terminated on an API error at report time, after all work was complete and verified; the orchestrator verified the final tree (build 0 errors, suite 2828/2828) and finalized.
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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->
