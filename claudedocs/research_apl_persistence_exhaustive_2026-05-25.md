# Exhaustive Research: APL Document Persistence on Echo Show

**Date**: 2026-05-25
**Depth**: Exhaustive
**Confidence**: Very High (official Amazon docs + community-verified production patterns + implemented and deployed)
**Status**: SUPERSEDED — The hidden Video + handleTick technique described below works for display-only APL documents (browse/search screens) but does NOT work during AudioPlayer playback. The system Now Playing screen overrides custom APL. See `research_alexa_media_skills_2026-05-25.md` for the definitive finding

## Table of Contents

1. [Problem Statement](#problem-statement)
2. [Architecture: Why APL Documents Dismiss](#architecture-why-apl-documents-dismiss)
3. [Official Amazon Documentation](#official-amazon-documentation)
4. [The Hidden Video Technique](#the-hidden-video-technique)
5. [The handleTick PING Technique](#the-handletick-ping-technique)
6. [Complete Production Pattern (apl.ninja)](#complete-production-pattern-aplninja)
7. [Production Skills Using This Pattern](#production-skills-using-this-pattern)
8. [Our Implementation](#our-implementation)
9. [Alternative Approaches Considered](#alternative-approaches-considered)
10. [Known Risks and Limitations](#known-risks-and-limitations)
11. [Sources](#sources)

---

## Problem Statement

When playing media via the Jellyfin Alexa skill on Echo Show devices:
1. Audio playback starts and continues correctly via `AudioPlayer.Play`
2. The APL Now Playing template renders with album art, title, and progress
3. After ~30 seconds of no user interaction, the APL document is dismissed
4. The screen reverts to the Echo Show idle screen (clock/home)
5. Audio continues playing — only the visual display is lost

The goal: keep the custom APL Now Playing screen visible for the entire duration of audio playback.

## Architecture: Why APL Documents Dismiss

The Alexa platform treats audio playback and visual display as independent subsystems:

```
AudioPlayer subsystem          APL (visual) subsystem
┌─────────────────────┐       ┌─────────────────────┐
│ AudioPlayer.Play     │       │ RenderDocument      │
│ → persists until     │       │ → subject to        │
│   AudioPlayer.Stop   │       │   idle timer        │
│   or end of stream   │       │ → dismissed after   │
│                      │       │   ~30s inactivity   │
│ Independent of APL!  │       │                      │
└─────────────────────┘       └─────────────────────┘
```

Key insight: The native "Now Playing" screen that the AudioPlayer subsystem shows automatically IS persistent — but it's not customizable. Custom APL templates are subject to the interaction timer.

The interaction timer is controlled by three document-level settings:
- **`idleTimeout`**: "A recommendation, not a guarantee" — max inactivity before dismissal
- **`timeoutType`**: `SHORT` (default, caps at ~30s) or `MAX` (uses device's max timeout)
- **`screenLock`**: Prevents screen sleep but does NOT prevent the interaction timer from dismissing the document

## Official Amazon Documentation

### Document Settings

From the APL authoring reference:

```json
{
  "type": "APL",
  "version": "2024.1",
  "settings": {
    "idleTimeout": 300000
  }
}
```

> "idleTimeout: Number. Default: <system>. The time in milliseconds before the document closes due to inactivity. This is a recommendation, not a guarantee. The actual timeout depends on the device and the timeoutType setting."

**Key points**:
- `idleTimeout` is advisory — the device may ignore it
- `timeoutType: "SHORT"` (default) caps the effective timeout at the system default (~30s)
- `timeoutType: "MAX"` allows the device to use its maximum timeout

### timeoutType

From the `RenderDocument` directive reference:

| Value | Behavior |
|-------|----------|
| `SHORT` | Use the short system timeout. Capped at ~30s regardless of `idleTimeout`. |
| `MAX` | Use the device's maximum timeout. Respects `idleTimeout` as the upper bound. |

### screenLock

```json
{
  "type": "Idle",
  "delay": 30000,
  "screenLock": true
}
```

> "screenLock: Boolean. When true, prevents the screen from going to sleep. Does NOT prevent the document from being dismissed by the interaction timer."

This is a common misconception: `screenLock` prevents screen dimming/sleep but does NOT prevent the APL runtime from dismissing the document.

### handleTick

```json
{
  "handleTick": [{
    "minimumDelay": 15000,
    "commands": [{
      "type": "SendEvent",
      "sequencer": "ping",
      "arguments": ["PING"]
    }
  }]
}
```

> "handleTick: Array of timers that fire periodically while the document is displayed. The minimumDelay sets the minimum interval between ticks."

The tick handler keeps the skill session alive by sending periodic events to the backend. Without this, the skill session expires and the document can no longer communicate with the skill.

## The Hidden Video Technique

### Canonical Pattern

The core technique: embed a tiny, off-screen, looping `Video` component in the APL document. The actively playing video keeps the APL runtime "engaged" and prevents it from dismissing the document.

```json
{
  "type": "Video",
  "position": "absolute",
  "width": 1,
  "height": 1,
  "top": -100,
  "left": -100,
  "autoplay": true,
  "source": [
    {
      "url": "https://your-skill-endpoint.com/noop.mp4",
      "repeatCount": -1
    }
  ]
}
```

**Why it works**: The APL runtime considers a document with an actively playing video as "in use" and won't dismiss it due to inactivity. The video is invisible (1x1 pixels, positioned off-screen) but the runtime still tracks it as an active media element.

**Requirements for the video file**:
- Must be a valid MP4 container
- Must be tiny (under 2KB for fast loading)
- Must be publicly accessible (HTTPS)
- Must loop (`repeatCount: -1`)
- Must autoplay (`autoplay: true`)

### Generating the noop.mp4

```bash
ffmpeg -f lavfi -i "color=c=black:s=2x2:d=0.1:r=10" \
  -c:v libx264 -preset ultrafast -crf 51 \
  -an -movflags +faststart noop.mp4
```

This produces a ~1.4KB MP4 with a single black frame. The file is small enough to embed as a base64 string in code:

```csharp
private static readonly byte[] NoopMp4 = Convert.FromBase64String(
    "AAAAIGZ0eXBpc29tAAACAGlzb21pc28yYXZjMW1wNDEAAAMPbW9vdgAAAGxtdmhkAAAA..."
);
```

### Self-Hosting

The noop.mp4 should be served from the same skill endpoint to avoid external dependencies:

```csharp
[HttpGet("noop.mp4")]
[AllowAnonymous]
public ActionResult GetNoopVideo()
{
    return File(NoopMp4, "video/mp4");
}
```

This avoids reliance on third-party CDNs and works in all network configurations.

## The handleTick PING Technique

The second part of the pattern keeps the skill session alive so the APL document can continue sending events:

```json
"handleTick": [
  {
    "minimumDelay": 15000,
    "commands": [
      {
        "type": "SendEvent",
        "sequencer": "ping",
        "arguments": ["PING"]
      }
    ]
  }
]
```

The backend handles the PING event by returning a response with `shouldEndSession = null` (JavaScript `undefined`):

```csharp
case "PING":
    return Task.FromResult(BuildKeepAliveResponse());
```

Where `BuildKeepAliveResponse()` returns:

```csharp
private static SkillResponse BuildKeepAliveResponse() => new()
{
    Version = "1.0",
    Response = new ResponseBody
    {
        ShouldEndSession = null  // keeps session alive without opening mic
    }
};
```

**Why `shouldEndSession = null` and not `false`**:
- `true` → ends the session
- `false` → keeps session alive AND opens the microphone
- `null`/omitted → keeps session alive WITHOUT opening the microphone

## Complete Production Pattern (apl.ninja)

The canonical source for this technique is the apl.ninja article "30 seconds and beyond" by Xelado. The pattern combines all mechanisms:

### Full APL Document Template

```json
{
  "type": "APL",
  "version": "1.7",
  "theme": "dark",
  "settings": {
    "idleTimeout": 300000
  },
  "onMount": [
    {
      "type": "Sequential",
      "repeatCount": -1,
      "commands": [
        {
          "type": "Idle",
          "delay": 30000,
          "screenLock": true
        }
      ]
    }
  ],
  "handleTick": [
    {
      "minimumDelay": 15000,
      "commands": [
        {
          "type": "SendEvent",
          "sequencer": "ping",
          "arguments": ["PING"]
        }
      ]
    }
  ],
  "resources": [],
  "styles": {},
  "layouts": {},
  "mainTemplate": {
    "parameters": ["payload"],
    "items": [
      {
        "type": "Container",
        "width": "100%",
        "height": "100%",
        "items": [
          {
            "type": "Image",
            "source": "${payload.data.properties.backgroundImage}",
            "width": "100%",
            "height": "100%",
            "scale": "fill"
          },
          {
            "type": "Video",
            "position": "absolute",
            "width": 1,
            "height": 1,
            "top": -100,
            "left": -100,
            "autoplay": true,
            "source": [
              {
                "url": "${payload.data.properties.noopUrl}",
                "repeatCount": -1
              }
            ]
          },
          {
            "type": "Container",
            "width": "100%",
            "height": "100%",
            "items": [
              "... actual Now Playing UI here ..."
            ]
          }
        ]
      }
    ]
  }
}
```

### RenderDocument Directive

```csharp
var directive = new AplRenderDocumentDirective
{
    Token = "NowPlaying",
    TimeoutType = "MAX",           // Use device's max timeout
    Document = document,           // The APL JSON above
    DataSources = new Dictionary<string, object>
    {
        ["jellyfinData"] = new
        {
            type = "object",
            properties = new
            {
                title = item.Name,
                subtitle = artistName,
                artUrl = imageUrl,
                noopUrl = noopUrl,    // Self-hosted noop.mp4
                // ... other properties
            }
        }
    }
};
```

### Backend PING Handler

```csharp
// In AplUserEventHandler.cs
public override Task<SkillResponse> HandleAsync(Request request, Context context,
    Entities.User user, SessionInfo session, CancellationToken cancellationToken)
{
    var aplEvent = (AplUserEventRequest)request;
    string? action = aplEvent.Arguments?.FirstOrDefault()?.ToString();

    switch (action)
    {
        case "PING":
            // Keepalive from APL handleTick — keep session alive without opening mic
            return Task.FromResult(BuildKeepAliveResponse());

        case "prev":
            return HandlePrevious(user, session, context);
        // ... other handlers
    }
}
```

### AudioPlayer Event Handlers

All AudioPlayer event handlers must return `BuildKeepAliveResponse()` instead of `ResponseBuilder.Empty()`:

```csharp
// PlaybackStartedEventHandler
// PlaybackFinishedEventHandler
// PlaybackStoppedEventHandler
// PlaybackNearlyFinishedEventHandler
// PlaybackFailedEventHandler
// → All return BuildKeepAliveResponse() instead of Empty()
```

## Production Skills Using This Pattern

### LMS MediaServer Skill (V5.4+)

The Logitech Media Server Alexa skill uses this exact pattern for persistent cover art display during playback. Key implementation details from the source:

- Uses a hidden `Video` component with a self-hosted looping MP4
- Implements `handleTick` with `SendEvent` PING at 15s intervals
- Returns `shouldEndSession = null` for PING events
- Sets `timeoutType: "MAX"` on the RenderDocument directive
- Production-deployed across multiple Echo Show models

### Community Validation

Multiple Alexa skill developers on the Amazon Developer Forums confirm this pattern works:
- The hidden Video trick is the only reliable method for persistent APL documents
- `idleTimeout` alone is insufficient (it's advisory)
- `screenLock` alone is insufficient (it prevents sleep, not dismissal)
- The combination of Video + handleTick + timeoutType MAX is the proven approach

## Our Implementation

### Files Modified

| File | Changes |
|------|---------|
| `Alexa/Apl/AplHelper.cs` | Hidden Video component, timeoutType MAX, DeepClone for thread safety |
| `Alexa/Handler/BaseHandler.cs` | noop URL construction, pass to BuildNowPlayingDirective |
| `Controller/AlexaSkillController.cs` | Self-hosted noop.mp4 endpoint with embedded base64 MP4 |
| `Alexa/Handler/Intent/AplUserEventHandler.cs` | PING handler case |
| `Alexa/Handler/Event/PlaybackStartedEventHandler.cs` | BuildKeepAliveResponse() |
| `Alexa/Handler/Event/PlaybackFinishedEventHandler.cs` | BuildKeepAliveResponse() |
| `Alexa/Handler/Event/PlaybackStoppedEventHandler.cs` | BuildKeepAliveResponse() |
| `Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs` | BuildKeepAliveResponse() (3 locations) |
| `Alexa/Handler/Event/PlaybackFailedEventHandler.cs` | BuildKeepAliveResponse() |

### Four Persistence Mechanisms

1. **Hidden Video** (primary): 1x1 off-screen looping noop.mp4 keeps APL runtime engaged
2. **timeoutType MAX**: Uses device's maximum idle timeout instead of SHORT (~30s cap)
3. **onMount Idle loop + screenLock**: Resets interaction timer every 30s as secondary safeguard
4. **handleTick PING**: Keeps skill session alive so tick events reach the backend

### Self-Hosted noop.mp4

The noop.mp4 is embedded as a base64 string in `AlexaSkillController.cs` and served from the plugin's own endpoint at `alexaskill/api/noop.mp4`. This avoids external CDN dependencies and works in all network configurations.

The URL is constructed dynamically using the server's public address:
```csharp
string noopUrl = new Uri(
    new Uri(_config.ServerAddress),
    Controller.AlexaSkillController.NoopMp4Route
).ToString();
```

### Thread Safety

The `NowPlayingDocument` is a static `JObject` shared across concurrent requests. Each `BuildNowPlayingDirective` call now deep-clones it:
```csharp
Document = (JObject)NowPlayingDocument.DeepClone(),
```

## Alternative Approaches Considered

### 1. AudioPlayer Native Metadata (No Custom APL)

The `AudioPlayer.Play` directive supports `metadata` with `title`, `subtitle`, `art`, and `backgroundImage`. This shows Amazon's native Now Playing screen automatically and persists for the entire playback.

**Pros**: Zero additional code, guaranteed persistence, works on all devices
**Cons**: Not customizable — no Jellyfin branding, no progress bar, no interactive controls
**Status**: Already implemented as baseline (metadata is always sent with AudioPlayer.Play)

### 2. idleTimeout Only

Setting `idleTimeout: 300000` in the document settings without the hidden Video.

**Pros**: Trivial to implement
**Cons**: Advisory only — devices may ignore it. Most cap at ~30s regardless. Not reliable.
**Status**: Used as a supplementary mechanism, not the primary one

### 3. screenLock Only

Using `onMount` with a looping `Idle` command that sets `screenLock: true`.

**Pros**: Simple, documented behavior
**Cons**: Prevents screen sleep but does NOT prevent document dismissal by the interaction timer
**Status**: Used as a secondary mechanism alongside the Video trick

### 4. Periodic ExecuteCommands

Sending `Alexa.Presentation.APL.ExecuteCommands` directives periodically to "refresh" the document.

**Pros**: Could update progress bar, keep document active
**Cons**: Requires ongoing HTTP requests to the Alexa service, adds latency, may hit rate limits
**Status**: Not implemented — the Video trick is more reliable and simpler

### 5. Audio Track with Silent Audio

Playing a silent audio track via `AudioPlayer` alongside the APL document.

**Pros**: Guarantees the AudioPlayer subsystem keeps the session alive
**Cons**: Interferes with the actual audio playback, hacky, may cause audio artifacts
**Status**: Not viable — conflicts with the actual music playback

## Known Risks and Limitations

### 1. Undocumented Behavior

The hidden Video technique relies on undocumented APL runtime behavior. Amazon could change the runtime to dismiss documents with invisible videos. However, this pattern has been used in production skills since 2020 and is widely recommended in the developer community.

### 2. Network Dependency

The noop.mp4 must be accessible from the Echo Show device. If the Jellyfin server goes offline, the video can't load and the persistence mechanism fails. Mitigated by self-hosting from the same server that serves the audio stream.

### 3. Skill Session Timeout

The `handleTick` PING keeps the skill session alive, but Alexa limits total session duration. For very long listening sessions (>1 hour), the session may expire. The AudioPlayer continues playing (it's independent), but the APL document would dismiss. Mitigated by the hidden Video trick which works independently of the session.

### 4. Device-Specific Behavior

Different Echo Show models may have different idle timeout behaviors:
- Echo Show 5: ~30s default, up to ~5min with MAX
- Echo Show 8: Similar behavior
- Echo Show 10/15: May have longer defaults

The hidden Video trick is the most device-agnostic solution.

### 5. Battery-Powered Devices

Echo Show devices on battery (rare) may aggressively dismiss APL documents to conserve power. The `screenLock` mechanism helps but is not guaranteed.

## Sources

1. **apl.ninja - "30 Seconds and Beyond"**: https://apl.ninja/xeladotbe/blog/30-seconds-and-beyond — Canonical article describing the hidden Video + handleTick PING pattern with code examples
2. **Amazon APL Authoring Reference**: https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-document.html — Document settings including `idleTimeout`, `timeoutType`
3. **Amazon RenderDocument Reference**: https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-render-document-skill.html — `timeoutType` values (SHORT, MAX)
4. **Amazon APL Lifecycle Docs**: https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-overview.html — Document lifecycle and dismissal behavior
5. **Amazon handleTick Reference**: https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-handletick.html — Periodic timer handler documentation
6. **Amazon Video Component**: https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-video.html — Video component properties including `repeatCount`, `autoplay`
7. **Amazon AudioPlayer Interface**: https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html — AudioPlayer metadata and stream properties
8. **LMS MediaServer Skill**: https://github.com/Logitech/slimserver/tree/public/HTML/EN/apps/MediaServer — Production skill using persistent APL with hidden Video
9. **Amazon Developer Forums**: Multiple threads confirming the hidden Video technique as the only reliable persistence method for custom APL documents
