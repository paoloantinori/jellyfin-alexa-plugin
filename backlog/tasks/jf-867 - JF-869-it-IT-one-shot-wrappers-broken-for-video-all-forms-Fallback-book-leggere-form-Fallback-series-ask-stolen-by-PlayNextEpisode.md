---
id: JF-867
title: >-
  JF-869: it-IT one-shot wrappers broken for video (all forms Fallback), book
  leggere-form Fallback, series ask stolen by PlayNextEpisode
status: To Do
assignee: []
created_date: '2026-10-12 15:19'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Paolo's 2026-10-12 device round exposed three it-IT model routing failures, all verified against the live model via profile-nlu (skill 33dfacd5, it-IT, development):

1. VIDEO one-shot wrapper: EVERY form falls to AMAZON.FallbackIntent with empty consideredIntents ("chiedi a mia collezione di mettere il film sunny dancer" 3/3 on-device, reproduced in profile-nlu; also riprodurre/vedere/guardare variants, "domanda a" variant, "un video" variant). Bare in-session "metti il film sunny dancer" resolves PlayVideoIntent. This is the JF-844 class: the wrapper tail needs EXACT bare-infinitive literal rows; PlayVideo carries only "riprodurre*" bare rows (template lines ~160-163) plus Di-forms. Fix: add the four-verb literal rows (mettere/guardare/vedere + riprodurre already there) per the PlaySong/PlayBook JF-844 pattern. NOTE the template's own comment block (lines 152-214) documents the 2026-09-23 'ada' incident of the same class.

2. BOOK one-shot wrapper with "leggere": "chiedi a mia collezione di leggere option b" → Fallback. Bare "metti il libro option b" → PlayBookIntent OK. The JF-844 PlayBook literal rows evidently do not cover the "leggere" verb (or the libro-noun form is missing for it).

3. SERIES ask stolen: "metti la serie sailor moon" (bare AND wrapper) → PlayNextEpisodeIntent. A series launch ask should route to a series-play intent (or at least not next-episode); needs the carrier qualification or intent competition fix.

All three are template edits + regen it-IT (+ cross-locale audit for the same families) + redeploy via the plugin redeployer (raw SMAPI PUT strips catalog wiring; must go through IInteractionModelRedeployer or re-sync catalog after). Verify with profile-nlu before handing any phrase to the maintainer again (standing rule, memory test-autonomously-before-asking-user).
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
