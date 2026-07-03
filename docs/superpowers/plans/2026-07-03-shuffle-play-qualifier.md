# Shuffle-at-Start Playlist Qualifier — Implementation Plan (JF-305)

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users start a playlist already shuffled ("shuffle the playlist X" / "play the playlist X in shuffle mode") so the first track is random, via a new `ShufflePlayIntent`, reusing JF-301's authoritative `DeviceQueueManager` shuffle state.

**Architecture:** A new additive `DeviceQueueManager.SetShuffledQueue(deviceId, itemIds, Random?)` shuffles the *whole* queue (position 0 included) and snapshots `OriginalItemIds`. A new DI service `PlaylistPlayBuilder` holds the shared playlist-resolution flow (extracted from `PlayPlaylistIntentHandler`) with a `shuffle` branch; both `PlayPlaylistIntentHandler` (refactored, `shuffle:false`) and the new `ShufflePlayIntentHandler` (`shuffle:true`) call it. A new `ShufflePlayIntent` (single `AMAZON.SearchQuery` `playlist` slot) is added to all 17 locales (it-IT via YAML `explicit_intents`) + `dialog.intents`.

**Tech Stack:** C# net9.0, xUnit, Alexa.NET, Jellyfin 10.11+ APIs. Python for model generation/validation + pytest NLU/E2E.

**Spec:** `docs/superpowers/specs/2026-07-03-shuffle-play-qualifier-design.md`

**Refinement vs spec (flagged):** The spec placed the shared method on `BaseHandler`, but `BaseHandler`'s ctor is `(sessionManager, config, loggerFactory)` only — it lacks `ILibraryManager`/`IUserManager`/`DeviceQueueManager`. So the shared flow lives in a new DI service `PlaylistPlayBuilder` (Chunk 2) instead. Concept unchanged.

**Conventions (from CLAUDE.md):** `Nullable enable`; `TreatWarningsAsErrors=true` so build with `-warnaserror`; `async/await` + `ConfigureAwait(false)`; NEVER use `dotnet test --no-build` after code changes; it-IT model is generated from YAML (edit template, regenerate); new intent → `dialog.intents` registration in all 17 locales.

**Shared context — build/test env (memory):** Redirect NuGet caches to /tmp before building in the sandbox:
```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
```

---

## Chunk 1: `DeviceQueueManager.SetShuffledQueue` (additive)

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs` (add `SetShuffledQueue` + `FisherYates`; refactor `ShuffleRemaining` tail loop to call `FisherYates`)
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

### Task 1.2: Implement `SetShuffledQueue` + `FisherYates`

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

/// <summary>Fisher–Yates shuffle, in place. Shared by SetShuffledQueue (full list)
/// and ShuffleRemaining (tail) so there is one shuffle implementation.</summary>
private static void FisherYates(List<string> list, Random rng)
{
    for (int i = list.Count - 1; i > 0; i--)
    {
        int j = rng.Next(i + 1);
        (list[i], list[j]) = (list[j], list[i]);
    }
}
```

- [ ] **Step 4: Refactor `ShuffleRemaining` to use `FisherYates` (behavior-preserving)** — replace its inline loop (`DeviceQueueManager.cs:304-308`) with `FisherYates(tail, Random.Shared);`. Delete the old `for` loop. (Tail + `Random.Shared` unchanged → observable behavior identical.)

- [ ] **Step 5: Run the new tests — verify pass**

```bash
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~SetShuffledQueue"
```
Expected: 4 passed.

- [ ] **Step 6: Run the JF-301 shuffle regression tests — verify still green**

```bash
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~ShuffleRemaining|FullyQualifiedName~RestoreOrder"
```
Expected: all pass (the `FisherYates` refactor is behavior-preserving).

- [ ] **Step 7: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Playback/DeviceQueueManager.cs Jellyfin.Plugin.AlexaSkill.Tests/Playback/DeviceQueueManagerTests.cs
git commit -m "feat(playback): add DeviceQueueManager.SetShuffledQueue (JF-305)"
```

---

## Chunk 2: `PlaylistPlayBuilder` service — extract shared flow

**Files:**
- Create: `Jellyfin.Plugin.AlexaSkill/Alexa/Playback/PlaylistPlayBuilder.cs`
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs` (becomes a thin caller)
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/EntryPoints/` plugin service registrar (register `PlaylistPlayBuilder`)
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Playback/PlaylistPlayBuilderTests.cs` (new) + keep `PlayPlaylistIntentHandlerTests` green

