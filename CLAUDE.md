# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin (net9.0) exposing an Alexa skill for media playback, search, and library management. Targets Jellyfin 10.11+.

## Build & Test

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests          # ~2476 unit tests
python3 scripts/validate_interaction_models.py        # Check all 17 models (JSON, slots, drift)
python3 scripts/validate_locales.py                   # Check locale key coverage (baseline-aware)
python3 scripts/validate_versions.py                  # Check version consistency across files
python3 scripts/validate_apl.py                       # Check APL templates validity
./scripts/run_nlu_tests.sh                            # NLU tests via Utterance Profiler API (needs ask CLI auth)
./scripts/run_nlu_tests.sh -k "en-US"                 # single locale
./scripts/run_nlu_tests.sh --dry-run                  # validate fixtures only, no SMAPI calls
./scripts/run_e2e_tests.sh                            # E2E via SMAPI simulate-skill (needs live Jellyfin)
```

Env vars: `ASK_SKILL_ID`, `SMAPI_DELAY` (default 1.5s), `SMAPI_TIMEOUT`, `JELLYFIN_URL`, `JELLYFIN_API_KEY`, `JELLYFIN_USER`.

## CI

GitHub Actions runs the validation/build pipeline on **PRs to main** and via manual `workflow_dispatch` — it does **not** rebuild on every push to main. Release builds run only on tag push (see [Release](#release)). Pipelines:
- `ci.yml` — PR-gated: **build-and-test** (Release build with `-warnaserror` + full test suite), **validate-models** (advisory), **validate-locales** (baseline-aware), **validate-versions**, **validate-build-yaml**
- `dev-build.yml` — manual-only (`workflow_dispatch`): downloadable dev DLL artifact zip
- `release-build.yml` — tag push only: build + test + zip + GitHub release + manifest update
- `pages.yml` — docs-site deploy (path-filtered to `docs-site/`)
- `codeql-analysis.yml` — security scan on PR/push to main + weekly schedule

## Project Layout

Plugin source lives under `Jellyfin.Plugin.AlexaSkill/` (the C# project root) — all `Alexa/`, `Configuration/`, and `Controller/` paths below are relative to it. Repo-root paths (`docs/`, `tests/`, `scripts/`, `Directory.Build.props`) have no prefix.

- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/` — 60 intent handlers (one per intent, inherit `BaseHandler`)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs` — shared utilities: `FuzzyMatch`, `HandleFuzzyMiss`, `RetryAsync`, stream URLs, library filters
- `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/` — 17 per-locale interaction model JSONs (`model_*.json`), generated from templates in `Alexa/InteractionModel/templates/`
- `Jellyfin.Plugin.AlexaSkill/Alexa/Locale/` — Response strings: keys in `ResponseStrings.cs`, values in 17 `<locale>.json` files
- `Jellyfin.Plugin.AlexaSkill/Alexa/SmapiManagement.cs` — SMAPI wrapper (skill CRUD, account linking, status polling)
- `Jellyfin.Plugin.AlexaSkill/Alexa/ModelDeployment/` — Custom interaction model validation, fetch, deploy, restore via SMAPI; `IInteractionModelRedeployer` rebuilds + redeploys all locale models (used by the invocation-name save path and the rebuild endpoint)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/` — Skill manifest generation
- `Jellyfin.Plugin.AlexaSkill/Alexa/Apl/` — APL visual template generation (carousel, NowPlaying screen)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Cache/` — In-memory cache layer for artist/item lookups
- `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/` — Music catalog browsing (browse categories, recently added, recommendations)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Directive/` — Alexa response directives (AudioPlayer, APL, template rendering)
- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/` — Dynamic entity slot updates via SMAPI
- `Jellyfin.Plugin.AlexaSkill/Alexa/Interface/` — Alexa interface capability detection (APL support, etc.)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Music/` — Music-specific data models and helpers
- `Jellyfin.Plugin.AlexaSkill/Alexa/Playback/` — Playback state and progressive queue management
- `Jellyfin.Plugin.AlexaSkill/Alexa/Util/` — Shared utility classes
- `Jellyfin.Plugin.AlexaSkill/Alexa/ArtistIndexService.cs` — In-memory artist index with event-driven refresh
- `Jellyfin.Plugin.AlexaSkill/Alexa/CircuitBreaker.cs` — Circuit breaker for external API resilience
- `Jellyfin.Plugin.AlexaSkill/Alexa/Util/ArtistSearch.cs` — 4-tier artist search fallback chain (shared by PlayArtist and FindSong)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Util/KeywordMatcher.cs` — Partial title tokenization and scoring for song search
- `Jellyfin.Plugin.AlexaSkill/Alexa/Directive/ElicitSlotDirective.cs` — Dialog.ElicitSlot support for multi-turn conversations
- `Jellyfin.Plugin.AlexaSkill/Alexa/ErrorClassifier.cs` — Categorizes errors for user-facing responses
- `Jellyfin.Plugin.AlexaSkill/Alexa/CustomerProfileService.cs` — Amazon customer profile lookups
- `Jellyfin.Plugin.AlexaSkill/Alexa/SlotMappings.cs` — Slot name → type mappings (consistency enforcement)
- `Jellyfin.Plugin.AlexaSkill/Alexa/FuzzyMatcher.cs` — Fuzzy string matching with configurable thresholds
- `Jellyfin.Plugin.AlexaSkill/Alexa/RetryHelper.cs` — Exponential backoff retry with timeout budget (default 6s)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Pipeline/` — Request routing pipeline
- `Jellyfin.Plugin.AlexaSkill/Configuration/` — Plugin config DTO + Jellyfin config UI (`config.html`)
- `Jellyfin.Plugin.AlexaSkill/Controller/` — ASP.NET API controllers (skill endpoint, config, simulator, health)
- `Jellyfin.Plugin.AlexaSkill/Alexa/SongNgramIndexService.cs` — In-memory n-gram + phonetic index for O(1) song title lookup
- `Jellyfin.Plugin.AlexaSkill/Alexa/ISongNgramIndex.cs` — Interface for song n-gram index (Search + SearchPhonetic)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Diagnostics/` — Diagnostic helpers for troubleshooting
- `Jellyfin.Plugin.AlexaSkill/Alexa/Entities/` — Data transfer objects and entity types
- `Jellyfin.Plugin.AlexaSkill/Alexa/EntryPoints/` — Plugin entry points (service registration, DI)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Exceptions/` — Custom exception types
- `Jellyfin.Plugin.AlexaSkill/Alexa/Lwa/` — Login with Amazon (LWA) OAuth flow
- `Jellyfin.Plugin.AlexaSkill/Alexa/ProactiveEvents/` — Proactive event notifications via Alexa
- `docs/` — 104 Mermaid diagrams covering 6 feature flows × 17 locales
- `tests/integration/` — NLU + E2E test suites (Python/pytest)
- `Directory.Build.props` — Version numbers (single source of truth)

