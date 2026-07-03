# Shuffle-at-Start Playlist Qualifier — Implementation Plan (JF-305)

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users start a playlist already shuffled ("shuffle the playlist X" / "play the playlist X in shuffle mode") so the first track is random, via a new `ShufflePlayIntent`, reusing JF-301's authoritative `DeviceQueueManager` shuffle state.

**Architecture:** A new additive `DeviceQueueManager.SetShuffledQueue(deviceId, itemIds, Random?)` shuffles the *whole* queue (position 0 included) and snapshots `OriginalItemIds`. The shared playlist-resolution flow lives as a new **`protected` method `BaseHandler.BuildPlaylistPlayResponseAsync(...)`** (deps `ILibraryManager`/`IUserManager`/`DeviceQueueManager` passed as parameters — `BaseHandler`'s ctor doesn't hold them, but being on `BaseHandler` gives natural access to all the `protected` helpers like `FuzzyMatch`/`HandleFuzzyMiss`/`MirrorQueueToSession`). Both `PlayPlaylistIntentHandler` (refactored, `shuffle:false`) and the new `ShufflePlayIntentHandler` (`shuffle:true`) call it. A new `ShufflePlayIntent` (single `AMAZON.SearchQuery` `playlist` slot) is added to all 17 locales (it-IT via YAML `explicit_intents`) + `dialog.intents`.

**Tech Stack:** C# net9.0, xUnit, Alexa.NET, Jellyfin 10.11+ APIs. Python for model generation/validation + pytest NLU/E2E.

**Spec:** `docs/superpowers/specs/2026-07-03-shuffle-play-qualifier-design.md`

**Refinement vs spec (flagged, review-driven):** The spec placed the shared method on `BaseHandler`; an earlier plan draft moved it to a `PlaylistPlayBuilder` service, but that **cannot compile** — `FuzzyMatch`/`HandleFuzzyMiss`/`SafeGetItemsResult`/`RetryAsync` (protected instance) and `MirrorQueueToSession`/`GetLocale`/`ResolveJellyfinUser`/`ApplyLibraryFilter` (protected static) are inaccessible from a non-subclass (CS0122). So the shared flow returns to a `protected` method **on `BaseHandler`** (`BuildPlaylistPlayResponseAsync`), with the 3 extra deps as method parameters. No new class, no DI change, no access-modifier promotions. Concept unchanged.

**Conventions (from CLAUDE.md):** `Nullable enable`; `TreatWarningsAsErrors=true` so build with `-warnaserror`; `async/await` + `ConfigureAwait(false)`; NEVER use `dotnet test --no-build` after code changes; it-IT model is generated from YAML (edit template, regenerate); new intent → `dialog.intents` registration in all 17 locales.

**Verified code facts (plan-reviewer, 2026-07-03):**
- `DeviceQueueManager` is `sealed : IDisposable`; fields `_queues`, `_logger`; helper `SchedulePersistInternal(deviceId)`; `SetQueue` ends ~line 146; `ShuffleRemaining` FY loop at lines 304–308. `DeviceQueue` has `ItemIds`, `OriginalItemIds`, `CurrentIndex`, `RepeatMode`, `PlaybackOrder`, `LastModifiedUtc`, `ItemPositionState`.
- `BaseHandler` ctor is `(ISessionManager, PluginConfiguration, ILoggerFactory)` only. Helpers: `FuzzyMatch` (protected instance, :1156), `HandleFuzzyMiss` (:1245), `SafeGetItemsResult` (:1049), `RetryAsync` (:1037), `MirrorQueueToSession` (protected static, :1375), `GetLocale` (static, :861), `ResolveJellyfinUser` (static, :1739), `ApplyLibraryFilter` (static, :962), `GetStreamUrl` (public, :374), `BuildAudioPlayerResponse` (public, :486/502). All reachable from a `protected` method ON `BaseHandler`.
- Registrar: `Jellyfin.Plugin.AlexaSkill/EntryPoints/Regulator.cs` (project root, NOT under `Alexa/`). `DeviceQueueManager` registered `AddSingleton` with factory at `:36-41`; `BaseHandler` reflection scan at `:100-119` (`AddSingleton(typeof(BaseHandler), handlerType)`) → MS DI resolves ctor params, so a handler adding `ILibraryManager`/`IUserManager`/`DeviceQueueManager?` params auto-resolves.
- Scripts exist: `scripts/generate_interaction_model.py` (arg = locale), `validate_interaction_models.py`, `validate_locales.py`, `validate_versions.py`, `run_nlu_tests.sh`, `run_e2e_tests.sh`.

