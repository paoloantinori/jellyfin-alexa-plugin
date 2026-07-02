# Playlist Shuffle Fix Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `AMAZON.ShuffleOnIntent` / `AMAZON.ShuffleOffIntent` actually change the order in which a playing playlist (and album/artist queue) is played, and stop leaking whole-library dynamic entities on playback-control responses.

**Architecture:** The plugin already has a persisted, per-device source of truth for queue state (`DeviceQueueManager` / `DeviceQueue.PlaybackOrder`), but the shuffle handlers never write it and the next-track resolver never reads it — instead they rely on an indirect, untested flag on Jellyfin's `session.PlayState`. This plan (1) confirms the exact runtime failure with the debug logs that already exist, then (2) makes `DeviceQueueManager` the authoritative shuffle state, (3) improves shuffle quality so tracks don't repeat immediately, (4) adds a defensive guard to the dynamic-entities interceptor, and (5) adds the missing test + E2E coverage.

**Tech Stack:** C# / .NET 9, Alexa.NET, Jellyfin plugin SDK (10.11+), xUnit (~2302 tests), SMAPI simulate-skill E2E (it-IT).

**Source issue:** https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/10 (latest comment — RUBIKOF follow-up after v0.9.1.0 fixed the original "playlist always empty" bug).

---

## Root-Cause Analysis (confirmed by code reading)

1. **`ShuffleOnIntentHandler.HandleAsync`** (`Alexa/Handler/Intent/ShuffleOnIntentHandler.cs:42`) only calls `SessionManager.OnPlaybackProgress(info, true)` with `PlaybackOrder = PlaybackOrder.Shuffle`, then returns `ResponseBuilder.Empty()`. It writes **nothing** to the plugin's own queue state.
2. Jellyfin's `SessionManager.UpdateNowPlayingItem` **does** execute `session.PlayState.PlaybackOrder = info.PlaybackOrder` (verified in jellyfin/jellyfin `Emby.Server.Implementations/Session/SessionManager.cs`), so the flag is *technically* set.
3. **`PlaybackNearlyFinishedEventHandler.ResolveNextItemId`** (`Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs:287`) reads `session.PlayState?.PlaybackOrder` and, when `Shuffle`, picks a random next track from `session.NowPlayingQueue` (`Random.Shared`, avoiding only the current index).
4. **Zero test coverage** of `ShuffleOnIntentHandler` / `ShuffleOffIntentHandler`. `GaplessPlaybackTests` sets `session.PlayState.PlaybackOrder = PlaybackOrder.Shuffle` *directly on the mock* — proving the resolve branch works **given the flag**, but no test exercises the handler actually setting it.
5. **Diverged state:** `DeviceQueueManager.SetPlaybackOrder()` and `DeviceQueue.PlaybackOrder` exist and are persisted, but `ResolveNextItemId` reads Jellyfin's `session.PlayState` instead. Two sources of truth, only one wired into playback.
6. **Open question the diagnostic must close:** whether `AMAZON.ShuffleOnIntent` is even routed to the skill during AudioPlayer playback. The documented auto-routed set during playback is Pause/Resume/Next/Previous/Stop only — ShuffleOn/ShuffleOff are *not* listed. If the handler is never entered, no plugin-side flag fix helps and the `Alexa.Media.PlayQueue` `SetShuffle` directive (the never-implemented archived task JF-218) is required instead.

**Empirical confirmation:** The reporter verified on-device that `AMAZON.ShuffleOnIntent` is sent (Alexa Developer Console) yet playback order is unchanged. That + items 1–5 above confirm a real bug; item 6 means the diagnostic in Task 1 is mandatory before committing to the Tasks 2–4 fix.

---

## File Structure

