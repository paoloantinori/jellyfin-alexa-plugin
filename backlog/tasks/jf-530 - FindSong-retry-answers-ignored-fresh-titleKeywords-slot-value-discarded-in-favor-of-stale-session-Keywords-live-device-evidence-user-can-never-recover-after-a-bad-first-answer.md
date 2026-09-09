---
id: JF-530
title: >-
  FindSong retry answers ignored: fresh titleKeywords slot value discarded in
  favor of stale session Keywords (live device evidence: user can never recover
  after a bad first answer)
status: To Do
assignee: []
created_date: '2026-09-09 16:15'
labels:
  - bug
  - find-song
  - multi-turn
  - device-evidence
dependencies: []
references:
  - corr=a39e9b55
  - corr=1113ebfe
  - corr=514443a3
  - JF-413
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From Paolo's live Echo session (2026-09-09 18:11-18:12, it-IT, Echo Show device AMAZPZMTZ..., corr chain fe11faf8 -> a39e9b55 -> 1113ebfe -> 514443a3): the FindSong multi-turn elicit flow stops understanding the user's answers after the first one.

Log-verified chain:
1. corr=fe11faf8: 'metti una canzone' opener -> FindSongIntent, elicit fired correctly, State=1 (AwaitingKeywords), Keywords=null. THE JF-413 ELICIT MECHANISM ITSELF WORKS on real ASR.
2. corr=a39e9b55: first answer captured as slot titleKeywords='what's for cooking' (what ASR heard). Stored: sessionData.Keywords='what's for cooking', State=0. n-gram search returned 150 songs (broad).
3. corr=1113ebfe: user answers AGAIN, slot titleKeywords='cup' (the real intended word - a Koop song). But the search ran with 'artist=Koop, keywords=what's for cooking' -> 0 songs. The FRESH slot value was captured by NLU but the handler searched with the STALE session Keywords. sessionData.ArtistId got set between turns (9c6c9122 = Koop).
4. corr=514443a3: third answer slot titleKeywords='walls for' ('waltz for...' heard) -> search again with stale keywords + artist=Sator -> 0 songs. User gives up: 'non ha mai capito le mie parole'.

The defect: on a retry turn (State=0 with FindSongSessionData in attributes, request routed to FindSongIntentHandler via the session-override 'Routing to FindSongIntentHandler due to active FindSong session'), the fresh slot value does not replace the stored Keywords. The AwaitingKeywords path DOES honor the fresh slot (reads it and stores before searching), so the stale-value path is a DIFFERENT state transition (likely State=0 falling into a path that uses sessionData.Keywords without re-reading the slot).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reproduce from the log chain: turn-2 stored Keywords='what's for cooking'; turn-3 slot titleKeywords='cup' but search ran with the stale stored value - locate the state transition that drops the fresh value (State=0 after turn 2; turn-3 routed via the FindSongSessionData override)
- [ ] #2 Fix: a fresh non-empty titleKeywords slot ALWAYS replaces stored Keywords on any FindSong turn; stored value used only when the slot is empty
- [ ] #3 Determine whether turn-2's 'what's for cooking' was pure ASR misrecognition or slot pollution; document the finding
- [ ] #4 Unit tests: retry-with-new-keywords uses the new value; empty-slot keeps stored; artist-scoped retry composes fresh keywords with stored artist
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