**Context:** The body of `PlayPlaylistIntentHandler.HandleAsync` (lines 77–232: slot → resolve user → query playlists → `FuzzyMatch`/`HandleFuzzyMiss` disambiguation → `PlaylistTrackResolver.GetAudioTracks` → build `queueItems` → queue state → `QueueContinuation` → `BuildAudioPlayerResponse`) is the shared flow. Move it into `PlaylistPlayBuilder.BuildResponseAsync(...)` with a `bool shuffle, Random? rng` branch at the queue-state step.

### Task 2.1: Create `PlaylistPlayBuilder` with the shared flow

- [ ] **Step 1: Create the service** — `Alexa/Playback/PlaylistPlayBuilder.cs`. Constructor takes the same deps `PlayPlaylistIntentHandler` uses: `ISessionManager`, `PluginConfiguration`, `ILibraryManager`, `IUserManager`, `ILoggerFactory`, `DeviceQueueManager?`. It does NOT inherit `BaseHandler` (BaseHandler lacks these deps) — instead inject `BaseHandler`-equivalent helpers it needs, or duplicate the few used (`ResolveJellyfinUser`, `FuzzyMatch`, `HandleFuzzyMiss`, `ApplyLibraryFilter`, `GetStreamUrl`, `MirrorQueueToSession`, `BuildAudioPlayerResponse`, `RetryAsync`, `SafeGetItemsResult`). **Simplest correct approach:** make `PlaylistPlayBuilder` an `internal` collaborator that holds the deps and exposes one method; have the two handlers pass themselves (`BaseHandler` instance) in, OR make the builder hold a reference to the calling `BaseHandler` for the helper methods.

  **Recommended shape** (minimal surface): `PlaylistPlayBuilder` is constructed by each handler with its already-injected deps; the build method takes the calling handler's helper surface via a small interface. To avoid over-engineering, give `PlaylistPlayBuilder` a public method:

```csharp
public async Task<SkillResponse> BuildResponseAsync(
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

  Move the existing `PlayPlaylistIntentHandler.HandleAsync` body (77–232) into this method. At the queue-state step (current lines 188–205), replace with the **shuffle branch**:

```csharp
session.NowPlayingQueue = queueItems;  // ordered, so MirrorQueueToSession can read track metadata

string deviceId = context.System.Device.DeviceID;
List<string> idList = playlistItems.Select(i => i.Id.ToString()).ToList();
BaseItem firstItem;

if (shuffle && _queueManager != null)
{
    _queueManager.SetShuffledQueue(deviceId, idList, rng);
    // Mirror the shuffled DeviceQueue order back into the session queue (metadata preserved).
    MirrorQueueToSession(_queueManager.GetOrCreateQueue(deviceId), session);
    string firstShuffledId = _queueManager.GetOrCreateQueue(deviceId).ItemIds[0];
    firstItem = _libraryManager.GetItemById(Guid.Parse(firstShuffledId));
}
else
{
    _queueManager?.SetQueue(deviceId, idList, 0);
    firstItem = _libraryManager.GetItemById(queueItems[0].Id);
}

if (firstItem == null)
{
    return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
}

session.FullNowPlayingItem = firstItem;

// Continuation uses the ordered full list (inherited limitation: progressive
// batches arrive in original order — only the initial-fetch batch is shuffled).
// ... (existing QueueContinuationStore.Set block, unchanged) ...