**Modify:**
- `Alexa/Handler/Intent/ShuffleOnIntentHandler.cs` — inject `DeviceQueueManager`, set authoritative shuffle state + reshuffle remaining queue tail.
- `Alexa/Handler/Intent/ShuffleOffIntentHandler.cs` — inject `DeviceQueueManager`, clear shuffle state + restore original queue order.
- `Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs` — read shuffle state from `DeviceQueueManager` (primary), keep `session.PlayState` as fallback.
- `Alexa/Playback/DeviceQueueManager.cs` — add `ShuffleRemaining(deviceId, currentItemId, originalOrder)` + `RestoreOrder(deviceId)` helpers.
- `Alexa/Playback/DeviceQueue.cs` — add `OriginalItemIds` field (snapshot for un-shuffle).
- `Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs` — skip built-in playback-control intents.
- `Alexa/Handler/BaseHandler.cs` — add a reusable Fisher–Yates `ShuffleInPlace<T>` if not already present (note: `ShuffleCopy<T>` exists at line 1358).

**Create (tests):**
- `Jellyfin.Plugin.AlexaSkill.Tests/Handler/ShuffleOnIntentHandlerTests.cs`
- `Jellyfin.Plugin.AlexaSkill.Tests/Handler/ShuffleOffIntentHandlerTests.cs`
- `Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueShuffleTests.cs`

---

## Chunk 1: Diagnostic (GATE — complete before Tasks 2–4)

### Task 1: Confirm which root-cause branch is active

**Why this gates everything:** Tasks 2–4 assume the `ShuffleOn` handler is actually invoked. If `AMAZON.ShuffleOnIntent` is not routed to the skill during AudioPlayer playback, those tasks won't fix anything and we must instead implement the `Alexa.Media.PlayQueue` `SetShuffle` directive (JF-218). 15 minutes of log-checking prevents building the wrong fix.

**Files:** none (read-only investigation).

- [ ] **Step 1: Enable plugin debug logging**

Edit `/config/logging.default.json` inside the `jellyfin` container (per project "Debug Logging Policy"), adding:
```json
"Jellyfin.Plugin.AlexaSkill": "Debug"
```
Then restart Jellyfin: `podman restart jellyfin`. No rebuild needed.

- [ ] **Step 2: Reproduce on-device**

On an Echo with a playlist playing, say the shuffle-on phrase (it-IT: "modalità casuale" / en-US: "shuffle on"). Let at least 3 track transitions happen. Then say shuffle-off.

- [ ] **Step 3: Pull logs and answer the two diagnostic questions**

```bash
podman logs jellyfin --since 20m 2>&1 | grep -E "ShuffleOn|ShuffleOff|Pre-fetching next|resolved next item|PlaybackNearlyFinished"
```

Record the answers:
- **Q1 (routing):** Is the line `ShuffleOn: entered, token=...` present?
  - **No** → root cause is routing. STOP. The fix is `Alexa.Media.PlayQueue` `SetShuffle` (JF-218); re-plan before continuing. Update this plan / open a separate task.
  - **Yes** → continue.
- **Q2 (flag read):** In the `Pre-fetching next track ...` / `resolved next item ...` lines that occur *after* the ShuffleOn, is `shuffle=Shuffle` or `shuffle=Default`?
  - `shuffle=Shuffle` but order still sequential → bug is inside `ResolveNextItemId`'s shuffle branch or the `session.NowPlayingQueue` contents (Tasks 2–3 apply).
  - `shuffle=Default` → the flag is being reset/not observed between handler and event (Tasks 2–3 apply — making `DeviceQueueManager` authoritative fixes this regardless).

- [ ] **Step 4: Commit the diagnostic findings**

Append the observed log lines and the Q1/Q2 verdicts to the backlog task's notes (`task_edit` → `notesAppend`). No code commit. Proceed to Chunk 2.

---

## Chunk 2: Authoritative shuffle state (the core fix)

> Applies when Task 1 Q1 = **Yes** (handler is entered). This chunk makes shuffle reliable by sourcing the flag from the same persisted component the resolver reads, and physically reorders the remaining queue so the effect is guaranteed even if the `session.PlayState` flag is flaky.

