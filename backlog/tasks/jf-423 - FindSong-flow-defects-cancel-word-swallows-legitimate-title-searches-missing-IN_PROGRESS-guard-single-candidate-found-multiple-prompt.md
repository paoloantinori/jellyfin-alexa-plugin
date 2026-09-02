---
id: JF-423
title: >-
  FindSong flow defects: cancel-word swallows legitimate title searches (missing
  IN_PROGRESS guard) + single-candidate 'found multiple' prompt
status: Done
assignee:
  - zai
created_date: '2026-08-31 15:02'
updated_date: '2026-09-01 22:44'
labels:
  - code-review
  - dialog
  - findsong
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:175'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:605'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/CancelWords.cs:15'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two code-review findings (2026-08-31, high effort) on the FindSong flow, same file, batched as one work order.

1. Cancel-word swallow (CONFIRMED). FindSongIntentHandler.cs:175: the cancel-word escape hatch gates on sessionData != null without the DialogState=='IN_PROGRESS' guard its sibling handlers deliberately carry. During an open FindSong elicit, a legitimate title search that IS a cancel word ('trova la canzone basta', or the song 'Stop' by Sam Brown / Spice Girls) returns 'Okay, I've stopped searching' instead of searching. Also CancelWords (CancelWords.cs:15) covers only Italian/English out of 17 locales: a de-DE 'stopp' during an open elicit in PlaySong/PlayAlbum gets a not-found instead of a cancel.

2. Single-candidate 'found multiple' prompt (lower-ranked finding). FindSongIntentHandler.cs:605: the JF-416 name-dedup (GroupBy name) can collapse the 1-4 candidate list to a single entry, yet the response still uses the FindSongFoundMultiple wording with a 1-item {1} list. Should branch to the single-found wording when dedup leaves one candidate.

FIX SHAPE: mirror the sibling handlers' IN_PROGRESS guard for the cancel check; audit/extend CancelWords per locale or document the supported subset; branch the disambiguation wording on post-dedup count.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Cancel-word check requires an in-progress dialog (session data + DialogState IN_PROGRESS or equivalent guard) so a mid-flow legitimate search for a cancel-word title searches instead of cancelling
- [x] #2 CancelWords covers the locales that can hit the flow, or the gap is documented in the code with a follow-up (17-locale audit result recorded)
- [x] #3 Unit tests: (a) open FindSong flow + titleKeywords='basta' searches; (b) genuine cancel ('basta' with no other content) still cancels; (c) first-invocation search for a cancel-word title still works
- [x] #4 FindSong disambiguation: when name-dedup (JF-416) collapses candidates to ONE, the response speaks the single-found wording, not 'found multiple' with a 1-item list
- [x] #5 Cancel-word detection inspects ALL slots of the incoming request (like PlayAlbum:111/PlaySong:160), so a cancel word captured into musician (or any other slot) during a force-routed FindSong session still cancels instead of being searched as an artist
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-08-31 second code-review pass added a third aspect + AC: the cancel hatch reads only titleKeywords; PlayAlbum/PlaySong check BOTH slots and gate on DialogState. During an open FindSong artist elicitation, 'annulla' routed to another intent with musician='annulla' is force-routed back to FindSongIntentHandler (FindSongSessionData present), the hatch sees titleKeywords=null, HandleAwaitingArtistAsync falls back to the musician slot, searches artist 'annulla', not-found, re-prompts: the elicitation-trap loop the hatch was meant to close.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
JF-423: the FindSong cancel hatch guards legitimate searches, cancels in any slot, and the dedup collapse no longer silently plays the wrong recording.

WHAT CHANGED (commit 0e509d1)
- Cancel-word escape hatch: the IN_PROGRESS dialog guard added (the PlaySong/PlayAlbum sibling idiom): a mid-flow remount searching a cancel-word TITLED song ('trova la canzone basta', 'Stop') searches; a genuine captured cancel still cancels, now in ANY slot (the all-slots predicate, force-routed 'annulla' in musician included).
- Consolidation (review round, three findings): AnySlotIsCancelWord moved to CancelWords (its declared home) with IsDialogInProgress beside it; the three drifted hatch copies unified; PlaySong/PlayAlbum switched from their two-named-slot ORs to the shared all-slots predicate, closing their latent sibling-slot gap.
- Single-candidate wording: a dedup collapse from MULTIPLE candidates (different recordings sharing a title, 'Stop' by two artists) now PROMPTS (keeping the negative-answer exit) instead of silently auto-playing one and announcing 'di Unknown'; a true single auto-plays with FindSongFoundOne (FoundOne local function, one definition).
- CancelWords: +3 it-IT StopIntent phrases; the 11-of-17 locale gap documented in code and FILED as JF-444 (locale-keyed vocabulary + profile-nlu vetting).
- FILED JF-445: the force-routed sibling-intent dialogState question (STARTED vs IN_PROGRESS) - the review found the hatch may be inert in exactly that regime, but the evidence conflicts (JF-411/JF-422 records) and the hatch log line now records dialogState, making on-device confirmation the right next step; redesigning the gate on speculation was rejected overnight.

VERIFICATION
- Tests: 5 cancel-hatch cases (mid-flow-not-in-progress searches; open-flow cancels incl. musician-slot; first-invocation searches) + the dedup pair (collapse-from-multiple prompts; true single plays). FindSongIntentHandlerTests 92. Suite 2823/2823; Release 0 warnings.
- Gates: /simplify (implementer) + code-review high (combined pass: the dedup-silent-play bug and the hatch consolidation BOTH from its findings; remainder filed JF-444/445).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [x] #6 NLU test fixtures updated if interaction model changed
- [x] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
