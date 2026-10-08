---
id: JF-819
title: >-
  JF-819 - the single-item (song-family) HLS prewrite serve is still UNWINDOWED: a
  single-chapter audiobook resume during a cold encode hits the JF-817 live-edge death
status: In Progress
assignee: []
created_date: '2026-10-08'
labels:
  - 1.0-blocker
  - bug
  - audiobooks
  - hls
dependencies:
  - JF-778
  - JF-817
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-817 /code-review high gate (finding 1, not covered by any
existing task; JF-818's "informational" note about this serve mischaracterized
a reachable audiobook resume path, amended same-turn).

JF-778 windowed the EPISODE prewrite serve; JF-817 windowed the AUDIOBOOK
CONCAT prewrite rows. The THIRD prewrite family, the single-item (song) serve
(`TryServePrewrittenVideoAudioPlaylist` via `TryServeOwnLiveVideoAudioPrewriteAsync`
and the first-fetch tail), still hands out the FULL un-windowed no-ENDLIST
prewrite, resume-sliced at `?start=`: exactly the JF-817 incident shape.

REACHABLE AUDIOBOOK PATH: `StreamHlsAudiobook` redirects a SINGLE-chapter book
(the one-file audiobook shape, and the JF-794 census found plenty) into
`StreamHlsVideoAudioCore`, threading the resume position (JF-686 made that
work). A deep resume (hours) on a cold cache of an hours-long single-file
book (above the prewrite runtime threshold; the project's own comment says
these encode "for minutes, not seconds") serves the full listing sliced at
the resume segment with no ENDLIST: the player joins at playlist end minus 3x
TARGETDURATION on the un-encoded tail and dies, verbatim the 2026-10-08
20:35 incident on a supported audiobook shape.

WHY NOT FIXED IN JF-817: the fix must ride the shared song-family core, and
TODAY'S SONG BEHAVIOR IS PINNED DELIBERATE: the JF-675 song pin asserts the
full prewrite serves mid-encode (seg_674 of a 45min/4s listing present), and
the JF-680 song twin pins the own-live prewrite row. Windowing the family
flips those pins, i.e. re-decides device-verified behavior for songs
(seconds-long encodes, where the window closes almost immediately and the
cost/benefit differs from books) and for single-chapter audiobooks (minutes,
where the window matters). That re-decision is the maintainer's, not a
drive-by in a blocker fix.

FIX SHAPE: mirror the JF-817 serve (the shared `ComputePrewriteWindow` +
`ResumeInsidePrewriteHonorBand` pair, song-calibrated constants at
SongHlsSegmentSeconds=4) onto `TryServePrewrittenVideoAudioPlaylist`, then
UPDATE the JF-675/JF-680 song pins to the windowed expectations and verify a
short song still serves ~its full listing once the encode completes
(ENDLIST row unchanged). Consider whether the 4s-segment lead should differ
for the single-file-book shape.
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

GATE-MARKER PROMOTION (2026-10-08, the JF-817 marker F1): promoted into the 1.0 milestone with the blocker label. The marker's judgment: JF-817 is a 1.0-blocker for this same death on the concat path, so shipping 1.0 with the death alive on the single-file book shape (common per the JF-794 census) is the headline bug half-fixed; the 'maintainer must re-decide the pinned JF-675/JF-680 song behavior' argument is a reason to force that decision before release, not to leave the flag off. The scope note stands: the song-family prewrite serves are PINNED full-listing by JF-675/JF-680, so the fix must re-decide those pins deliberately, not drive-by flip them.
