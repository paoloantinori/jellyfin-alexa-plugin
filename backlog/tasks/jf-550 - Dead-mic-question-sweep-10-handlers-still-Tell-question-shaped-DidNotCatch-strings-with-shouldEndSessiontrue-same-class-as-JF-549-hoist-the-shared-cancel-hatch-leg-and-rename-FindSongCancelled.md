---
id: JF-550
title: >-
  Dead-mic question sweep: 10+ handlers still Tell question-shaped DidNotCatch*
  strings with shouldEndSession=true (same class as JF-549); hoist the shared
  cancel-hatch leg and rename FindSongCancelled
status: Done
assignee: []
created_date: '2026-09-12 16:55'
updated_date: '2026-09-13 12:05'
labels:
  - bug
  - reliability
  - interaction-model
dependencies: []
references:
  - JF-549
  - scripts/validate_question_responses.py
  - .claude/skills/dead-mic-sweep/SKILL.md
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
- [x] #1 Every listed handler's empty-slot branch returns BuildDialogElicitResponse (Dialog.ElicitSlot + ShouldEndSession=false + reprompt), never ResponseBuilder.Tell with a question string
- [x] #2 Every converted intent is registered in dialog.intents in ALL 17 locale templates (anti-pattern #9) with elicitationRequired:false
- [x] #3 Every converted handler carries the elicitation-trap cancel hatch (hoist the shared IN_PROGRESS leg into a BaseHandler helper; FindSong keeps its wider session-state-gated hatch)
- [x] #4 The FindSongCancelled locale key is renamed to a flow-neutral key (e.g. FlowCancelled) in all 17 locale files with all call sites updated in the same change
- [x] #5 Unit tests pin the elicit shape for each converted handler (mirroring PlayEpisodeIntentHandlerTests JF-549 tests)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PATTERN CODIFIED (2026-09-13, user directive): the site list is now produced mechanically by scripts/validate_question_responses.py (dead-mic detector: question-shaped locale keys crossed against Tell sites) and the fix procedure is the .claude/skills/dead-mic-sweep skill. First run reproduces this task's hand-made enumeration exactly: 13 sites across 10 files (BaseHandler:4974; AddToQueue:84; PlayNext:85; QueryArtistLibrary:98; PlayArtistSongs:179; PlayNextEpisode:79; PlayPodcast:71; BrowseLibrary:298; PlayMoodMusic:409; SleepTimer:79; SetReminder:77,101,107). When the sweep completes, consider promoting --strict into the CI validate job so the class cannot re-enter.

REVIEW ROUND 1 (high effort, 2026-09-13, uncommitted diff): one must-fix before commit. BrowseLibraryIntentHandler.cs:299 - the cancel hatch sits INSIDE HandleGenresQuery, which is unreachable as a hatch: entering HandleGenresQuery requires browse_category to be a genre-category key (genres/generi/géneros/gêneros/أنواع/शैलियाँ/ジャンル) while the hatch requires a slot value from the CancelWords vocabulary, and the two sets are disjoint (only slot is browse_category). The browse_category elicit this branch opens re-enters through HandleAsync, which has NO hatch: a captured 'ferma'/'stop' answer lands in the unrecognized-category ResponseBuilder.Ask branch (line ~130) and re-asks in a loop instead of FlowCancelled. Fix: hoist the BuildCancelDuringOpenElicit call to HandleAsync entry (above the empty-category Ask at line ~105), same shape as the other 12 sites. Everything else verified clean: allSlotNames parity vs model slot sets for all 15 BuildDialogElicitResponse sites + FindSong/PlayRadio; SetReminder 3-shape collapse equivalent, creation path untouched; params change source-compatible; models byte-identical to templates (md5 en-US); languageModel unchanged everywhere (dialog-only diff, prompts removed only in the 6 legacy locales with exactly the 3 Elicit.* ids); validator Phase 8 green; 3696/3696 tests both TFMs; dead-mic detector 0 remaining sites. Cosmetic: 3 test files have joined lines (two statements on one line) at PlayArtistSongsIntentHandlerTests.cs:778, PlayNextEpisodeIntentHandlerTests.cs:185, PlayPodcastIntentHandlerTests.cs:133 - fix in passing.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
COMPLETED and DEPLOYED 2026-09-13 13:30-14:05. All 13 dead-mic Tell-question sites converted to BuildDialogElicitResponse elicits (mic open + reprompt): PlayNextEpisode, PlayPodcast, SleepTimer, SetReminder x3 (collapsed to one reminder-null elicit), AddToQueue, PlayNext, QueryArtistLibrary, PlayArtistSongs, BrowseLibrary (genres), PlayMoodMusic, PlayPlaylist + ShufflePlay (caller-side, naming the invoking intent); BaseHandler's shared playlist Tell removed (contract documented). All converted intents carry BuildCancelDuringOpenElicit (hoisted to BaseHandler, 4 inline hatches collapsed; BrowseLibrary's hoisted to HandleAsync entry per code-review - the in-branch placement was unreachable because a cancel word never resolves as a genre category). Dialog registration: 11 intents added to dialog.intents in all 17 templates (elicitationRequired:false), 6 legacy PlayEpisode blocks normalized (elicitationRequired:true + dangling Elicit.* prompt refs dropped); validator Phase 8 now machine-checks elicit-target dialog registration + slot parity (negative-tested). FindSongCancelled renamed FlowCancelled across 17 locales + call sites. Tests: 10 new DeadMicSweepElicitTests + TestHelpers.AssertElicitsSlot (8 copies collapsed) + 4 updated pins + BuildDialogElicitResponse now params string[] (13 hoisted arrays deleted). Detector: 0 live sites (was 13/10 files). LIVE: DLL deployed (md5 ed5ee504 verified active, config intact), all 17 locale models rebuilt SUCCEEDED, live it-IT model carries 18 dialog entries incl. all 11 new, SeriesName catalog wiring preserved through the rebuild (JF-552 graft), e2e 7 passed + 1 documented skip (the bare empty-name playlist form routes BrowseLibrary/Fallback - routing data recorded in JF-557). Suite 3696/3696 both TFMs; CI green. Follow-ups filed from reviews: JF-556 (Phase 8 blind spots: BuildElicitSlotResponse callers, allSlotNames parity, CI advisory), JF-557 (BrowseLibrary filter slot undeclared - genre path dead end).
<!-- SECTION:FINAL_SUMMARY:END -->

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
