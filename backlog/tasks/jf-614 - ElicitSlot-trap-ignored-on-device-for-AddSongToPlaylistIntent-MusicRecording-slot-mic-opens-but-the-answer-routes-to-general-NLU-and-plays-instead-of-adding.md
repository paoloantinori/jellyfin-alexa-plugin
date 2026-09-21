---
id: JF-614
title: >-
  ElicitSlot trap ignored on-device for AddSongToPlaylistIntent (MusicRecording
  slot?): mic opens but the answer routes to general NLU and plays instead of
  adding
status: Done
assignee: []
created_date: '2026-09-21 10:37'
updated_date: '2026-09-21 14:53'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
On-device 2026-09-21 (JF-601 verification): the AddSongToPlaylistIntent song-missing elicit emits the CORRECT response server-side (Dialog.ElicitSlot slotToElicit=song + updatedIntent + shouldEndSession=false + reprompt, verified in podman response bodies at corr=2b913ed1/ccbe9249), the intent IS dialog-registered in the live it-IT model (verified via get-interaction-model, entry byte-identical in shape to FindSongIntent's), the mic DID reopen on device (the user's spoken title was captured), but the utterance routed through general NLU to PlaySongIntent (10:13:56Z) which PLAYED the song instead of filling the song slot. So Alexa ignored the ElicitSlot trap while honoring the open session.

Evidence and eliminations: dialog registration live and shape-identical to the working FindSongIntent entry (both elicitationRequired:false, types matching their languageModel); the directive wire shape is the shared BuildElicitSlotResponse builder that FindSong uses in production daily. The remaining structural difference: AddSong's slots are AMAZON.MusicRecording (ER-backed) while FindSong's working slot is AMAZON.SearchQuery. Hypothesis: ElicitSlot slot-trapping behaves differently for ER-backed slot types on-device (unproven).

DEVICE RE-VERIFICATION 2026-09-21 (second battery, podman sequence): the ElicitSlot trap WORKS on-device - the open elicit captured the user's spoken "stop" INTO the song slot (song="stop", dialogState=IN_PROGRESS at 12:38:54/12:39:18), refuting the original "trap ignored" hypothesis. The REAL failure: the AMAZON.MusicRecording song slot does not FILL from free-text titles during dialog management - "aggiungi soul coughing alla playlist prova echo" filled playlist_target but left song EMPTY (12:38:22), so the elicit looped "quale canzone?" and never progressed; the user's stop attempts landed in the slot as values. FindSong does not have this problem because titleKeywords is AMAZON.SearchQuery (pure text).

Design direction: the song slot of AddSongToPlaylistIntent must become a pure-text capture. Constraint: AMAZON.SearchQuery cannot coexist with another slot in one sample (anti-pattern #2) and the same slot name cannot change type across intents (PlaySong.song is MusicRecording), so the redesign is: rename the slot (e.g. song_query, SearchQuery) with SONG-ONLY samples ("aggiungi la canzone {song_query}"), resolve the playlist in a second turn (elicit playlist_target after the song is found) or parse it handler-side, and keep the cancel hatch. Also observed the same session (12:37:57) stealing a CREATE phrase ("crea una playlist chiamata prova echo sei" -> AddSongToPlaylistIntent playlist_target="chiamata prova echo 6"): the device NLU competition vs the newly added song-less sample needs a post-rebuild probe battery once the redesign lands. Until fixed, the multi-turn add-song flow is unreliable on-device; the one-shot full phrase works only when the title resolves in ER.
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
Shipped 2026-09-21 (commit c109dd9a, deployed as the net10.0 build + 17/17 model rebuild). The complete redesign: both AddSong dialog slots are pure text (song_query SearchQuery new name; playlist_target renamed type to SearchQuery - unique name, no cross-intent constraint), the greedy song-only capture splits its playlist clause handler-side (per-language markers), the song resolves BEFORE the playlist elicit (fail fast, the recorded design order), the bare verb+SearchQuery catch-all samples removed (anti-pattern #3 vs the queue family), each elicit echoes the other slot's current value in the updatedIntent (ElicitSlotDirective gained optional slot values), hi/ja regained playlist-first samples, fixtures pin capture spans exactly and guard the create-steal. Simulate battery on the real pipeline: the full one-shot phrase ADDS end-to-end ("Rapsodia... aggiunto alla playlist prova echo"), the split logs fire, create is clean, podcast plays. ALSO discovered: minix runs Jellyfin 12.1.0 (a day of net9.0 deploys threw MissingMethod on the playlist interface); the net10.0 build is now the minix deploy line (memory updated). Gates: literal /code-review high (10 findings: 8 applied, F6 mirrors mid-review, F10 filed with JF-613) + literal /simplify (6 findings applied); tests 4192/4192 both TFMs. Remaining device probe: the second elicit turn (say the playlist after the song question) - the only step simulate cannot exercise.
<!-- SECTION:FINAL_SUMMARY:END -->
