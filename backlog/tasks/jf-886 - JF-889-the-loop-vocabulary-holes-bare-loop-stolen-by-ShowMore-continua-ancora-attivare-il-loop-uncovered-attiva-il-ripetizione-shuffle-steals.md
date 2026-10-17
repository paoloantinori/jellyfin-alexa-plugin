---
id: JF-886
title: >-
  JF-889: the loop vocabulary holes - bare 'loop' stolen by ShowMore
  ('continua/ancora'), 'attivare il loop' uncovered, 'attiva il ripetizione'
  shuffle-steals
status: Done
assignee: []
created_date: '2026-10-14 16:09'
updated_date: '2026-10-17 14:43'
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
CLOSED by transcription evidence 2026-10-17 (the hassio agent's RVH read, Echo cucina G0911B059457150L): the 2026-10-14 17:16:06 utterance was 'chiedi a mia collezione di attivare' - NO loop word at all; the ASR cut the tail (likely 'di attivare il loop' truncated) and the bare infinitive routed to ShowMoreIntent via the bridge. The JF-889 loop-vocabulary fixes remain valid and live-verified on their own merits (the probed shapes 'attiva il loop' family all route correctly post-deploy, battery 10/10); the original incident was a DIFFERENT shape. The bare truncated infinitive 'attivare' landing on ShowMore is filed as its own row (the truncated-tail class). gate-exempt: doc-only closure on transcription evidence; no code this task.
<!-- SECTION:FINAL_SUMMARY:END -->
