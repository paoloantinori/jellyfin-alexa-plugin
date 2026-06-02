# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin (net9.0) exposing an Alexa skill for media playback, search, and library management. Targets Jellyfin 10.11+.

## Build & Test

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests          # ~2065 unit tests
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

GitHub Actions runs on every PR and push to main (`ci.yml`):
- **build-and-test**: Release build with `-warnaserror` + full test suite
- **validate-models**: Interaction model structural validation (advisory)
- **validate-locales**: Locale key coverage vs baseline (fails on new gaps only)
- **validate-versions**: Directory.Build.props / build.yaml / manifest.json consistency

CI validates models, locales, and versions on every PR and push to main.

## Project Layout

- `Alexa/Handler/Intent/` — 58 intent handlers (one per intent, inherit `BaseHandler`)
- `Alexa/Handler/BaseHandler.cs` — shared utilities: `FuzzyMatch`, `HandleFuzzyMiss`, `RetryAsync`, stream URLs, library filters
- `Alexa/InteractionModel/` — 17 per-locale interaction model JSONs (`model_*.json`), generated from templates in `Alexa/InteractionModel/templates/`
- `Alexa/Locale/` — Response strings: keys in `ResponseStrings.cs`, values in 17 `<locale>.json` files
- `Alexa/SmapiManagement.cs` — SMAPI wrapper (skill CRUD, account linking, status polling)
- `Alexa/ModelDeployment/` — Custom interaction model validation, fetch, deploy, restore via SMAPI
- `Alexa/Manifest/` — Skill manifest generation
- `Alexa/Apl/` — APL visual template generation (carousel, NowPlaying screen)
- `Alexa/Cache/` — In-memory cache layer for artist/item lookups
- `Alexa/Catalog/` — Music catalog browsing (browse categories, recently added, recommendations)
- `Alexa/Directive/` — Alexa response directives (AudioPlayer, APL, template rendering)
- `Alexa/DynamicEntities/` — Dynamic entity slot updates via SMAPI
- `Alexa/Interface/` — Alexa interface capability detection (APL support, etc.)
- `Alexa/Music/` — Music-specific data models and helpers
- `Alexa/Playback/` — Playback state and progressive queue management
- `Alexa/Util/` — Shared utility classes
- `Alexa/ArtistIndexService.cs` — In-memory artist index with event-driven refresh
- `Alexa/CircuitBreaker.cs` — Circuit breaker for external API resilience
- `Alexa/ErrorClassifier.cs` — Categorizes errors for user-facing responses
- `Alexa/CustomerProfileService.cs` — Amazon customer profile lookups
- `Alexa/SlotMappings.cs` — Slot name → type mappings (consistency enforcement)
- `Alexa/FuzzyMatcher.cs` — Fuzzy string matching with configurable thresholds
- `Alexa/RetryHelper.cs` — Exponential backoff retry with timeout budget (default 6s)
- `Alexa/Pipeline/` — Request routing pipeline
- `Configuration/` — Plugin config DTO + Jellyfin config UI (`config.html`)
- `Controller/` — ASP.NET API controllers (skill endpoint, config, simulator, health)
- `docs/` — 102 Mermaid diagrams covering 6 feature flows × 17 locales
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

## Cross-Media-Type Fallback

When a handler's primary search finds no results (e.g., PlaySongIntent finds no song), `BaseHandler.BuildArtistSongsResponseAsync` falls back to artist search. This is shared across PlaySong, PlayAlbum, and PlayVideo handlers via `BaseHandler`.

## Search Response Mode

`SearchResponseMode` controls the speed/recall trade-off for artist search, configurable per-user or globally (`DefaultSearchResponseMode` in config):
- **Thorough** (default): full 4-tier fallback chain with disambiguation prompts. Best recall.
- **Fast**: single query or reduced tiers with auto-play. Fastest response, may miss obscure matches.

In Fast mode, `SearchWithAsrFallbackAsync` skips compound-word retries. Handlers call `GetSearchResponseMode(user)` to resolve the effective mode.

## ASR Compound-Word Fix

When enabled (`AsrCompoundWordFixEnabled`), `SearchWithAsrFallbackAsync` in `BaseHandler` retries the original query with joined/split word variants. For example, "lazy bones" retries as "lazybones". Only triggers when the original query returns no results.

## Code Conventions

- `Nullable enable` on — nullability annotations required
- `jellyfin.ruleset` controls code analysis (AllEnabledByDefault, `TreatWarningsAsErrors` = true)
- Intent handlers use `async/await` with `ConfigureAwait(false)`
- Feature flag tests use one file per flag, `AssertDisabledWhenFlagOff` helper

## Interaction Models

17 locale files. The it-IT model is **generated from a YAML template** — do NOT edit the JSON directly.

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
- **AudioPlayer responses**: `BuildAudioPlayerResponse` sets `ShouldEndSession=true`. Amazon routes pause/resume/next/previous intents to the skill automatically when audio is playing, regardless of session state. Using `ShouldEndSession=false` on Play responses kept an active session that prevented the Echo from routing "stop/ferma" to `AMAZON.PauseIntent` (it sent `SessionEndedRequest` instead).
- **Resume item resolution**: Prefer `context.AudioPlayer.Token` over `session.FullNowPlayingItem`. Jellyfin's `PlaybackStopped` event clears `FullNowPlayingItem` before the resume request arrives, but `AudioPlayer.Token` survives.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space NLU tests with `SMAPI_DELAY=1.5`.
- **ValueTuple serialization**: Never store `ValueTuple` in session attributes — Newtonsoft.Json serializes as Item1/Item2. Use named DTOs.
- **Config.Users in API responses**: Never send `config.Users` via `updatePluginConfiguration` — it can wipe skill config entries. Use dedicated endpoints.
- **Entity resolution for slot synonyms**: `slot.Value` always contains the raw spoken text (e.g. "gli album"). To get the canonical value ("album"), extract from `slot.Resolution.Authorities[0].Values[0].Value.Name` when `Status.Code == "ER_SUCCESS_MATCH"`. See `BrowseLibraryIntentHandler.GetCanonicalSlotValue()` for the pattern.

## Release

1. Bump version in `Directory.Build.props` and `build.yaml`
2. Run full test suite (unit + NLU for all locales)
3. Commit, tag with version, push: `git push origin main --tags`
4. Update `manifest.json` with new version entry (see `release.sh`)

## Backlog Workflow

This project uses Backlog.md MCP. Before creating tasks, call `backlog.get_backlog_instructions()` with `instruction` selector for `task-creation`, `task-execution`, or `task-finalization` guides.