### Task 2: Add `OriginalItemIds` + shuffle/restore helpers to DeviceQueueManager

**Files:**
- Modify: `Alexa/Playback/DeviceQueue.cs`
- Modify: `Alexa/Playback/DeviceQueueManager.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueShuffleTests.cs`

- [ ] **Step 1: Write the failing test**

`Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueShuffleTests.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class DeviceQueueShuffleTests : IDisposable
{
    private readonly string _dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dq_test_" + Guid.NewGuid().ToString("N"));
    private readonly DeviceQueueManager _mgr;

    public DeviceQueueShuffleTests()
    {
        System.IO.Directory.CreateDirectory(_dir);
        _mgr = new DeviceQueueManager(_dir, NullLogger<DeviceQueueManager>.Instance);
    }

    [Fact]
    public void ShuffleRemaining_KeepsCurrentFirst_RandomizesTail_StoresOriginal()
    {
        var ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _mgr.SetQueue("dev", ids, currentIndex: 0);
        _mgr.ShuffleRemaining("dev", currentItemId: "0");
        DeviceQueue q = _mgr.GetOrCreateQueue("dev");

        Assert.Equal("Shuffle", q.PlaybackOrder);
        Assert.NotNull(q.OriginalItemIds);
        Assert.Equal(ids, q.OriginalItemIds);            // original preserved
        Assert.Equal("0", q.ItemIds[0]);                 // current stays first
        Assert.Equal(ids.Count, q.ItemIds.Count);        // no loss/dup
        Assert.NotEqual(ids, q.ItemIds);                 // tail was reordered
    }

    [Fact]
    public void ShuffleRemaining_NoOp_WhenQueueTooShort()
    {
        _mgr.SetQueue("dev", new List<string> { "a", "b" }, 0);
        _mgr.ShuffleRemaining("dev", "a");
        DeviceQueue q = _mgr.GetOrCreateQueue("dev");
        Assert.Null(q.OriginalItemIds);                  // not shuffled
    }

    [Fact]
    public void RestoreOrder_RevertsToOriginal_WhenShuffled()
    {
        var ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
        _mgr.SetQueue("dev", ids, 0);
        _mgr.ShuffleRemaining("dev", "0");
        _mgr.RestoreOrder("dev");
        DeviceQueue q = _mgr.GetOrCreateQueue("dev");

        Assert.Equal("Default", q.PlaybackOrder);
        Assert.Null(q.OriginalItemIds);
        Assert.Equal(ids, q.ItemIds);
    }

    public void Dispose()
    {
        try { System.IO.Directory.Delete(_dir, true); } catch { }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~DeviceQueueShuffleTests"`
Expected: FAIL — `DeviceQueueManager` does not define `ShuffleRemaining` / `RestoreOrder`, and `DeviceQueue` has no `OriginalItemIds`.

- [ ] **Step 3: Add `OriginalItemIds` to `DeviceQueue`**

In `Alexa/Playback/DeviceQueue.cs`, add after the `PlaybackOrder` property:
```csharp
/// <summary>
/// Gets or sets the original (pre-shuffle) order of <see cref="ItemIds"/>.
/// Populated by <see cref="DeviceQueueManager.ShuffleRemaining"/> so that
/// <see cref="DeviceQueueManager.RestoreOrder"/> can un-shuffle. Null when not shuffling.
/// </summary>
public List<string>? OriginalItemIds { get; set; }
```

- [ ] **Step 4: Implement `ShuffleRemaining` and `RestoreOrder`**

