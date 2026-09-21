---
id: JF-614
title: >-
  ElicitSlot trap ignored on-device for AddSongToPlaylistIntent (MusicRecording
  slot?): mic opens but the answer routes to general NLU and plays instead of
  adding
status: To Do
assignee: []
created_date: '2026-09-21 10:37'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On-device 2026-09-21 (JF-601 verification): the AddSongToPlaylistIntent song-missing elicit emits the CORRECT response server-side (Dialog.ElicitSlot slotToElicit=song + updatedIntent + shouldEndSession=false + reprompt, verified in podman response bodies at corr=2b913ed1/ccbe9249), the intent IS dialog-registered in the live it-IT model (verified via get-interaction-model, entry byte-identical in shape to FindSongIntent's), the mic DID reopen on device (the user's spoken title was captured), but the utterance routed through general NLU to PlaySongIntent (10:13:56Z) which PLAYED the song instead of filling the song slot. So Alexa ignored the ElicitSlot trap while honoring the open session.

Evidence and eliminations: dialog registration live and shape-identical to the working FindSongIntent entry (both elicitationRequired:false, types matching their languageModel); the directive wire shape is the shared BuildElicitSlotResponse builder that FindSong uses in production daily. The remaining structural difference: AddSong's slots are AMAZON.MusicRecording (ER-backed) while FindSong's working slot is AMAZON.SearchQuery. Hypothesis: ElicitSlot slot-trapping behaves differently for ER-backed slot types on-device (unproven).

Next probes in order: (1) simulate-skill multi-turn (turn 1 the elicit trigger, turn 2 a bare title) - simulate runs the real Alexa dialog pipeline server-side and shows whether the slot fills; (2) same probe against the FindSong flow as the control; (3) if simulate reproduces the ignore, try changing the song slot's dialog type or the FindSongByArtist precedent (JellyfinArtist elicit) as comparisons; (4) fallback design if ER slots genuinely cannot elicit: keep the mic open with a session attribute router (the FindSongSessionData precedent) that catches the next utterance's song-ish content and routes it into the add flow. Until fixed, the on-device add-song flow dies at the question: the user must re-issue the full command one-shot.
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