string item_id = firstItem.Id.ToString();
return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, firstItem, user, context);
```

  **Helpers needed by the builder** (`ResolveJellyfinUser`, `FuzzyMatch`, `HandleFuzzyMiss`, `ApplyLibraryFilter`, `SafeGetItemsResult`, `RetryAsync`, `GetStreamUrl`, `MirrorQueueToSession`, `BuildAudioPlayerResponse`, `Logger`): these live on `BaseHandler`. **Cleanest:** make `PlaylistPlayBuilder` extend `BaseHandler` is NOT possible (ctor). Instead, pass the calling `BaseHandler` handler into the build method as the helper source, OR re-home these as `internal static`/`protected` methods reachable by the builder. **Decision for implementer:** the least-risk path is to keep these helpers on `BaseHandler` (where they are) and have `PlaylistPlayBuilder` accept a `BaseHandler handler` parameter in `BuildResponseAsync`, calling `handler.FuzzyMatch(...)`, etc. Promote the needed helpers from `private` to `internal` on `BaseHandler` if not already accessible. This keeps `PlayPlaylistIntentHandler`'s observable behavior identical.

### Task 2.2: Regression — refactor `PlayPlaylistIntentHandler` to call the builder

- [ ] **Step 2: Write/keep a regression test** — ensure an existing or new `PlayPlaylistIntentHandlerTests` test asserts: given a matching playlist, the response plays `queueItems[0]` (original order), `DeviceQueue.PlaybackOrder == "Default"`, `OriginalItemIds == null`. (If no such test exists, add one mirroring the shuffle test in Chunk 3 but with `shuffle:false` expectations.)

- [ ] **Step 3: Refactor `PlayPlaylistIntentHandler.HandleAsync`** to:

```csharp
public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
{
    string locale = GetLocale(request);
    IntentRequest intentRequest = (IntentRequest)request;
    string? playlistName = intentRequest.Intent.Slots?.TryGetValue("playlist", out var slot) == true ? slot.Value : null;
    return _playlistPlayBuilder.BuildResponseAsync(
        playlistName ?? string.Empty, intentRequest, context, user, session, locale,
        shuffle: false, rng: null, cancellationToken);
}
```

  Inject `PlaylistPlayBuilder _playlistPlayBuilder` via the handler's ctor (add to its DI params; auto-wired by the reflection registrar since it's a registered service — see Step 5).

- [ ] **Step 4: Register `PlaylistPlayBuilder` in DI** — in the plugin's service registrar (`Alexa/EntryPoints/`, the file that registers `DeviceQueueManager`), add `services.AddSingleton<PlaylistPlayBuilder>();` (match however `DeviceQueueManager` is registered — it's a singleton). Verify its deps resolve.

- [ ] **Step 5: Build + run full test suite (no `--no-build`)**

```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj
```
Expected: 0 build warnings; all tests pass (including existing `PlayPlaylistIntentHandler`/shuffle regression tests — this is the sentinel that the refactor is behavior-preserving).

- [ ] **Step 6: Commit**

```bash
git add Jellyfin.Plugin.AlexaSkill/Alexa/Playback/PlaylistPlayBuilder.cs \
        Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs \
        Jellyfin.Plugin.AlexaSkill/Alexa/EntryPoints/ \
        Jellyfin.Plugin.AlexaSkill.Tests/Playback/PlaylistPlayBuilderTests.cs
git commit -m "refactor(playback): extract PlaylistPlayBuilder shared flow (JF-305)"
```

---

## Chunk 3: `ShufflePlayIntentHandler` + `IntentNames`

**Files:**
- Modify: `Jellyfin.Plugin.AlexaSkill/Alexa/IntentNames.cs` (add `ShufflePlay`)
- Create: `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/ShufflePlayIntentHandler.cs`
- Test: `Jellyfin.Plugin.AlexaSkill.Tests/Handler/Intent/ShufflePlayIntentHandlerTests.cs`

### Task 3.1: Register the intent name

- [ ] **Step 1: Add constant** in `IntentNames.cs` near the play intents:

```csharp
public const string ShufflePlay = "ShufflePlayIntent";
```

### Task 3.2: Failing handler test

- [ ] **Step 3: Write failing test** — `ShufflePlayIntentHandlerTests.cs`. Assert: handler `CanHandle` an `IntentRequest` named `ShufflePlayIntent`; and (with a mocked `PlaylistPlayBuilder` + seeded rng) `HandleAsync` delegates to the builder with `shuffle:true` and the resulting `DeviceQueue` is shuffled with `OriginalItemIds != null`. Follow the existing handler-test pattern in `Jelly.Plugin.AlexaSkill.Tests/Handler/Intent/` (mock `ISessionManager`, `PluginConfiguration`, `ILoggerFactory`; instantiate the handler directly).

```csharp
[Fact]
public void CanHandle_ShufflePlayIntent()
{
    var handler = new ShufflePlayIntentHandler(_sessionManager, _config, _playlistPlayBuilder, _loggerFactory);
    var req = new IntentRequest { Intent = new Intent { Name = IntentNames.ShufflePlay } };
    Assert.True(handler.CanHandle(req));
}

