---
id: JF-509
title: >-
  Title fill swallows the media noun ('film ada' instead of 'ada'): strip
  leading localized media nouns in PlayVideoIntentHandler before the search
status: Done
assignee: []
created_date: '2026-09-06 17:16'
updated_date: '2026-09-06 17:54'
labels:
  - nlu-fill-drift
  - video
dependencies: []
references:
  - corr=655db7c3
  - JF-441
  - JF-489
  - JF-504
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-09-06 19:00 device session (corr=655db7c3): 'voglio guardare il film ada' routed to PlayVideoIntent correctly but the AMAZON.SearchQuery title fill swallowed the carrier noun (title='film ada'; the same utterance filled 'ada' the previous day), so the search ran for a movie NAMED 'film ada' and the response was 'Spiacente, non ho trovato nessun video con il titolo film ada'. The SearchQuery slot has no catalog to anchor its fill boundary, so the drift is between model builds (same class as JF-441's album-carrier bleed). FIX (JF-489 pattern applied to the title slot): PlayVideoIntentHandler strips a leading localized media noun + articles (it/en/de/es/fr/pt/nl families) before the search; a value that is ONLY the noun/article returns the did-not-catch prompt. Pin tests: 'film ada' searches 'ada' and finds the movie; 'il film' alone prompts. The invocation-wrapped one-shot ('chiedi a mia collezione di riprodurre il film ada') remains Fallback on-device (open item on JF-504; every clean inner string routes in profile-nlu): in-session carrier forms are the reliable shape.
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
Closed 2026-09-06 (commits f4c44a33 + 4108dcf4 + e61c191c + f9684c1a, deployed and live-verified). The swallowed-carrier title ('film ada' for 'ada') now heals through a raw-first chain: raw query -> ONE stripped exact retry (leading localized media noun + articles, it/en/de/es/fr/pt/nl; word-fragment guard; null on unchanged or all-noun input) BEFORE the fuzzy; the fuzzy AND the multi-result FuzzyMatch both run on the STRIPPED value when one exists (live traces: raw-fuzzy scored the unrelated 'Cicada' at 50, and the multi-match block was the last raw-value leak). The simplify pass drove the design: caught the preemptive strip breaking real titles ('Film Stars Don't Die in Liverpool') and forced the raw-first contract; also dropped a dead parameter, folded the two table passes, removed ungrammatical entries. 4 pin tests. LIVE-VERIFIED end-to-end on the simulator post-deploy: title='film ada' resolves 'Ada: My Mother the Architect' and the JF-505 gate correctly refuses the screenless simulator launch (on a Show it plays). The invocation-wrapped one-shot remains Fallback on-device (open on JF-504).
<!-- SECTION:FINAL_SUMMARY:END -->
