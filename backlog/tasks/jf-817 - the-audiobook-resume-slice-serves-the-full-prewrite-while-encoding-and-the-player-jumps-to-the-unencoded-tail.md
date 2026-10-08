---
id: JF-817
title: >-
  JF-817 - the audiobook resume slice serves the FULL prewrite while the encode runs,
  and the player jumps to the un-encoded tail (the JF-778 defect class on the resume path)
status: Done
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
- [x] #1 dotnet build passes with 0 errors (Release `-warnaserror`, both TFMs: 0 warnings, 0 errors)
- [x] #2 dotnet test passes (full suite 5517/5517 on net10.0 AND net9.0; baseline 5511 + 6 new pins)
- [x] #3 No new compiler warnings introduced (Release `-warnaserror` clean; plugin csproj carries TreatWarningsAsErrors)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model changes)
- [x] #7 E2E test added for new intent or handler logic (server-side endpoint pins per the task's own verification bar: 5 new pins in VideoAudioControllerTests + 1 constants pin in VideoAudioControllerPureTests, all exercising the real StreamHlsAudiobook endpoint against planted live-encode fixtures; no new intent/handler exists)
- [x] #8 Locale response strings added to all 17 locales (N/A: no locale/speech changes)
- [x] #9 /simplify passed (4 parallel angles; applied: test-arrange consolidation + episode-locals idiom; skips recorded in JF-818)
- [x] #10 /code-review high passed (6 findings: 2 confirmations of the already-filed JF-780 residuals; 4 filed same-turn as JF-818 sub-item 3, JF-819, JF-820, and the JF-780 third-residual extension; none block the shipped fix)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINALSUMMARY:BEGIN -->
Implemented 2026-10-08 (worktree worker, commits 2babcd8e red pins ->
4defc6dd fix -> 22b40c7a simplify -> final record).

THE FIX: the two during-encode audiobook prewrite rows of StreamHlsAudiobook
(the concurrent-encode guard and the first-fetch tail, the 20:35 incident's
own row per its log line) now serve through ServeWindowedAudiobookPrewriteAsync:
the served listing BEGINS at the resume segment (inside the honor band) AND
CAPS at the growing window edge max(floor=2, min(head+1, elapsed+lead=3)) at
the audiobook 10s calibration, computed by the ONE shared pair
ComputePrewriteWindow + ResumeInsidePrewriteHonorBand extracted from the
JF-778 episode block (its EXTRACTION TRIGGER fired; the episode path is
byte-identical in behavior, all its pins green). Outside the honor band the
offset drops with a log (the JF-780 decisions applied unchanged). The
completed-encode ENDLIST cache-hit serve keeps today's full-listing slice
exactly (companion pin). One full playlist read per serve kept (the content
threads through as the serve's preloaded payload). The served slice's clock
stays relative to the resume point (the documented limitation, unchanged).

RED PROOF (server-side, run on the unmodified serve before the fix, net10.0,
output in commit 2babcd8e): a planted live encode (3036-segment prewrite,
head at seg_0022, prewrite aged 200s) requested with ?start=210s (segment
21) served 3015 entries (the full remaining prewrite, seg_3035 present) ->
the core pin failed expected 2 / actual 3015; MidWindowResume failed 63 /
3015; GrowthAndCapsAtHead and FirstFetch failed 2 / 3036. Post-fix all four
pass: the resume serve is exactly seg_0021..seg_0022 (MEDIA-SEQUENCE:21, no
ENDLIST), the window grows with the prewrite age and caps at the encoded
head, and the first fetch serves a 2-entry window of the 3036-entry book.
Companion pins green: the completed encode serves the full listing sliced at
segment 21 (seg_0021..seg_3035, ENDLIST intact), and the floor relation
(floor - 1 <= SegmentHoldLookahead) is pinned for the audiobook family.

VERIFICATION: touched classes green on both TFMs (VideoAudioControllerTests
179/179, PureTests + builder 144/144); full suite 5517/5517 on net10.0 and
net9.0; Release -warnaserror clean. The JF-778 episode pins stayed green
throughout (the episode path's only change is the behavior-preserving
extraction).

GATES: /simplify (4 angles; applied the test-arrange consolidation and the
episode-locals idiom alignment; the orchestration hoist and two minor items
skipped with reasons, filed as JF-818). /code-review high (6 findings, none
blocking: the two JF-780 residual confirmations; the song-family unwindowed
prewrite with single-chapter audiobook resumes reachable -> JF-819; the
pre-existing foreign-generation stale-prewrite guard row -> JF-820; the
non-vanish PhysicalFile degrade arm -> JF-818 sub-item 3; the
completion-handoff base flip -> JF-780 third residual). The drop log's
"serving from the beginning" wording kept deliberately: it states what the
server does (the listing begins at segment 0), mirroring the episode family's
pinned wording.

DEVICE NOTE: the residual set now on file (JF-780's three shapes on the
audiobook path, plus JF-819's single-chapter path) means the next device
round should probe: a cold-cache BOOK resume minutes deep (expect
start-at-0 during the encode, then the completion handoff), and the same on
a SINGLE-FILE book (still unwindowed, JF-819).
<!-- SECTION:FINALSUMMARY:END -->
