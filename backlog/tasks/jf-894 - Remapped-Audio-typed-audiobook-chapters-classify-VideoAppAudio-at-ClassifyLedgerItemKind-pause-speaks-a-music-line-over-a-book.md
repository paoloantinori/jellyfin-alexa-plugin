---
id: JF-894
title: >-
  Remapped Audio-typed audiobook chapters classify VideoAppAudio at
  ClassifyLedgerItemKind: pause speaks a music line over a book
status: To Do
assignee: []
created_date: '2026-10-16 09:26'
labels: []
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found in the JF-871/872 code-review round (worker handback, 2026-10-16). SHAPE: an Audio-typed remapped audiobook chapter (the JF-784/JF-799 shape: chapters under a book folder where the chapter items are Audio-typed) classifies PlayingMedium.VideoAppAudio in PlaybackLaunchBuilder.ClassifyLedgerItemKind (PlaybackLaunchBuilder.cs:1149), because AudiobookItems.IsAudioBook (Alexa/Util/AudiobookItems.cs:21) tests only 'item is AudioBook' while the VideoApp audiobook launch records the CHAPTER itemId in the ledger (PlaybackLaunchBuilder.cs:2975). Consequence after JF-871/872: voice pause/stop over such a book speaks MusicStoppedByVoice ("ho fermato la musica") where it spoke VideoStoppedByVoice before; both wrong, the book line BookStoppedByVoice is what the medium deserves. Pre-existing classifier hole shared by EVERY flat-gate consumer (the JF-858/JF-890 medium gates read the same classification), not a JF-871/872 regression: the wording change only made the misclassification audible. FIX DIRECTION: widen the book discriminator at the classifier (chapter item whose resolved book ancestor passes the JF-793-verified climb, the same seam TryResolveVerifiedParentFolder owns) so remapped chapters classify VideoAppAudiobook; keep the classifier the ONE owner (no belt of book checks in PauseIntentHandler). PIN: a remapped Audio-typed chapter under a verified book folder pauses to BookStoppedByVoice.
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
