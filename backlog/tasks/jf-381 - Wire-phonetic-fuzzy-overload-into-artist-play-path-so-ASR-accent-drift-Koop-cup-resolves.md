---
id: JF-381
title: >-
  Wire phonetic fuzzy overload into artist-play path so ASR accent drift
  (Koop->cup) resolves
status: To Do
assignee: []
created_date: '2026-07-25 19:19'
labels:
  - enhancement
  - artist-search
  - phonetic
  - fuzzy-match
  - asr
dependencies: []
modified_files:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Unit/FuzzyMatcherTests.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Koop case (2026-07-25): an it-IT Echo transcribed 'Koop' as 'cup'. The plugin already has the machinery to solve this without hand-rolled rules or a new library: ArtistIndexService pre-computes Double Metaphone codes for every artist, and FuzzyMatcher has a phonetic-aware overload (FindBestMatch with phoneticLookup, score bonus when codes match) already used by song search. The artist-play path does NOT use it - PlayArtistSongsIntentHandler tier-4 calls the Levenshtein-only FuzzyMatch overload (BaseHandler.cs:1282), ignoring _artistIndex's codes.

Algorithm analysis (vendored DoubleMetaphone.cs: K->'K' line 160, ProcessC emits 'K' before back vowels, all vowels encode the same line 80-82) indicates Koop/cup/coop/cop share code 'KP'. AC#1 (a proper unit test, not a probe) confirms or refutes this execution-side and gates the fix.

This replaces the hand-rolled c/k/oo approach (JF-379, reverted 2026-07-25): that solved the wrong layer. The Metaphone wiring reuses proven machinery with no new dependency. JF-379's per-language research stays as the follow-up for accent cases Metaphone (an English algorithm) does not model well.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Unit test (TDD, AC gates the fix): assert FuzzyMatcher.FindBestMatch with the PHONETIC overload matches query 'cup' to a candidate named 'Koop', given a phoneticLookup that returns DoubleMetaphone.Encode('Koop') for its id. If this fails, the Metaphone path does NOT solve Koop and the approach is wrong - stop and reconsider. If it passes, the code collision is confirmed and the test becomes the regression guard.
- [ ] #2 Wire PlayArtistSongsIntentHandler tier-4 fuzzy to the phonetic-aware FuzzyMatch overload: pass _artistIndex's pre-computed Double Metaphone codes via the candidateIdSelector + phoneticLookup args. Currently it calls the Levenshtein-only overload (BaseHandler.cs:1282), ignoring the codes _artistIndex already holds.
- [ ] #3 Live verify on minix: with the wired change, PlayArtistSongs for slot value 'cup' resolves to artist 'Koop' (simulate ASR delivering 'cup' for the spoken 'Koop')
- [ ] #4 On-device confirmation (manual): say 'suona koop' on the it-IT Echo and confirm it now plays (was failing before with ASR hearing 'cup')
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
