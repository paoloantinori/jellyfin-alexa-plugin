---
id: JF-423
title: >-
  FindSong flow defects: cancel-word swallows legitimate title searches (missing
  IN_PROGRESS guard) + single-candidate 'found multiple' prompt
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
updated_date: '2026-08-31 17:21'
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
- [ ] #1 Cancel-word check requires an in-progress dialog (session data + DialogState IN_PROGRESS or equivalent guard) so a mid-flow legitimate search for a cancel-word title searches instead of cancelling
- [ ] #2 CancelWords covers the locales that can hit the flow, or the gap is documented in the code with a follow-up (17-locale audit result recorded)
- [ ] #3 Unit tests: (a) open FindSong flow + titleKeywords='basta' searches; (b) genuine cancel ('basta' with no other content) still cancels; (c) first-invocation search for a cancel-word title still works
- [ ] #4 FindSong disambiguation: when name-dedup (JF-416) collapses candidates to ONE, the response speaks the single-found wording, not 'found multiple' with a 1-item list
- [ ] #5 Cancel-word detection inspects ALL slots of the incoming request (like PlayAlbum:111/PlaySong:160), so a cancel word captured into musician (or any other slot) during a force-routed FindSong session still cancels instead of being searched as an artist
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-08-31 second code-review pass added a third aspect + AC: the cancel hatch reads only titleKeywords; PlayAlbum/PlaySong check BOTH slots and gate on DialogState. During an open FindSong artist elicitation, 'annulla' routed to another intent with musician='annulla' is force-routed back to FindSongIntentHandler (FindSongSessionData present), the hatch sees titleKeywords=null, HandleAwaitingArtistAsync falls back to the musician slot, searches artist 'annulla', not-found, re-prompts: the elicitation-trap loop the hatch was meant to close.
<!-- SECTION:NOTES:END -->

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
