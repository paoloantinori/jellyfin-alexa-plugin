---
id: JF-886
title: >-
  JF-889: the loop vocabulary holes - bare 'loop' stolen by ShowMore
  ('continua/ancora'), 'attivare il loop' uncovered, 'attiva il ripetizione'
  shuffle-steals
status: To Do
assignee: []
created_date: '2026-10-14 16:09'
updated_date: '2026-10-14 17:18'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's 2026-10-14 17:16 device round: a loop command landed on ShowMoreIntent (the 'Non ho una lista precedente da continuare' answer). Live probes (profile-nlu, 2026-10-14): bare 'loop' -> ShowMoreIntent (STOLEN by the pagination family: samples 'continua'/'ancora'/'altro' catch it); 'attivare il loop' (with article) -> AMAZON.FallbackIntent (uncovered); 'attiva il ripetizione' -> ShuffleAllOnIntent (wrong steal). The three are one family of holes around the loop vocabulary: FIX (probe-first, JF-844 discipline): add the article-carrying bare-infinitive rows ('attivare il loop', 'disattivare il loop', 'attivare la ripetizione' variants) to LoopAllOn/LoopAllOff in templates/it-IT.yaml, and evaluate the bare 'loop' word: if adding loop-shaped rows does not claim it from ShowMore, consider whether the bare single noun belongs to the loop family (it plausibly does: a user saying just 'loop' during playback means loop-on; but 'ancora' during a list means show-more: the shapes are string-close, probe carefully and document the trade-off). Pin: 'attiva il loop' stays loop-on, shuffle controls unchanged ('attiva il mescolamento' -> ShuffleAllOn). NOTE: the exact device utterance is still unknown (no programmatic transcription source; Paolo may read the Alexa app voice history); the fix should cover the probed gaps regardless, and the task stays open for the device retest with whatever the transcription reveals.
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
EXECUTED on branch jf889 (600325d0), merged to main 18edf72e, pushed, DEPLOYED (DLL + it-IT rebuild SUCCEEDED) and LIVE-VERIFIED post-deploy (all 10 probes green): the four steals fixed ('attiva il loop' -> LoopAllOn, 'disattiva il loop'/'disattiva la ripetizione' -> LoopAllOff, 'attiva il ripetizione' -> LoopAllOn); the DECISION row WON: bare 'loop' -> LoopAllOnIntent (ShowMore released it; the row stays); pagination controls intact ('mostra di più'/'continua'/'ancora' -> ShowMore); shuffle boundary intact ('attiva/disattiva il mescolamento' -> ShuffleAllOn/Off). The worker's live probe map found FOUR steals beyond the task's three (the article-less JF-887 rows already carried 'attivare il loop' by generalization). 21 template rows (full article sub-family incl. the gender-confused 'il ripetizione' twins completed in one pass), 16 fixture pins, 2 battery wrapper rows, mirrors regenerated. Gates: /simplify (4 agents) + /code-review high (5 findings, all 5 applied and live-verified). Suite 5958/5958 BOTH TFMs; validators PASS at the 275 baseline. RESIDUALS: the task stays open for the device retest once Paolo reads the Alexa app voice history (the exact 17:16 utterance is still unknown; the fixed family covers every probed shape). Out-of-scope filed: two pre-existing cross-intent duplicate samples at HEAD ('riprendi a guardare', 'attiva la radio', simplify-agent find, predates this change) noted for a future cleanup; the 15/17 battery residue (fermare-dopo-un-minuto, podcast) reclassified as simulate-layer drift (bare tails route correctly on profile-nlu).
<!-- SECTION:FINAL_SUMMARY:END -->
