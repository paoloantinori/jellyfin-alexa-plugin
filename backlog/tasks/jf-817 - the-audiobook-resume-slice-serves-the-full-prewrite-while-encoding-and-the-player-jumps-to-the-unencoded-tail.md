---
id: JF-817
title: >-
  JF-817 - the audiobook resume slice serves the FULL prewrite while the encode runs,
  and the player jumps to the un-encoded tail (the JF-778 defect class on the resume path)
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - bug
  - audiobooks
  - hls
  - 1.0-blocker
dependencies:
  - JF-778
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found live by Paolo's device round (2026-10-08 20:35, log-verified). The exact JF-778
defect class, on a path JF-778 did not cover: the AUDIOBOOK resume while the encode
is still running.

**The evidence**: "chiedi a mia collezione di mettere il libro di upside of
irrationality" routed correctly (the JF-816 di-connector fix), the fuzzy search matched
the ASR-garbled title ("upside of your rationality" -> Dan Ariely - The Upside to
Irrationality), the deep resume resolved start=210s (segment ~21), VideoApp.Launch
fired with the sliced URL. The first segment fetch (seg_0022) was served via the
GetSegment hold (appeared after 214ms). THEN the device requested seg_3055.ts, the
LAST segment of the 3056-segment book, and kept retrying it while the encode head was
at 21 -> 28 -> 44 -> 80. No playback. (The log: "serving pre-written playlist for
parent e9720b84 (100 chapters)" then the GetSegment misses on seg_3055.)

**The mechanism**: the resume slice rewrites the playlist to BEGIN at the resume
segment (~seg_21) but serves the ENTIRE remaining prewrite (3035 entries, no ENDLIST).
ExoPlayer resolves a no-ENDLIST playlist's start at playlist end minus 3x
TARGETDURATION (the media3 live-edge formula, the same one from the JF-778 incident)
and probes the tail. seg_3055 is hours of encode away; the player gives up.

**The fix shape (mirror JF-778's windowing onto the resume slice)**: while the encode
is live, the resume serve must cap the listing to the encoded region: begin at the
resume segment (the slice semantics) AND cap at the growing window edge
(K = max(floor, min(head+1, elapsed+lead))), growing as the encode advances. When the
encode completes (ENDLIST), the serve is today's full listing. The episode prewrite
serve already does this (JF-778's TruncateToFirstSegments + the mtime-anchored
window); the audiobook resume path needs the same treatment, sharing the ONE
windowing helper rather than a second copy.

**Verification bar**: the red shape is reproducible server-side without a device:
cold audiobook cache, request the playlist with ?start=<ticks for segment ~21>, read
the served listing, it must contain only encoded-region segments (today it lists all
3035). Companion pins: completed encode serves the full listing; the resume slice
still begins at the resume segment (not segment 0); the JF-778 episode pins stay
green.
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
