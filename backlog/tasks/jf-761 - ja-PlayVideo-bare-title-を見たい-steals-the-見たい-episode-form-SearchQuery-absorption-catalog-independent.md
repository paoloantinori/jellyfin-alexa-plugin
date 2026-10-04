---
id: JF-761
title: >-
  ja PlayVideo bare "{title} を見たい" steals the 見たい episode form (SearchQuery
  absorption, catalog-independent)
status: To Do
assignee: []
created_date: '2026-10-04 20:05'
labels:
  - interaction-model
  - nlu
  - ja-JP
dependencies: []
references:
  - JF-551
  - JF-459
  - JF-684
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-551 closure probe (2026-10-04, live profile-nlu, 4x-stable).

The ja-JP episode-addressing form "{series_name} のシーズン {season_number} エピソード {episode_number} を見たい" (a COMMITTED PlayEpisodeIntent sample) is stolen by PlayVideoIntent's bare carrier "{title} を見たい" (AMAZON.SearchQuery): the whole span up to 見たい lands in title (live: "breaking bad のシーズン 1 エピソード 3 を見たい" and "the bear のシーズン 1 エピソード 3 を見たい" both SELECT PlayVideoIntent with title="... のシーズン 1 エピソード 3", 4/4 repeats). This is the one REMAINING stable episode misroute in the 8 JF-551 locales: it survives even with a catalog-matched series (ER_SUCCESS_MATCH), so unlike the pt/es sibling steals it is NOT the catalog NO_MATCH confidence class but pure SearchQuery absorption.

Why it is filed rather than fixed in JF-551: every fix shape lives OUTSIDE PlayEpisode's own samples (which already carry the exact losing form; anchors cannot outrank a slot that absorbs the entire utterance):
1. Qualify or drop PlayVideo's bare "{title} を見たい" (keep "映画 {title} を見たい" etc.). Risk: it is the most natural bare video-by-name request in ja, so removing it likely regresses video routing; needs the JF-459-style live A/B (deploy candidate, probe video one-shots + episode forms, revert on regression).
2. Accept the steal and de-duplicate: drop PlayEpisode's 見たい sample (it never wins; today it is dead weight) and keep 再生して as the only episode carrier. Cheapest, but removes 見たい episode addressing entirely rather than fixing it.
3. Handler-side: let PlayVideoIntentHandler detect the episode-shaped title (シーズン N エピソード N pattern) and delegate to the episode path. No model change, but adds a parsing convention to a handler.

Context for whoever picks it up: the 再生して forms (both word orders) route PlayEpisodeIntent cleanly since the 2026-09-26/27 ja rebuilds, so this is a single-form residual, not a broken locale. Probe protocol and the nondeterminism discipline (4x repeats, selectedIntent-first parsing) are in the locale-routing-probe skill and tests/integration/smapi_client.py.
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
