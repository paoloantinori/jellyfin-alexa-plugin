---
id: JF-694
title: >-
  JF-694 - one-chapter book resume never gets a position to serve: the tracker
  write gate is Folder-only while the single-chapter redirect keys segments by
  the chapter leaf; plus the overrun ?start= zero-segment slice
status: To Do
assignee: []
created_date: '2026-10-01'
labels:
  - audiobooks
  - resume
  - tracking
dependencies: []
references:
  - >-
    backlog/tasks/jf-686 - Single-chapter-audiobook-resume-redirect-drops-start-ticks-a-resume-launch-on-a-one-chapter-book-restarts-from-0-00.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-01 same-turn from the JF-686 code-review round (high effort, findings 1 and 2 of 3; finding 1 code-verified by the worker before filing). The JF-686 serve-side fix (the redirect no longer drops ?start=) is correct and pinned, but review traced the mint side and found the fixed serve path is LATENT for one-chapter books: no production launch can deliver a positive ?start= to a single-chapter book today, so the user-visible restart the task title describes persists at the TRACKING layer.

FINDING 1 (verified): a one-chapter book can never hold a tracker position.
- The single-chapter redirect re-mints a CHAPTER-scoped token and the song core builds segment URLs keyed by the CHAPTER id (VideoAudioController.cs hlsBaseUrl = /alexaskill/api/video-audio/{chapterId}/segments/).
- GetSegment's write gate ShouldRecordPositionProgress records ONLY when GetItemById(itemId) is a Folder (JF-499 W1, VideoAudioController.cs ~3540); the chapter is a leaf (AudioBook/Audio), so nothing is recorded under EITHER the chapter key (write-only dead weight by design) or the book key the resume path reads.
- Every resume-URL mint site therefore sees a permanently cold tracker for one-chapter books and refuses to mint ?start=>0: ResumeIntentHandler.TryBuildNativeControlsBookResumeAsync (trackedTicks <= 0 returns null, chapter-relative UserData fallback deliberately cannot slice, JF-567), PlayBookIntentHandler (trackedTicks > 0 gate), LaunchRequestHandler (useResumePlaylist = true only on trackedTicks > 0), YesIntentHandler (the offeredTicks fallback only runs when the offer set UseResumePlaylist, which required tracker > 0 at offer time).
- Net effect: StreamHlsAudiobook's single-chapter branch only ever binds startTicks = null in production; the JF-686 sliced serve runs solely under the unit test's synthetic direct call. Multi-chapter books are unaffected (the concat cache keys segments by the parent Folder, which the gate records).

FIX DIRECTIONS (needs a design decision, NOT a drive-by):
(a) Canonicalize at record time: when GetSegment's itemId resolves to a LEAF whose parent is an audiobook folder, record under the parent book key (ResumeMath.GetAudiobookBookKey equivalent). MUST mind the JF-625 MusicAlbum interplay: album concat serves segments keyed by the ALBUM id (a Folder, already recorded) and album resume sums track offsets via AlbumPlayService rather than reading the tracker, so a leaf-under-album canonicalization must not start feeding album keys into the audiobook tracker.
(b) Re-key the redirect's segment URLs and token to parentId (segments cached under the chapter's dir, so FindSegmentPath + token scoping would all move): bigger blast radius, touches the JF-309 token contract and the JF-685 no-token seam documentation.

FINDING 2 (verified, pre-existing on all three resume routes, JF-686 only extends the exposure to the song serve): a ?start= beyond the playlist's total duration yields a ZERO-SEGMENT playlist. BuildSlicedPlaylist's EXTINF walk returns null for beyond-total (and for missing/unparseable durations), the flat-divisor fallback then computes a startSegment past the last segment, and the emitter keeps nothing: the device receives header + MEDIA-SEQUENCE:N + ENDLIST with no segments and fails playback. The route is [AllowAnonymous] with the token in the URL, so any URL holder can append &start=huge; a stale tracker position over a shortened item reaches the same shape. DESIGN CONSTRAINT the worker hit live: the obvious guard (zero-keep slice serves the base unsliced) CONTRADICTS two existing green pins, BuildResumePlaylist_Sliced_NoExtinf_FallsBackToFlatDivisor and BuildResumePlaylist_Sliced_UnparseableExtinf_FallsBackToFlatDivisor, which pin the flat fallback's zero-keep MEDIA-SEQUENCE rewrites on no-EXTINF / 2-segment playlists. The guard therefore needs to DISTINGUISH beyond-total (metadata proved the position unplayable) from metadata-missing (the fallback exists precisely for that), i.e. a richer TryResolveStartSegmentByExtinf result, not a silent re-pin of the fallback contract.
<!-- SECTION:DESCRIPTION:END -->