[Fact]
public async Task HandleAsync_DelegatesToBuilder_WithShuffleTrue()
{
    // _playlistPlayBuilder is a mock/stub that captures the shuffle arg and returns a canned response
    var handler = new ShufflePlayIntentHandler(_sessionManager, _config, _playlistPlayBuilder, _loggerFactory);
    var response = await handler.HandleAsync(_request, _context, _user, _session, default);
    Assert.True(_playlistPlayBuilder.LastShuffleFlag);   // builder was told to shuffle
    Assert.NotNull(response);
}
```

- [ ] **Step 4: Run — verify fail** (`ShufflePlayIntentHandler` doesn't exist yet).

### Task 3.3: Implement the handler

- [ ] **Step 5: Create `ShufflePlayIntentHandler.cs`** — mirror `PlayPlaylistIntentHandler`'s structure but delegate to the builder with `shuffle:true`:

```csharp
public class ShufflePlayIntentHandler : BaseHandler
{
    private readonly PlaylistPlayBuilder _playlistPlayBuilder;

    public ShufflePlayIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        PlaylistPlayBuilder playlistPlayBuilder,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _playlistPlayBuilder = playlistPlayBuilder;
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
        return _playlistPlayBuilder.BuildResponseAsync(
            playlistName ?? string.Empty, intentRequest, context, user, session, locale,
            shuffle: true, rng: null, cancellationToken);
    }
}
```

  (Auto-DI-registered by the `BaseHandler` reflection scan — no manual registration. Verify in build.)

- [ ] **Step 6: Build + test**

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj --filter "FullyQualifiedName~ShufflePlayIntentHandler"
```
Expected: pass.

- [ ] **Step 7: Commit**

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
python3 scripts/validate_interaction_models.py     # all 17 must pass (JSON, slots, drift)
python3 scripts/validate_locales.py
```
Expected: 0 errors. Confirm `ShufflePlayIntent` present in `model_it-IT.json` `languageModel.intents` AND `dialog.intents`.

### Task 4.2: Other 16 locales (direct JSON edit)

- [ ] **Step 4: For each of `en-US en-AU en-CA en-GB en-IN es-ES es-MX es-US fr-CA fr-FR de-DE hi-IN ja-JP nl-NL pt-BR`** — add a `ShufflePlayIntent` to `languageModel.intents` (localized samples + the single `AMAZON.SearchQuery` `playlist` slot) AND a matching `dialog.intents` entry.

  **en-US samples** (mirror the user's request): `shuffle the playlist {playlist}`, `play the playlist {playlist} in shuffle mode`, `play the playlist {playlist} on shuffle`, `play {playlist} shuffled`.
  Other locales: translate the qualifier ("en modo aleatorio"/es, "en mode aléatoire"/fr, "im Zufallsmodus"/de, etc.). Use the slot name `playlist` with type `AMAZON.SearchQuery` consistently.

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
- Modify: `tests/integration/fixtures/<locale>.yaml` (NLU fixtures; en-US + it-IT minimum, ideally all 17)
- Modify: `tests/integration/fixtures/e2e_it-IT.yaml` (E2E)

### Task 5.1: NLU fixtures

- [ ] **Step 1: Add fixtures** to `tests/integration/fixtures/it-IT.yaml` and `en-US.yaml` (and others if present):

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

### Task 5.2: Deploy candidate + profile-nlu gate (anti-pattern #3 verification)

- [ ] **Step 3: Deploy the updated it-IT model** (per CLAUDE.md — discover the current skill ID fresh, do NOT trust cached IDs):

```bash
ASK_SKILL_ID=$(ask smapi list-skills-for-vendor | python3 -c "import json,sys; print(next(s['skillSummary']['skillId'] for s in json.load(sys.stdin)['skills'] if (s['skillSummary'].get('nameByLocale',{}).get('en-US') or s['skillSummary'].get('name',''))=='Jellyfin'))")
python3 -c "import json; d=json.load(open('Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json')); json.dump({'interactionModel':d}, open('/tmp/payload.json','w'))"
ask smapi set-interaction-model --skill-id "$ASK_SKILL_ID" --stage development --locale it-IT --interaction-model file:/tmp/payload.json
ask smapi get-skill-status --skill-id "$ASK_SKILL_ID"   # wait for SUCCEEDED
```

- [ ] **Step 4: profile-nlu the routing gate**

```bash
for u in "riproduci la playlist variado" "mescola la playlist variado" "riproduci la playlist variado in modalità casuale"; do
  ask smapi profile-nlu --skill-id "$ASK_SKILL_ID" --stage development --locale it-IT --utterance "$u" \
    | python3 -c "import json,sys; d=json.load(sys.stdin); print(repr(sys.argv[1]), '->', d.get('selectedIntent',{}).get('name'), d.get('selectedIntent',{}).get('slots',{}))" "$u"
  sleep 1.5
