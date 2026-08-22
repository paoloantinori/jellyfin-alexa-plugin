---
id: JF-391
title: >-
  PlayPlaylistIntent sample starvation causes playlist requests to route to
  PlayAlbumIntent (30:1 imbalance it-IT, 10:1 de/fr/es)
status: To Do
assignee: []
created_date: '2026-08-22 08:48'
labels:
  - bug
  - nlu
  - interaction-model
  - playlist
  - i18n
  - routing
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
LIVE EVIDENCE 2026-08-22 (user on-device it-IT):
"chiedi a mia collezione di riprodurre la playlist janis joplin greatest hits" was routed to PlayAlbumIntent (slot: album=janis joplin greatest hits) instead of PlayPlaylistIntent. The album handler's fuzzy fallback found the right album and played correctly, but the announcement says "album" and the routing is wrong.

SAMPLE IMBALANCE (mechanically verified from the generated models):
- it-IT: PlayPlaylistIntent=12 samples (hand-written list) vs PlayAlbumIntent=354 (Cartesian product) = 30:1
- de-DE: Playlist=2 vs Album=20 = 10:1
- fr-FR: Playlist=2 vs Album=20 = 10:1
- es-ES: Playlist=2 vs Album=20 = 10:1
- en-US: Playlist=20 vs Album=9 = balanced

ROOT CAUSE (it-IT): the user said "di riprodurre la playlist X". PlayPlaylistIntent has "Di riprodurre playlist X" (no article) and "Riproduci la playlist X" (article but imperative) but NOT "Di riprodurre la playlist X" (infinitive + article). PlayAlbumIntent has "Di riprodurre la raccolta X" (where "raccolta" is semantically similar to "playlist") with 354 training examples. The NLU statistically favors the 354-sample intent, especially when the form matches exactly.

ROOT CAUSE (other locales): only 2 bare samples ("Spiele die Playlist X" / "Spiele meine Playlist X" in de-DE) against 20 album samples. No competition possible.

FIX DIRECTION:
- it-IT: add missing forms to the template + ideally switch PlayPlaylistIntent to the same Cartesian product generation as PlayAlbumIntent (using the imperative/infinitive vocabulary)
- Other locales: add 10-15 forms each covering imperative/infinitive/possessive variants
- Guard: verify no new NLU competition issues (anti-pattern #3: short/greedy patterns stealing from specific intents)
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 it-IT: add the missing infinitive+article forms to the PlayPlaylistIntent samples in the YAML template (Di riprodurre la playlist, Di suonare la playlist, Di mettere la playlist, Di pleiare la playlist, + Ask/Tell forms)
- [ ] #2 de-DE, fr-FR, es-ES: add meaningful sample coverage (currently only 2 samples each; target 10-15 forms covering imperative, infinitive, and possessive variants)
- [ ] #3 Remaining 13 locales: audit and add samples where coverage is thin
- [ ] #4 Anti-pattern check: new samples must not steal utterances from other intents (run NLU tests after)
- [ ] #5 Regenerate it-IT model from template; validate all 17 models
- [ ] #6 Deploy models to all active locales and verify routing with profile-nlu
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
