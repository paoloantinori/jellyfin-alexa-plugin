---
id: JF-405
title: >-
  On-device verification checklist: multi-turn fixes (JF-394/395/396/397),
  StopIntent experiment (JF-402), sample residuals (JF-399), stop data
  collection (JF-392)
status: To Do
assignee: []
created_date: '2026-08-23 07:09'
updated_date: '2026-08-29 09:12'
labels:
  - testing
  - on-device
  - verification
milestone: m-15
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Single checklist of everything that needs a REAL Echo/on-device verification and cannot be closed from unit tests or profile-nlu alone. Work through it whenever at the device; tick items in the notes.

1. JF-395 exit-by-no (deployed): FindSong multi-candidate -> say "nessuna" -> skill must exit cleanly with the FindSongDisambigAbandoned message (NOT repeat 'di il numero o il titolo'). Also test "no, la seconda" still PICKS the second.
2. JF-396 picker words (deployed): during a FindSong disambiguation answer with ordinal/count words: it-IT "la seconda"/"il quarto", plus any de/fr/es/pt if the device locale allows.
3. JF-394 resume decline (deployed): open skill with a resume offer -> "no" -> later stray "sì" must NOT resume the declined item (expect UnexpectedYes-style response or new flow).
4. JF-397 fallback mid-dialog (deployed): during a disambiguation 'did you mean X?' say something unintelligible -> skill must REPEAT the question (session stays open), not end the conversation.
5. JF-402 StopIntent experiment: profile-nlu "ferma" against a de- no, it-IT model WITHOUT the custom samples (requires a temporary model variant); if it still routes to StopIntent, remove the samples and delete the documented exception.
6. JF-399 residuals observed on profile-nlu 2026-08-23: de-DE "suche ein lied mit liebe im titel" resolves to NO intent (needs another phrasing or sample); pt-BR "sobre o mar" phrasing routes but keyword not captured; fr-FR "je cherche une chanson sur la pluie" misroutes to PlaySongIntent. Decide: more samples or accept (FindSong elicits keywords next turn anyway).
7. JF-392 data collection: with DiagnosticInteractionLogging enabled (already ON for the main user), collect N>20 stop attempts; note approximate time of each ignored 'alexa stop' so the [diag] log can classify it.
8. Multi-Echo sanity after the 0.12.0.x multi-turn deploy: one interactive FindSong session end-to-end (artist -> keywords -> pick -> play).
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review-pass update (deploy 348b171): items 1-4 now test the FULL routing (the first deploy had JF-397 dead code: FindSong intercepted all fallbacks). NEW checks to add from the /code-review pass: (a) plain 'no' during a FindSong disambiguation may route to AMAZON.NoIntent with EMPTY slots - built-in intents carry no slot values - verify the ElicitSlot dialog captures 'no' as titleKeywords (else the exit needs a NoIntent branch reading the FindSong session); (b) verify the debug log line 'FallbackIntent: active resume offer, re-asking resume prompt' appears once (confirms ILibraryManager DI resolved non-null in FallbackIntentHandler); (c) triage note: response logs show PRE-removal merged attributes including __remove_attributes (ResponseBodyLoggingInterceptor runs first in reverse order) - the final payload is correct, don't misread logs.

CHECKLIST REFRESHED 2026-08-29 for tonight's change set: docs/manual-verification-2026-08-29.md (committed). Covers the Koop flow (routing, fast-speech ASR, artist-question fallback, context retention), cancel words during open questions (both regimes), the PlaySong elicit round-trip incl. musician survival, stop decomposition (informational), the en-* Musician canonicalization check, the duplicate-track regression, and the PlaybackStarted stall telemetry. The original multi-turn items (JF-394-397) remain from the previous checklist; sample residuals JF-399 partially superseded by JF-414's multilingual push; stop data collection JF-392 CLOSED (two failure modes identified and fixed/documented).
<!-- SECTION:NOTES:END -->
