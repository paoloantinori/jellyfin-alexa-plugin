# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin (net9.0) exposing an Alexa skill for media playback, search, and library management. Targets Jellyfin 10.11+.

## Build & Test

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests
```

## Project Layout

- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/` — Intent handlers (one per intent, inherit `BaseHandler`)
- `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/` — Per-locale interaction model JSON files (17 locales)
- `Jellyfin.Plugin.AlexaSkill/Alexa/Locale/` — Response string localizations
- `Jellyfin.Plugin.AlexaSkill/Alexa/Apl/` — APL visual template helpers
- `Jellyfin.Plugin.AlexaSkill/Alexa/Cache/` — In-memory caching layer
- `Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/` — SMAPI catalog management
- `Jellyfin.Plugin.AlexaSkill/Alexa/Directive/` — Alexa response directives (AudioPlayer, APL)
- `Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/` — Dynamic entity slot updates
- `Jellyfin.Plugin.AlexaSkill/Alexa/Manifest/` — Skill manifest generation
- `Jellyfin.Plugin.AlexaSkill/Alexa/Playback/` — Playback state and queue management
- `tests/integration/` — NLU and E2E test suites (Python/pytest, use SMAPI)
- `manifest.json` — Jellyfin plugin manifest (version entries)
- `build.yaml` — Plugin metadata (version, targetAbi, artifacts)
- `Directory.Build.props` — Version numbers (single source of truth)

## Handler Pattern

All intent handlers live in `Alexa/Handler/Intent/` and inherit `BaseHandler`. Each implements:
- `CanHandle(Request)` — returns true if this handler should process the request
- `HandleAsync(Request, Context, User, Session, CancellationToken)` — executes the intent

The `RequestPipeline` in `Alexa/Pipeline/` routes requests to handlers. `BaseHandler` provides shared utilities:
- `FuzzyMatch(query, candidates, selector)` — best-match selection using FuzzyStrings
- `DisambiguationHelper.AskFirstMatch()` — voice prompt for ambiguous results
- `GetStreamUrl()` / `GetVideoStreamUrl()` — Jellyfin `/stream?static=true` endpoints
- `RetryAsync()` — library query retry with logging

New intents need: handler class + entry in `Alexa/IntentNames.cs` + interaction model samples + locale response strings.

## Localization

Response strings are defined in `Alexa/Locale/ResponseStrings.cs` as keys, with per-locale values in `Alexa/Locale/<locale>.json`. To add a new string:
1. Add a const key in `ResponseStrings.cs`
2. Add translations in each locale JSON file
3. Use `ResponseStrings.Get("Key", locale)` in handlers — missing locale = runtime exception

## Code Conventions

- `Nullable enable` is on — nullability annotations required
- `jellyfin.ruleset` controls code analysis (AllEnabledByDefault)
- `TreatWarningsAsErrors` is false — warnings are advisory
- Intent handlers use `async/await` with `ConfigureAwait(false)`

## Interaction Models

17 locale files in `Alexa/InteractionModel/model_*.json`. After editing:
1. Wrap in `{"interactionModel": <model>}` for SMAPI
2. Deploy: `ask smapi set-interaction-model --skill-id <ID> --stage development --locale <XX> --interaction-model file:payload.json`
3. Wait for build: `ask smapi get-skill-status --skill-id <ID>`

## NLU Integration Tests

```bash
./scripts/run_nlu_tests.sh                  # all locales
./scripts/run_nlu_tests.sh -k "en-US"       # single locale
./scripts/run_nlu_tests.sh --dry-run         # validate fixtures only
./scripts/validate_model.sh                  # validate interaction model JSON syntax
```

Requires `ask` CLI authenticated. Test fixtures in `tests/integration/fixtures/*.yaml`.
Env vars: `ASK_SKILL_ID`, `SMAPI_DELAY` (default 1.5s), `SMAPI_TIMEOUT`.

### E2E Tests

E2E tests use SMAPI `simulate-skill` (full Alexa pipeline including NLU + skill execution) rather than `profile-nlu` (NLU-only). The `smapi_client.py` automatically prefixes utterances with locale-aware invocation patterns (e.g. `"chiedi a jellyfin player di ..."` for it-IT).

```bash
./scripts/run_e2e_tests.sh                                         # requires live Jellyfin
./scripts/run_e2e_tests.sh --dry-run                               # validate fixtures only
```

E2E tests are auto-skipped without Jellyfin connection. Provide via CLI flags or env vars:
`--jellyfin-url` / `JELLYFIN_URL`, `--jellyfin-api-key` / `JELLYFIN_API_KEY`, `--jellyfin-user` / `JELLYFIN_USER`

**E2E fixture files**: `tests/integration/fixtures/e2e_*.yaml` (e.g. `e2e_en-US.yaml`, `e2e_it-IT.yaml`). Each fixture requires `locale`, `invocation_name`, and a `tests` list with `utterance`, `expected_intent`, `expected_slots`, and `expected_response_type`.

**Important**: `simulate-skill` routes through Alexa's full NLU which competes with built-in Amazon skills. en-US E2E tests are unreliable for this reason — prefer it-IT for simulate-skill testing. Use `expected_response_type: any` since SMAPI simulate-skill does not reliably return skill execution payload (outputSpeech/directives).

## Key Gotchas

- **Stream endpoints**: Audio uses `/Audio/{id}/stream?static=true`, video uses `/Videos/{id}/stream?static=true`. The `/Download` endpoint lacks proper HTTP headers (Content-Type, Range support) needed by Alexa AudioPlayer.
- **AMAZON.SearchQuery cannot coexist with other slot types** in the same utterance. Use custom slot types (e.g. `MediaType`) for slots that appear alongside typed slots like `TimePeriod`.
- **Slot name consistency**: Alexa requires the same slot name to use the same slot type across all intents in a locale.
- **Stop vs Pause**: `AMAZON.StopIntent` must return `ResponseBuilder.Empty()` (device already stopped locally). `AMAZON.PauseIntent` must return `AudioPlayerStop()`. Returning the wrong response type causes the device to ignore the request.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space live NLU tests with `SMAPI_DELAY=1.5`. Model builds take ~15-30s.

## Release

1. Bump version in `Directory.Build.props` and `build.yaml`
2. Run full test suite (unit + NLU for all locales)
3. Commit, tag with version, push: `git push origin main --tags`
4. Update `manifest.json` with new version entry (see `release.sh`)

<!-- BACKLOG.MD MCP GUIDELINES START -->

<CRITICAL_INSTRUCTION>

## BACKLOG WORKFLOW INSTRUCTIONS

This project uses Backlog.md MCP for all task and project management activities.

**CRITICAL GUIDANCE**

- If your client supports MCP resources, read `backlog://workflow/overview` to understand when and how to use Backlog for this project.
- If your client only supports tools or the above request fails, call `backlog.get_backlog_instructions()` to load the tool-oriented overview. Use the `instruction` selector when you need `task-creation`, `task-execution`, or `task-finalization`.

- **First time working here?** Read the overview resource IMMEDIATELY to learn the workflow
- **Already familiar?** You should have the overview cached ("## Backlog.md Overview (MCP)")
- **When to read it**: BEFORE creating tasks, or when you're unsure whether to track work

These guides cover:
- Decision framework for when to create tasks
- Search-first workflow to avoid duplicates
- Links to detailed guides for task creation, execution, and finalization
- MCP tools reference

You MUST read the overview resource to understand the complete workflow. The information is NOT summarized here.

</CRITICAL_INSTRUCTION>

<!-- BACKLOG.MD MCP GUIDELINES END -->
