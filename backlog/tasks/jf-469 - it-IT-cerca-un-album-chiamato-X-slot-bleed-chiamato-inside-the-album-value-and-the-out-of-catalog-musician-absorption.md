---
id: JF-469
title: >-
  it-IT 'cerca un album chiamato X' slot bleed ('chiamato' inside the album
  value) and the out-of-catalog musician absorption
status: Done
assignee: []
created_date: '2026-09-03 13:49'
updated_date: '2026-09-04 14:56'
labels: []
dependencies: []
references:
  - JF-441 (the probe matrix and mechanism proof)
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml
    (JF-441 comment block)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-441 probe matrix (2026-09-03): the chiamato-family slot bleed has a remaining shape. Probes against the deployed it-IT model (skill 33dfacd5):
- 'c'e un album chiamato dark side of the moon' -> PlayAlbumIntent, album=null, musician absorbs the span (out-of-catalog title; catalog entity weighting, Amazon-side)
- 'un album chiamato X' / 'cerca un album chiamato X' -> PlayAlbumIntent, album='chiamato X' (the literal 'chiamato' bleeds INTO the slot value; happens even for in-catalog titles: 'un album chiamato surfer rosa' -> album='chiamato surfer rosa')
- JF-441 added 'un album chiamato {album}' and 'un disco chiamato {album}' samples to fix the play-shape bleed (post-deploy probe obligations recorded in the JF-441 closure); the CERCA shape ('cerca un album chiamato X' -> album='chiamato X') was deliberately NOT covered (adding a cerca variant to a play intent is wrong; cerca belongs to SearchMedia/FindSong families).

Triage needed: whether a correct home exists for the cerca+chiamato shape (SearchMediaIntent or a FindSong variant with an album-noun carrier), or whether the handler side should strip a leading 'chiamato/chiamata' from album slot values (a value-normalization fix in GetSlotValue or the album handlers, locale-aware, mirroring the carrier-strip patterns in BaseHandler). Probe-first per the repo rules; check what 'cerca un album chiamato X' selects today and what the user experience is (the album search for 'chiamato X' will fuzzy-miss or worse, fuzzy-match something wrong).

Acceptance criteria:
- Probe evidence for the cerca shape (selected intent + slot value) recorded.
- Either a model sample fix at the right intent, or a documented handler-side value normalization with tests, or a documented-wontfix with rationale.
- The 'un album chiamato {album}' bleed fix verified post-deploy (JF-441's obligation) before closing this, to avoid double-handling the same family.
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
POST-DEPLOY EVIDENCE (JF-441 closure, 2026-09-03 16:01 model): the JF-441 secondary sample addition did NOT fix the slot fill. 'un album chiamato surfer rosa' with the sample now deployed fills musician='surfer rosa' (album empty; the same statistical-filler theft as the out-of-catalog shape, now on an IN-CATALOG title), and 'un disco chiamato surfer rosa' selects NO intent at all. Scope of this task is therefore the chiamato-family FILL problem across ALL shapes (c'e/un/cerca/un disco), Amazon-side weighting versus sample presence; intent selection is intact on the album and c'e forms. Consider handler-side value normalization (strip a leading 'chiamato/chiamata' from the album slot, locale-aware) more seriously: the model layer is evidenced insufficient.

REVIEW NOTE (2026-09-04, /simplify + code-review pass on the working-tree diff): no findings at or above the reporting bar. One below-threshold observation filed here per the same-turn landing rule: the JF-469 unit pins cover raw-hit-no-retry, stripped-retry order, not-found-names-raw, literally-titled album, and the strip predicate edge cases, but there is NO pin asserting that a STRIPPED-retry hit carries no FoundAlbumInstead announcement (the angle-1 'a stripped hit must not announce' contract holds only by construction today: fuzzyAlbumAnnouncement stays null because the retry assigns `albums` directly; a future edit routing the retry through the fuzzy branch would silently announce on a clean play). Suggested pin: assert the stripped-hit response speech does not contain the FoundAlbumInstead string (or that OutputSpeech is the plain now-playing/silent shape).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented, deployed, and live-verified (commit 0d4959ac).

Probe matrix (deterministic): the bleed persists on cerca/c'e/che-si-chiama carriers; 'un album chiamato X' now fills clean on the rebuilt model (JF-441 obligation discharged); the model layer is evidenced insufficient, so the fix is handler-side. Design: TryStripLeadingAlbumCallingWord with a locale-keyed it-IT prefix map (chiamato/chiamata/che si chiama/di nome, space-suffixed so fragments and bare words never strip), raw-first bounded: the raw query runs, and only on a raw miss AND a calling-word prefix does exactly one extra indexed retry run with the stripped title (the JF-383 pattern). Raw hit never retries (pinned: exactly one query); stripped hit plays without announcement; not-found names the raw value; an album literally titled with a leading calling word stays findable (pinned). Other locales never strip (survey + pin): no calling-word PlayAlbum samples exist outside it-IT. The cascade (TryAlbumFallbackAsync) deliberately not wired: its input is PlaySong's song slot whose evidenced bleeds carry the carrier itself, so the predicate would never fire (documented for future wiring on new probe evidence).

Live smoke post-deploy: simulator album='chiamato surfer rosa' (raw misses, prefix present) PLAYS via the stripped retry (Audio directive, the JF-469 log line firing exactly once). The other-locales calling-word survey recorded in the task notes: song-path samples exist in en/es/pt/fr but no album-path bleed shapes.

19 tests + one e2e routing entry ('un album chiamato surfer rosa'); worker mutation kills 7/19 with both raw-first pins green; suite 3169/3169; Release 0 warnings; validators baseline; NLU dry-run unchanged. Review: zero findings >= 80; the below-threshold gap (no pin asserting the stripped hit carries no announcement; holds by construction) recorded in the notes. Device tests for Paolo: cerca un album chiamato surfer rosa plays; c'e un album chiamato thriller not-founds naming thriller; che si chiama shape plays; a literally-titled 'Chiamato X' album still plays raw.
<!-- SECTION:FINAL_SUMMARY:END -->