**Shared context — build/test env (memory):** Redirect NuGet caches to /tmp before building in the sandbox:
```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
```

---

## Chunk 1: `DeviceQueueManager.SetShuffledQueue` (additive)

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs` (add `SetShuffledQueue` + private `FisherYates`; do NOT modify `ShuffleRemaining` — honors the spec non-goal literally)
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueManagerTests.cs`

### Task 1.1: Failing tests for `SetShuffledQueue`

- [ ] **Step 1: Write failing tests** — append to `DeviceQueueManagerTests.cs`:

```csharp
// =====================================================================
// SetShuffledQueue (JF-305: shuffle-at-start playlist qualifier)
// =====================================================================

[Fact]
public void SetShuffledQueue_ShufflesAllItems_StoresOriginal_SetsShuffleState()
{
    List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
    _manager.SetShuffledQueue("dev", ids, new Random(42));

    DeviceQueue q = _manager.GetOrCreateQueue("dev");

    Assert.Equal("Shuffle", q.PlaybackOrder);
    Assert.Equal(0, q.CurrentIndex);
    Assert.NotNull(q.OriginalItemIds);
    Assert.Equal(ids, q.OriginalItemIds);                                       // pre-shuffle order preserved
    Assert.Equal(ids.Count, q.ItemIds.Count);                                   // no loss/duplication
    Assert.Equal(new HashSet<string>(ids), new HashSet<string>(q.ItemIds));     // same set of ids
    Assert.NotEqual(ids, q.ItemIds);                                            // order changed (full list, incl pos 0)
}

[Fact]
public void SetShuffledQueue_MatchesSeededFisherYates()
{
    // Deterministic: replicate the exact seeded permutation the impl must produce.
    List<string> ids = Enumerable.Range(0, 20).Select(i => i.ToString()).ToList();
    List<string> expected = new(ids);
    var rngExpected = new Random(42);
    for (int i = expected.Count - 1; i > 0; i--)
    {
        int j = rngExpected.Next(i + 1);
        (expected[i], expected[j]) = (expected[j], expected[i]);
    }

    _manager.SetShuffledQueue("dev", ids, new Random(42));
    DeviceQueue q = _manager.GetOrCreateQueue("dev");

    Assert.Equal(expected, q.ItemIds);
    Assert.Equal(ids, q.OriginalItemIds);
    Assert.NotEqual(ids[0], q.ItemIds[0]);   // position 0 changed — the FR's core requirement
}

[Fact]
public void SetShuffledQueue_SmallQueue_StillSetsState_PreservesItems()
{
    var ids = new List<string> { "a", "b" };
    _manager.SetShuffledQueue("dev", ids, new Random(1));

    DeviceQueue q = _manager.GetOrCreateQueue("dev");

    Assert.Equal("Shuffle", q.PlaybackOrder);
    Assert.NotNull(q.OriginalItemIds);
    Assert.Equal(ids, q.OriginalItemIds);
    Assert.Equal(2, q.ItemIds.Count);
}

[Fact]
public void SetShuffledQueue_PreservesItemPositionStateAcrossReset()
{
    _manager.SetQueue("dev", new List<string> { "a", "b", "c" }, 0);
    _manager.GetOrCreateQueue("dev").ItemPositionState["a"] = 1234L;

    _manager.SetShuffledQueue("dev", new List<string> { "a", "b", "c" }, new Random(9));

    DeviceQueue q = _manager.GetOrCreateQueue("dev");
    Assert.Equal(1234L, q.ItemPositionState["a"]);
}
```

