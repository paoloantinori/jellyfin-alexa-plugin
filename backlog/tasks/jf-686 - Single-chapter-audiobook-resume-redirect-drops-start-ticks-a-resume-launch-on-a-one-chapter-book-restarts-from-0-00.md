---
id: JF-686
title: >-
  JF-686 - single-chapter audiobook resume redirect drops ?start=, a resume
  launch on a one-chapter book restarts from 0:00
status: Done
assignee: []
created_date: '2026-09-30'
labels:
  - audiobooks
  - resume
dependencies: []
references:
  - >-
    backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn from the JF-682 code-review round (high effort, finding 3 of 5); PRE-EXISTING gap, untouched by JF-682's diff (the review surfaced it inside the rewritten branch and JF-682's mandate was the empty-secret 503 only).

THE GAP: `PlaybackLaunchBuilder.BuildAudiobookResumeResponse` mints `audiobook/{parentId}/stream.m3u8?start=<ticks>` whenever the resolved position is positive (ResumeIntentHandler and YesIntentHandler both route through it), and `StreamHlsAudiobook` binds `startTicks` and slices the MULTI-chapter playlist via `ServeAudiobookPlaylistAsync`. But the SINGLE-CHAPTER branch calls `StreamHlsVideoAudioCore(chapterId, chapterToken)` and drops `startTicks`: the song core serves the full unsliced playlist and has no `?start=` handling of its own, so a "resume from 20 minutes" on a one-chapter book (or any book that resolves to a single child) plays from the beginning while the device's seek bar shows a fresh timeline. The user-visible shape is a resume that silently restarts, exactly the class the sliced-playlist mechanism (JF-499 W2) was built to remove for multi-chapter books.

FIX DIRECTION: thread the resume offset into the single-chapter serve the way the episode path does (`ServeEpisodePlaylistAsync` slices + serves): either a start-aware serve for the redirected core (slice the song-core playlist at the offset using the same `AudiobookPlaylistBuilder.BuildResumePlaylist` mechanics with the song core's segment duration) or route single-chapter books through the same slicing helper. Needs a red pin first (resume launch on a one-chapter book must serve a sliced playlist whose first EXTINF starts at/below the offset), plus a device check that the song core's 3-digit/10s segment geometry slices correctly.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no intent/handler change; controller serve logic pinned by the two new unit pins)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings changed)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Fix notes (worker, 2026-10-01)

CHOSEN SHAPE: controller-side threading, the episode path's exact precedent (`StreamHlsEpisodeCore(itemId, startTicks)`). The single-chapter branch of `StreamHlsAudiobook` now calls `StreamHlsVideoAudioCore(chapterId, chapterToken, startTicks ?? 0)`, and the core's four serve return paths (fast-path validated, concurrent validated, prewrite-during-encode, live-partial fallback) all route through a new slice-aware serve. The /simplify round consolidated that serve with its episode twin into ONE shared body, `ServePlaylistSlicedAsync(path, startTicks, segmentSeconds, overrideToken, preloadedContent)`, with `ServeEpisodePlaylistAsync` and `ServeVideoAudioPlaylistAsync` reduced to named one-line delegates binding `EpisodeHlsSegmentSeconds` / `SongHlsSegmentSeconds`. FACT CORRECTION to the task text: the song core cuts 4s segments (`SongHlsSegmentSeconds = 4`), not the 10s the description guessed; the delegate binds the actual const, and the slicer's EXTINF accumulation makes the flat divisor a fallback only (real per-segment durations win, so the geometry question shrinks to the on-device probe below).

REJECTED ALTERNATIVES:
1. Builder-side AudioPlayer switch (detect one chapter in `BuildAudiobookResumeResponse`, emit `AudioPlayer.Play` + offsetMs): rejected; the builder has no chapter count without a new library query on every resume, AudioPlayer forfeits the Echo Show seek bar (the point of the VideoApp HLS path), and the static flat stream bypasses the HLS segment-request position tracker.
2. Encode-time offset (`-ss` in the song core for redirected books): rejected; a new start-scoped cache-key dimension and a full second encode per resume position, exactly what the sliced-playlist mechanism (JF-499 W2) exists to avoid.
3. Slice the ActionResult at the redirect: rejected; the redirect receives an opaque result over four serve paths, so a wrapper would re-read the playlist file itself, losing the JF-677 verdict-preload reuse and the vanish-to-re-encode translation.
4. Bind `?start=` inside the shared `ServePlaylistWithTokenAsync` funnel: rejected; it would silently give resume semantics to routes that have none (the song route, the variant live serve). Explicit threading keeps the no-position serves byte-identical (pinned by the no-start control).

PINS (VideoAudioControllerTests, beside the JF-682/JF-685 twins): `StreamHlsAudiobook_SingleChapterResume_Start_ServesSlicedPlaylistAtOffset` (one chapter, completed 12x4s playlist, start=40s, asserts MEDIA-SEQUENCE:10 + seg_010?token= kept + seg_000..009 dropped + ENDLIST preserved; RED proof run: with the redirect reverted the full unsliced playlist answers and exactly these asserts flip) and `StreamHlsAudiobook_SingleChapterResume_NoStart_ServesUnslicedPlaylist` (the no-overblock control: no `?start=` keeps the pre-fix byte shape; green before AND after).