In `Alexa/Playback/DeviceQueueManager.cs`, add:
```csharp
/// <summary>
/// Shuffles the queue items after the currently-playing item, keeping the
/// current item first. Snapshots the original order into <see cref="DeviceQueue.OriginalItemIds"/>
/// so <see cref="RestoreOrder"/> can revert. No-op for queues with fewer than 3 items.
/// Used by ShuffleOnIntentHandler so sequential advancement plays a shuffled order.
/// </summary>
public void ShuffleRemaining(string deviceId, string currentItemId)
{
    if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue))
    {
        _logger.LogDebug("ShuffleRemaining: no queue for device {DeviceId}", deviceId);
        return;
    }

    if (queue.ItemIds.Count < 3)
    {
        return;
    }

    int current = queue.ItemIds.IndexOf(currentItemId);
    if (current < 0 || current >= queue.ItemIds.Count - 2)
    {
        // current is last or not found — nothing meaningful to shuffle
        return;
    }

    // Snapshot original order only on first shuffle (don't clobber on repeat toggles)
    queue.OriginalItemIds ??= new List<string>(queue.ItemIds);

    List<string> head = queue.ItemIds.Take(current + 1).ToList();
    List<string> tail = queue.ItemIds.Skip(current + 1).ToList();
    ShuffleInPlace(tail);

    queue.ItemIds = head.Concat(tail).ToList();
    queue.PlaybackOrder = "Shuffle";
    queue.LastModifiedUtc = DateTime.UtcNow;
    SchedulePersistInternal(deviceId);

    _logger.LogDebug("ShuffleRemaining: shuffled tail for device {DeviceId} after index {Index}", deviceId, current);
}

/// <summary>
/// Restores the queue to its pre-shuffle order. No-op if not currently shuffled.
/// Used by ShuffleOffIntentHandler.
/// </summary>
public void RestoreOrder(string deviceId)
{
    if (!_queues.TryGetValue(deviceId, out DeviceQueue? queue) || queue.OriginalItemIds == null)
    {
        return;
    }

    queue.ItemIds = new List<string>(queue.OriginalItemIds);
    queue.OriginalItemIds = null;
    queue.PlaybackOrder = "Default";
    queue.LastModifiedUtc = DateTime.UtcNow;
    SchedulePersistInternal(deviceId);

    _logger.LogDebug("RestoreOrder: restored original order for device {DeviceId}", deviceId);
}

private static void ShuffleInPlace<T>(IList<T> list)
{
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = Random.Shared.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~DeviceQueueShuffleTests"`
Expected: PASS (all 3).

> **Note on `Random.Shared`:** non-deterministic, so the test asserts "tail differs from original" which holds with overwhelming probability for 20 items. If it ever flakes, increase the count or seed a dedicated `Random` injected via constructor. Do not seed `Random.Shared` (it is process-global).

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueue.cs \
        Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs \
        Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueShuffleTests.cs
git commit -m "feat(playback): add ShuffleRemaining/RestoreOrder to DeviceQueueManager"
```

---

### Task 3: Wire `DeviceQueueManager` into `ShuffleOnIntentHandler`

**Files:**
- Modify: `Alexa/Handler/Intent/ShuffleOnIntentHandler.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Handler/ShuffleOnIntentHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

Mirror the existing handler-test style (see `Jellyfin.Plugin.AlexaSkill.Tests/Handler/PlayArtistSongsIntentHandlerTests.cs` for the mock `ISessionManager` + `PluginConfiguration` + `ILoggerFactory` setup). Key assertions:
```csharp
[Fact]
public async Task HandleAsync_SetsShuffleState_AndReshufflesQueueTail()
{
    // Arrange: DeviceQueueManager with a 10-item queue at index 0
    var mgr = new DeviceQueueManager(_tempDir, NullLogger<DeviceQueueManager>.Instance);
    mgr.SetQueue("device-1", Enumerable.Range(0, 10).Select(i => i.ToString()).ToList(), 0);
    var handler = new ShuffleOnIntentHandler(_sessionManager.Object, _config, NullLoggerFactory.Instance, mgr);

    // Act: build a request whose context.AudioPlayer.Token == "0"
    var response = await handler.HandleAsync(_request, _contextWithToken("0"), _user, _session, default);

    // Assert
    DeviceQueue q = mgr.GetOrCreateQueue("device-1");
    Assert.Equal("Shuffle", q.PlaybackOrder);
    Assert.NotNull(q.OriginalItemIds);
    Assert.Equal("0", q.ItemIds[0]);               // current stays first
    Assert.Equal(10, q.ItemIds.Count);
    // session.NowPlayingQueue tail also reshuffled
    Assert.Equal(10, _session.NowPlayingQueue.Count);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~ShuffleOnIntentHandlerTests"`