- [ ] **Step 2: Run tests — verify they fail (method doesn't exist yet)**

```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~SetShuffledQueue"
```
Expected: compile FAIL (`DeviceQueueManager does not contain a definition for SetShuffledQueue`).

### Task 1.2: Implement `SetShuffledQueue` + private `FisherYates`

- [ ] **Step 3: Add the method + helper** — in `DeviceQueueManager.cs`, immediately after `SetQueue` (after line ~146), add:

```csharp
/// <summary>
/// Sets a freshly-shuffled queue for a device. Snapshots the original order into
/// <see cref="DeviceQueue.OriginalItemIds"/>, Fisher–Yates shuffles ALL items
/// (including position 0, so the first-played track is random), and sets
/// PlaybackOrder=Shuffle + CurrentIndex=0. Used by <c>ShufflePlayIntentHandler</c>
/// to start a playlist already shuffled. <paramref name="rng"/> is injectable for
/// deterministic unit tests; defaults to the process-global Random.Shared.
/// ADDITIVE: does not alter SetQueue/ShuffleRemaining/RestoreOrder (JF-301 path).
/// </summary>
/// <param name="deviceId">The Alexa device ID.</param>
/// <param name="itemIds">The ordered media item IDs (pre-shuffle).</param>
/// <param name="rng">Optional seeded random for tests.</param>
public void SetShuffledQueue(string deviceId, List<string> itemIds, Random? rng = null)
{
    Random random = rng ?? Random.Shared;

    // Preserve ItemPositionState across queue resets (same behavior as SetQueue).
    Dictionary<string, long>? existingPositions = null;
    if (_queues.TryGetValue(deviceId, out DeviceQueue? oldQueue))
    {
        existingPositions = oldQueue.ItemPositionState;
    }

    List<string> original = new List<string>(itemIds);
    List<string> shuffled = new List<string>(itemIds);
    FisherYates(shuffled, random);

    var queue = new DeviceQueue
    {
        ItemIds = shuffled,
        OriginalItemIds = original,
        CurrentIndex = 0,
        RepeatMode = "None",
        PlaybackOrder = "Shuffle",
        LastModifiedUtc = DateTime.UtcNow,
        ItemPositionState = existingPositions ?? new Dictionary<string, long>()
    };

    _queues[deviceId] = queue;
    SchedulePersistInternal(deviceId);

    _logger.LogDebug(
        "Shuffled queue set for device {DeviceId}: {Count} items, order=Shuffle",
        deviceId, shuffled.Count);
}

/// <summary>Fisher–Yates shuffle, in place. Used by SetShuffledQueue.
/// (ShuffleRemaining keeps its own inline loop unchanged — spec non-goal.)</summary>
private static void FisherYates(List<string> list, Random rng)
{
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }
}
```

- [ ] **Step 4: Run the new tests — verify pass**

```bash
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~SetShuffledQueue"
```
Expected: 4 passed.

- [ ] **Step 5: Run the JF-301 shuffle regression tests — verify untouched**

```bash
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~ShuffleRemaining|FullyQualifiedName~RestoreOrder"
```
Expected: all pass (ShuffleRemaining was not modified).

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueManagerTests.cs
git commit -m "feat(playback): add DeviceQueueManager.SetShuffledQueue (JF-305)"
```

---

## Chunk 2: Shared `BaseHandler.BuildPlaylistPlayResponseAsync` (extract flow)

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` (add `protected` method)
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs` (becomes a thin caller)
- Test: keep `Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/PlayPlaylistIntentHandlerTests.cs` green (regression sentinel)

**Why a method on `BaseHandler`, not a service:** `BaseHandler`'s ctor is `(ISessionManager, PluginConfiguration, ILoggerFactory)` — it doesn't hold `ILibraryManager`/`IUserManager`/`DeviceQueueManager`. But the flow needs many `protected` helpers (`FuzzyMatch`, `HandleFuzzyMiss`, `SafeGetItemsResult`, `RetryAsync`, `MirrorQueueToSession`, `GetLocale`, `ResolveJellyfinUser`, `ApplyLibraryFilter`) that only a `BaseHandler` subclass (or a method ON `BaseHandler`) can reach. So: put the shared flow as a `protected` method on `BaseHandler`, pass the 3 extra deps as parameters. No ctor change, no DI change, no access-modifier promotions.

### Task 2.1: Add the shared method to `BaseHandler`

- [ ] **Step 1: Add `BuildPlaylistPlayResponseAsync` to `BaseHandler.cs`** — a `protected async Task<SkillResponse>` method. Move the existing body of `PlayPlaylistIntentHandler.HandleAsync` (current lines 77–232) into it. Signature:

```csharp
/// <summary>
/// Shared playlist-play flow used by PlayPlaylistIntent (shuffle=false) and
/// ShufflePlayIntent (shuffle=true). Resolves the playlist, builds the queue,
/// and — when shuffle is true — shuffles the whole queue at build time via
/// DeviceQueueManager.SetShuffledQueue so the first track is random.
/// </summary>
protected async Task<SkillResponse> BuildPlaylistPlayResponseAsync(
    ILibraryManager libraryManager,
    IUserManager userManager,
    Playback.DeviceQueueManager? queueManager,
    string playlistName,
    IntentRequest intentRequest,
    Context context,
    Entities.User user,
    SessionInfo session,
    string locale,
    bool shuffle,
    Random? rng,
    CancellationToken cancellationToken)
```

  Body = the existing `PlayPlaylistIntentHandler.HandleAsync` flow, with **one change** at the queue-state step (current lines 188–205). Replace those lines with:

```csharp
session.NowPlayingQueue = queueItems;  // ordered, so MirrorQueueToSession can read track metadata

string deviceId = context.System.Device.DeviceID;
List<string> idList = playlistItems.Select(i => i.Id.ToString()).ToList();
BaseItem firstItem;

if (shuffle && queueManager != null)
{
    queueManager.SetShuffledQueue(deviceId, idList, rng);
    // Mirror the shuffled DeviceQueue order back into the session queue (metadata preserved).
    MirrorQueueToSession(queueManager.GetOrCreateQueue(deviceId), session);
    string firstShuffledId = queueManager.GetOrCreateQueue(deviceId).ItemIds[0];
    firstItem = libraryManager.GetItemById(Guid.Parse(firstShuffledId));
}
else
{
    queueManager?.SetQueue(deviceId, idList, 0);
    firstItem = libraryManager.GetItemById(queueItems[0].Id);
}

if (firstItem == null)
{
    return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
}

session.FullNowPlayingItem = firstItem;
```

  The rest of the flow (the `QueueContinuationStore.Set` block and the final `return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, firstItem, user, context);`) is unchanged. Inside the method, replace the handler's field refs (`_libraryManager`→`libraryManager`, `_userManager`→`userManager`, `_queueManager`→`queueManager`); `ResolveJellyfinUser`, `ApplyLibraryFilter`, `SafeGetItemsResult`, `RetryAsync`, `FuzzyMatch`, `HandleFuzzyMiss`, `GetStreamUrl`, `BuildAudioPlayerResponse`, `MirrorQueueToSession`, `GetLocale`, `Logger`, `SessionManager` are all reachable because this method is on `BaseHandler`. Add `using MediaBrowser.Controller.Playlists;` if not already present (for `PlaylistTrackResolver`).

  The required `using` directives are the same set already at the top of `PlayPlaylistIntentHandler.cs` (copy them into `BaseHandler.cs` if missing — `Jellyfin.Data.Enums`, `MediaBrowser.Controller.Dto`, `MediaBrowser.Model.Entities`, `MediaBrowser.Model.Querying`, `MediaBrowser.Controller.Playlists`).

### Task 2.2: Regression — refactor `PlayPlaylistIntentHandler` to call the shared method

- [ ] **Step 2: Verify/seed the regression test.** Open `Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/PlayPlaylistIntentHandlerTests.cs`. If a test asserts "given a matching playlist, the response plays the first track in original order with `DeviceQueue.PlaybackOrder == Default`," keep it. If none exists, add one:

```csharp
[Fact]
public async Task HandleAsync_NonShuffle_PlaysFirstTrack_Ordered()
{
    // ... existing harness setup (matching playlist, mocked ILibraryManager returns tracks) ...
    var response = await _handler.HandleAsync(_request, _context, _user, _session, default);
    // Assert: first streamed item is the playlist's first track (ordered),
    // and (if queueManager is observable) DeviceQueue.PlaybackOrder == "Default".
}
```

- [ ] **Step 3: Refactor `PlayPlaylistIntentHandler.HandleAsync`** to a thin caller:

```csharp
public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
{
    string locale = GetLocale(request);
    IntentRequest intentRequest = (IntentRequest)request;
    string? playlistName = intentRequest.Intent.Slots?.TryGetValue("playlist", out var slot) == true ? slot.Value : null;
    return BuildPlaylistPlayResponseAsync(
        _libraryManager, _userManager, _queueManager,
        playlistName ?? string.Empty, intentRequest, context, user, session, locale,
        shuffle: false, rng: null, cancellationToken);
}
```

  (`_libraryManager`, `_userManager`, `_queueManager` already exist on `PlayPlaylistIntentHandler`. The `using static`/`IntentRequest`/`Context`/`Entities.User`/`SessionInfo` namespaces are already imported.)

- [ ] **Step 4: Build + run full test suite (no `--no-build`)**

```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj
```
Expected: 0 build warnings; all tests pass — the existing `PlayPlaylistIntentHandler` tests are the sentinel that the refactor is behavior-preserving (the `shuffle:false` branch is identical to the old code).

- [ ] **Step 5: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs \
        Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs \
        Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/PlayPlaylistIntentHandlerTests.cs
git commit -m "refactor(handler): extract BuildPlaylistPlayResponseAsync onto BaseHandler (JF-305)"
```