GUARD CLASS: the launch shape is UNCHANGED (still the token-gated `audiobook/{parentId}/stream.m3u8?start=<ticks>` URL the builder mints), so the new serve stays in the token-gated-HLS class and the JF-687 refusal pins need no new member; verified by the 11 `PlaybackLaunchBuilderStreamTokenSecretTests` + the JF-682 redirect-503 pin + the two `BuildAudiobookResumeResponse` pins all green (15/15 focused run), and the sliced playlist carries the re-minted chapter token (`seg_010.ts?token=` asserted).

CODE-REVIEW (high) OUTCOME: 3 findings. Finding 1 (dominant) CONFIRMED by code reading and FILED as JF-694: the serve-side drop this task filed was real but LATENT, because the tracker write gate is Folder-only while the redirect keys segments by the chapter leaf, so every mint site sees a permanently cold tracker for one-chapter books and never mints ?start=>0; the user-visible one-chapter restart persists at the tracking layer and needs its own design round (the JF-625 MusicAlbum interplay rules out a drive-by). This diff stands: the serve must honor the contract of its own URL, and the fix unblocks any future position source. Finding 2 (overrun ?start= slices to a zero-segment playlist) was ATTEMPTED and REVERTED: the obvious chokepoint guard contradicts two existing green pins of the flat-divisor fallback's zero-keep shapes, so it is filed inside JF-694 with the design constraint spelled out. Finding 3 (doc triplication) was already satisfied by the /simplify consolidation: mechanics live once on `ServePlaylistSlicedAsync`; the remaining restatements are the pre-existing episode doc and standard param docs.

RESIDUAL: the on-device probe the description asks for (song core's 3-digit segment geometry slicing on a real Echo Show) is NOT done by this worker; the slicer is the live-verified multi-chapter mechanism and now reads actual EXTINF durations, but the maintainer's next device round should confirm a one-chapter book resume on hardware (and will hit the JF-694 tracking gap first: the serve is ready, the position never arrives).

## Rework round (orchestrator gate-marker, 2026-10-01)

Six findings, all landed in commit 2 of this task:
- F1 (NEW FAILURE MODE, applied): the live-partial fallback (cold-cache serve after a skipped prewrite) sliced the STILL-GROWING playlist: start past the partial's total kept ZERO segments and killed the player, where pre-JF-686 the same request served the full live playlist and merely restarted at 0:00. Fixed by dropping the offset on that fallback (startTicks forced to 0), mirroring the album twin's cold-serve, with the Information log naming the drop; the same guard rides the two validated serve closures via the verdict's own signal (a null threaded Content is the own-live growing row, so those serve full too). Pinned by StreamHlsAudiobook_SingleChapterResume_ColdCacheLivePartial_ServesFullUnsliced (RED proof run: fallback reverted to passing startTicks, the served playlist lost both segments, exactly the death shape).
- F2 (contract direction, documented + locked): the JF-499 P2 EXTINF walk serves the FIRST segment whose cumulative start is >= the offset, i.e. it rounds UP (at-or-after), skipping up to one segment of unheard audio; the original pin comment wrongly said "at/below". The comment is corrected, and StreamHlsAudiobook_SingleChapterResume_Start_MidSegment_RoundsUpToNextSegmentBoundary (start=41s over 4s segments, serves seg_011 with true start 44s) LOCKS the direction deliberately: it is shared with the live-verified multi-chapter and episode paths, and the tracker's conservative (highWaterMark-1) under-report is the re-hear margin on the other side. Switching to round-down would flip the pin and needs its own design round.
- F3 (RFC 8216, applied): the no-tag insert fallback put the synthesized #EXT-X-MEDIA-SEQUENCE at index 0, BEFORE #EXTM3U; the insert now lands immediately after the EXTM3U line (leading insert kept only for a base with no EXTM3U at all). Pinned by BuildResumePlaylist_Sliced_NoSequenceTag_InsertsAfterExtm3u (RED proof run: insert reverted to index 0, the assert flipped).
- F4 (compile safety, applied): startTicks is REQUIRED on TryServePrewrittenVideoAudioPlaylist and TryServeOwnLiveVideoAudioPrewriteAsync (the core passes its own value explicitly; the core's own optional default stays for the public route and the JF-685 pin).
- F5 (debug-logging policy, applied): ServePlaylistSlicedAsync logs slice-vs-full with path, startTicks, segmentSeconds, and the RESOLVED start segment (Debug-gated; the builder's resolution arithmetic extracted into AudiobookPlaylistBuilder.ResolveStartSegment so the log line shares the ONE computation instead of duplicating it). The itemId rides inside the logged cache path (the serve layer has no separate id).
- F6 (fixture, applied): ArrangeSingleChapterBookAsync now plants the PRODUCTION shape (TARGETDURATION + MEDIA-SEQUENCE headers, so the slice drives the tag-REPLACE branch production takes, and full /alexaskill/api/video-audio/{chapterId}/segments/ URIs, the hls_base_url lines ffmpeg writes); segments=0 plants nothing (the cold-cache F1 shape). The no-tag insert branch keeps its own builder-level pin (F3's), which is where it is reachable.