## Debug Logging Policy

Keep `Logger.LogDebug(...)` calls in production code to aid triage. These are filtered by Serilog's `Information` default level and have negligible performance impact. When debugging, enable them by adding an override to `/config/logging.default.json` in the Jellyfin container:

```json
"Jellyfin.Plugin.AlexaSkill": "Debug"
```

No code changes or rebuilds needed — just edit the config and restart Jellyfin.

Debug logs should capture: resolved intent/slot/entity names, matched Jellyfin item IDs, playback position values (ticks/ms), user resolution, and handler branching decisions. This helps verify that Alexa NLU results match the correct Jellyfin entries without redeploying.

## Handler Pattern

Handlers inherit `BaseHandler` and implement `CanHandle()` + `HandleAsync()`. `BaseHandler` provides:

- `FuzzyMatch(query, candidates, selector)` — best-match via FuzzyStrings library with Double Metaphone phonetic pre-filter for improved non-English name matching
- `HandleFuzzyMiss()` — disambiguation with voice prompts; auto-plays near-exact matches (score >= 90) without qualifier
- `GetStreamUrl()` / `GetVideoStreamUrl()` — `/Audio|Videos/{id}/stream?static=true`
- `RetryAsync(operation, label)` — retry with exponential backoff, 6s timeout budget
- `IfFeatureDisabled()` — short-circuit on feature flags
- `ApplyLibraryFilter()` / `FilterByContentAccess()` — per-user library and content type gating
- `BuildPauseResponse()` — `AudioPlayerStop()` + `ShouldEndSession=true` (ends session; Alexa routes resume via `AMAZON.ResumeIntent` automatically when audio was recently stopped)

New intents need: handler class + `IntentNames.cs` entry + interaction model samples + 17 locale response strings.

## Artist Search Fallback Chain

`PlayArtistSongsIntentHandler` has a 4-tier fallback (each is a separate DB query):
1. `SearchTerm` — Jellyfin search index
2. `NameStartsWith` first word — prefix with single word
3. `NameStartsWith` full query — prefix with full string
4. `NameContains` full query — substring match anywhere in name

All tiers go through `FuzzyMatch` to filter false positives. Results are served from the in-memory `ArtistIndexService` when available.

## Song Search Pipeline

`FindSongIntentHandler` uses a 3-stage search chain in `SearchAndRespondAsync()`:
1. **N-gram index** (`SongNgramIndexService.Search`) — O(1) bigram/single-token lookup → `KeywordMatcher.Score` with 100% keyword coverage. Fast path.
2. **Phonetic index** (`SongNgramIndexService.SearchPhonetic`) — Double Metaphone phonetic code lookup → `KeywordMatcher.ScorePhonetic` with 50% keyword coverage + 0.75 penalty. Cold path, only on exact-match miss. Protected by `PhoneticSongSearchEnabled` feature flag (default: true).
3. **DB fallback** — Jellyfin search API query. Slowest, last resort.

The n-gram index is a background hosted service (`SongNgramIndexService`) that loads all `Audio` items at startup, builds bigram/single-token/phonetic dictionaries, and refreshes on library changes (debounced 5s). Pre-computed phonetic codes make phonetic lookup O(1) — only the user's 2-3 keywords need encoding at query time.

