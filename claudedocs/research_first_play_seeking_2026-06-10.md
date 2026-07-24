# Research: Seekable Video from First Play for Alexa VideoApp

**Date**: 2026-06-10
**Status**: Research complete — for implementation decision

---

## Executive Summary

Three viable approaches exist for getting seekable video from the very first play. The key discovery: **Alexa VideoApp officially supports HLS** (`.m3u8`), not just MP4. This opens the door to streaming with built-in seeking.

| Approach | First Play Start | Seeking on 1st Play | Complexity | Recommendation |
|----------|-----------------|---------------------|------------|----------------|
| **A: HLS on-the-fly** | ~2-4s | ✅ Within generated segments | High | Best UX, most work |
| **B: Stream-while-writing** | ~2-5s | ❌ No (forward only) | Medium | Fast start, no seek |
| **C: Hybrid pipe + cache** | ~1-2s | ❌ First play, ✅ after | Medium | Best balance |

**Current state**: First play has no seeking; second play has full seeking via background remux to `.fs.mp4`.

---

## Option A: HLS On-the-Fly

### How it works
```
ffmpeg -i audio.mp3 -loop 1 -i art.jpg \
  -c:v libx264 -tune stillimage -preset ultrafast \
  -c:a aac -b:a 128k \
  -hls_time 4 -hls_list_size 0 -hls_flags append_list \
  -hls_segment_filename /cache/seg_%03d.ts \
  /cache/stream.m3u8
```

1. On request, start ffmpeg generating HLS segments (4s each)
2. VideoApp.Launch points to `https://server/alexaskill/api/video-audio/{id}/stream.m3u8`
3. Echo Show fetches `.m3u8` playlist, then individual `.ts` segments
4. First segment ready in ~2-4 seconds
5. Each segment is a complete, seekable unit
6. Player can seek within already-generated segments
7. After generation completes, final `.m3u8` is a static, fully-seekable HLS stream

### Pros
- **Seeking from first play** (within generated segments)
- Industry-standard streaming format
- Built-in adaptive bitrate support (if needed)
- Official VideoApp support (`.m3u8` listed in docs)
- No Content-Length needed
- Segments are individually cacheable

### Cons
- **Highest complexity**: Multiple HTTP endpoints (playlist + segments), file lifecycle management, cleanup
- **HLS latency**: Player typically buffers 3 segments → 12s initial delay for 4s segments
- **Many small files**: For a 15-minute audiobook at 4s segments = 225 `.ts` files + playlist
- **Multiple HTTP requests**: Echo Show makes separate requests for playlist + each segment
- **Cleanup complexity**: Must garbage-collect segments after playback
- **URL structure change**: VideoApp source URL changes from `.mp4` to `.m3u8`

### Effort estimate
- New endpoint for `.m3u8` serving
- New endpoint for segment `.ts` serving
- Segment lifecycle management (create, track, cleanup)
- Change VideoApp source URL generation in handler code
- 1-2 days of development + testing

---

## Option B: Stream-While-Writing (FileStream with FileShare.ReadWrite)

### How it works
```csharp
// Start ffmpeg writing to cache file (frag_keyframe+empty_moov)
// Immediately open the same file for reading with FileShare.ReadWrite
var fs = new FileStream(cachePath, new FileStreamOptions
{
    Mode = FileMode.Open,
    Access = FileAccess.Read,
    Share = FileShare.ReadWrite,  // don't lock out ffmpeg
    Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
});

return new FileStreamResult(fs, "video/mp4")
{
    EnableRangeProcessing = false  // can't range on growing file
};
```

1. On cache miss, start ffmpeg writing fragmented MP4 to disk
2. Immediately begin streaming the file to the client while ffmpeg writes
3. The fragmented MP4 is playable as soon as the first `moof`+`mdat` fragment arrives
4. ASP.NET uses chunked transfer encoding (no Content-Length)
5. When ffmpeg finishes writing, the stream completes naturally
6. Background remux to `.fs.mp4` for seeking on subsequent plays

### Pros
- **Fast first play**: Starts streaming within seconds (first fragment)
- **Moderate complexity**: Minimal code change from current approach
- **No new endpoints**: Same URL, different response mode
- **Works for any content length**: Even audiobooks start quickly

### Cons
- **No seeking on first play**: Chunked transfer + fragmented MP4 = forward-only
- **Uncertain VideoApp compatibility**: VideoApp may expect Content-Length for MP4
  (though HLS/MPEG-TS support suggests chunked is fine)