---

## Chunk 3: `ShufflePlayIntentHandler` + `IntentNames`

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs` (add `ShufflePlay`)
- Create: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ShufflePlayIntentHandler.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/ShufflePlayIntentHandlerTests.cs`

### Task 3.1: Register the intent name

- [ ] **Step 1: Add constant** in `IntentNames.cs` near the play intents (after `PlayPlaylist`):

```csharp
public const string ShufflePlay = "ShufflePlayIntent";
```

### Task 3.2: Failing handler test

- [ ] **Step 2: Write failing test** — `ShufflePlayIntentHandlerTests.cs`. Mirror the existing handler-test pattern in `Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/` (mock `ISessionManager`, `PluginConfiguration`, `ILoggerFactory`; instantiate the handler directly with real `ILibraryManager`/`IUserManager`/`DeviceQueueManager?` stubs or mocks). Assert delegation to the shared method with `shuffle:true`:

```csharp
[Fact]
public void CanHandle_ShufflePlayIntent()
{
    var handler = new ShufflePlayIntentHandler(_sessionManager, _config, _libraryManager, _userManager, _queueManager, _loggerFactory);
    var req = new IntentRequest { Intent = new Intent { Name = IntentNames.ShufflePlay } };
    Assert.True(handler.CanHandle(req));
}

[Fact]
public async Task HandleAsync_DelegatesToSharedFlow_WithShuffleTrue()
{
    // With a matching playlist stubbed in ILibraryManager and a seeded DeviceQueueManager,
    // the response must reflect a shuffled queue (PlaybackOrder=Shuffle, OriginalItemIds set,
    // first item != playlist's first track for a multi-track fixture).
    var handler = new ShufflePlayIntentHandler(_sessionManager, _config, _libraryManager, _userManager, _queueManager, _loggerFactory);
    var response = await handler.HandleAsync(_request, _context, _user, _session, default);
    DeviceQueue q = _queueManager.GetOrCreateQueue(_deviceId);
    Assert.Equal("Shuffle", q.PlaybackOrder);
    Assert.NotNull(q.OriginalItemIds);
}
```

  (The exact harness setup — mock `ILibraryManager.GetItemsResult`, `IUserManager.GetUserById`, a `TempDir`-backed `DeviceQueueManager` — mirrors `PlayPlaylistIntentHandlerTests`. If that harness isn't easily reusable, a thinner test asserting only `CanHandle` + that `HandleAsync` calls the shared path via a spy is acceptable; the end-to-end shuffle behavior is covered by the `DeviceQueueManager.SetShuffledQueue` unit tests in Chunk 1 and the E2E in Chunk 5.)

- [ ] **Step 3: Run — verify fail** (`ShufflePlayIntentHandler` doesn't exist yet).

### Task 3.3: Implement the handler

- [ ] **Step 4: Create `ShufflePlayIntentHandler.cs`** — mirror `PlayPlaylistIntentHandler`'s ctor (same deps: `sessionManager, config, libraryManager, userManager, loggerFactory, queueManager`) but delegate with `shuffle:true`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for ShufflePlayIntent — play a playlist already shuffled
/// (first track random). Shares the playlist-play flow with PlayPlaylistIntent.
/// </summary>
public class ShufflePlayIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly Playback.DeviceQueueManager? _queueManager;

    public ShufflePlayIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        Playback.DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _queueManager = queueManager;
    }

    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null
            && string.Equals(intentRequest.Intent.Name, IntentNames.ShufflePlay, StringComparison.Ordinal);
    }

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? playlistName = intentRequest.Intent.Slots?.TryGetValue("playlist", out var slot) == true ? slot.Value : null;
        return BuildPlaylistPlayResponseAsync(
            _libraryManager, _userManager, _queueManager,
            playlistName ?? string.Empty, intentRequest, context, user, session, locale,
            shuffle: true, rng: null, cancellationToken);
    }
}
```

  Auto-DI-registered by the `BaseHandler` reflection scan at `EntryPoints/Regulator.cs:100-119`; MS DI resolves the ctor params (incl. `DeviceQueueManager?` singleton). No manual registration.

- [ ] **Step 5: Build + test**

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~ShufflePlayIntentHandler"
```
Expected: pass.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs \
        Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ShufflePlayIntentHandler.cs \
        Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/ShufflePlayIntentHandlerTests.cs
