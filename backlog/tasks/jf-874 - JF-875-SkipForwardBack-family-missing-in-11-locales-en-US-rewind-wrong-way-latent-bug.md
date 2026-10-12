---
id: JF-874
title: >-
  JF-875: SkipForwardBack family missing in 11 locales (+ en-US rewind wrong-way
  latent bug)
status: Done
assignee: []
created_date: '2026-10-12 18:37'
updated_date: '2026-10-12 22:19'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Cross-locale audit result from JF-869 (report-only there per dispatch): SkipForwardBackIntent exists in en-US/GB/CA/AU/IN and now it-IT, but is MISSING in de-DE, es-ES/MX/US, fr-FR/CA, hi-IN, ja-JP, nl-NL, pt-BR, ar-SA (11 locales): any "salta avanti"-class ask selects nothing there (the JF-684 no-selection class; on it-IT this was Paolo's live "salta avanti has no effect" finding). Rollout: per-locale natural skip vocabulary (verbs + direction words + time units), the SeekVocabulary.cs ER-first resolution already supports localized values over shared English ids, seek_amount digit/word-form pairing per locale (it-IT uses ItalianNumberWords per JF-814; other locales need their own word-number tables or digit-only scope), bare direction rows must be probed for NextIntent/PreviousIntent steals first (the it-IT lesson: bare avanti/indietro stole NextIntent and the rows were dropped). NLU fixtures per locale; the scratch skill 65da1ba5 is available for pre-deploy A/B.

ALSO fold in (found by the same lane): en-US "rewind {seek_amount} {seek_unit}" carries a wrong-way latent bug (empty seek_direction defaults to forward, so "rewind 30 seconds" would seek FORWARD) - the it-IT rows were reworked to avoid it; fix the en family rows the same way (direction-carrying rewind/forward rows, or a handler-side synonym map).
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
EXECUTED on branch jf875 (b5c48235), merged to main 6ebbea5f (mirrors regenerated post-merge, FRESH verified), pushed, deployed: DLL + 16-locale model rebuild via the plugin redeployer. Live deploy results: 12 locales SUCCEEDED; es-ES/MX/US and ja-JP read NEVER_BUILT (locales inactive on this dev skill, the standing JF-399 class; the models are in the repo and apply where active). Live probes: en-US rewind 30 seconds -> SkipForwardBackIntent with direction ER 'back' (the wrong-way bug closed on the vendor model) and skip forward -> 'forward'; de 'spule 30 sekunden vorwärts', fr 'saute 30 secondes en avant', nl 'spring 30 seconden vooruit', ar, and hi with the model's own Devanagari forms all route with direction filled. ONE live-only miss: pt-BR 'pula/pule N segundos para frente' (amount-before-direction) falls to PlaySongIntent on this trainer roll while 'avança 30 segundos' and 'pule para frente 30 segundos' are green (scratch had all three): recorded as the same bounded trainer-variance family as the documented es-US 'salta atrás' and ar 'ارجع' misses; no new task, re-probe on the next model touch. Scope correction from the worker: only en-US and it-IT carried the intent before, so the rollout covered FIFTEEN locales. en wrong-way fix: the shipped direction-LITERAL rows left seek_direction EMPTY so every back literal seeked forward (vendor baseline captured: rewind/skip back both direction-absent); reworked to the JF-869 slotted discipline, rewind now a back synonym. SeekVocabularyTests derives all direction/unit values+synonyms from the embedded models. Gates: /simplify (3 applied, 1 skip with test-layer enforcement) + /code-review high (5 applied/dispositioned, 1 filed). Tests 5880/5880 BOTH TFMs; Release -warnaserror clean; validators PASS (259 vs 289 baseline); mirrors regenerated. Residual JF-878 filed (fr/es/nl wrapper twins, gated).
<!-- SECTION:FINAL_SUMMARY:END -->
