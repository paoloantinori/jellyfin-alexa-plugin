---
id: JF-430
title: >-
  Triplicated elicit-response builders + third literal copy of the disambig
  session-attribute keys (flagged twice, never tracked)
status: To Do
assignee: []
created_date: '2026-09-01 06:05'
updated_date: '2026-09-01 11:20'
labels:
  - code-review
  - cleanup
  - dialog
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs:487
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs:436'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FindSongIntentHandler.cs:900'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:460
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Cut-at-the-cap cleanup flagged by TWO code-review passes (2026-08-31 pass 1 and pass 3) and never filed until the 2026-09-01 audit. Three near-identical elicit-response builders exist across PlayAlbumIntentHandler (~line 487), PlaySongIntentHandler (~:436), FindSongIntentHandler (~:900); and the disambiguation session-attribute keys are hand-assembled in a THIRD literal copy inside the JF-420 branch of PlayArtistSongsIntentHandler (flagged in pass 3's cut list). Consolidation target: one elicit builder + one disambig-attribute writer, both shared.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The three elicit-response builders (PlayAlbum ~:487, PlaySong ~:436, FindSong ~:900) collapse into ONE shared builder (BaseHandler or Util helper), parameterized on slot name/prompt/re-prompt
- [ ] #2 The disambig session-attribute literal keys ('disambig_matches'/'disambig_index'/'disambig_type' + ConversationalFlows.MarkOthersInactive) have a single definition; the JF-420 branch uses it too
- [ ] #3 No behavior change: existing dialog-flow tests stay green unchanged
- [ ] #4 Coordinate with JF-420.2 (same code region: it reworks the numbered prompt; land order decided there)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-01 update from the JF-420.2 simplify pass: the keys themselves are now single-defined (JF-420.2 routed all 3 writer sites through DisambiguationHelper.Attr* constants), but the DICTIONARY BUILD + MarkOthersInactive ritual is still triplicated (DisambiguationHelper.BuildAttributes is private with no extra-entries param; PlayArtistSongs:534 + BaseHandler:1724 are token-identical, BaseHandler:1550 adds 2 crossmedia keys). This task's remaining scope = promote BuildAttributes to internal with an optional extraEntries param and collapse the three sites. Also noted by the altitude agent: crossmedia_notfound_query/type remain a 2-site literal family (BaseHandler:1558 + NoIntentHandler:92) - fold into this cleanup.
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