git commit -m "feat(alexa): add ShufflePlayIntentHandler (JF-305)"
```

---

## Chunk 4: Interaction model — all 17 locales

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/it-IT.yaml` (`explicit_intents` + `dialog.intents`)
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json` (regenerated)
- Modify: the other 16 `model_*.json` (add `ShufflePlayIntent` to `languageModel.intents` + `dialog.intents`)

**Anti-patterns (CLAUDE.md):** #2 `AMAZON.SearchQuery` alone (OK — single slot); #3 NLU competition (verify via profile-nlu in Chunk 5); #4 all 17 locales; #6 do NOT expand `vocabulary` (use `explicit_intents`); #9 register in `dialog.intents`.

### Task 4.1: it-IT YAML template

- [ ] **Step 1: Add to `explicit_intents`** in `it-IT.yaml`:

```yaml
  ShufflePlayIntent:
    samples:
      - "Mescola la playlist {playlist}"
      - "Mescola playlist {playlist}"
      - "Riproduci la playlist {playlist} in modalità casuale"
      - "Riproduci la playlist {playlist} a caso"
      - "Suona la playlist {playlist} in modalità casuale"
    slots:
      - name: playlist
        type: AMAZON.SearchQuery
```

- [ ] **Step 2: Add to `dialog.intents`** in the same YAML:

```yaml
    - name: ShufflePlayIntent
      confirmationRequired: false
      slots:
        - name: playlist
          type: AMAZON.SearchQuery
          confirmationRequired: false
          elicitationRequired: false
