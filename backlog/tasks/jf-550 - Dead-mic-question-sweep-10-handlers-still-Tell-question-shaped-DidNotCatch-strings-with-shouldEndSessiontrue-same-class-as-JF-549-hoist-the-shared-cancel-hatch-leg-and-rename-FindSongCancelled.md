---
id: JF-550
title: >-
  Dead-mic question sweep: 10+ handlers still Tell question-shaped DidNotCatch*
  strings with shouldEndSession=true (same class as JF-549); hoist the shared
  cancel-hatch leg and rename FindSongCancelled
status: To Do
assignee: []
created_date: '2026-09-12 16:55'
labels:
  - bug
  - reliability
  - interaction-model
dependencies: []
references:
  - JF-549
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/CancelWords.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-549 /simplify altitude review (F1+F2, 2026-09-12). JF-549 fixed the dead-mic question (a question string shipped via ResponseBuilder.Tell, shouldEndSession=true, so Alexa asks with the mic closed and the user's answer goes nowhere) in PlayEpisodeIntentHandler only. The SAME shape survives at these sites, each speaking a locale string that ends in a question:

- PlayNextEpisodeIntentHandler.cs:79 - DidNotCatchSeriesName ("Quale serie vorresti guardare?") - the SAME locale key as the JF-549 incident, in the direct sibling intent; also lacks the cancel hatch
- PlayPodcastIntentHandler.cs:71 - DidNotCatchPodcastName ("Quale podcast vorresti ascoltare?")
- SleepTimerIntentHandler.cs:79 - DidNotCatchSleepTimer ("Per quanto devo impostare il timer...?")
- SetReminderIntentHandler.cs:77,101,107 - DidNotCatchReminderTime ("Quando devo ricordartelo?")
- AddToQueueIntentHandler.cs:84 and PlayNextIntentHandler.cs:85 - DidNotCatchQueueItem ("Cosa vorresti aggiungere alla coda?")
- QueryArtistLibraryIntentHandler.cs:98 and PlayArtistSongsIntentHandler.cs:179 - DidNotCatchArtistName ("Quale artista vorresti cercare?")
- BrowseLibraryIntentHandler.cs:298 - DidNotCatchBrowseCategory
- PlayMoodMusicIntentHandler.cs:409 - DidNotCatchMood
- BaseHandler.cs:~4974 - DidNotCatchPlaylistName (PlayPlaylist path)

Legitimately Tell-shaped (retry instructions, NOT questions; do not convert): DidNotCatchVideoTitle, DidNotCatchChannelName, DidNotCatchGenre, DidNotCatchDecade.

Fix shape per site: the shared BuildDialogElicitResponse (BaseHandler), which requires dialog.intents registration in all 17 templates per intent and, once the elicit opens, the elicitation-trap cancel hatch. F2 folds in here: the hatch call-site is now byte-identical in PlaySong/PlayAlbum/PlayRadio/PlayEpisode (4 copies); hoist the shared IN_PROGRESS leg into a BaseHandler helper (FindSong's wider hatch stays separate - it is session-state-gated with force-route disjuncts). Also rename the FindSongCancelled locale key (now spoken by 5 handlers with no FindSong involvement) to a flow-neutral key in the same sweep.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every listed handler's empty-slot branch returns BuildDialogElicitResponse (Dialog.ElicitSlot + ShouldEndSession=false + reprompt), never ResponseBuilder.Tell with a question string
- [ ] #2 Every converted intent is registered in dialog.intents in ALL 17 locale templates (anti-pattern #9) with elicitationRequired:false
- [ ] #3 Every converted handler carries the elicitation-trap cancel hatch (hoist the shared IN_PROGRESS leg into a BaseHandler helper; FindSong keeps its wider session-state-gated hatch)
- [ ] #4 The FindSongCancelled locale key is renamed to a flow-neutral key (e.g. FlowCancelled) in all 17 locale files with all call sites updated in the same change
- [ ] #5 Unit tests pin the elicit shape for each converted handler (mirroring PlayEpisodeIntentHandlerTests JF-549 tests)
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