Expected: FAIL — handler has no `DeviceQueueManager` ctor parameter.

- [ ] **Step 3: Modify the handler**

`Alexa/Handler/Intent/ShuffleOnIntentHandler.cs` — add the dependency and the reshuffle:
```csharp
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;   // add to usings

public class ShuffleOnIntentHandler : BaseHandler
{
    private readonly DeviceQueueManager? _queueManager;

    public ShuffleOnIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _queueManager = queueManager;
    }

    public override bool CanHandle(Request request) =>
        request is IntentRequest ir
        && string.Equals(ir.Intent.Name, IntentNames.AmazonShuffleOn, StringComparison.Ordinal);

    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        PlaybackState requestState = context.AudioPlayer;
        string deviceId = context.System.Device.DeviceID;
        string? currentToken = requestState.Token;

        Logger.LogDebug("ShuffleOn: entered, token={Token}, offset={OffsetMs}ms", currentToken, requestState.OffsetInMilliseconds);

        // 1) Keep Jellyfin's session PlayState in sync (for the dashboard UI).
        long positionTicks = TimeSpan.FromMilliseconds(requestState.OffsetInMilliseconds).Ticks;
        PlaybackProgressInfo info = new PlaybackProgressInfo
        {
            SessionId = session.Id,
            ItemId = new Guid(currentToken),
            RepeatMode = session.PlayState.RepeatMode,
            PositionTicks = positionTicks,
            PlaybackOrder = PlaybackOrder.Shuffle,
        };
        await SessionManager.OnPlaybackProgress(info, true).ConfigureAwait(false);

        // 2) Authoritative plugin-side state: persisted per-device, read by the resolver.
        _queueManager?.SetPlaybackOrder(deviceId, "Shuffle");

        // 3) Physically reorder the remaining queue (both persisted device queue and
        //    the operative session.NowPlayingQueue that ResolveNextItemId reads).
        if (_queueManager != null && Guid.TryParse(currentToken, out Guid currentId))
        {
            _queueManager.ShuffleRemaining(deviceId, currentToken);
            MirrorQueueToSession(_queueManager.GetOrCreateQueue(deviceId), session);
        }

        return ResponseBuilder.Empty();
    }

    /// <summary>
    /// Rebuilds session.NowPlayingQueue from the device queue's current order so the
    /// (possibly reshuffled) order is what PlaybackNearlyFinished advances through.
    /// </summary>
    private static void MirrorQueueToSession(DeviceQueue queue, SessionInfo session)
    {
        if (queue.ItemIds.Count == 0)
        {
            return;
        }

        var existing = session.NowPlayingQueue
            .Where(q => Guid.TryParse(q.PlaylistItemId, out _)) // preserve PlaylistItemId mapping by id
            .ToDictionary(q => q.Id);

        var rebuilt = new List<QueueItem>(queue.ItemIds.Count);
        foreach (string id in queue.ItemIds)
        {
            if (Guid.TryParse(id, out Guid g) && existing.TryGetValue(g, out QueueItem? qi))
            {
                rebuilt.Add(qi);
            }
            else if (Guid.TryParse(id, out g))
            {
                rebuilt.Add(new QueueItem { Id = g });
            }
        }
        session.NowPlayingQueue = rebuilt;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~ShuffleOnIntentHandlerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ShuffleOnIntentHandler.cs \
        Jellyfin.Plugin.AlexaSkill.Tests/Handler/ShuffleOnIntentHandlerTests.cs
git commit -m "fix(shuffle): make ShuffleOn set authoritative state and reshuffle queue"
```

