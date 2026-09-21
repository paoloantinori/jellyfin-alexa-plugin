---
id: JF-601
title: >-
  Dead-mic playlist prompts: SpecifySongForPlaylist question rides a
  session-ending Tell; convert to the JF-550 elicit pattern with dialog.intents
  registration
status: Done
assignee: []
created_date: '2026-09-20 19:13'
updated_date: '2026-09-20 21:15'
labels: []
dependencies: []
references:
  - commit 560b484c
  - >-
    backlog/tasks/jf-600 -
    Playlist-create-leak-chiamata-carrier-captured-into-playlist_target-and-the-empty-song-branch-asks-the-wrong-question.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Gate review of 560b484c (JF-600) found that the new missing-slot prompts in the playlist-edit family speak questions on ResponseBuilder.Tell (shouldEndSession=true): AddSongToPlaylistIntentHandler.cs:72 asks "Quale canzone vuoi aggiungere alla playlist X?" / "Which song should I add to the playlist X?" and then closes the session, so the user's spoken song title routes to general NLU/Amazon Music and the add never completes. The repo's own detector scripts/validate_question_responses.py flags this as the ONLY live dead-mic site in the codebase (exits with 1 live site). The sibling handlers PlayPlaylistIntentHandler.cs:92 and ShufflePlayIntentHandler.cs:82 already use BuildDialogElicitResponse with a cancel-word hatch (the JF-550 fix pattern) for the same class of question. The pre-change branch was an equally question-shaped Tell (DidNotCatchPlaylistName "Quale playlist vorresti ascoltare?"), so the shape pre-exists but the commit shipped brand-new question strings in 17 locales without the elicit pattern. CRITICAL constraint: AddSongToPlaylistIntent is NOT registered in dialog.intents in any of the 17 models (18 dialog intents exist, none is AddSongToPlaylistIntent), so a Dialog.ElicitSlot fix is silently dropped without model registration (anti-pattern #9); registration must be added to all 17 YAML templates and models regenerated, with elicitationRequired:false since the dialog is code-driven.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SpecifySongForPlaylist response elicits the song slot with the mic open (Dialog.ElicitSlot pattern from PlayPlaylistIntentHandler JF-550) or an equivalently mic-open shape; scripts/validate_question_responses.py exits green with zero live dead-mic sites
- [ ] #2 AddSongToPlaylistIntent (and any intent used for the elicit) is registered in dialog.intents in ALL 17 locale templates and models regenerate byte-exact (validate_interaction_models.py Phase 8 passes)
- [ ] #3 The SpecifyPlaylistName retry Tells in AddCurrent/RemoveCurrent/Create are either converted to the same mic-open shape or explicitly kept Tell with a documented reason in the code comment (they are retry imperatives, not questions; the detector excludes them by design)
- [ ] #4 Unit tests cover: empty song slot with filled playlist opens the mic and names the stripped playlist; empty playlist slot answers the neutral retry; cancel-word escape during the open elicit still works (BuildCancelDuringOpenElicit precedent)
- [ ] #5 17 locale files carry any new string keys; NLU fixtures updated if samples change
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented 2026-09-20 (uncommitted, under the literal /code-review gate): AddSongToPlaylistIntentHandler's two missing-slot branches now elicit via BuildElicitSlotResponse (song / playlist_target) with the JF-550 BuildCancelDuringOpenElicit hatch in front; AddSongToPlaylistIntent registered in dialog.intents in ALL 17 templates (4-space sibling indent, parse-gated insert); models regenerated; validate_question_responses.py now reports 0 live dead-mic sites; tests updated to AssertElicitsSlot + stripped-name speech assertions.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Fixed 2026-09-20 (commit 72b1989a). The two missing-slot prompts of AddSongToPlaylist now elicit via BuildElicitSlotResponse (mic open) with the JF-550 cancel hatch; AddSongToPlaylistIntent registered in dialog.intents in all 17 templates; validate_question_responses.py reports 0 live dead-mic sites; the elicit slot arrays were inline (JF-612 later lifted that constraint: the checker resolves hoisted declarations, see jf-612). Sibling retry-imperative Tells kept with the rationale owned once on SpecifyPlaylistNameTell (AC#3 documented-keep). Live-verified: simulator AddSong with playlist only -> "Quale canzone vuoi aggiungere alla playlist prova echo?" with shouldEndSession=false + Dialog.ElicitSlot directive. Gates: literal /code-review high (2 rounds, all findings applied) + /simplify + tests 4186/4186.
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
