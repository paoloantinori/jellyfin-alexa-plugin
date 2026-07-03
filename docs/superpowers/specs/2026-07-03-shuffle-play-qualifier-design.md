# Shuffle-at-Start Qualifier for Playlist Playback — Design Spec

**Date:** 2026-07-03
**Status:** Approved (playlist-only scope)
**Related:** [JF-305](../../../backlog/tasks) · issue #10 (RUBIKOF comment, 2026-07-03) · JF-301 (shuffle infrastructure) · issue #3 (neeleysc, artist shuffle-from-start — out of scope here)

## Goal

Let a user request shuffle **in the initial play command**, so a playlist's queue is shuffled *before* playback begins (first track random too) — instead of requiring a separate "shuffle on" after playback starts.

**Requested utterances (from the FR):**
- "Play the playlist Variado in shuffle mode"
- "Shuffle the playlist Variado"
- "Reproduce la lista de reproducción Variado en modo aleatorio" (es-MX)

## Background

### Current behavior
`PlayPlaylistIntentHandler.HandleAsync` builds `session.NowPlayingQueue` in original order and plays `queueItems[0]`. To shuffle today the user must start playback, then say "shuffle on" (`AMAZON.ShuffleOnIntent`) mid-playback — the first track is never random.

### Reusable infrastructure (do not reinvent)
- **JF-301 shuffle store (the state, not the mid-playback method):** `DeviceQueueManager` is the authoritative shuffle store. `RestoreOrder` reverts to `OriginalItemIds`; `BaseHandler.MirrorQueueToSession` mirrors the order into `session.NowPlayingQueue`; the gapless resolver reads this store. **Important constraint (verified):** the existing `ShuffleRemaining(deviceId, currentItemId)` is *not* usable at build time — it requires an already-playing `currentItemId`, keeps that item first, only Fisher–Yates shuffles the tail, and no-ops for queues < 3 items or when the current item isn't found. So it cannot produce a random first track. The build-time path needs a **new additive method** (below), not `ShuffleRemaining`.
- **`SetQueue` does not set `OriginalItemIds`** (verified, `DeviceQueueManager.cs:121`): a fresh `DeviceQueue` leaves `OriginalItemIds` null, which makes `RestoreOrder` a no-op. The new method must populate `OriginalItemIds` from the pre-shuffle order so mid-playback `ShuffleOff` works.
- **Playlist resolution:** `PlayPlaylistIntentHandler` already resolves a playlist's members via `PlaylistTrackResolver` (the #10 fix). The new handler must reuse this, not duplicate it.
- **Prior art:** `ShuffleArtistSongs` config flag (artist-only) and `PlayRandomIntentHandler` (shuffle from library).

### NLU baseline (gathered 2026-07-03, it-IT, skill `33dfacd5…`)
| Utterance | Routes to | Slot |
|---|---|---|
| `riproduci la playlist variado` | `PlayPlaylistIntent` | `playlist="variado"` |
| `riproduci la playlist variado in modalità casuale` | `PlayPlaylistIntent` | `playlist="variado in modalità casuale"` ❌ |
| `mescola la playlist variado` | `AMAZON.FallbackIntent` | — |