- **File sharing complexity**: Must handle ffmpeg failure mid-stream gracefully
- **No Range support**: `EnableRangeProcessing = false` for growing files

### Effort estimate
- Modify `StreamVideoAudio` to start ffmpeg, then stream while writing
- Handle the "file not yet created" initial window
- Background remux remains the same
- 0.5-1 day of development + testing

---

## Option C: Hybrid Pipe + Disk Cache

### How it works
1. On cache miss, start ffmpeg with **two outputs**:
   - **stdout** (pipe): stream directly to HTTP response (chunked, fMP4)
   - **file**: write fragmented MP4 to disk for caching
2. Pipe ffmpeg's stdout to `Response.BodyWriter` for immediate streaming
3. When pipe completes, the disk file is also complete
4. Background remux disk file to `.fs.mp4` for future seeking
5. On cache hit, serve `.fs.mp4` (if available) or `.mp4` via PhysicalFile

### Implementation sketch
```csharp
// Start ffmpeg writing to file AND pipe
// Use -f mp4 -movflags frag_keyframe+empty_moov for the file output
// Use tee or separate ffmpeg process for stdout

// OR simpler: write to file, read from file with FileShare.ReadWrite
// Stream the file to response while ffmpeg writes it
```

Actually, ffmpeg can't write to both a file and stdout simultaneously with different movflags.
The simpler variant is **Option B** — write to file, read from file concurrently.

### Pros
- **Fastest first play**: Sub-second start time with pipe
- **Cache built during first play**: File is ready for remux immediately
- **Full seeking on second play**: Same as current `.fs.mp4` approach

### Cons
- **No seeking on first play**: Same as Option B
- **ffmpeg stdout deadlock risk**: Must read stderr concurrently
- **Client disconnection handling**: Must kill ffmpeg if client disconnects
- **No Content-Length**: Chunked transfer for first play

### Effort estimate
- Similar to Option B
- 0.5-1 day of development + testing

---

## Comparison Matrix

| Criterion | Current (file-then-serve) | Option A (HLS) | Option B (stream-while-write) | Option C (pipe+cache) |
|-----------|--------------------------|-----------------|-------------------------------|----------------------|
| First play start | 10-30s (full encode) | 2-4s | 2-5s | 1-2s |
| Seek on 1st play | ❌ | ✅ (partial) | ❌ | ❌ |
| Seek on 2nd play | ✅ | ✅ | ✅ | ✅ |
| Duration on 1st play | 0 | ✅ | 0 | 0 |
| Content-Length | ✅ (complete file) | N/A (HLS) | ❌ (chunked) | ❌ (chunked) |
| Alexa compatibility | ✅ Proven | ✅ (docs say HLS) | ⚠️ Unproven | ⚠️ Unproven |
| Code complexity | Current | High | Low | Medium |
| File management | Simple | Complex | Simple | Simple |

---

## Recommendations

### Conservative (Recommended): Option B — Stream-while-writing
- **Smallest code change** from current architecture
- Eliminates the 10-30s first-play delay
- No new endpoints, no file format changes
- Seeking still deferred to second play (via `.fs.mp4` remux)
- **Risk**: VideoApp may not handle chunked MP4 well (moderate confidence it works)

### Aggressive: Option A — HLS on-the-fly
- **Best UX**: Seeking from first play, proper duration display
- **Highest confidence**: HLS is explicitly documented as VideoApp-supported
- **Highest cost**: New endpoints, segment management, URL structure changes
- Best if seeking on first play is a must-have

### Low-risk improvement: Current approach + pipe streaming
- Try Option B first; if VideoApp rejects chunked MP4, fall back to current approach
- The `.fs.mp4` background remux is already working and provides seeking on second play

---

## Sources

- Amazon VideoApp Interface Reference (developer.amazon.com, Oct 2025) — supported formats table
- ffmpeg `libavformat/movenc.c` source — seekability check for faststart
- ffmpeg `ffmpeg-formats.html` — movflags documentation
- Jakub Jaburek, "Piping MP4 from FFmpeg" (jaburjak.cz, 2021)
- Vlad Poberezhny, "In-browser live video using fMP4" (Medium, 2023)
- ASP.NET Core FileStreamResult documentation (learn.microsoft.com)
- Stack Overflow: ffmpeg pipe output patterns, fMP4 streaming, ASP.NET chunked response
- ISO 14496-12 (MP4 file format spec) — sidx, moov, moof box definitions