```

- [ ] **Step 3: Regenerate + validate**

```bash
python3 scripts/generate_interaction_model.py it-IT
python3 scripts/validate_interaction_models.py
python3 scripts/validate_locales.py
```
Expected: 0 errors. Confirm `ShufflePlayIntent` present in `model_it-IT.json` `languageModel.intents` AND `dialog.intents`.

### Task 4.2: Other 16 locales (direct JSON edit, with translation table)

- [ ] **Step 4: For each locale below, add a `ShufflePlayIntent`** to its `model_<locale>.json` `languageModel.intents` (localized samples + single `AMAZON.SearchQuery` `playlist` slot) AND a matching `dialog.intents` entry (same shape as Task 4.1 Step 2). Use these concrete samples (slot name `playlist`, type `AMAZON.SearchQuery` in every locale; **flagged for native review** — `*` marks lower-confidence phrasings):

| Locale | Samples |
|---|---|
| en-US, en-AU, en-CA, en-GB, en-IN | `shuffle the playlist {playlist}` · `play the playlist {playlist} in shuffle mode` · `play the playlist {playlist} on shuffle` · `play {playlist} shuffled` |
| es-MX, es-ES, es-US | `reproduce la lista {playlist} en modo aleatorio` · `mezcla la lista {playlist}` · `reproduce la lista de reproducción {playlist} en modo aleatorio` |
| fr-FR, fr-CA | `lis la playlist {playlist} en mode aléatoire` · `mélange la playlist {playlist}` · `mets la playlist {playlist} en mode aléatoire` |
| de-DE | `spiele die Playlist {playlist} in zufälliger Reihenfolge` · `mische die Playlist {playlist}` · `spiele die Playlist {playlist} im Zufallsmodus` |
| pt-BR | `toque a playlist {playlist} em modo aleatório` · `embaralhe a playlist {playlist}` |
| nl-NL | `speel de playlist {playlist} in willekeurige volgorde` * · `shuffle de playlist {playlist}` * |
| hi-IN | `प्लेलिस्ट {playlist} शफल में चलाओ` * · `प्लेलिस्ट {playlist} शफल करो` * |
| ja-JP | `プレイリスト {playlist} をシャッフルで再生して` · `シャッフルでプレイリスト {playlist} を再生して` |

  For each locale, the JSON intent object shape:
```json
{
  "name": "ShufflePlayIntent",
  "samples": [ "..." ],
  "slots": [ { "name": "playlist", "type": "AMAZON.SearchQuery" } ]
}
```
  Locale response strings: none added (reuses existing `NotFoundPlaylist`/`PlaylistEmpty`/`DidNotCatchPlaylistName`/`MediaNotFound`). If a native speaker reviews the `*` locales, update before release.

- [ ] **Step 5: Validate cross-locale consistency**

```bash
python3 scripts/validate_interaction_models.py
python3 scripts/validate_versions.py
```
Expected: 0 errors; cross-locale drift check confirms `ShufflePlayIntent` exists in all 17 with consistent slot name/type.

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/
git commit -m "feat(models): add ShufflePlayIntent to all 17 locales (JF-305)"
```

