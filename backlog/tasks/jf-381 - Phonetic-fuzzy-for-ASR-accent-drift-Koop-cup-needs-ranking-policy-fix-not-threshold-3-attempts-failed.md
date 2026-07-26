---
id: JF-381
title: >-
  Phonetic fuzzy for ASR accent drift (Koop->cup) - needs ranking-policy fix,
  not threshold (3 attempts failed)
status: To Do
assignee: []
created_date: '2026-07-26 05:41'
labels:
  - enhancement
  - artist-search
  - phonetic
  - fuzzy-match
  - asr
  - blocked
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An it-IT Echo transcribed "Koop" as "cup" (2026-07-25). Three fix attempts all failed on the same root cause: FindBestMatch ranks by Levenshtein, and a coincidental substring (cup in Porcupine) always beats a phonetic match (cup~=Koop) on that metric.\n\nATTEMPT HISTORY:\n- v1 (PhoneticMatchFloor): cup->Koop scored 60 (floored), but Porcupine Tree scored 90 (containment) and won. Live test: cup played Porcupine Tree.\n- v2 (length-gated floor): Porcupine Tree scored 90 via substring containment regardless of the floor gate. Did not need the floor.\n- v3 (containment early-return gate + score cap): Porcupine Tree still won because capped score (89) > Koop floored score (60).\n\nROOT CAUSE: this is a RANKING POLICY problem, not a threshold problem. The scoring formula ranks by Levenshtein, and no threshold tweak can make a phonetic match beat a coincidental substring containment.\n\nFIX DIRECTION: when a phonetic code collision exists for a length-matched candidate, PREFER it (higher rank) over a substring-containment candidate with a large length difference. Three approaches to evaluate:\n(a) Two-pass: first check for phonetic matches among length-matched candidates, fall back to Levenshtein if none found.\n(b) Boost phonetic-match scores ABOVE containment (floor at ContainmentScore+1 when codes match AND length-matched).\n(c) A dedicated phonetic tier in the artist search before the existing fuzzy tiers.\n\nAll code reverted; branch is clean. The podcast fix, follow-me work, and honest-string changes on the branch are unaffected and verified.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Design the ranking policy: when a phonetic code collision exists for a length-matched candidate (e.g. cup->Koop, both code KP, within length band), it must rank HIGHER than a substring-containment candidate with a large length difference (e.g. cup->Porcupine Tree, coincidental containment). Three approaches to evaluate: (a) two-pass (check phonetic first, fall back to Levenshtein), (b) phonetic floor at ContainmentScore+1 when length-matched, (c) a dedicated phonetic tier before the fuzzy tiers
- [ ] #2 Implement the chosen approach in FuzzyManager's phonetic FindBestMatchWithScore overload only (do not change the non-phonetic path)
- [ ] #3 Unit test: cup + Koop + Porcupine Tree all in the candidate set -> Koop wins (the regression guard that broke v1/v2/v3)
- [ ] #4 Live verify on minix against the real library (which has Porcupine Tree): PlayArtistSongs slot=cup resolves to Koop, NOT Porcupine Tree
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