---

### Task 4: Wire `DeviceQueueManager` into `ShuffleOffIntentHandler` (symmetric)

**Files:**
- Modify: `Alexa/Handler/Intent/ShuffleOffIntentHandler.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Handler/ShuffleOffIntentHandlerTests.cs`

- [ ] **Step 1: Write the failing test**

Assert that after `HandleAsync`, `DeviceQueue.PlaybackOrder == "Default"`, `OriginalItemIds == null`, and `ItemIds` is back to original order; `session.NowPlayingQueue` mirrored.

- [ ] **Step 2: Run → FAIL** (no `DeviceQueueManager` ctor param).

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~ShuffleOffIntentHandlerTests"`

- [ ] **Step 3: Implement** — same ctor injection pattern as Task 3; in `HandleAsync`:
```csharp
_queueManager?.SetPlaybackOrder(deviceId, "Default");
_queueManager?.RestoreOrder(deviceId);
if (_queueManager != null)
{
    MirrorQueueToSession(_queueManager.GetOrCreateQueue(deviceId), session);
}
// keep PlaybackOrder.Default via OnPlaybackProgress for Jellyfin UI sync
```
(Factor `MirrorQueueToSession` into `BaseHandler` as a `protected static` so both handlers share it — see Task 5 refactor note.)

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** with `fix(shuffle): make ShuffleOff restore original queue order`.

---

## Chunk 3: Resolver reads authoritative state + quality polish

### Task 5: `ResolveNextItemId` reads `DeviceQueueManager` first; factor shared helper

**Files:**
- Modify: `Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs:287-368` (`ResolveNextItemId`)
- Modify: `Alexa/Handler/BaseHandler.cs` — promote `MirrorQueueToSession` to `protected static`.

- [ ] **Step 1: Write the failing test**

In `Jellyfin.Plugin.AlexaSkill.Tests/Handler/GaplessPlaybackTests.cs`, add a test where `session.PlayState.PlaybackOrder == Default` BUT the `DeviceQueueManager` queue has `PlaybackOrder == "Shuffle"`, and assert the resolved next item comes from the shuffled tail (i.e., the device queue wins).

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Update `ResolveNextItemId`**

Change the order-source resolution near the top of the method (`PlaybackNearlyFinishedEventHandler.cs:289-290`):
```csharp
RepeatMode repeatMode = session.PlayState?.RepeatMode ?? RepeatMode.RepeatNone;

// Authoritative source: the persisted per-device queue (written by ShuffleOn/Off).
// Fall back to Jellyfin session PlayState only when no device queue exists.
PlaybackOrder playbackOrder;
if (_queueManager != null)
{
    string order = _queueManager.GetOrCreateQueue(context.System.Device.DeviceID).PlaybackOrder;
    playbackOrder = string.Equals(order, "Shuffle", StringComparison.Ordinal)
        ? PlaybackOrder.Shuffle
        : PlaybackOrder.Default;
}
else
{
    playbackOrder = session.PlayState?.PlaybackOrder ?? PlaybackOrder.Default;
}
```

Because Task 3 already reshuffled `session.NowPlayingQueue`, the existing sequential-advance branch (`nextPos = currentIndex + 1`) now plays the shuffled order naturally. **Keep** the existing `if (playbackOrder == PlaybackOrder.Shuffle ...)` random-pick branch as a secondary mechanism — but since the queue is already reshuffled, it now picks within an already-randomized tail, which is acceptable. (If Task 1 Q2 revealed the random-pick branch is itself buggy, the reshuffle makes it moot.)

- [ ] **Step 4: Promote `MirrorQueueToSession` to `BaseHandler`** and have both shuffle handlers call the shared copy (remove the duplicated private copies from Tasks 3–4). Add a unit test asserting `MirrorQueueToSession` preserves `PlaylistItemId` for matching ids and drops nothing.

