---
id: JF-445
title: >-
  Verify and fix: force-routed sibling-intent cancel words likely arrive
  dialogState=STARTED, making the JF-423 all-slots hatch inert in its target
  misroute regime
status: In Progress
assignee: []
created_date: '2026-09-01 22:27'
updated_date: '2026-09-03 03:42'
labels:
  - code-review
  - dialog
  - verification
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:180'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:414'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Review finding from the JF-423 gates (2026-09-02, three angles + one verifier, evidence conflicting): the all-slots cancel hatch (AnySlotIsCancelWord) is gated on DialogState==IN_PROGRESS, but the misroute regime it was written for (JF-423 AC#5: 'annulla' resolved to a sibling intent with musician='annulla', force-routed back by AlexaSkillController:414) likely arrives with that sibling intent's just-STARTED dialog state, not IN_PROGRESS - so the conjunction may never fire on real traffic and the trap loop the AC claims closed stays open. Counter-evidence: JF-411 closure records IN_PROGRESS mid-flow branches as simulator-verified, and JF-422 records captured elicit replies arriving IN_PROGRESS (same-intent captures). The unit test fabricates IN_PROGRESS so it cannot detect this. The added log line records dialogState, making on-device confirmation cheap. NOTE the design tension to resolve in the fix: widening to STARTED risks false-cancelling multi-word searches naming real artists whose name IS a cancel word (the band 'Basta'); a bare-word guard on the slot value resolves it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Gather evidence from podman logs: the JF-423 hatch log line now records dialogState for every force-routed FindSong request during open sessions; on-device or simulate-skill, trigger a sibling-intent misroute ('annulla' resolving to PlaySongIntent during an open FindSong artist elicit) and read the actual dialogState (expected STARTED per the JF-411 on-device observation for fresh sibling resolutions)
- [ ] #2 If STARTED confirmed: extend the hatch gate to accept the force-routed shape (sessionData present + cancel word in a NON-primary slot + dialogState IN_PROGRESS-or-STARTED), keeping the Basta-band false-positive guard (a multi-word utterance naming a real artist must still search - the cancel must be a BARE cancel word, single-token slot value)
- [ ] #3 If IN_PROGRESS observed instead: close this task with the evidence, the current gate already covers it
- [ ] #4 Unit tests for whichever shape lands
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
- [ ] #10 /code-review high passed (no blocking findings remaining)
- [ ] #11 Findings applied or tracked
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
AC#1 resolution (review gate 2026-09-03, finding 3): the evidence basis for the STARTED
widening is the live-fetched Alexa Dialog Interface Reference contract, NOT a live
misroute capture. Under manual dialog control dialogState is STARTED when the intent is
invoked, IN_PROGRESS mid-dialog, and COMPLETED only under delegation. The JF-411
on-device record is the adjacent half only (a fresh invocation of a dialog-registered
intent arriving STARTED); it is NOT the misroute shape itself. Zero hatch log lines in
48h of minix logs, so no on-device confirmation of the misroute's dialogState exists.
The widening therefore rests on the docs contract plus fail-safe behavior in both
directions: a misroute that arrives IN_PROGRESS was already covered by disjunct 1 of
the hatch (unchanged), and null/COMPLETED behavior is unchanged. On-device confirmation
remains open: the dialogState log line stays in place, and a future on-device 'annulla'
misroute reading closes it definitively.

Bare-artist sacrifice, recorded and pinned (review gate 2026-09-03, finding 1): a
mid-flow bare single-word search for an artist named exactly a cancel word (the band
"Basta") now cancels instead of searching; recovery is one turn (the user asks again).
The shape is string-indistinguishable from the misroute the widening must catch, so this
is the same class of deliberate trade-off as the JF-377 prompt-not-reject design. Pinned
by FindSongIntentHandlerTests.ForceRoutedSiblingMisroute_StartedDialog_BareCancelWordArtistName_Cancels.

Fresh-regime vocabulary trim, recorded (review gate 2026-09-03, finding 2): the STARTED
leg of CancelWords.IsForceRoutedCancelCapture consults a narrower per-locale
'probed bare words' set (ProbedBareWordsByLocale) instead of the locale's full set. For
it-IT that set is exactly the single-token probe-table rows with a routed Stop/Cancel
intent: ferma, annulla, basta, cancella, annullare, stoppa. The legacy single-token
words without a routing row (fermo: ShowMoreIntent; fermare and arresta: NO_SELECTION;
stop and cancel: no it-IT probe row) no longer cancel a fresh sibling request; the
IN_PROGRESS leg keeps the full legacy set (JF-423 live evidence). Every other locale's
set was probe-vetted word-by-word by JF-444, so those locales are unchanged (their
multi-word entries were already inert under the bare guard).

Recorded residuals (review gate 2026-09-03, finding 2): (a) hi-IN residual: the hi-IN
cancel phrase "बंद करो" is two tokens, so the fresh-regime bare guard keeps it inert;
a hi-IN STARTED misroute carrying that phrase in a sibling slot does not cancel (the
flow's own not-found path handles it; no probe-vetted trim entry was needed for hi-IN
because its set is already probe-vetted). (b) Sibling intents absent from the models'
dialog.intents arrays (PlayArtistSongs, AddToQueue, PlayNext, QueryArtistLibrary) carry
no dialogState at all, so the STARTED predicate never fires for them and those misroute
shapes stay uncovered.

Next-touch batches, tracked here (review gate 2026-09-03, finding 4; skipped in this
batch, do on the next touch of these files): (a) comment dedup: the elicitation-trap
escape-hatch explanation is now maintained in four copies (FindSongIntentHandler,
PlaySongIntentHandler, PlayAlbumIntentHandler, and the CancelWords helper docs);
consolidate toward CancelWords as the single source. (b) expected-speech test helper:
the cancel-word tests repeatedly fetch ResponseStrings.Get("FindSongCancelled", locale)
and compare against TestHelpers.GetSpeechText; add a shared assert-speaks /
assert-does-not-speak helper in TestHelpers to dedupe.
<!-- SECTION:NOTES:END -->