## Cross-Media-Type Fallback

When a handler's primary search finds no results (e.g., PlaySongIntent finds no song), `BaseHandler.BuildArtistSongsResponseAsync` falls back to artist search. This is shared across PlaySong, PlayAlbum, and PlayVideo handlers via `BaseHandler`.

## Live TV Channel Playback

Live TV channels must launch via `VideoApp.Launch` (like movies/episodes), NOT `AudioPlayer.Play`: the static `/Audio|Videos/{id}/stream?static=true` endpoint returns HTTP 500 for a live source. `PlayChannelIntentHandler` delegates URL resolution to `ILiveTvStreamResolver` (`Alexa/Util/`), which calls `/Items/{channelId}/PlaybackInfo?AutoOpenLiveStream=true` and picks:
- **Direct-remote** (`Protocol=="Http"` + `SupportsDirectStream` + http(s) `Path`): the remote HLS master URL (H.264/AAC) is played directly by ExoPlayer — primary IPTV/M3U path.
- **Fallback** (tuners needing transcode): `/Videos/{id}/master.m3u8?MediaSourceId=…[&LiveStreamId=…]`.

The resolver is a DI singleton with a bounded 5s HTTP timeout; `null` → handler speaks `MediaTypeNotAvailable`. Use `ShouldEndSession = null` for the VideoApp response. Hardware tuners (HDHomeRun/DVB) are less tested than IPTV.

## Search Response Mode

`SearchResponseMode` controls the speed/recall trade-off for artist search, configurable per-user or globally (`DefaultSearchResponseMode` in config):
- **Thorough** (default): full 4-tier fallback chain with disambiguation prompts. Best recall.
- **Fast**: single query or reduced tiers with auto-play. Fastest response, may miss obscure matches.

In Fast mode, `SearchWithAsrFallbackAsync` skips compound-word retries. Handlers call `GetSearchResponseMode(user)` to resolve the effective mode.

## ASR Compound-Word Fix

When enabled (`AsrCompoundWordFixEnabled`), `SearchWithAsrFallbackAsync` in `BaseHandler` retries the original query with joined/split word variants. For example, "lazy bones" retries as "lazybones". Only triggers when the original query returns no results.

## PostPlay Behavior

When a single song finishes and the queue is empty, `PostPlayBehavior` controls what happens next (configurable per-user or globally via `DefaultPostPlayBehavior`):
- **Stop** (default): silence after queue exhaustion
- **AutoPlay**: `PlaybackNearlyFinishedEventHandler` detects queue exhaustion, finds similar tracks via `FindRadioTracksAsync`, enqueues the first one, and enables `RadioModeState` for gapless continuation

AutoPlay is handled entirely in `PlaybackNearlyFinished` — it enqueues the next track before the current one ends, so there's no gap and no speech announcement. After the first AutoPlay track, `RadioModeState` handles subsequent transitions via the existing `AutoPopulateRadioTracks()`.

Handlers call `GetPostPlayBehavior(user)` to resolve per-user override → global default (same pattern as `GetSearchResponseMode`).

## Code Conventions

- `Nullable enable` on — nullability annotations required
- `jellyfin.ruleset` controls code analysis (AllEnabledByDefault, `TreatWarningsAsErrors` = true)
- Intent handlers use `async/await` with `ConfigureAwait(false)`
- Feature flag tests use one file per flag, `AssertDisabledByFlagOff` helper
- **NEVER use `dotnet test --no-build` after code changes** — it runs against stale DLLs and misses failures that CI catches. Always omit `--no-build` when source files have changed.

## Interaction Models

17 locale files. The it-IT model is **generated from a YAML template** — do NOT edit the JSON directly.

**Key vocabulary** (it-IT template, expanded via Cartesian product):
- `imperative`: [Riproduci, Suona, Metti, Pleia, Ascolta]
- `infinitive`: [Di riprodurre, Di suonare, Di mettere, Di pleiare, Di ascoltare]
- `artist_carrier`: [la band, il gruppo, il cantante, la cantante] — disambiguates artist from radio/genre
- `song_noun`: [il brano, la canzone, il pezzo, la traccia]
- `media_noun`: [brani, canzoni, musica, un brano, una canzone, un pezzo, una traccia]

**Model generator** (it-IT): `python3 scripts/generate_interaction_model.py it-IT`
- Template: `Alexa/InteractionModel/templates/it-IT.yaml`
- Output: `Alexa/InteractionModel/model_it-IT.json`
- To add new slot values, samples, or vocabulary: edit the YAML template, then regenerate.
- Other locales: edit JSON directly (only it-IT has the generator).

After editing:
1. Wrap in `{"interactionModel": <model>}` for SMAPI
2. Deploy: `ask smapi set-interaction-model --skill-id <ID> --stage development --locale <XX> --interaction-model file:payload.json`
3. Wait for build (~15-30s): `ask smapi get-skill-status --skill-id <ID>`

