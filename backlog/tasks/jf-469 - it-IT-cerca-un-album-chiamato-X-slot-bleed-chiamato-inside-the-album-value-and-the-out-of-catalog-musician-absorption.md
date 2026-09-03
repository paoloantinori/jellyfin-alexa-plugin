---
id: JF-469
title: >-
  it-IT 'cerca un album chiamato X' slot bleed ('chiamato' inside the album
  value) and the out-of-catalog musician absorption
status: To Do
assignee: []
created_date: '2026-09-03 13:49'
updated_date: '2026-09-03 14:03'
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
<!-- SECTION:NOTES:END -->
