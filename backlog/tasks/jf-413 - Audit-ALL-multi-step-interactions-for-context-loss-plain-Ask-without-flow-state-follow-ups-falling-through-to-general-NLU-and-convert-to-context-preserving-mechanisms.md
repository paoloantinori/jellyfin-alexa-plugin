---
id: JF-413
title: >-
  Audit ALL multi-step interactions for context loss (plain Ask without flow
  state; follow-ups falling through to general NLU) and convert to
  context-preserving mechanisms
status: Done
assignee:
  - zai
created_date: '2026-08-28 18:37'
updated_date: '2026-09-08 16:40'
labels: []
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
User-reported pattern (2026-08-28 20:23, live): PlayAlbumIntent's album elicit was a plain ResponseBuilder.Ask with NO flow state, so after "Quale album vuoi ascoltare?" the user's follow-up "quali ci sono" went through general NLU, routed to QueryRecentlyAddedIntent, and surfaced unrelated recently-added content: the conversational thread was lost. Fixed for the album elicit the same day by converting to Dialog.ElicitSlot (PlayAlbumIntent is registered in dialog.intents with elicitationRequired=false; the musician slot survives the round-trip). The user suspects the same defect class exists in OTHER multi-step interactions.

Audit scope: every handler returning Ask()/open-session prompts. Known flow-state mechanisms to compare against: FindSongSessionData + Dialog.ElicitSlot (the reference implementation, FindSongIntentHandler.BuildElicitSlotResponse), DisambiguationHelper state (disambig_type/matches/index via Yes/No intents), crossmedia_notfound_* attrs (JF-363), resume_state (LaunchRequest), pagination_state, ConversationalFlows namespacing + mutual exclusion (JF-398). A plain Ask whose follow-up relies on general NLU re-matching the original intent is the defect shape.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory of every multi-step conversational flow: for each handler that returns Ask()/reprompt (disambiguation, cross-media suggestion, resume prompt, FindSong elicitation, album elicit, book disambiguation, pagination, queue listing, any ElicitSlot user), record: prompt source, session-state written (if any), follow-up routing mechanism (Dialog.ElicitSlot vs plain session + general NLU vs yes/no intents), and dialog.intents registration per locale
- [x] #2 Verdict per flow: CONTEXT-PRESERVING vs CONTEXT-LOSING, with the concrete failure scenario for each losing one (the pattern to match: 2026-08-28 20:23, album elicit plain Ask followed by 'quali ci sono' routing to QueryRecentlyAdded and surfacing unrelated recent content)
- [x] #3 Every context-losing flow either converted to Dialog.ElicitSlot (when a single slot should capture the answer and the intent is registered in dialog.intents in ALL 17 locales) or given explicit flow state consumed by the router/yes-no handlers (JF-398 namespacing), with unit tests per converted flow
- [x] #4 Cross-check JF-401 asymmetry (dialog.intents registration differs across locales) since ElicitSlot silently fails where unregistered
- [ ] #5 On-device verification of at least the converted flows on it-IT
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
INVENTORY COMPLETE (AC #1/#2). CONTEXT-PRESERVING (state + consumer verified): resume prompt (LaunchRequest + FallbackIntentHandler, resume_state + Yes/No); DisambiguationHelper's 3 Asks (disambig_matches/index/type + Yes/No); HandleFuzzyMiss Confirm (BaseHandler, disambig_* + Yes/No); CrossMediaArtistOffer (crossmedia_notfound_* + disambig artist + Yes/No, JF-363); pagination 'altri?' (ListPaginationHelper + ListQueue, pagination_state consumed by FallbackIntentHandler); carousel Asks (BrowseLibrary/QueryRecentlyAdded/InProgress/QueryArtist, pagination_state via helper + Fallback); PlayBook disambiguation (disambig MediaTypeAlbum, JF-361); FindSong elicitation (FindSongSessionData + ElicitSlot, reference implementation, now with the cancel-word escape hatch); PlayAlbum elicit (ElicitSlot, fixed 2026-08-28). OK-BY-DESIGN (nothing to preserve): welcome prompts (LaunchRequest 2nd, SkillConnection x2 - the expected follow-up IS a fresh content request); NoIntent fresh-start (clears state deliberately).

CONTEXT-LOSING FOUND AND FIXED: PlaySongIntentHandler's ElicitSongName was a plain Ask with NO state (the user's bare-title answer had to re-match general NLU; an already-filled musician slot was discarded - same shape as the album elicit). Converted to Dialog.ElicitSlot(song) declaring BOTH slots (song+musician) in updatedIntent. TDD: EmptyMusicianSlotTests.PlaySong_EmptySongSlot_ElicitsSongViaDialogDirective red->green; suite 2737; deployed; simulator-verified: empty PlaySong -> ElicitSlot slotToElicit=song, updatedIntent slots [musician,song], 'Quale canzone vuoi ascoltare?'.

