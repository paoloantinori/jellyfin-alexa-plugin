---
id: JF-894
title: >-
  Remapped Audio-typed audiobook chapters classify VideoAppAudio at
  ClassifyLedgerItemKind: pause speaks a music line over a book
status: Done
assignee: []
created_date: '2026-10-16 09:26'
updated_date: '2026-10-16 10:43'
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf894 (e2160b4f), merged to main 605eb146, pushed, DEPLOYED to minix (net10.0 Release into AlexaSkill_0.12.1.0, active-DLL md5-verified), config survived, probes green (play pipeline, idle-pause JF-482 shape). ALTITUDE CHOSEN: launch-time verdict, NOT a classifier climb — the audiobook VideoApp launch sites know the medium is a book for free (their arms run only after the verified climb accepted), so the ledger carries DeviceQueueManager.LaunchRoute.VideoAppAudiobook beside the chapter id, and ClassifyLedgerItemKind reads it O(1); a classification-time climb was rejected as provably not music-safe (a plain song inside its MusicAlbum is exactly the shape the type-agnostic climb accepts; the classifier has no caller context; the JF-799 caller-intent doctrine). Kernel verdict arm sits after the kind arms and before the music arm (mis-minted video-kind entries keep the kind answer, pinned). Backward compat: pre-upgrade entries (route VideoApp) keep today's classification, accepted residual, no migration, documented in code. Red proof: the end-to-end pin (launch via the real builder, then pause) failed both TFMs pre-fix with the music line over the book. Gates (in e2160b4f): 0-warning build both TFMs, 6029/6029 both TFMs (+11 pins), /simplify 4 agents (shared IsVideoAppRoute predicate et al.), /code-review high 6 findings (4 applied, 2 dispositioned). Residuals filed: JF-898 (the rejected-climb fresh-launch arm still mints bare VideoApp; decide with the JF-799 family); the rollback-window route-parse degradation accepted as the standard no-migration stance. NLU/model N/A. gate-exempt: gates ran in the dispatched worker's transcript; commit e2160b4f carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
