# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin (net9.0) exposing an Alexa skill for media playback, search, and library management. Targets Jellyfin 10.11+.

## Build & Test

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests          # ~2084 unit tests
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
- `docs/` — 103 Mermaid diagrams covering 6 feature flows × 17 locales
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
- Feature flag tests use one file per flag, `AssertDisabledWhenFlagOff` helper

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
- **AudioPlayer responses**: `BuildAudioPlayerResponse` sets `ShouldEndSession=true`. Amazon routes pause/resume/next/previous intents to the skill automatically when audio is playing, regardless of session state. Using `ShouldEndSession=false` on Play responses kept an active session that prevented the Echo from routing "stop/ferma" to `AMAZON.PauseIntent` (it sent `SessionEndedRequest` instead).
- **Resume item resolution**: Prefer `context.AudioPlayer.Token` over `session.FullNowPlayingItem`. Jellyfin's `PlaybackStopped` event clears `FullNowPlayingItem` before the resume request arrives, but `AudioPlayer.Token` survives.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space NLU tests with `SMAPI_DELAY=1.5`.
- **ValueTuple serialization**: Never store `ValueTuple` in session attributes — Newtonsoft.Json serializes as Item1/Item2. Use named DTOs.
- **Config.Users in API responses**: Never send `config.Users` via `updatePluginConfiguration` — it can wipe skill config entries. Use dedicated endpoints.
- **AudioPlayer event restrictions**: `PlaybackFinished` and `PlaybackNearlyFinished` can ONLY return `AudioPlayer.Play` directives — no `outputSpeech`, `reprompt`, or `shouldEndSession=false`. Any response needing speech must come from intent handlers, not AudioPlayer events.
- **Entity resolution for slot synonyms**: `slot.Value` always contains the raw spoken text (e.g. "gli album"). To get the canonical value ("album"), extract from `slot.Resolution.Authorities[0].Values[0].Value.Name` when `Status.Code == "ER_SUCCESS_MATCH"`. See `BrowseLibraryIntentHandler.GetCanonicalSlotValue()` for the pattern.
- **Dialog.ElicitSlot requires model registration**: Any intent that uses `Dialog.ElicitSlot` directives MUST be listed in the interaction model's `dialog.intents` array. Without this registration, Alexa **silently ignores** the directive — the session stays open (`ShouldEndSession=false`) but the user's follow-up goes through general NLU, which routes music queries to Amazon Music instead of back to the skill. Set `elicitationRequired: false` on slots when controlling dialog manually from code. This must be done in ALL 17 locales. For it-IT, add to the YAML template's `dialog` section and regenerate.

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
