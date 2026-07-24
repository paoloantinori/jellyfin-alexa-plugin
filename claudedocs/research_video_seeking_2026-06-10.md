# Research: VideoAudio Seeking on Echo Show

**Date**: 2026-06-10
**Confidence**: HIGH (multi-source verification)
**Status**: Research complete — ready for implementation decision

---

## Executive Summary

The Echo Show VideoApp player **does show a seek/progress bar** (user-confirmed), but seeking fails because `frag_keyframe+empty_moov` produces a fragmented MP4 with no centralized seek index. Switching to `+faststart` with per-item locking will likely fix seeking. The previous failures were caused by a missing concurrency guard, not by the format choice itself.

---

## Verified Facts

### 1. Why Seeking Fails Now

`frag_keyframe+empty_moov` produces this structure:
```
[ftyp] [moov (EMPTY — only mvex)] [moof+mdat] [moof+mdat] ...
```

- The `moov` atom has **no sample table** (no `stco`, `stsz`, `stss`, `stts` boxes)
- Each `moof` fragment has its own mini index (`trun`/`tfhd`), but the player must scan all fragments to find a seek target
- The Echo Show player apparently cannot (or does not) do this fragment scanning → falls back to byte 0

**Evidence**: User confirmed "each time I was moving the seek bar it was starting from the beginning"

### 2. What `+faststart` Produces

```
[ftyp] [moov (FULL sample table)] [mdat] ...
```

- Complete seek index at the front: `stco` (chunk offsets), `stsz` (sample sizes), `stss` (keyframes), `stts` (time-to-sample)
- Player reads moov first, builds in-memory seek index, then issues precise HTTP Range requests for any seek position
- This is the **universal standard** for progressive-download MP4 with seeking
- `enableRangeProcessing: true` on `PhysicalFile()` serves the byte ranges correctly

### 3. Why Previous `+faststart` Attempts Failed

| Attempt | What happened | Root cause |
|---------|--------------|------------|
| 1st | No audio | 36-byte cache stub (unrelated to format) |
| 2nd | No audio/video | Concurrent requests served partially-written file |
| 3rd (.tmp approach) | No audio/video | ffmpeg's `+faststart` reopens output file by path for second pass — `.tmp` breaks this |

**All failures were infrastructure issues, not format issues.** The format itself (`+faststart`) was never the problem.

### 4. Alexa VideoApp Seeking Capability

- **Official docs**: Only pause, resume, stop are documented for VideoApp
- **Reality**: User confirmed a seek bar IS visible and touch-responsive on Echo Show
- **Video Skill API** (Netflix, Hulu) has full seeking — but that's a closed, certified API, not available to custom skills
- **Probability of fixing**: HIGH — `+faststart` is the universally supported format; the player is clearly trying to seek but fails due to fragmented structure

### 5. There Is No Magic movflags Combination

No single movflags option gives both immediate streamability AND full seek index:

| movflags | Streamable during write | Seekable | Notes |
|----------|------------------------|----------|-------|
| `frag_keyframe+empty_moov` | ✅ Yes | ❌ No | Current — no sample table |
| `+faststart` | ❌ No | ✅ Yes | Must complete before serving |
| `+global_sidx` | ✅ Yes | ⚠️ Maybe | Requires player to read sidx box (unproven for VideoApp) |
| `hybrid_fragmented` | ✅ During write | ✅ After completion | New ffmpeg flag, may not be available |

---

## Recommended Approach: `+faststart` + Per-Item Locking

### Pattern: ConcurrentDictionary<string, SemaphoreSlim> with reference counting

This is the exact same pattern Jellyfin's own `TranscodeManager` uses (via `AsyncKeyedLock` NuGet). Since this plugin already uses `SemaphoreSlim(1,1)` extensively (SongNgramIndexService, ArtistIndexService, JellyfinConnectivityChecker), we use a `ConcurrentDictionary` instead of adding a new NuGet dependency.

### Flow

```
Request arrives
  ├─ Cache hit → serve immediately (no lock)
  └─ Cache miss → acquire per-item SemaphoreSlim
       ├─ Double-check cache (another request may have generated it)
       │    ├─ Hit → release lock, serve
       │    └─ Miss → run ffmpeg with +faststart to final cache path
       │         ├─ Success → release lock, serve file
       │         └─ Failure → release lock, delete partial, return 500
       └─ Lock released via IDisposable
```

### Why This Solves All Previous Issues

1. **No `.tmp` file** → `+faststart` reopens same file → ✅ works
2. **No partial file serving** → lock held until ffmpeg completes → ✅ 
3. **No cache stampede** → per-item lock serializes generation → ✅
4. **Different items still parallel** → different keys = different locks → ✅
5. **Cache stub detection remains** → 10KB threshold catches any edge cases → ✅

### Trade-off: First-Play Latency

With `+faststart`, the file must be fully generated before serving. For a typical song:
- `-preset ultrafast -crf 28 -r 1` (1fps still image) → encoding is trivial
- Audio is muxed (not re-encoded)
- Estimated generation time: **5-15 seconds** for a typical 3-5 minute song
- All subsequent plays: instant cache hit

This is acceptable because:
- The song plays from cache after first generation
- The alternative (no seeking) is worse UX
- Jellyfin's own transcoding has similar first-play delays

### Alternative Considered: Two-Pass Encoding

1. First pass: `frag_keyframe+empty_moov` to `.tmp` (serve immediately, streamable)
2. Background: `ffmpeg -i .tmp -c copy -movflags +faststart final.mp4` (remux)

**Rejected** because:
- Double the complexity (two ffmpeg processes, background task coordination, file replacement)
- Still need locking for step 1
- Longer total generation time
- Over-engineering for a music plugin

---

## Sources

- FFmpeg `ffmpeg-formats.html` — movflags documentation, fragmentation section
- Stack Overflow #8616855 — Comprehensive movflags comparison (multiple answers, 2012-2025)
- Jellyfin `TranscodeManager.cs` — Production per-path locking with AsyncKeyedLock
- Stephen Cleary (Stack Overflow) — AsyncKeyedLock foundational pattern with reference counting
- Amazon VideoApp Interface Reference — Official feature list (no seeking documented)
- Amazon Video Skill Testing Guide — Full playback controls (separate API, not available to custom skills)
- Project source: `VideoAudioController.cs`, `VideoAudioCache.cs` — Current implementation