- [ ] **Step 5: Run → PASS** (new test + full `GaplessPlaybackTests` suite).

Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests --filter "FullyQualifiedName~GaplessPlaybackTests"`

- [ ] **Step 6: Commit** with `fix(shuffle): resolver reads DeviceQueueManager order; share MirrorQueueToSession`.

---

### Task 6: Shuffle quality — avoid immediate repeats (OPTIONAL polish)

Only do this if, after Tasks 2–5, on-device testing still shows unsatisfying repetition (the random-pick branch can revisit a recently-played track).

**Files:**
- Modify: `Alexa/Playback/DeviceQueue.cs` — add `RecentItemIds` (small ring buffer, capacity ~5).
- Modify: `Alexa/Playback/DeviceQueueManager.cs` — `RecordPlayed(deviceId, itemId)` appends to ring buffer.
- Modify: `PlaybackNearlyFinishedEventHandler` — call `RecordPlayed` on each advance; filter `RecentItemIds` out of the random-pick candidate pool.

TDD: test that two consecutive random picks differ and that with a 5-item queue + capacity-5 history no track repeats within a full cycle. Commit: `feat(shuffle): avoid immediate track repeats`.

---

## Chunk 4: Dynamic-entities defense + tests

### Task 7: Defensive guard in `DynamicEntitiesInterceptor`

**Context / confidence:** The reporter saw whole-library dynamic entities (artists/songs not in the playlist) in the `ShuffleOn` response. Reading `DynamicEntitiesInterceptor.ProcessAsync`, the mid-session path returns early for non-TV/non-book intents — so this symptom most likely occurs when Alexa classifies the utterance as a **new session** (the new-session branch injects artists/albums/last-played unconditionally). Regardless of exact trigger, built-in playback-control intents should never carry whole-library entities. This is a low-risk defensive fix.

**Files:**
- Modify: `Alexa/DynamicEntities/DynamicEntitiesInterceptor.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/DynamicEntities/DynamicEntitiesInterceptorTests.cs`

- [ ] **Step 1: Write the failing test**

Assert that for a `LaunchRequest` whose... no — assert for an `IntentRequest` named `AMAZON.ShuffleOnIntent` marked `session.New = true`, `ProcessAsync` does **not** add a `Dialog.UpdateDynamicEntities` directive. (Currently it would, because new-session injects unconditionally.)

- [ ] **Step 2: Run → FAIL.**

- [ ] **Step 3: Add the guard**

In `DynamicEntitiesInterceptor.ProcessAsync`, right after the AudioPlayer-directive skip (after line 58), add:
```csharp
// Built-in playback-control intents must never trigger a whole-library
// dynamic-entity refresh (issue #10 follow-up: ShuffleOn leaked the entire
// catalog). They carry no slot to resolve.
if (IsPlaybackControlIntent(intentName))
{
    return;
}
```
and add the helper:
```csharp
private static readonly HashSet<string> PlaybackControlIntents = new(StringComparer.Ordinal)
{
    "AMAZON.ShuffleOnIntent", "AMAZON.ShuffleOffIntent",
    "AMAZON.NextIntent", "AMAZON.PreviousIntent",
    "AMAZON.LoopOnIntent", "AMAZON.LoopOffIntent",
    "AMAZON.RepeatOnIntent", "AMAZON.RepeatOffIntent",
    "AMAZON.PauseIntent", "AMAZON.ResumeIntent",
    "AMAZON.StopIntent", "AMAZON.CancelIntent",
    "AMAZON.StartOverIntent",
};

private static bool IsPlaybackControlIntent(string? intentName) =>
    intentName != null && PlaybackControlIntents.Contains(intentName);
```

- [ ] **Step 4: Run → PASS.**
- [ ] **Step 5: Commit** with `fix(dynamic-entities): skip whole-library refresh on playback-control intents`.

---

## Chunk 5: Full-suite, E2E, verification, close the loop

### Task 8: Build, full unit suite, deploy, verify

- [ ] **Step 1: Full build (0 warnings, `-warnaserror`)**
Run: `dotnet build Jellyfin.Plugin.AlexaSkill.sln`
Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Full unit suite** (NEVER `--no-build` after changes — project rule)
Run: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests`
Expected: all pass (~2302 + new tests).

