# Audiobook Seeking & Position Display

Date: 2026-05-20

## Problem

Audiobooks can be 8+ hours long. After removing the APL NowPlaying overlay (Echo's built-in player takes visual priority), users have:

1. No way to see elapsed/total time during playback
2. No way to skip forward/backward within long content (GoToChapterIntent works but only for chapter boundaries, and many audiobooks have chapters that are 30+ minutes)

## Solution

### 1. Relative Seeking — SkipForwardBackIntent

Custom intent for relative time skipping.

**Slots:**
- `seek_direction` (custom slot type: `forward`, `back`)
- `seek_amount` (AMAZON.NUMBER)
- `seek_unit` (custom slot type: `seconds`, `minutes`)

**Utterances (en-US examples):**
- "skip forward {seek_amount} {seek_unit}"
- "go back {seek_amount} {seek_unit}"
- "skip {seek_direction}"
- "forward {seek_amount} {seek_unit}"

**Defaults:**
- No amount → 30 seconds
- No direction → forward

**Handler logic:**
1. Resolve current position from `session.PlayState.PositionTicks` (fallback: `context.AudioPlayer.OffsetInMilliseconds`)
2. Parse seek_amount + seek_unit into ticks
3. Add/subtract from current position, clamp to [0, item runtime]
4. Return `BuildAudioPlayerResponse(ReplaceAll, ..., offsetMs)`

**Edge cases:**
- Already at beginning → "You're at the beginning"
- Would go past end → clamp to end, announce "Skipping to the end"
- No media playing → "Nothing is playing"

### 2. Absolute Seeking — JumpToPositionIntent

Custom intent for jumping to a specific time position.

**Slots:**
- `position_hours` (AMAZON.NUMBER, optional)
- `position_minutes` (AMAZON.NUMBER, required)
- `position_seconds` (AMAZON.NUMBER, optional)

**Utterances (en-US examples):**
- "jump to {position_hours} hour {position_minutes} minutes"
- "go to {position_minutes} minutes"
- "skip to {position_hours} hours"

**Handler logic:**
1. Parse hours + minutes + seconds into total milliseconds
2. Clamp to [0, item runtime]
3. Return `BuildAudioPlayerResponse(ReplaceAll, ..., offsetMs)`

**Edge cases:**
- Position past end → "That's past the end of this content, it's X hours long"
- Position is 0 or negative → clamp to 0
- No media playing → "Nothing is playing"

### 3. Position Display — APL Card + Standard Card

**APL Card** (Echo Show devices):
- Enhance `TryAttachNowPlayingCard` in `MediaInfoIntentHandler` to include progress bar
- Uses simple Container + Frame pattern (no AlexaProgressBar, no layout imports)
- Shows: track/book name, elapsed time, total time, visual progress bar

**Standard Card** (Alexa app, all devices):
- Add `StandardCard` to `MediaInfoIntent` responses
- Title: "Now Playing"
- Body: "{item name}\n{elapsed} / {total}"
- Always sent; APL card sent additionally on APL devices

### 4. Proactive Position Announcement

**New per-user setting**: `AnnouncePositionOnResume` (boolean, default: off)

- In `ResumeIntentHandler`: when enabled and offset > 0, set `OutputSpeech` (SSML) on the AudioPlayer response saying "Resuming at {position} of {total}"
- In `PlayBookIntentHandler`: when enabled, announce "Playing {title}, {total duration}" before starting
- When off (default): no behavioral change

### 5. Feature Flag & A/B Testing

**New feature flag**: `SeekEnabled` (boolean, default: off)

When disabled:
- `SkipForwardBackIntent` and `JumpToPositionIntent` handlers return early via `IfFeatureDisabled()`
- APL card changes are not applied
- Intents exist in interaction models but are dormant

When enabled:
- Both handlers active
- Enhanced APL card on MediaInfoIntent
- Standard Card always sent (low risk, additive)

`AnnouncePositionOnResume` is independent per-user setting, not tied to `SeekEnabled`.

## Files to Create/Modify

### New files
- `Alexa/Handler/Intent/SkipForwardBackIntentHandler.cs`
- `Alexa/Handler/Intent/JumpToPositionIntentHandler.cs`

### Modified files
- `Alexa/IntentNames.cs` — add `SkipForwardBack`, `JumpToPosition`
- `Alexa/Handler/Intent/MediaInfoIntentHandler.cs` — enhanced APL card + Standard Card
- `Alexa/Handler/Intent/ResumeIntentHandler.cs` — proactive announcement (gated)
- `Alexa/Handler/Intent/PlayBookIntentHandler.cs` — proactive announcement (gated)
- `Alexa/Handler/BaseHandler.cs` — shared `FormatPositionAnnouncement` helper
- `Configuration/PluginConfiguration.cs` — `SeekEnabled` flag, `AnnouncePositionOnResume` per-user setting
- `Configuration/config.html` — UI for new settings
- `Alexa/InteractionModel/model_*.json` (17 files) — new intents + slot types + utterances
- `Alexa/Locale/<locale>.json` (17 files) — new response strings
- `Controller/SkillController.cs` — register new handlers in DI

### Test files
- New unit tests for both handlers
- Updated `FeatureFlagTests.cs` for new flag
- Updated locale/model validation

## Response Strings Needed (per locale)

- `SkipForwardAnnouncement` — "Skipped forward to {position}"
- `SkipBackAnnouncement` — "Skipped back to {position}"
- `AtBeginning` — "You're at the beginning"
- `JumpedToPosition` — "Jumped to {position} of {total}"
- `PositionPastEnd` — "That's past the end, this content is {total} long"
- `ResumingAtPosition` — "Resuming at {position} of {total}"
- `PlayingBookAnnouncement` — "Playing {title}, {total_duration}"
