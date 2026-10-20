---
id: JF-914
title: >-
  Audit the music-of wrapper tails in en/de/fr for the JF-844-class steal the
  it-IT regression proved (probe-first; add tail rows only where condemned)
status: Done
assignee: []
created_date: '2026-10-20 05:57'
updated_date: '2026-10-20 10:57'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-10-20 it-IT regression fix's code review (finding 4): the JF-844-class wrapper-tail music rows landed it-IT only (four bare-infinitive rows, condemned by device corr 15656cee); the equivalent shapes are absent in the other infinitive-wrapper locales and unaudited. Reviewer's map: en-US PlayArtistSongs carries 'play music by {musician}' but its 'to ...' wrapper family covers only article shapes ('to play the {musician}'); de-DE has no 'zu spielen Musik von {musician}'-class tail; fr-FR has no 'de mettre la musique de {musician}' (only 'de lire les chansons de'); each locale's PlayMoodMusic competitor exists in imperative form ('play {mood} music', 'spiele {mood} musik', 'joue de la musique {mood}'). IF the same invocation-tail steal fires there, the request is lost with no fixture pin to reveal it. SCOPE: probe-first per locale (simulate the composed music-of one-shots with each locale's real invocation name; condemned shapes get their own bare/wrapper tail rows per the JF-844/JF-877 pattern); en may legitimately need none (its wrapper payload is the bare imperative, the JF-875 note) - the audit must PROVE that per locale rather than assume it. Sequenced after JF-914 (the it-IT hotfix) to keep the hotfix minimal.
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
CORRECTION (filing time): this task IS JF-914 (the MCP assigned the ID; the original title's 'JF-915' prefix was a manual numbering guess and is removed). The 'Sequenced after JF-914' sentence in the description should read 'Sequenced after JF-915 (the it-IT hotfix record)'.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
AUDIT COMPLETE 2026-10-20 12:5x, verdict: NO model rows needed in en-US, de-DE, or fr-FR; zero diff (the branch carries nothing; the task closes as an audit with negative results, all probe-proven). en-US: GREEN full-pipeline ("play music by" and "listen to music by" route PlayArtistSongs; the JF-875 note proven). fr-FR: model GREEN on profile-nlu for every composed form (mettre/lire + the norah spelling; the trainer generalizes even the bare forms); fr simulate is in the per-locale outage (the battery's own control fails) so the full-pipeline reading stays parked like the es-MX/es-US ones. de-DE: the bare payload routes on profile-nlu; the composed verb-tail fails at the documented de invocation layer (not the model). RESIDUALS FILED: the en "put on music by X" PlaySong steal (its own task, sample competition, NOT a wrapper gap); the de "nach + verb-tail" shape is model-unclaimed while the noun tail claims QueryArtistLibrary (re-probe only if the de invocation layer ever heals; recorded in the report). Gate-exempt: zero diff, audit-only.
<!-- SECTION:FINAL_SUMMARY:END -->