NLU test fixtures in `tests/integration/fixtures/<locale>.yaml`. NLU tests use the **Utterance Profiler API** (`ask smapi profile-nlu`) which tests intent/slot routing against the saved model directly — no model build or skill endpoint required. E2E fixtures in `tests/integration/fixtures/e2e_<locale>.yaml` use `simulate-skill` (full pipeline, needs live endpoint).

**en-US E2E tests are unreliable** — `simulate-skill` competes with built-in Amazon skills. Prefer it-IT for simulate-skill testing.

## Recurring Mistakes — DO NOT REPEAT

- **NEVER use cached skill IDs** — The Alexa skill ID changes every time config is wiped (plugin creates a new skill). ALWAYS run `ask smapi list-skills-for-vendor` and find the current Jellyfin skill ID BEFORE any SMAPI operation. Never trust skill IDs from memory files, environment variables, or previous sessions.
- **NEVER access plugin files on the host filesystem** — Jellyfin runs in a podman container named `jellyfin`. Plugin files are INSIDE the container at `/config/data/plugins/AlexaSkill_<version>/`. Use `podman exec jellyfin ...` to read files, `podman cp` to copy files in/out, `podman logs jellyfin` for logs. Never try `scp` to host paths or `find /` on the host.

## Key Gotchas

- **Stream endpoints**: Audio uses `/Audio/{id}/stream?static=true`, video uses `/Videos/{id}/stream?static=true`. Do NOT use `/Download` — lacks Content-Type and Range headers needed by AudioPlayer.
- **AMAZON.SearchQuery** cannot coexist with other slot types in the same utterance. Use custom slot types (e.g. `MediaType`) instead.
- **Slot name consistency**: Same slot name must use same slot type across all intents in a locale.
- **Stop vs Pause**: `AMAZON.StopIntent`/`AMAZON.CancelIntent` → `AudioPlayerStop()` + `ShouldEndSession=true`. `AMAZON.PauseIntent` → `AudioPlayerStop()` + `ShouldEndSession=true` + optional position card. All paths send `AudioPlayer.Stop` directive to guarantee audio stops. Do NOT use `ResponseBuilder.Empty()` for stop/cancel — it lacks the `AudioPlayer.Stop` directive.
- **AudioPlayer responses**: `BuildAudioPlayerResponse` sets `ShouldEndSession=true`. Amazon routes **pause/resume** to the active skill automatically when audio is playing, regardless of session state. **Stop/Next/Previous are NOT reliably routed** during playback (see next item — claimed by the default music service). Using `ShouldEndSession=false` on Play responses kept an active session that prevented the Echo from routing "stop/ferma" to `AMAZON.PauseIntent` (it sent `SessionEndedRequest` instead).
- **Stop/Next/Previous + content switching during playback → default music service (skill competition)**: While AudioPlayer is playing, Amazon auto-routes ONLY **Pause/Resume** to the active skill. **Stop/Next/Previous are frequently claimed by the device's default music service** (Amazon Music/Spotify), so the skill never receives them — verified on-device 2026-07-02 (zero `StopIntent`/`NextIntent`/`PlaybackStopped` events for "stop"/"ferma"/"avanti"; Alexa simulator `ConsideredIntents` = `<IntentForDifferentSkill>`). A fresh content request ("play a different playlist/album/artist/song") is likewise NOT auto-routed — it goes to the default music service. Workaround: use **Pause** (always routes to the active player) or one-shot with the invocation name (`ask <invocation> to stop` / it-IT `chiedi a mia collezione ferma` — imperative, NOT the infinitive "fermare" which resolves to no intent). Not fixable plugin-side: custom `AudioPlayer` skills cannot claim the device's default-music slot (reserved for the Music/Radio/Podcast Skill API, Amazon-partnership-only — same reason custom skills get no seek bar). `PlaybackController` interface is NOT a fix (per Amazon docs it serves hardware buttons only, has no STOP op, and is never sent for voice). Keeping the session open does NOT help (and `JF-299` shows `shouldEndSession=false` is harmful). This is platform behavior, not a bug.
- **Resume item resolution**: Prefer `context.AudioPlayer.Token` over `session.FullNowPlayingItem`. Jellyfin's `PlaybackStopped` event clears `FullNowPlayingItem` before the resume request arrives, but `AudioPlayer.Token` survives.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space NLU tests with `SMAPI_DELAY=1.5`.
- **ValueTuple serialization**: Never store `ValueTuple` in session attributes — Newtonsoft.Json serializes as Item1/Item2. Use named DTOs.
- **Config.Users in API responses**: Never send `config.Users` via `updatePluginConfiguration` — it can wipe skill config entries. Use dedicated endpoints.
- **AudioPlayer event restrictions**: ALL AudioPlayer event handlers (`PlaybackStarted`/`Finished`/`NearlyFinished`/`Stopped`/`Failed`) must return only `AudioPlayer.Play` or a keep-alive ack — **never `shouldEndSession=false`**. Amazon rejects it with `InvalidResponse: "Response may not have shouldEndSession set to false"`, surfacing as "Qualcosa è andato storto" / "Something went wrong" on every playback. Use `BaseHandler.BuildKeepAliveResponse()` (`shouldEndSession=null`) or `BuildEndSessionResponse()` (`true`). Don't try to keep the session open via `shouldEndSession=false` on events for StopIntent routing — it's rejected, and stop/ferma routes fine via the platform's normal AudioPlayer routing. (JF-299)
- **Invocation name (JF-297/JF-300)**: An empty `UserSkill.InvocationName` means "use locale defaults" (`Config.LocaleInvocationNames` → it-IT "mia collezione"; other locales → `Config.InvocationName` "jellyfin player"). A non-empty custom name applies to ALL 17 locales incl. it-IT. `LocaleInvocationNames` is default-only (NOT an unconditional override). Changing the name in settings triggers a redeploy to Amazon via `IInteractionModelRedeployer` (build + `UpdateSkillAsync` + poll, ~15–30s, longer if the SMAPI access token needs refresh) — no Alexa-console edit or re-auth needed. A one-time migration in the `Plugin` ctor clears legacy stored defaults so existing users keep locale defaults.
- **profile-nlu vs on-device divergence**: `ask smapi profile-nlu` (Utterance Profiler) tests intent/slot routing against the saved model in isolation; a real Echo adds ASR + competition from other installed skills, so routing can differ. `AMAZON.MusicRecording`/`Musician` slots capture the spoken text regardless of catalog match (PlaySong works for non-catalog titles). Trust profile-nlu for model routing; verify behavior on-device or via the plugin Simulator endpoint. (JF-298)
- **Entity resolution for slot synonyms**: `slot.Value` always contains the raw spoken text (e.g. "gli album"). To get the canonical value ("album"), extract from `slot.Resolution.Authorities[0].Values[0].Value.Name` when `Status.Code == "ER_SUCCESS_MATCH"`. See `BrowseLibraryIntentHandler.GetCanonicalSlotValue()` for the pattern.
- **Dialog.ElicitSlot requires model registration**: Any intent that uses `Dialog.ElicitSlot` directives MUST be listed in the interaction model's `dialog.intents` array. Without this registration, Alexa **silently ignores** the directive — the session stays open (`ShouldEndSession=false`) but the user's follow-up goes through general NLU, which routes music queries to Amazon Music instead of back to the skill. Set `elicitationRequired: false` on slots when controlling dialog manually from code. This must be done in ALL 17 locales. For it-IT, add to the YAML template's `dialog` section and regenerate.
- **Slot values ≤ 140 chars (Alexa hard limit)**: Alexa rejects slot values and synonyms longer than 140 characters with `InvalidResponse`, crashing **every** skill request (e.g. libraries with long artist fields like musical cast lists). Any code building catalog/dynamic-entity slot values MUST cap length via `SlotValueHelper.Truncate` (applied in `CatalogPayload` and `DynamicEntityBuilder`).
- **After a DLL hot-swap, verify the ACTIVE dll**: Jellyfin migrates the plugin to a versioned dir (`AlexaSkill_<Version>`) when the AssemblyVersion changes and may install the catalog release there, displacing your hot-swapped dev DLL. Always deploy into the CURRENT versioned dir (`ls /config/data/plugins/ | grep AlexaSkill`) and verify the **running** DLL (`podman cp` it out → compare size + `strings | grep` for a unique identifier), not just the file you pushed.