- [ ] **Step 3: Deploy per `.claude.local.md` deploy checklist** — BACKUP config first, deploy into the CURRENT versioned dir, verify the ACTIVE dll, restore config, discover skill ID, enable skill.

- [ ] **Step 4: On-device / simulator verification matrix**
- Play a playlist (it-IT) → say "modalità casuale" → confirm next 3 tracks are out of original order and no track repeats immediately.
- Say "disattiva modalità casuale" (shuffle off) → confirm order returns to original.
- During shuffle, inspect the `ShuffleOn` response → confirm NO `Dialog.UpdateDynamicEntities` directive (whole-library leak gone).
- Check logs: `ShuffleOn: entered` present; subsequent `Pre-fetching next track ... shuffle=Shuffle` with reshuffled items.

- [ ] **Step 5: E2E via SMAPI simulate-skill** (it-IT, reliable; en-US competes with built-ins). Add a fixture `tests/integration/fixtures/e2e_it-IT.yaml` case covering `ShuffleOnIntent` mid-playlist if feasible (note: `simulate-skill` may not model AudioPlayer queue advancement — if it can't, cover the handler-level Simulator endpoint instead per `.claude/CLAUDE.md`).

Run:
```bash
./scripts/run_e2e_tests.sh -k "shuffle" \
  --jellyfin-url "$JELLYFIN_URL" \
  --jellyfin-api-key "$JELLYFIN_API_KEY" \
  --jellyfin-user "$JELLYFIN_USER" -v
```

- [ ] **Step 6: Commit any new fixtures** with `test(e2e): add shuffle playlist coverage`.

- [ ] **Step 7: Reply on issue #10** — comment that shuffle support is implemented in the next release build, summarizing the root cause (flag set on Jellyfin PlayState but the plugin's authoritative per-device queue wasn't consulted, so the resolver kept sequential order) and the fix. Offer a dev build via `dev-build.yml` (`workflow_dispatch`) for the reporter to test (per memory `dev_build_reporter_only.md`).

---

## Risks & Notes

- **DI is additive and safe.** `DeviceQueueManager?` is an optional ctor param defaulting to null, resolved via the existing DI/ActivatorUtilities pipeline (same pattern as `PlayPlaylistIntentHandler`). Existing tests that construct the handler without it keep compiling.
- **`session.NowPlayingQueue` is the operative list.** `ResolveNextItemId` reads it (not `DeviceQueue.ItemIds`), so the reshuffle must be mirrored into it — that's what `MirrorQueueToSession` does. `DeviceQueue.ItemIds` is the persisted original-order reference and crash-recovery source.
- **Progressive/continuation queues.** For playlists larger than `GetInitialFetchSize()`, `TryFetchContinuationBatch` appends more items to `session.NowPlayingQueue` near the end. Reshuffle happens at ShuffleOn time on the currently-loaded slice; newly fetched batches append in DB order. Acceptable for v1; if users want fully-shuffled large playlists, a follow-up can shuffle appended batches when `PlaybackOrder == Shuffle`.
- **Do not seed `Random.Shared`.** It is process-global and shared. The Fisher–Yates in `ShuffleInPlace` uses it directly.
- **Task 1 is a hard gate.** If the diagnostic shows `ShuffleOn` is never entered (routing), stop and re-plan around `Alexa.Media.PlayQueue` `SetShuffle` (JF-218) — Tasks 2–7 would not fix that case.

## Skills referenced
- @superpowers:executing-plans (or @superpowers:subagent-driven-development) — to execute this plan
- @superpowers:test-driven-development — every task is red→green
- @superpowers:verification-before-completion — Task 8 verification matrix
- @deploy — deploy checklist (`.claude.local.md`)