Two facts fall out: (1) the qualifier words **leak into the `playlist` slot today** (Jellyfin then can't find the playlist); (2) there is **no existing home** for a shuffle-play utterance.

## Design decision: dedicated `ShufflePlayIntent` (Option B)

The intuitive design — adding an optional `shuffle` slot to `PlayPlaylistIntent` (Option A) — is **blocked**: `PlayPlaylistIntent.playlist` is `AMAZON.SearchQuery`, and `AMAZON.SearchQuery` cannot coexist with any other slot type (anti-pattern #2 — SMAPI rejects the build). Switching `playlist` to a custom slot type would regress arbitrary-playlist-name capture, so it is not an acceptable escape.

A **dedicated `ShufflePlayIntent`** with a single SearchQuery `playlist` slot is legal (one slot, no coexistence issue) and supports both phrasings the user wants. This is the chosen design.

(Album/artist slots are `AMAZON.MusicRecording` / `Musician` / `AlbumName` — not SearchQuery — so a `shuffle` qualifier slot *would* be legal for them. That is a second mechanism and is explicitly **deferred**; see Scope.)

## Detailed design

### Interaction model
- New intent **`ShufflePlayIntent`**, single slot `playlist` = `AMAZON.SearchQuery`.
- Samples (qualifier + verb styles):
  - **en-US:** `shuffle the playlist {playlist}`, `play the playlist {playlist} in shuffle mode`, `play {playlist} shuffled`, `play the playlist {playlist} on shuffle`
  - **it-IT (YAML template → `generate_interaction_model.py it-IT`):** `mescola la playlist {playlist}`, `riproduci la playlist {playlist} in modalità casuale`, `riproduci la playlist {playlist} a caso`, `riproduci a caso la playlist {playlist}`
  - Other 15 locales: direct JSON edit, equivalent phrasings.
- **`dialog.intents` registration** in all 17 locales (anti-pattern #9 — without it, any future `Dialog.ElicitSlot` targeting this intent silently fails).
- Slot-name consistency enforced (anti-pattern: same slot name → same type across intents in a locale). `playlist` already exists as `AMAZON.SearchQuery` on `PlayPlaylistIntent`, so reusing the name + type is consistent.
- All changes applied to **all 17 locales simultaneously** (anti-pattern #4).

### Handler
New `ShufflePlayIntentHandler : BaseHandler`:
1. `CanHandle`: matches `IntentNames.ShufflePlayIntent`.
2. `HandleAsync`:
   - Resolve the `playlist` slot value → playlist item + members via the **shared `BaseHandler.BuildPlaylistPlayResponseAsync(...)` method** (extraction decision locked below), called with `shuffle:true`. Reuses the existing locale response keys (`NotFoundPlaylist`, `PlaylistEmpty`, `DidNotCatchPlaylistName`) — **no new response strings**.
   - Build the ordered item-id list (same as plain play).
   - **Shuffle at build time** by calling the new `_queueManager.SetShuffledQueue(deviceId, itemIds)` (below): it snapshots `OriginalItemIds`, Fisher–Yates shuffles the *entire* list (position 0 included), and sets `PlaybackOrder=Shuffle` + `CurrentIndex=0`.
   - `MirrorQueueToSession` so the gapless resolver sees the shuffled order.
   - Return `BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(shuffled[0]), …)` — first played item is a **random** one.
- `IntentNames.cs`: add `ShufflePlayIntent` constant.
- **DI:** `ShufflePlayIntentHandler : BaseHandler` is auto-registered via the reflection scan in `EntryPoints` (`IsSubclassOf(typeof(BaseHandler))`) — no manual registration needed.

Unlike "play then `ShuffleOn`" (which keeps the already-playing first track fixed), this yields a random **first** track — the FR's core ask — because the whole queue is shuffled before the first `Play` directive. Subsequent gapless advances read the authoritative shuffled `DeviceQueue`; mid-playback `ShuffleOff` correctly `RestoreOrder`s (because `OriginalItemIds` is populated).

### DeviceQueueManager: new `SetShuffledQueue` API (additive)
Add a new method — **do not modify** `SetQueue`, `ShuffleRemaining`, or `RestoreOrder` (keeps JF-301 `ShuffleOn/Off` behavior untouched):

```csharp
// Snapshots the original order, Fisher–Yates shuffles ALL items (including
// position 0), and sets a fresh queue with PlaybackOrder=Shuffle.
// rng is injectable for deterministic unit tests; defaults to Random.Shared.
public void SetShuffledQueue(string deviceId, List<string> itemIds, Random? rng = null)
```

Contract:
1. `OriginalItemIds = new List<string>(itemIds)` (the true pre-shuffle order) — always set, even for tiny queues, so `RestoreOrder` works.
2. Fisher–Yates over the full `itemIds` using `rng ?? Random.Shared` (position 0 included → random first track).
3. Build the `DeviceQueue` exactly like `SetQueue` (preserve `ItemPositionState` across resets), with `ItemIds = shuffled`, `OriginalItemIds = snapshot`, `PlaybackOrder = "Shuffle"`, `CurrentIndex = 0`, `RepeatMode = "None"`.
4. Persist (debounced) + log.

Edge cases: for 0–1 items the shuffle is trivially identity but state is still set (so `ShuffleOff`/consistency hold). Meaningful randomization requires ≥ 2 items.

DRY (optional, behavior-preserving): extract a private `static void FisherYates(List<string> list, Random rng)` helper and have both `ShuffleRemaining` (on its tail list, with `Random.Shared`) and `SetShuffledQueue` (on the full list) call it. `ShuffleRemaining`'s observable behavior is unchanged.

### NLU competition verification (acceptance gate)
Verified with `ask smapi profile-nlu` after a candidate deploy, across locales:
- `play the playlist X` / `riproduci la playlist X` (no qualifier) → **must stay `PlayPlaylistIntent`** (not stolen).
- `shuffle the playlist X` / `mescola la playlist X` / `play the playlist X in shuffle mode` / `riproduci la playlist X in modalità casuale` → **`ShufflePlayIntent`**.

This is the core anti-pattern #3 risk and is encoded as NLU fixtures (below).

### Testing (TDD: unit + NLU + integration)
- **Unit (xUnit):**
  - `DeviceQueueManager.SetShuffledQueue`: with an injected seeded `Random`, the resulting `ItemIds` is a deterministic permutation; `OriginalItemIds` equals the input order; `PlaybackOrder == "Shuffle"`; `CurrentIndex == 0`; the full set of ids is preserved (no drops/dupes). Include a ≥3-item case asserting position-0 changed, and a ≤2-item edge case asserting state still set + ids preserved.
  - `ShufflePlayIntentHandler` resolves a playlist → calls `SetShuffledQueue` → served first item is `shuffled[0]` (deterministic via the seeded rng); `DeviceQueue.OriginalItemIds != null`.
  - `ShuffleRemaining`/`RestoreOrder`/`SetQueue` behavior is **unchanged** (regression sentinel tests for JF-301 `ShuffleOn/Off` stay green).
  - Regression: plain `PlayPlaylistIntent` still serves `queueItems[0]` in original order, `DeviceQueue.PlaybackOrder=Default`, `OriginalItemIds == null`.
  - `ShuffleOff` on a qualifier-started queue `RestoreOrder`s to the original playlist order (because `OriginalItemIds` was populated at build time).
  - Seedability comes from `SetShuffledQueue`'s injectable `Random` param — `ShuffleRemaining` (which uses non-injectable `Random.Shared`) is untouched.
- **NLU fixtures** (`tests/integration/fixtures/<locale>.yaml`): add the new utterances, `expected_intent: ShufflePlayIntent`, `expected_slots.playlist`. Existing `PlayPlaylistIntent` fixtures remain green.
- **Integration/E2E** (`run_e2e_tests.sh` / `simulate-skill`, it-IT — the reliable simulate-skill locale): `mescola la playlist X` → skill receives `ShufflePlayIntent` and serves a shuffled queue (assert via persisted `DeviceQueue` state + first-streamed item).

### Scope
- **In scope:** playlists (the FR's explicit ask), all 17 locales, unit + NLU + integration tests.
- **Deferred (follow-up tasks):** album + artist shuffle-from-start (would use a `shuffle` qualifier slot — legal for their non-SearchQuery slot types — or extend `ShufflePlayIntent`); a global/per-user "default shuffle" config. `ShuffleArtistSongs` (artist-only) already partially covers the artist case.

### Non-goals
- No changes to `DeviceQueueManager.SetQueue` / `ShuffleRemaining` / `RestoreOrder` — i.e. the JF-301 `AMAZON.ShuffleOn/OffIntent` path is untouched. The only `DeviceQueueManager` change is **additive** (new `SetShuffledQueue` method; the `FisherYates` helper extraction is optional and behavior-preserving). The qualifier is an alternate *entry* into the same shuffle state.
- No change to gapless auto-advance, radio mode, or PostPlay.

## Acceptance criteria (carried from JF-305)
1. `play playlist X in shuffle mode` / `shuffle playlist X` (and per-locale equivalents) start playback with a randomized first track and shuffled gapless order.
2. Plain `play playlist X` (no qualifier) still routes to `PlayPlaylistIntent` and plays in original order (profile-nlu verified).
3. Shuffle state uses JF-301's authoritative `DeviceQueueManager`; mid-playback ShuffleOn/Off behave correctly on a qualifier-started queue.
4. `ShufflePlayIntent` registered in `dialog.intents` across all 17 locales; it-IT via YAML template + regenerate.
5. NLU fixtures + unit tests added; no regression to JF-301 or plain play.
6. `dotnet build` 0 warnings (`-warnaserror`), full `dotnet test` green.

## Risks & mitigations
- **NLU competition** (#3): `ShufflePlayIntent` samples overlap with `PlayPlaylistIntent` ("play the playlist …"). Mitigation: qualifier/verb carriers required in samples; profile-nlu gate; keep `PlayPlaylistIntent` fixtures as regression sentinels.
- **SearchQuery slot capture of the qualifier** (the baseline leak): ensure `ShufflePlayIntent` samples put static qualifier words outside the `{playlist}` slot so the slot captures only the name. Verify via profile-nlu slot inspection.
- **Shared playlist-resolution refactor (coupled to the shuffle mechanism):** the resolution body in `PlayPlaylistIntentHandler.HandleAsync` (~100 lines: disambiguation, fuzzy match, queue-continuation caching, `SetQueue`) is intertwined with the queue-build. Extracting it and inserting `SetShuffledQueue` are not independent decisions — the extraction boundary determines where the ordered id-list is available to shuffle. Mitigation: decide the extraction shape first in the plan, expose the ordered `itemIds` at a clean seam, then call `SetShuffledQueue`; keep `PlayPlaylistIntent` tests green as the regression sentinel.

## Resolved decisions (locked 2026-07-03, per user)

- **Extraction shape — Option (a):** a shared `BaseHandler.BuildPlaylistPlayResponseAsync(playlistName, session, ctx, user, locale, shuffle: bool, rng: Random?)` method. `PlayPlaylistIntentHandler` is refactored to call it with `shuffle:false` (behavior-preserving; existing tests are the regression sentinel); `ShufflePlayIntentHandler` calls it with `shuffle:true`. The only internal branch is `SetQueue`+ordered[0] vs `SetShuffledQueue`+shuffled[0]. Rationale: matches the conceptual reality ("play this playlist, optionally shuffled"), zero duplication, one source of truth.
- **`session.NowPlayingQueue` sequence (locked):** build ordered `queueItems` → set `session.NowPlayingQueue` (so `MirrorQueueToSession` can read track metadata) → `SetShuffledQueue` (when `shuffle`) → `MirrorQueueToSession` (overwrites with the shuffled `DeviceQueue` order, metadata preserved) → `session.FullNowPlayingItem = shuffled[0]` → `BuildAudioPlayerResponse(shuffled[0])`.
- **it-IT YAML phrasings:** add `ShufflePlayIntent` as a new **`explicit_intents`** entry (NOT a `vocabulary` expansion — avoids anti-pattern #6). Hand-written samples: `Mescola la playlist {playlist}`, `Mescola playlist {playlist}`, `Riproduci la playlist {playlist} in modalità casuale`, `Riproduci la playlist {playlist} a caso`, `Suona la playlist {playlist} in modalità casuale`; same `playlist` (`AMAZON.SearchQuery`) slot; plus a `dialog.intents` entry (anti-pattern #9). Candidate phrasings are **confirmed empirically at plan-time** via generate → deploy candidate → `profile-nlu`: plain `Riproduci la playlist X` must stay `PlayPlaylistIntent`; qualified phrases must resolve to `ShufflePlayIntent`.
- **Inherited limitation (acknowledged, not solved):** for playlists larger than the initial fetch, `QueueContinuation.CachedTracks` is the ordered full list, so progressive batches appended near end-of-track arrive in original order — only the first (initial-fetch) batch is shuffled. JF-301's `ShuffleRemaining` has the identical limitation; the FR (random first track) is satisfied for the initial batch. Most playlists fit in the initial fetch. Out of scope to fix here.
