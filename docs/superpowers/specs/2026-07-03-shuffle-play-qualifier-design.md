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
- **JF-301 shuffle machinery:** `DeviceQueueManager` is the authoritative shuffle store. `ShuffleRemaining` physically reorders the remaining queue and snapshots `OriginalItemIds`; `RestoreOrder` reverts. `BaseHandler.MirrorQueueToSession` mirrors the order into `session.NowPlayingQueue`. The gapless resolver reads this store. An inline qualifier is a new *entry point* into this same state at queue-build time.
- **Playlist resolution:** `PlayPlaylistIntentHandler` already resolves a playlist's members via the correct Jellyfin playlist-members API (the #10 fix). The new handler must reuse this, not duplicate it.
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
   - Resolve the `playlist` slot value → playlist item + members, **reusing `PlayPlaylistIntentHandler`'s resolution logic** (extract to a shared helper if not already cleanly callable — preferred over duplicating the Jellyfin playlist-members call).
   - Build the ordered queue (same as plain play).
   - **Shuffle at build time** via JF-301 machinery: set `DeviceQueue.PlaybackOrder = Shuffle`; call `ShuffleRemaining(...)` (reorders remaining tracks, captures `OriginalItemIds`); `MirrorQueueToSession`.
   - Return `BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(firstShuffledItem), …)` — first played item is a (random) shuffled one.
- `IntentNames.cs`: add `ShufflePlayIntent` constant.

The result is indistinguishable from "play the playlist, then immediately ShuffleOn" — but atomic and with a random first track. Subsequent gapless advances read the authoritative shuffled `DeviceQueue`, and mid-playback `ShuffleOff` correctly `RestoreOrder`s.

### NLU competition verification (acceptance gate)
Verified with `ask smapi profile-nlu` after a candidate deploy, across locales:
- `play the playlist X` / `riproduci la playlist X` (no qualifier) → **must stay `PlayPlaylistIntent`** (not stolen).
- `shuffle the playlist X` / `mescola la playlist X` / `play the playlist X in shuffle mode` / `riproduci la playlist X in modalità casuale` → **`ShufflePlayIntent`**.

This is the core anti-pattern #3 risk and is encoded as NLU fixtures (below).

### Testing (TDD: unit + NLU + integration)
- **Unit (xUnit):**
  - `ShufflePlayIntentHandler` resolves a playlist → resulting `DeviceQueue` has `PlaybackOrder=Shuffle` and `OriginalItemIds != null`; the served first item is from the shuffled set.
  - Use a **seedable shuffle** (injectable `Random`/seed) so "first item is non-original-first" is a deterministic assertion, not flaky.
  - Regression: plain `PlayPlaylistIntent` still serves `queueItems[0]` in original order, `DeviceQueue.PlaybackOrder=Default`.
  - `ShuffleOff` on a qualifier-started queue restores original order (cross-check with JF-301).
- **NLU fixtures** (`tests/integration/fixtures/<locale>.yaml`): add the new utterances, `expected_intent: ShufflePlayIntent`, `expected_slots.playlist`. Existing `PlayPlaylistIntent` fixtures remain green.
- **Integration/E2E** (`run_e2e_tests.sh` / `simulate-skill`, it-IT — the reliable simulate-skill locale): `mescola la playlist X` → skill receives `ShufflePlayIntent` and serves a shuffled queue (assert via persisted `DeviceQueue` state + first-streamed item).

### Scope
- **In scope:** playlists (the FR's explicit ask), all 17 locales, unit + NLU + integration tests.
- **Deferred (follow-up tasks):** album + artist shuffle-from-start (would use a `shuffle` qualifier slot — legal for their non-SearchQuery slot types — or extend `ShufflePlayIntent`); a global/per-user "default shuffle" config. `ShuffleArtistSongs` (artist-only) already partially covers the artist case.

### Non-goals
- No changes to `AMAZON.ShuffleOn/OffIntent` behavior (JF-301). The qualifier is an alternate *entry* into the same shuffle state.
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
- **Shared playlist-resolution refactor:** extracting the resolution from `PlayPlaylistIntentHandler` must not change plain-play behavior. Mitigation: extract behind a helper, keep `PlayPlaylistIntent` tests green.

## Open questions (to resolve during planning)
- Extraction shape for shared playlist resolution (private helper on `BaseHandler` vs a service) — decide for least disruption.
- Exact it-IT YAML vocabulary additions (which verb/slot phrasings) — verify with profile-nlu that they route to `ShufflePlayIntent` and don't collide.