done
```
Expected: plain → `PlayPlaylistIntent`; `mescola …` and `… in modalità casuale` → `ShufflePlayIntent`. If the qualified phrase still routes to `PlayPlaylistIntent` (qualifier leaking into the slot), add more discriminator samples / refine phrasings and re-test. If a non-qualified phrase is stolen, tighten `ShufflePlayIntent` samples.

- [ ] **Step 5: Run live NLU tests** (needs ask CLI auth)

```bash
./scripts/run_nlu_tests.sh -k "shuffle or mescola or casuale or playlist"
```
Expected: pass.

### Task 5.3: E2E (simulate-skill, it-IT)

- [ ] **Step 4: Add E2E case** to `tests/integration/fixtures/e2e_it-IT.yaml` (per CLAUDE.md, it-IT is the reliable simulate-skill locale; en-US competes with built-ins). Requires a deployed skill + live Jellyfin (deploy the DLL per `.claude.local.md` deploy checklist first).

```bash
./scripts/run_e2e_tests.sh -k "mescola or casuale" \
  --jellyfin-url "$JELLYFIN_URL" --jellyfin-api-key "$JELLYFIN_API_KEY" --jellyfin-user "$JELLYFIN_USER" -v
```
Expected: skill receives `ShufflePlayIntent` and serves a shuffled queue (verify first-streamed item differs from the playlist's first track; check persisted `DeviceQueue` state via logs: `PlaybackOrder=Shuffle`, `OriginalItemIds` set).

### Task 5.4: Full validation + finalize

- [ ] **Step 5: Full build + test (no `--no-build`)**

```bash
export NUGET_PACKAGES=/tmp/nuget_pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget_http
dotnet build Jellyfin.Plugin.AlexaSkill.sln -warnaserror
dotnet test Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj
```
Expected: 0 warnings; all tests pass (target is the full suite, ~2300+).

- [ ] **Step 6: Update JF-305 acceptance criteria** — check off AC#1–#7 in the backlog task via `task_edit` (acceptanceCriteriaCheck), record the profile-nlu verdict + on-device/E2E result in notes.

- [ ] **Step 7: Commit fixtures/tests**

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
- [ ] JF-301 `ShuffleOn/Off` regression tests still green (additive change only).
- [ ] No new response strings (reuses `NotFoundPlaylist`/`PlaylistEmpty`/`DidNotCatchPlaylistName`/`MediaNotFound`).

## Risks (carried from spec)

- **NLU competition (#3):** `ShufflePlayIntent` and `PlayPlaylistIntent` share "play the playlist …" surface — gated by the profile-nlu step (Chunk 5).
- **Extraction refactor:** `PlaylistPlayBuilder` extraction must preserve `PlayPlaylistIntent` behavior — existing tests are the sentinel; promote only the needed `BaseHandler` helpers to `internal`.
- **SearchQuery qualifier leak:** ensure `ShufflePlayIntent` samples place static qualifier words outside `{playlist}` so the slot captures only the name (verify via profile-nlu slot inspection).
- **Inherited limitation:** large playlists (> initial fetch) only shuffle the first batch — documented, out of scope.