---

## Chunk 5: NLU fixtures + integration/E2E + profile-nlu gate

**Files:**
- Modify: `tests/integration/fixtures/<locale>.yaml` (NLU fixtures; it-IT + en-US minimum)
- Modify: `tests/integration/fixtures/e2e_it-IT.yaml` (E2E)

### Task 5.1: NLU fixtures

- [ ] **Step 1: Add fixtures** to `tests/integration/fixtures/it-IT.yaml` and `en-US.yaml`:

```yaml
- id: "mescola la playlist variado"
  expected_intent: ShufflePlayIntent
  expected_slots:
    playlist: variado
- id: "riproduci la playlist variado in modalità casuale"
  expected_intent: ShufflePlayIntent
  expected_slots:
    playlist: variado
- id: "riproduci la playlist variado"           # regression sentinel — must stay PlayPlaylistIntent
  expected_intent: PlayPlaylistIntent
```

- [ ] **Step 2: Dry-run fixtures (no SMAPI)**

```bash
./scripts/run_nlu_tests.sh --dry-run
```
Expected: fixtures parse cleanly.

### Task 5.2: Deploy candidate + profile-nlu gate (anti-pattern #3)

- [ ] **Step 1: Deploy the updated it-IT model** (per CLAUDE.md — discover the current skill ID fresh, do NOT trust cached IDs):

```bash
ASK_SKILL_ID=$(ask smapi list-skills-for-vendor | python3 -c "import json,sys; print(next(s['skillSummary']['skillId'] for s in json.load(sys.stdin)['skills'] if (s['skillSummary'].get('nameByLocale',{}).get('en-US') or s['skillSummary'].get('name',''))=='Jellyfin'))")
python3 -c "import json; d=json.load(open('Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json')); json.dump({'interactionModel':d}, open('/tmp/payload.json','w'))"
ask smapi set-interaction-model --skill-id "$ASK_SKILL_ID" --stage development --locale it-IT --interaction-model file:/tmp/payload.json
ask smapi get-skill-status --skill-id "$ASK_SKILL_ID"   # wait for SUCCEEDED
```

- [ ] **Step 2: profile-nlu the routing gate**