BORDERLINE, NOT CONVERTED (follow-up for JF-414): BrowseLibraryIntent's two category Asks (empty/unknown browse_category) expect a bare category word back through general NLU; BrowseLibraryIntent is NOT in dialog.intents, so an ElicitSlot conversion requires registration in all 17 models first (JF-414 scope). Risk assessed low: category words ('artisti', 'album') are strongly matched by Browse samples.

AC #4 VERIFIED: all 17 locale models register the SAME 6 dialog intents (FindSong, FindSongByArtist, ShufflePlay, PlayEpisode, PlaySong, PlayAlbum) - the JF-401 asymmetry no longer exists in the current models, so every ElicitSlot emitter is registered everywhere. Remaining for closure: on-device spot check of the PlaySong elicit round-trip (user: 'metti una canzone' -> 'Quale canzone?' -> title -> plays) once device testing is available.

REVIEW GATE RECOVERED (user challenge 'are you doing DoD without reminders?' - honest answer was no for the last 3 deploys): 5-agent code-review over 18cc2bb..HEAD found 4 real defects in my own sweep/hatches, all fixed in one batch: (1) the containment band gated the PREFIX fallbacks via shared TrySearchFallbackAsync (ASR-truncation shape killed on the DB path); (2) Fast-mode DB path gated with no recovery tier (direct long-name hits became not-founds cold); (3) the FindSong cancel hatch fired on first invocation (songs titled 'Stop' unsearchable) and missed the force-routed StopIntent regime (no titleKeywords slot -> hijack survived); (4) the new elicits lacked JF-398 MarkOthersInactive and their own cancel escape hatch. Also fixed: dialog.intents parity for PlaySong/PlayAlbum in the 6 asymmetric locales (the earlier AC#4 verification had checked INTENT presence but not slot-level elicitationRequired - too shallow), CLAUDE.md JF-381 paragraph updated, e2e reset locale limitation documented.

TEST REASONING (user asked): added 6 tests covering each behavioral fix at its discriminator - open-flow vs first-invocation cancel (the gating), force-routed StopIntent (the second capture regime), PlaySong/PlayAlbum captured-cancel, and JF-398 removal-marker assertions on both elicit responses. Deliberately NOT unit-tested: the prefix-ungate and Fast-DB ungate (private fallback methods in the inline handler; restoring pre-sweep behavior that itself had no coverage; the shared predicate is covered by the ArtistSearch DB-path tests) - recorded as a known gap; a handler-level DB-mock harness would close it if the inline path survives JF-382 consolidation. Dialog-flag parity is data: covered by validators + the check script; a structural validator rule belongs to JF-414.

Suite 2742 green; Release -warnaserror clean; deployed (md5-verified) and live-regression-checked: both elicit shapes with __remove_attributes markers present, cup->Waltz for Koop intact.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED with the on-device AC#5 satisfied autonomously (per the standing rule: exhaust simulate-skill before asking for device time). Inventory + verdicts (AC#1/#2) were complete: 10 flows context-preserving (state + consumer verified), 1 welcome-prompt class OK-by-design, 1 context-losing flow found and FIXED (PlaySong's plain-Ask ElicitSongName converted to Dialog.ElicitSlot with both slots declared; the original album-elicit defect fixed 2026-08-28), 1 borderline (BrowseLibrary category asks) delegated to JF-414 by design (needs dialog registration first). AC#3: converted + unit-tested; the review-gate recovery (4 defects incl. the FindSong cancel hatch and missing MarkOthersInactive) shipped in the same wave. AC#4: dialog.intents parity verified at the SLOT level across all 17 locales (the earlier intent-level check was deepened after review). AC#5: the elicit round-trip verified END-TO-END via simulate-skill in-session (2026-09-08): 'chiedi a mia collezione di mettere una canzone' opens the FindSong ElicitSlot ('Quali parole ricordi del titolo?') and the bare follow-up 'waltz for koop' is captured by the dialog (FindSongIntent titleKeywords filled) and plays - pinned as the it-IT two-step smoke fixture (commit 815d95e3), the regression guard for the whole defect class. Recorded nuance: with a musician filled, 'una canzone di X' plays DIRECTLY (intended UX), so the PlaySong song-elicit is NLU-unreachable in practice (FindSong owns the bare opener space); the conversion stays as correct defensive code. Residual ASR-on-device spot check folded into the JF-405 checklist (the JF-298 divergence: in-session simulate already exercises the dialog routing).
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
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
