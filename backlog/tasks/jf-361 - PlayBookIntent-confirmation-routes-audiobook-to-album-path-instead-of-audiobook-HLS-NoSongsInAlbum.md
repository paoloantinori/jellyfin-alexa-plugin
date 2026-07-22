---
id: JF-361
title: >-
  PlayBookIntent confirmation routes audiobook to album path instead of
  audiobook HLS (NoSongsInAlbum)
status: To Do
assignee: []
created_date: '2026-07-22 12:16'
labels:
  - bug
  - playback
  - audiobook
  - handler-routing
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
DISCOVERED 2026-07-22 during pre-release testing.

PlayBookIntent finds the audiobook "Option B" (3 results, disambiguation carousel shows correctly), but when the user says "yes" to confirm, the handler responds "Non ci sono canzoni nell'album Option B" — it treats the audiobook item as a MUSIC ALBUM and searches for songs/tracks inside it, instead of routing to the audiobook HLS concat path (StreamHlsAudiobook via GetAudiobookVideoAudioUrl).

LOG EVIDENCE (2026-07-22 14:13:36-49):
- PlayBookIntent received "option B" → found 3 AudioBook items (ids 54917d92, e12e3694, cda5813e) → disambiguation prompt
- User said "yes" → YesIntent → handler responded "Non ci sono canzoni nell'album Option B: Facing Adversity..."
- The response mentions "canzoni nell'album" (songs in the album) — this is the NoSongsInAlbum string, not an audiobook error. The handler fell into the album playback path instead of the audiobook path.

ROOT CAUSE HYPOTHESIS: PlayBookIntentHandler (or YesIntentHandler when resuming a PlayBook disambiguation) may be routing to BuildAudioPlayerResponse / BuildArtistSongsResponseAsync instead of the audiobook VideoApp.Launch path. The audiobook items ARE found as AudioBook type, but the confirmation/YesIntent handler doesn't check the item type or doesn't route to StreamHlsAudiobook.

NOTE: This is NOT a JF-309 (token) issue — the request never reached the video-audio endpoints. It's a handler routing bug in the PlayBook confirmation path.

Items in the library: 3 copies of "Option B: Facing Adversity, Building Resilience, And Finding Joy" (AudioBook type, ids 54917d92-eaa6-8605-c3fe-9897477c426b, e12e3694-99db-24a5-2222-4386ac6d0f0a, cda5813e-1611-6b46-a469-284f1ab736d9). ParentId: b98c5b595df212c59d864c2838256c72.

Acceptance criteria:
- PlayBookIntent → disambiguation → "yes" on an AudioBook item launches the audiobook HLS concat path (VideoApp.Launch with GetAudiobookVideoAudioUrl), NOT the album/song playback path.
- Unit test covers the PlayBook + YesIntent confirmation path for an AudioBook item type.
- The "Non ci sono canzoni nell'album" response never appears for AudioBook items.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
