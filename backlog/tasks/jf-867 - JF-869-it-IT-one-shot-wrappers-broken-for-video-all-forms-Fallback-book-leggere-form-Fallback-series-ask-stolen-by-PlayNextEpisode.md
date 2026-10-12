---
id: JF-867
title: >-
  JF-869: it-IT one-shot wrappers broken for video (all forms Fallback), book
  leggere-form Fallback, series ask stolen by PlayNextEpisode
status: Done
assignee: []
created_date: '2026-10-12 15:19'
updated_date: '2026-10-12 18:37'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf869 (316e5410), merged to main f2c77341, pushed. Verified by scratch-skill A/B (JF-560 precedent, baseline reproduced first, final stable across two identical rebuilds): book wrappers leggere/mettere/riprodurre il libro X route PlayBook (were Fallback); new it-IT SkipForwardBack family fills direction/amount/unit (salta avanti was no-selection); la-serie family exact-anchored (PlayNextEpisode by design: empty episode_position = NextUp series-play, JF-583); bare infinitives mettere/guardare/vedere il film X route PlayVideo. VIDEO one-shot wrappers NOT row-fixable (SearchQuery span wrapper-hostile; full row density fails, MusicRecording swap disqualified by control collapse); architectural fix routed to JF-874 (MovieTitle catalog type); skip-family cross-locale rollout + en-US rewind wrong-way bug routed to JF-875; noun-less leggere residual noted in JF-874 (device-vs-profiler). C#: SeekVocabulary ER-first resolution (also fixes en-US backward-synonym), ItalianNumberWords amounts, GetResolvedValue single id-walk owner, 4 new unit tests. Gates: /simplify (3 agents; 3 applied, FrozenSet declined) + /code-review high (4 applied). Tests 5795/5795 BOTH TFMs; Release -warnaserror clean; validators PASS (289 vs 291 baseline warnings, zero rows lost); mirrors regenerated; NLU dry-run 8/8. Deployment: model via the plugin redeployer (locale-scoped it-IT), never a raw SMAPI PUT (strips catalog wiring); scratch probe skill 65da1ba5 left in place (never enabled, deletion banned) for future A/B.
<!-- SECTION:FINAL_SUMMARY:END -->
