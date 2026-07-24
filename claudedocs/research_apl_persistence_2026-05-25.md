# Research: APL Now Playing Screen Persistence on Echo Show

**Date**: 2025-05-25
**Confidence**: High (Amazon official docs + community-verified patterns)
**Status**: Research complete — awaiting implementation decision

## Problem Statement

When playing media via the Jellyfin Alexa skill on Echo Show:
1. A gray background with song name and duration appears briefly
2. Album art/graphics load (native AudioPlayer metadata screen)
3. After ~30 seconds, the display reverts to the idle Echo Show home screen (gray with clock)

Audio continues playing — only the visual display is lost.

## Root Cause

The Alexa platform treats **visual templates** and **audio playback** as completely separate subsystems:

- `AudioPlayer.Play` starts audio that persists independently
- APL visual templates have an **interaction timer** (~30 seconds default) that dismisses the document when no user interaction occurs
- The native Now Playing screen (from AudioPlayer `metadata`) *should* persist, but the APL Now Playing template (sent on-demand) is subject to the interaction timer

## Current Codebase State

| Component | Status | Notes |
|-----------|--------|-------|
| AudioPlayer `metadata` | Already included | `BuildAudioPlayerResponse` sends `title`, `subtitle`, `art`, `backgroundImage` |
| APL Now Playing template | Only on-demand | Shown when user asks "what's playing?" via `TryAttachNowPlayingCard` |
| APL during playback | NOT sent | Normal playback handlers don't attach APL directives |
| `idleTimeout` setting | Not configured | APL documents use system default (~30s) |

## Solution Options

### Option A: Rely on Native AudioPlayer Metadata (Easiest)

**What**: The AudioPlayer `metadata` property already provides Amazon's native Now Playing screen automatically.

**Current state**: The codebase already sends metadata. The native screen shows album art, title, and subtitle.

**Why it may revert**: If the native Now Playing screen is reverting to clock despite metadata being set, possible causes are:
- Stream URL issues causing audio to stall
- Device firmware behavior (some Echo Show models have shorter display timeouts)
- AudioPlayer token reuse causing stale metadata cache

**Effort**: Minimal — verify metadata is correct, ensure unique tokens per track
**Limitation**: Cannot customize layout beyond `title`, `subtitle`, `art`, `backgroundImage`

### Option B: Hidden Video + Tick Handler Ping-Pong (Persistent Custom APL)

**What**: Keep a custom APL Now Playing screen visible indefinitely using a community-proven technique.

**How it works** (two parts):

**Part 1 — Keep APL document alive**: Add a 1x1 off-screen looping Video component:
```json
{
  "type": "Video",
  "position": "absolute",
  "width": 1, "height": 1,
  "top": -100, "left": -100,
  "source": [{ "url": "https://example.com/noop.mp4", "repeatCount": -1 }],
  "autoplay": true
}
```

**Part 2 — Keep skill session alive**: Add a `handleTick` handler that sends periodic events:
```json
"handleTick": [{
  "minimumDelay": 15000,
  "commands": [{
    "type": "SendEvent",
    "sequencer": "ping",
    "arguments": ["PING"]
  }]
}]
```

The backend handles PING by returning `shouldEndSession = undefined` (keeps session open without opening mic).

**Effort**: Moderate — modify APL template, add PING handler in request pipeline
**Advantage**: Full custom layout with progress bar, controls, album art rotation
**Risk**: Uses undocumented behavior; Amazon could change it. Requires a publicly hosted noop MP4.

### Option C: `idleTimeout` Extension (Simple but Limited)

**What**: Set `idleTimeout` in the APL document settings to extend the display duration.

```json
{
  "type": "APL",
  "version": "2024.1",
  "settings": { "idleTimeout": 300000 }
}
```

**Effort**: Minimal — one line change
**Limitation**: Capped by device (~5 minutes max). Not truly persistent for long listening sessions.

## Recommendations

### Primary (Immediate Fix)
**Option A** — Verify the native AudioPlayer metadata screen works correctly. This should persist for the entire duration of playback with zero code changes. If the user is already seeing album art, the metadata IS working. The "reverting to clock" may be a device-specific issue or a stream interruption.

### Secondary (Custom Persistent Display)
**Option B** — If the native screen isn't sufficient and the user wants a custom Jellyfin-branded Now Playing screen with progress bars, album art rotation, and playback controls that stays visible for the entire listening session, implement the hidden Video + tick handler pattern.

### Implementation Path for Option B
1. Modify `AplHelper.NowPlayingTemplate` to include hidden Video + handleTick
2. Send APL Now Playing alongside every `AudioPlayer.Play` directive (in `BuildAudioPlayerResponse`)
3. Add PING event handler in the request pipeline (return empty response with `shouldEndSession = undefined`)
4. Host a minimal noop MP4 publicly (or embed a data URI)
5. Optionally add progress bar updates via `ExecuteCommands` + `SetValue`

## Sources

- Amazon APL Interface Reference: developer.amazon.com/alexa/alexa-presentation-language/apl-interface
- APL Lifecycle docs: developer.amazon.com/alexa/alexa-presentation-language/apl-bp-understand-apl-lifecycles
- AudioPlayer metadata: developer.amazon.com/alexa/custom-skills/audioplayer-interface-reference
- Community persistence technique: apl.ninja/xeladotbe/blog/30-seconds-and-beyond
- LMS MediaServer skill (production example): forums.lyrion.org (MediaServer V5.4)