```bash
for u in "riproduci la playlist variado" "mescola la playlist variado" "riproduci la playlist variado in modalità casuale"; do
  ask smapi profile-nlu --skill-id "$ASK_SKILL_ID" --stage development --locale it-IT --utterance "$u" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(repr(sys.argv[1]), '->', d.get('selectedIntent',{}).get('name'), d.get('selectedIntent',{}).get('slots',{}))" "$u"
  sleep 1.5
done
```
Expected: plain → `PlayPlaylistIntent`; `mescola …` and `… in modalità casuale` → `ShufflePlayIntent`. If the qualified phrase still routes to `PlayPlaylistIntent` (qualifier leaking into the slot), add more discriminator samples / refine phrasings and re-test. If a non-qualified phrase is stolen, tighten `ShufflePlayIntent` samples.

- [ ] **Step 3: Run live NLU tests** (needs ask CLI auth)

```bash
./scripts/run_nlu_tests.sh -k "shuffle or mescola or casuale or playlist"
```
Expected: pass.

### Task 5.3: E2E (simulate-skill, it-IT)

- [ ] **Step 1: Deploy the DLL** per the `.claude.local.md` deploy checklist (backup config, build Release, hot-swap, verify active DLL, restore config). Then add an E2E case to `tests/integration/fixtures/e2e_it-IT.yaml` and run (it-IT is the reliable simulate-skill locale per CLAUDE.md):

```bash
./scripts/run_e2e_tests.sh -k "mescola or casuale" \
  --jellyfin-url "$JELLYFIN_URL" --jellyfin-api-key "$JELLYFIN_API_KEY" --jellyfin-user "$JELLYFIN_USER" -v
```
Expected: skill receives `ShufflePlayIntent` and serves a shuffled queue (first-streamed item differs from the playlist's first track; persisted `DeviceQueue` shows `PlaybackOrder=Shuffle`, `OriginalItemIds` set — verify via `podman logs jellyfin`).

### Task 5.4: Full validation + finalize

- [ ] **Step 1: Full build + test (no `--no-build`)**

```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj
```
Expected: 0 warnings; all tests pass (full suite, ~2300+).

- [ ] **Step 2: Update JF-305 acceptance criteria** — check off AC#1–#7 in the backlog task via `task_edit` (`acceptanceCriteriaCheck [1,2,3,4,5,6,7]`), record the profile-nlu verdict + on-device/E2E result in notes.

- [ ] **Step 3: Commit fixtures/tests**

```bash
git add tests/integration/fixtures/
git commit -m "test(nlu/e2e): ShufflePlayIntent fixtures + profile-nlu gate (JF-305)"
```

---

## Definition of Done (JF-305)

- [ ] `dotnet build -warnaserror` passes with 0 warnings.
- [ ] Full `dotnet test` suite green.
- [ ] `validate_interaction_models.py` + `validate_locales.py` + `validate_versions.py` pass.
- [ ] profile-nlu confirms plain → `PlayPlaylistIntent`, qualifier/verb → `ShufflePlayIntent` (it-IT; spot-check en-US).
- [ ] E2E (it-IT simulate-skill) shows a shuffled first track + `DeviceQueue` in `Shuffle` with `OriginalItemIds`.
- [ ] JF-301 `ShuffleOn/Off` regression tests still green (additive change only — `ShuffleRemaining` untouched).
- [ ] No new response strings (reuses `NotFoundPlaylist`/`PlaylistEmpty`/`DidNotCatchPlaylistName`/`MediaNotFound`).

## Risks (carried from spec)

- **NLU competition (#3):** `ShufflePlayIntent` and `PlayPlaylistIntent` share "play the playlist …" surface — gated by the profile-nlu step (Chunk 5).
- **Extraction refactor:** `BuildPlaylistPlayResponseAsync` must preserve `PlayPlaylistIntent` behavior — existing tests are the sentinel; the `shuffle:false` branch is line-identical to the old code.
- **SearchQuery qualifier leak:** ensure `ShufflePlayIntent` samples place static qualifier words outside `{playlist}` so the slot captures only the name (verify via profile-nlu slot inspection).
- **Inherited limitation:** large playlists (> initial fetch) only shuffle the first batch — documented, out of scope.
- **Locale translations:** the `*`-marked samples (nl-NL, hi-IN) are best-effort — have a native speaker review before release.
