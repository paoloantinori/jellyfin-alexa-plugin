# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin (net9.0) exposing an Alexa skill for media playback, search, and library management. Targets Jellyfin 10.11+.

## Build & Test

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests          # ~1424 unit tests
python3 scripts/validate_interaction_models.py        # Check all 17 models (JSON, slots, drift)
python3 scripts/validate_locales.py                   # Check locale key coverage (baseline-aware)
python3 scripts/validate_versions.py                  # Check version consistency across files
python3 scripts/validate_apl.py                       # Check APL templates validity
./scripts/run_nlu_tests.sh                            # NLU tests (needs ask CLI auth)
./scripts/run_nlu_tests.sh -k "en-US"                 # single locale
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

- `Alexa/Handler/Intent/` — 57 intent handlers (one per intent, inherit `BaseHandler`)
- `Alexa/Handler/BaseHandler.cs` — shared utilities: `FuzzyMatch`, `HandleFuzzyMiss`, `RetryAsync`, stream URLs, library filters
- `Alexa/InteractionModel/` — 17 per-locale interaction model JSONs (`model_*.json`)
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

- `FuzzyMatch(query, candidates, selector)` — best-match via FuzzyStrings library
- `HandleFuzzyMiss()` — disambiguation with voice prompts; auto-plays near-exact matches (score >= 90) without qualifier
- `GetStreamUrl()` / `GetVideoStreamUrl()` — `/Audio|Videos/{id}/stream?static=true`
- `RetryAsync(operation, label)` — retry with exponential backoff, 6s timeout budget
- `IfFeatureDisabled()` — short-circuit on feature flags
- `ApplyLibraryFilter()` / `FilterByContentAccess()` — per-user library and content type gating
- `BuildPauseResponse()` — `AudioPlayerStop()` + `ShouldEndSession=false` (keeps session open for resume)

New intents need: handler class + `IntentNames.cs` entry + interaction model samples + 17 locale response strings.

## Artist Search Fallback Chain

`PlayArtistSongsIntentHandler` has a 4-tier fallback (each is a separate DB query):
1. `SearchTerm` — Jellyfin search index
2. `NameStartsWith` first word — prefix with single word
3. `NameStartsWith` full query — prefix with full string
4. `NameContains` full query — substring match anywhere in name

All tiers go through `FuzzyMatch` to filter false positives. See JF-163 for planned in-memory optimization.

## Code Conventions

- `Nullable enable` on — nullability annotations required
- `jellyfin.ruleset` controls code analysis (AllEnabledByDefault, `TreatWarningsAsErrors` = true)
- Intent handlers use `async/await` with `ConfigureAwait(false)`
- Feature flag tests use one file per flag, `AssertDisabledWhenFlagOff` helper

## Interaction Models

17 locale files. After editing:
1. Wrap in `{"interactionModel": <model>}` for SMAPI
2. Deploy: `ask smapi set-interaction-model --skill-id <ID> --stage development --locale <XX> --interaction-model file:payload.json`
3. Wait for build (~15-30s): `ask smapi get-skill-status --skill-id <ID>`

NLU test fixtures in `tests/integration/fixtures/<locale>.yaml`. E2E fixtures in `tests/integration/fixtures/e2e_<locale>.yaml`.

**en-US E2E tests are unreliable** — `simulate-skill` competes with built-in Amazon skills. Prefer it-IT for simulate-skill testing.

## Recurring Mistakes — DO NOT REPEAT

- **NEVER use cached skill IDs** — The Alexa skill ID changes every time config is wiped (plugin creates a new skill). ALWAYS run `ask smapi list-skills-for-vendor` and find the current Jellyfin skill ID BEFORE any SMAPI operation. Never trust skill IDs from memory files, environment variables, or previous sessions.
- **NEVER access plugin files on the host filesystem** — Jellyfin runs in a podman container named `jellyfin`. Plugin files are INSIDE the container at `/config/data/plugins/AlexaSkill_<version>/`. Use `podman exec jellyfin ...` to read files, `podman cp` to copy files in/out, `podman logs jellyfin` for logs. Never try `scp` to host paths or `find /` on the host.

## Key Gotchas

- **Stream endpoints**: Audio uses `/Audio/{id}/stream?static=true`, video uses `/Videos/{id}/stream?static=true`. Do NOT use `/Download` — lacks Content-Type and Range headers needed by AudioPlayer.
- **AMAZON.SearchQuery** cannot coexist with other slot types in the same utterance. Use custom slot types (e.g. `MediaType`) instead.
- **Slot name consistency**: Same slot name must use same slot type across all intents in a locale.
- **Stop vs Pause**: `AMAZON.StopIntent` → `ResponseBuilder.Empty()`. `AMAZON.PauseIntent` → `BuildPauseResponse()` (AudioPlayerStop + ShouldEndSession=false). Wrong response type = device ignores the request. Never use bare `AudioPlayerStop()` for pause — it defaults `ShouldEndSession=true`, which prevents resume routing.
- **Resume item resolution**: Prefer `context.AudioPlayer.Token` over `session.FullNowPlayingItem`. Jellyfin's `PlaybackStopped` event clears `FullNowPlayingItem` before the resume request arrives, but `AudioPlayer.Token` survives.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space NLU tests with `SMAPI_DELAY=1.5`.
- **ValueTuple serialization**: Never store `ValueTuple` in session attributes — Newtonsoft.Json serializes as Item1/Item2. Use named DTOs.
- **Config.Users in API responses**: Never send `config.Users` via `updatePluginConfiguration` — it can wipe skill config entries. Use dedicated endpoints.

## Release

1. Bump version in `Directory.Build.props` and `build.yaml`
2. Run full test suite (unit + NLU for all locales)
3. Commit, tag with version, push: `git push origin main --tags`
4. Update `manifest.json` with new version entry (see `release.sh`)

## Backlog Workflow

This project uses Backlog.md MCP. Before creating tasks, call `backlog.get_backlog_instructions()` with `instruction` selector for `task-creation`, `task-execution`, or `task-finalization` guides.