## Audiobook HLS Streaming

Multi-chapter audiobooks use `VideoApp.Launch` with an HLS concat stream that joins all chapters into one continuous video-audio stream. This gives the Echo Show a seek bar with the full book duration.

**Endpoint**: `/alexaskill/api/video-audio/audiobook/{parentId}/stream.m3u8`

### Working approach: 1fps video + audio copy + 10-second segments (JF-292)

**Encoding**: ~2 minutes for an 8.3h audiobook. All 3,001 segments pre-generated before playback starts.

```
ffmpeg -f concat -safe 0 -i chapters.txt \
  -f lavfi -i 'color=c=black:s=1280x720:d=999999' \
  -map 0:a -map 1:v \
  -c:a copy \
  -c:v libx264 -tune stillimage -preset ultrafast -crf 51 \
  -r 1 -g 1 -pix_fmt yuv420p \
  -shortest \
  -f hls \
  -hls_time 10 \
  -hls_segment_type mpegts \
  -hls_segment_filename seg_%04d.ts \
  -hls_list_size 0 \
  stream_raw.m3u8
```

**Key parameters**:
- `-c:a copy` — remuxes MP3 audio without re-encoding (instant)
- `-c:v libx264 -crf 51 -r 1` — 1fps black frame video at minimum quality (keyframe every 1s for seeking)
- `-shortest` — **CRITICAL**: stops encoding when audio ends (without it, the `d=999999` color generator produces an infinite video stream)
- `-hls_time 10` — **CRITICAL**: 10-second segments. ExoPlayer (Echo Show's player) requires standard HLS segment durations (6-10s). 250-second segments cause playback to stall after seeking.
- `-hls_segment_filename seg_%04d.ts` — 4-digit names (max 9,999 segments; `IsValidSegmentName` only accepts 3-4 digit names)

**Why 1fps video**: VideoApp.Launch requires a video track for the seek bar to work. Audio-only HLS plays but seeking doesn't jump to the correct position. 1fps black frames at CRF 51 add minimal overhead (~5KB per second of video) while providing keyframes every second for accurate seeking.

**Why 10-second segments**: ExoPlayer's buffer management assumes standard HLS durations. With 250-second/3MB segments, seeking fetches an entire 3MB file and the player stalls waiting for the full download. With 10-second/~160KB segments, seeking is instant and the player buffers ahead normally. Verified with 3 consecutive seeks on Echo Show, all successful with continuous playback.

**Post-encoding**: rewrite the playlist to use GUID-relative URLs (`/alexaskill/api/video-audio/{parentId}/segments/seg_NNNN.ts`), not bare filenames. The `GetSegment` endpoint validates `itemId` as a GUID, then `FindSegmentPath` scans for directories matching `{GUID}_*` to find the actual cache directory (named `{parentId}_{artModifiedTicks}`).

**Cache size**: ~472MB for an 8.3h audiobook (vs ~3.6GB for 25fps video approach).

**Cache validation**: segment count must be **>= chapter count** (not exactly equal), because 10-second segments produce far more entries than chapters. Only invalidate if segment count is clearly incomplete (< chapters).

### Key gotchas

- **`-shortest` is mandatory**: Without it, the `color=c=black:d=999999` input generates an infinite video stream, producing hundreds of thousands of empty segments.
- **10-second segments, not 250**: ExoPlayer requires standard HLS segment durations. Long segments cause silent seek failures.
- **4-digit segment names max**: `IsValidSegmentName` validates `seg_NNNN.ts` (3-4 digits only). Do NOT use `%05d` or higher.
- **Playlist URLs use GUID-only parentId**: The route `{itemId}/segments/{segmentName}` validates `itemId` with `Guid.TryParse`. Use `parentId` (the clean GUID), not the composite `{parentId}_{artModifiedTicks}`.
- **VideoApp.Launch requires video track**: Echo Show's VideoApp expects H.264 video. Audio-only HLS plays but seeking doesn't work. 1fps black frames provide the keyframes needed for seeking.
- **Echo Show VideoApp does NOT support AudioPlayer features**: No album art, no metadata, no queue management. Trade-off: seek bar (VideoApp) vs album art (AudioPlayer).
- **AudioPlayer custom skills get NO scrubber/progress bar**: Only the Music/Radio/Podcast Skill API (Amazon partnership required) gets the native player with seek bar. Custom skills using `AudioPlayer.Play` get only play/pause/next/previous buttons.
- **Pre-written event playlists (no ENDLIST) DO work**: A playlist WITHOUT `#EXT-X-ENDLIST` is an event playlist — the player plays available segments without failing on missing ones. This is how first-play gets correct total duration. VOD playlists (WITH ENDLIST) referencing missing segments DO fail.
- **`-g 1` is mandatory for HLS video-audio (not just audiobooks)**: The HLS muxer can only cut segments at video keyframes. Without an explicit `-g` (keyframe interval), libx264's default GOP (250) at 1fps yields a keyframe only every ~4 min → segments span ~4 min → the first segment takes ~18–20s of encode to appear (the cache-miss "forever" delay). `VideoCodecArgs` must include `-g 1` (the audiobook path sets it; the single-item song path was missing it — fixed). Applies to any HLS video-audio path.

### Audiobook Resume (NativeControlsForBooks)

When `NativeControlsForBooks` is on, audiobooks play via VideoApp and resume from the last position. Position is tracked by watching HLS segment requests (`AudiobookPositionTracker`, keyed by book parent-folder ID, conservative `(highWaterMark−1)×10s`). Resume serves a **sliced** playlist (`AudiobookPlaylistBuilder`, `?start=<ticks>`) that begins at the target segment.

- **Why sliced, not `#EXT-X-START`**: The Echo Show's ExoPlayer **ignores `#EXT-X-START`** (verified on hardware — resume restarted from 0 even with the hint correctly served). Slicing works because it uses the same event-playlist mechanism as first-play. The `StartHint` strategy is kept in `AudiobookPlaylistBuilder` as a dormant fallback but is not active.
- **Known limitation — resume clock is relative**: Because the sliced playlist's first segment IS the resume segment, the player's elapsed-time/seek-bar timeline is **relative to the resume point**, not the book's absolute timeline. Resuming at 3:00 of a book shows as `0:00` on the seek bar, and the bar spans `[resumePoint → end]`. This is the unavoidable cost of seek-bar resume on the Echo; the audio position is correct. (The only way to get absolute time is `#EXT-X-START`, which the Echo ignores, or AudioPlayer, which has no seek bar.)
- **All playlist return paths must inject `?start=`**: `StreamHlsAudiobook` has four playlist returns (cache hit, encode-in-progress, concurrent-generated, post-encode). All route through `ServeAudiobookPlaylistAsync`, which injects the resume slice when `startTicks > 0`. Do not add a raw `PhysicalFile(playlist)` return without it.
- **Tracker key is the book parent-folder GUID**: `GetSegment` records segments keyed by the URL `itemId` (= parentId). Resume lookups use `item.ParentId`. `AudiobookPositionTracker.NormalizeKey` canonicalizes both to GUID `"N"` format — do not bypass it, or record (dashed URL) and read (`"N"`) keys will silently mismatch and resume will fall back to 0.

## Interaction Model Anti-Patterns — DO NOT REPEAT

These patterns have caused bugs repeatedly across many sessions. Every rule here was extracted from real commits that fixed real failures.

### 1. Static Samples Without Slots (MOST COMMON — 7+ incidents)

**NEVER add a concrete utterance like `"mostra artisti"` to an intent that uses slots.** Alexa's NLU preferentially matches the static variant and delivers an empty slot to the handler.

```
# ❌ WRONG — "mostra artisti" matches but browse_category is empty
"mostra artisti"
"mostra libri"
"Mostra {browse_category}"

# ✅ RIGHT — only slotted variants; handler prompts if slot is empty
"Mostra {browse_category}"
"Sfoglia {browse_category}"
"Elenca {browse_category}"
```

**Detection**: After editing any model JSON, search for samples without `{`:
```bash
grep -rn '"[A-Z][a-z].*"' model_*.json | grep -v '{' | grep samples
```

### 2. AMAZON.SearchQuery Coexistence (9+ incidents)

**`AMAZON.SearchQuery` CANNOT coexist with ANY other slot type in the same intent.** SMAPI rejects the model build. Use custom slot types instead.

```
# ❌ WRONG — two different slot types, one is SearchQuery
"slots": [{"name": "media_type", "type": "MediaType"}, {"name": "query", "type": "AMAZON.SearchQuery"}]

# ✅ RIGHT — use custom types for all slots
"slots": [{"name": "media_type", "type": "MediaType"}, {"name": "time_period", "type": "TimePeriod"}]
```

**Detection**: Already caught by `validate_interaction_models.py` check #6.

### 3. NLU Intent Competition (9+ incidents)

**Short/greedy patterns on one intent steal utterances from more specific intents.** Always qualify broad intents with carrier words or media-type nouns.

```
# ❌ WRONG — SearchMediaIntent captures "trova una canzone" before FindSongIntent
"Cerca {query}"          # too greedy
"Trova {query}"          # too greedy

# ✅ RIGHT — qualify with media type so specific intents can match
"Cerca un film {query}"
"Cerca il contenuto {query}"
```

**Detection**: Run NLU test suite after ANY model change. Watch for intent misclassification.

### 4. Cross-Locale Drift (8+ incidents)

**Always add new intents/slots to ALL 17 locales simultaneously.** For it-IT, edit the YAML template and regenerate (`python3 scripts/generate_interaction_model.py it-IT`). For others, edit JSON directly.

**Detection**: Already caught by `validate_interaction_models.py` cross-locale checks.

### 5. Custom Samples on Built-in Intents (3 incidents, all locales)

**NEVER add custom samples to `AMAZON.*` intents.** They break built-in behavior. If you need custom phrases, create a new custom intent.

```
# ❌ WRONG — breaks built-in NextIntent
{"name": "AMAZON.NextIntent", "samples": ["avanti", "successivo"]}

# ✅ RIGHT — empty samples, handle "avanti" in handler code or a custom intent
{"name": "AMAZON.NextIntent", "samples": []}
```

**Detection**: `grep -rn '"AMAZON\.' model_*.json | grep -v '"samples": \[\]'`

### 6. Vocabulary Expansion Side Effects (YAML Template Only)

Adding a verb to `imperative`/`infinitive` vocabulary in the it-IT YAML template generates samples across ALL template intents via Cartesian product. A verb appropriate for one intent may produce nonsensical samples for another.

**Rule**: After editing the YAML template vocabulary, regenerate and inspect the diff for unexpected samples across unrelated intents.

### 7. Slot Value Guards Must Use IsNullOrWhiteSpace

When Alexa partially matches, slots arrive as empty strings or whitespace. Always use `IsNullOrWhiteSpace`, never `IsNullOrEmpty`:

```csharp
// ❌ WRONG — " " passes through
if (!string.IsNullOrEmpty(genreSlot))

// ✅ RIGHT — " " is caught
if (!string.IsNullOrWhiteSpace(genreSlot))
```

### 8. Missing Slot Type Values for Test Fixtures

Test fixtures that use entity names (album/song/genre) must have those names in the corresponding custom slot type. If the slot type doesn't include the value, NLU can't fill the slot.

**Rule**: Cross-reference test fixture `expected_slots` values against slot type `values` arrays in the model JSON.

### 9. Dialog.ElicitSlot Without Model Registration (1 incident — silently broken)

**`Dialog.ElicitSlot` silently fails if the target intent is not listed in the model's `dialog.intents` array.** No error, no warning. The session stays open, the user hears the prompt, but the directive is dropped. The user's follow-up goes through Alexa's general NLU, which routes music queries to Amazon Music. The skill never receives the request.

```json
// ❌ WRONG — model has no dialog section or FindSongIntent is missing from dialog.intents
// Code sends Dialog.ElicitSlot for FindSongIntent → Alexa silently ignores it

// ✅ RIGHT — model's dialog.intents includes the target intent
"dialog": {
  "intents": [
    {
      "name": "FindSongIntent",
      "confirmationRequired": false,
      "slots": [
        { "name": "titleKeywords", "type": "AMAZON.SearchQuery", "confirmationRequired": false, "elicitationRequired": false }
      ]
    }
  ]
}
```

**Detection**: If a handler uses `ElicitSlotDirective`, verify the intent appears in `dialog.intents` in the model JSON:
```bash
python3 -c "import json; d=json.load(open('model_it-IT.json')); m=d.get('interactionModel',d); print([i['name'] for i in m.get('dialog',{}).get('intents',[])])"
```

**Why this is so insidious**: Everything *looks* like it works — Alexa speaks the prompt, the user responds — but the response goes to Amazon Music instead of the skill. There is zero error feedback.

## Release

The CI workflow (`release-build.yml`) handles building, testing, zipping, creating the GitHub release, computing the manifest checksum, and committing the updated manifest back to main. It triggers on tag push.

**Pre-flight checklist (before tagging):**

1. **Bump version** in `Directory.Build.props` AND `build.yaml` (4-part format, e.g. `0.5.0.0`)
2. **Update `build.yaml` changelog** — this becomes the manifest changelog and GitHub release description
3. **Add placeholder entry to `manifest.json`** — add a new version object with `"checksum": "placeholder"`, `"changelog": "placeholder"`, correct `sourceUrl` and `targetAbi`. The CI replaces checksum and changelog after building the zip.
4. **Run `python3 scripts/validate_versions.py`** — must show all 3 sources match
5. **Build and test locally**: `dotnet build` (0 warnings) + `dotnet test` (all pass)
6. **Verify `icon.jpg` exists** at `Jellyfin.Plugin.AlexaSkill/icon.jpg` — the release workflow copies it as `icon.png` into the zip

**Tag and push:**

```bash
git add Directory.Build.props build.yaml manifest.json
git commit -m "Release v0.5.0.0"
git tag 0.5.0.0
git push origin main --tags
```

**Post-release verification:**

1. Check CI workflow passed: `gh run list --workflow=release-build.yml --limit=1`
2. Verify GitHub release exists: `gh release view 0.5.0.0`
3. Verify manifest.json was committed with correct checksum (not "placeholder")
4. Verify the zip contains all expected DLLs + icon.png: download and `unzip -l`
5. **Set curated GitHub release notes (MANDATORY — never skip, recurring mistake).** `release-build.yml` uses `generate_release_notes: true`, which only lists PR titles — but this repo commits directly to `main` with no PRs, so the auto body is a bare ~100-byte `compare/...` link. The `build.yaml` changelog flows into `manifest.json` but NOT onto the GitHub release page. Write curated markdown (Features/Fixes/Internals + Known limitations, sourced from `build.yaml`'s changelog) to a file and apply:
   Write **user-facing** curated markdown — plain language describing what the user can now do / what changed for them, plus anything they need to know (limitations, device support). **No code symbols, no class/handler/API names, no internal mechanisms** — keep the technical root-cause and internals in the GitHub issue, NOT the release notes. Write in English (the catalog/changelog lingua franca), source the gist from `build.yaml`'s changelog, save to a file and apply:
   ```bash
   gh release edit <tag> --notes-file /tmp/release_notes_<tag>.md
   gh release view <tag> --json body -q '.body' | wc -c   # must be hundreds+ of bytes, NOT ~100
   ```

**How the CI release works:**

1. Triggers on tag push (`*`)
2. Validates tag matches `Directory.Build.props` version
3. Runs validation scripts (models, locales, versions)
4. `dotnet publish --configuration Release` + `dotnet test`
5. Creates zip from publish output + `icon.jpg` (copied as `icon.png`)
6. Creates GitHub release with `softprops/action-gh-release` (auto-generated release notes)
7. `add_release_to_manifest.py` computes MD5 from the zip, updates manifest.json
8. Commits manifest.json back to main

**Common pitfalls:**

- **Wrong checksum** → Jellyfin rejects the download. The CI computes the checksum from the actual zip, so this should be correct. NEVER manually edit checksums.
- **Missing icon** → The zip must contain `icon.png`. The workflow copies `icon.jpg` as `icon.png`. If `icon.jpg` is missing from the source tree, the zip will have no icon.
- **Stale changelog** → `build.yaml` changelog becomes the manifest entry. Update it before tagging.
- **Version mismatch** → Tag must match `Directory.Build.props` version exactly, or CI rejects it.

## Backlog Workflow

This project uses Backlog.md MCP. Before creating tasks, call `backlog.get_backlog_instructions()` with `instruction` selector for `task-creation`, `task-execution`, or `task-finalization` guides.
