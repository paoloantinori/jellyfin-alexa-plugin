# Jellyfin Alexa Skill Plugin

C# Jellyfin plugin exposing an Alexa skill for media playback, search, and library management. Dual-target: net9.0 (Jellyfin 10.11.x, the shipping line) + net10.0 (Jellyfin 12.0, pre-release line).

## Build & Test

**Dual-target build (JF-307 Phase-2, landed 2026-09-09):** both csprojs are `net9.0;net10.0` with Condition'd Jellyfin refs (10.11.8 for net9.0, 12.0.0-rc7 for net10.0). The SYSTEM SDK 9 cannot restore the multi-target project (NETSDK1045): full builds need SDK 10 (`~/.dotnet-jf307`, 10.0.400, or CI's 10.0.x). The system-SDK net9.0 recipe: `dotnet restore <proj> -p:TargetFramework=net9.0` then `dotnet build <proj> -f net9.0 --no-restore`. XML comments in csproj files must NOT contain `--` (MSB4025). NoWarn: CS1591;CS1573;CA1873 (519-site log-argument churn contradicts the debug-logging policy); NU1904 (Refit 4.7.51 GHSA advisory, pinned by Alexa.NET.Management - the library update is the tracked fix, NOT bumped: SMAPI runtime risk). CA2025 is NOT suppressed: fixed properly (JF-307 Phase-2).

**Jellyfin 12.0 auth change (live-verified 2026-09-09 on the production 12.0.0 box):** the `X-Emby-Token` header and lowercase `api_key` query are REJECTED (401) on the general API surface; working shapes are `?ApiKey=<key>` (capital) and `Authorization: MediaBrowser Token="<key>"`. The e2e/simulator tooling needed this switch. CAVEAT (re-verified 2026-09-10): BOTH stream endpoints (`/Audio/{id}/stream` and `/Videos/{id}/stream`) serve media with NO key at all (206, real bytes; general API correctly 401s). This is NOT a 12.0 regression to report: it is a long-standing upstream design gap, already tracked as jellyfin/jellyfin#13984 (open since 2025-04, split from #5415, labels bug+security; 12.0 tightened header auth while leaving streams open). Do not draft a duplicate report. Plugin stream URLs keep working regardless; our signed-token gate (JF-309) covers only our own `/alexaskill` endpoints, never Jellyfin's native stream routes.

**/tmp per-user quota failure mode:** silent shell deaths (every command exit-1, no output) + a previously-green suite going mass-red = the per-user /tmp tmpfs quota (12.7G) is full. Check `quota -s` and `df -h /tmp` FIRST. Cause class: dispatched workers each exporting their own NuGet cache copy (~414-535M each). Dedupe down to the canonical `/tmp/nuget-pkgs`; never let a worker invent its own cache path. `/tmp` also holds other projects' artifacts (ML models, wt-* worktrees) - look before deleting.

**Coverage caveat:**
**Coverage caveat:** unit tests (below) and E2E tests (`run_e2e_tests.sh`) assert response *correctness*, not response *latency*. They will NOT catch a play-path regression that exceeds Alexa's ~8s response window (→ `INVALID_RESPONSE` on-device). The only guard for that is `RetryHelperTests.Sync_AlwaysTransient_StopsWithinTimeoutBudget` — it locks the invariant that `RetryAsync` stops retrying once its timeout budget (`AlexaRequestTimeoutMs`=6000) is exhausted, which is the mechanism that keeps throwing/slow play-path queries from blowing the Alexa budget (JF-358/JF-359). A live-timing E2E assertion is intentionally not used — it's flaky and environment-dependent.

```bash
dotnet build Jellyfin.Plugin.AlexaSkill.sln
dotnet test Jellyfin.Plugin.AlexaSkill.Tests          # ~2800 unit tests
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

## Stop / Session Routing During Playback (THE REFERENCE)

This topic has bitten us repeatedly since 2026-07. Everything verified lives HERE; do not touch `shouldEndSession` on any play/stop/event path without re-reading this section. Full research dossier with sources: `claudedocs/research_alexa-videoapp-stop-routing_2026-09-07.md`.

### The documented platform model (Amazon docs, verified 2026-09-07)

- **`shouldEndSession` semantics** (Request/Response JSON Reference): `true` ends the session; `false`/`null` present in the JSON opens the microphone (reprompt flow); **absent (undefined)**: on a screen device, if the response includes screen content, the session stays open **up to 30 more seconds with the microphone CLOSED**. Inside that window "Alexa, \<utterance\>" (wake word REQUIRED) routes to the skill as a continuing session; speech WITHOUT the wake word is ignored. Responses to `AMAZON.StopIntent` must use `true`.
- **VideoApp.Launch responses must omit `shouldEndSession` entirely** ("Do not include the shouldEndSession parameter in the response, even if you set the value to null"). Our builders comply (`ShouldEndSession = null`, which Alexa.NET omits from the JSON). On Echo Show this buys the 30-second closed-mic session above; there is no way to buy more. No keepalive mechanism exists for VideoApp.
- **The VideoApp interface has exactly ONE directive: `VideoApp.Launch`.** There is NO `VideoApp.Stop` and no pause directive: once video starts, the skill CANNOT programmatically dismiss or pause the player. `AMAZON.CancelIntent` is not supported with VideoApp; the back button is always shown and cannot be hidden. VideoApp playback also emits NO requests/events at all when the video finishes, so the skill never learns a VideoApp playback ended; that is why TV episode auto-advance (JF-324) exists only on the AudioPlayer path, where `PlaybackNearlyFinished` does fire.
- **The VideoApp doc claims** `AMAZON.PauseIntent`/`AMAZON.StopIntent` "work with VideoApp" and "send the same message to the skill" (voice controls: pause/resume, stop/close). Our device evidence supports that claim ONLY inside the 30s session window; outside it we have never observed a stop arriving.

### Audio transport commands and the stop-honoring contract (AudioPlayer)

Documented in the Standard Built-in Intents table (verified 2026-09-07): while the skill **is playing audio or was the most recent skill to play audio**, users can invoke these intents **WITHOUT the invocation name**: `AMAZON.PauseIntent` and `AMAZON.ResumeIntent` (both MUST be implemented), plus `AMAZON.NextIntent`, `AMAZON.PreviousIntent`, `AMAZON.StartOverIntent`, `AMAZON.RepeatIntent`, and `AMAZON.CancelIntent`, which are sent to the skill **even if not in the intent schema**. `AMAZON.StopIntent` is NOT in that list: it is the exit-skill intent; its response must have `shouldEndSession` `true` or `null`, and implementing it is a certification requirement.

Honoring contract:

- To stop audio from the skill: send the `AudioPlayer.Stop` directive (or `ClearQueue` with `CLEAR_ALL`, which also empties the queue). During playback, ANY voice request temporarily pauses the stream and auto-resumes it when the interaction completes; the device then sends `PlaybackStopped`, whose correct response is the keep-alive ack (never `shouldEndSession=false`, JF-299).
- `AudioPlayer.Play` responses: the docs state it directly: set `shouldEndSession=true`; with `false`, "Alexa sends the stream to the device for playback, and then pauses the stream to listen for the user's response". That sentence is the documented mechanism behind JF-299's play-true rule.
- Screen tap controls during audio: tapping pause stops playback natively with NO request to the skill; next/previous/play taps arrive as `PlaybackController.*CommandIssued` requests (handled: Pause/Next/Previous/Resume/Play cover the command types).
- We implement the full documented transport set (Next/Previous/Pause/Resume/StartOver/Loop on-off/Shuffle on-off handlers; stop/cancel ride `PauseIntentHandler` with `AudioPlayerStop()` + `ShouldEndSession=true`).

Divergence to keep honest: the docs say next/previous/startover/repeat/cancel route to the playing skill unconditionally; our 2026-07-02 on-device test ("stop"/"ferma"/"avanti") showed none of them arriving (default-music-service claim), while pause/resume matched the docs. Open confound: "avanti" may not be a canonical it-IT NextIntent word ("successivo" is). Re-test with the documented words before treating either the doc claim or the observed claim as final.

### Our live evidence (log-verified; this is the "every tanto va" pattern, explained)

- **AudioPlayer playback** (verified on-device 2026-07-02, pause re-confirmed 2026-09-07): Amazon auto-routes ONLY **Pause/Resume** to the active skill. **Stop/Next/Previous are frequently claimed by the device's default music service** (Amazon Music/Spotify): zero `StopIntent`/`NextIntent`/`PlaybackStopped` events for "stop"/"ferma"/"avanti"; Alexa simulator `ConsideredIntents` = `<IntentForDifferentSkill>`. A fresh content request ("play a different playlist/album/artist/song") is likewise NOT auto-routed; it goes to the default music service. Not fixable plugin-side: custom `AudioPlayer` skills cannot claim the device's default-music slot (reserved for the Music/Radio/Podcast Skill API, Amazon-partnership-only; same reason custom skills get no seek bar). `PlaybackController` is NOT a fix (per Amazon docs it serves hardware buttons only, has no STOP op, and is never sent for voice). Keeping the session open does NOT help (and `JF-299` shows `shouldEndSession=false` is harmful).
- **VideoApp playback** (2026-09-07 device session, podman-log verified): video launched at 14:11:35 with a compliant response (`shouldEndSession` ABSENT); exactly ONE `AMAZON.StopIntent` arrived at 14:11:54, +19s after launch, same sessionId (`new:false`), i.e. inside the documented 30s window. A second video launched at 14:23:31 from a session that had an open reprompt exchange before it, with an IDENTICAL compliant launch-response shape: ZERO stop requests for the following ~110s of playback while the user tried repeatedly. The prior open/closed state of the session did NOT change the launch response's own session semantics; the difference between the two outcomes is timing (inside vs outside the 30s window) and/or the wake word being prefixed, which our logs cannot distinguish.
- **A stop that arrives does NOT stop the video**: at 14:11:54 the skill answered with `AudioPlayer.Stop` + session end (docs-compliant) and the video kept playing. Corroborated by SO 60500295 (a VideoApp skill where StopIntent arrives but a speech-only response leaves the video streaming). With no VideoApp.Stop directive, there is no supported way for the skill to dismiss the player. The documented exits are the on-screen back button and the platform's own stop/close voice control.

### Verdict (2026-09-07, exhaustive research)

- "An open session changes stop routing": **TRUE and documented**, but bounded: only within ~30s of the last skill response, only on screen devices, only with the wake word prefixed. This fully explains "sometimes it works". We are NOT leaving a wrong session state on the video path: omitting `shouldEndSession` is what the docs mandate and already maximizes the window.
- Outside that window, NO session shape, directive, or interface choice we control makes stop/next/previous reach this skill during AudioPlayer or VideoApp playback. Platform limit, not our bug.
- Even when a stop DOES arrive during video, we cannot stop the video. "Not honored" is the platform's structural behavior, not a handler defect.
- UNTESTED device probe: whether "Alexa, chiudi" (close) during VideoApp playback dismisses the player natively (docs list "stop/close" as the VideoApp voice controls). If it works, that is the user-facing instruction to give.

### Plugin rules (DO NOT VIOLATE)

- `VideoApp.Launch` responses: never set `shouldEndSession` (keep `null`).
- Play / `AudioPlayer.Play` responses: `ShouldEndSession=true` (`BuildAudioPlayerResponse` sets it). `false` on plays kept an active session that made the Echo send `SessionEndedRequest` instead of routing stop (JF-299).
- ALL AudioPlayer event handlers (`PlaybackStarted`/`Finished`/`NearlyFinished`/`Stopped`/`Failed`): return only `AudioPlayer.Play` or a keep-alive ack, **never `shouldEndSession=false`**. Amazon rejects it with `InvalidResponse: "Response may not have shouldEndSession set to false"`, surfacing as "Qualcosa è andato storto" / "Something went wrong" on every playback. Use `BaseHandler.BuildKeepAliveResponse()` (`shouldEndSession=null`) or `BuildEndSessionResponse()` (`true`). Do not try to keep the session open on events to help stop routing; it is rejected and unnecessary (JF-299).
- `AMAZON.StopIntent`/`AMAZON.CancelIntent`: `AudioPlayerStop()` + `ShouldEndSession=true`. `AMAZON.PauseIntent`: same, plus optional position card. All paths send the `AudioPlayer.Stop` directive to guarantee audio stops. Never `ResponseBuilder.Empty()` for stop/cancel; it lacks the directive.
- Never copy session attributes onto session-ending responses (JF-387; see Key Gotchas).
- User-facing workaround for non-routable commands during playback: use **Pause** (always routes during AudioPlayer), or one-shot with the invocation name (`ask <invocation> to stop` / it-IT `chiedi a mia collezione ferma`, imperative, NOT the infinitive "fermare" which resolves to no intent). For video: the back button; possibly "chiudi" (untested).
- The it-IT `AMAZON.StopIntent` custom samples ("ferma", JF-402) help one-shot and in-session routing only; they cannot claim playback routing.

## CI

GitHub Actions runs the validation/build pipeline on **push to main**, PRs to main, and manual `workflow_dispatch`. Release builds run only on tag push (see [Release](#release)). The build-and-test job **installs ffmpeg** (apt) because ubuntu-latest runner images no longer ship it and `ResolveFfmpegPath` File.Exists-gates the mocked `/usr/bin/ffmpeg`: a test that depends on an ambient ffmpeg binary passes locally and 503s on CI (confirmed 2026-09-10). Prefer injecting a fake-script path (`WriteRecordingFakeFfmpeg`) over relying on the ambient one. Pipelines:
- `ci.yml` — PR-gated: **build-and-test** (Release build with `-warnaserror` + full test suite), **validate-models** (advisory), **validate-locales** (baseline-aware), **validate-versions**, **validate-build-yaml**
- `dev-build.yml` — manual-only (`workflow_dispatch`): downloadable dev DLL artifact zip
- `release-build.yml` — tag push only: build + test + zip + GitHub release + manifest update
- `pages.yml` — docs-site deploy (path-filtered to `docs-site/`)
- `codeql-analysis.yml` — security scan on PR/push to main + weekly schedule

## Project Layout

Plugin source lives under `Jellyfin.Plugin.AlexaSkill/` (the C# project root) — all `Alexa/`, `Configuration/`, and `Controller/` paths below are relative to it. Repo-root paths (`docs/`, `tests/`, `scripts/`, `Directory.Build.props`) have no prefix.

- `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/` - 61 intent handlers (one per intent, inherit `BaseHandler`)
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
- `Jellyfin.Plugin.AlexaSkill/Alexa/Util/DebouncedLibraryIndexService.cs`: shared lifecycle base for both index services (debounce + failed-load retry + dispose ordering; a third index derives from this, never copies)
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
- `IfMediaTypeDisabled()`: entry gate for single-media-type handlers (speaks `MediaTypeNotAvailable`); JF-467/JF-466 use it on the music-only handlers (PlaySong, PlayAlbum, FindSong, PlayMoodMusic, PlayArtistSongs, PlayByGenre). Placement contract on the method doc: AFTER the empty-slot prompt, BEFORE the first query and the "searching" announcement. The shared cross-media fallbacks (`TryEntityFallbackAsync`, `TryAlbumFallbackAsync`) gate on `IsMusicEnabled` instead (null means no confident match)
- `ApplyLibraryFilter()` / `FilterByContentAccess()` — per-user library and content type gating
- `BuildPauseResponse()` — `AudioPlayerStop()` + `ShouldEndSession=true` (ends session; Alexa routes resume via `AMAZON.ResumeIntent` automatically when audio was recently stopped)

New intents need: handler class + `IntentNames.cs` entry + interaction model samples + 17 locale response strings.

## Artist Search Fallback Chain

`PlayArtistSongsIntentHandler` has a 4-tier fallback (each is a separate DB query):
1. `SearchTerm` - Jellyfin search index
2. `NameStartsWith` first word - prefix with single word
3. `NameStartsWith` full query - prefix with full string
4. `NameContains` full query - substring match anywhere in name

All tiers go through `FuzzyMatch` (phonetic-aware via `FuzzyMatchPhonetic` in `BaseHandler`, which passes `ArtistIndexService`'s pre-computed Double Metaphone codes) to filter false positives and resolve ASR accent drift (e.g. "Koop" heard as "cup" on an it-IT Echo, both code "KP"). Results are served from the in-memory `ArtistIndexService` when available.

**Tier-1 length gate (JF-381, extended 2026-08-29):** the in-memory tier-1 Contains filter (`a.Name.Contains(musician)`) skips candidates whose name is more than 10 chars longer than the query. This prevents coincidental substring matches (e.g. "cup" in "Porcupine Tree") from short-circuiting before the phonetic/fuzzy tiers can find the intended accent-drift match. The phonetic `FuzzyMatchPhonetic` overload floors length-matched code-collision scores above `ContainmentScore` so they beat substring matches. The gate (shared predicate `ArtistSearch.PassesContainmentBand`, single definition) now applies to EVERY containment-shaped candidate source in BOTH search implementations: in-memory tier-1, the database-fallback SearchTerm tier-1, and the NameContains fallbacks. Prefix-shaped tiers (`NameStartsWith`) are deliberately NOT gated: a short query at the start of a long name is the intended ASR-truncation shape ("crash" -> "Crash Test Dummies"). Exception: the inline Fast-mode DB path (single SearchTerm query, no recovery tier) stays ungated, because gating there with no fallback tier turned direct long-name hits into not-founds during the cold-index window.

**Tier-2 partial first-word deferral (JF-417, 2026-08-30):** when a multi-word query's tier-2 prefix match produces a candidate that is essentially just the first word (candidate length <= firstWord+2, firstWord < 50% of query), the match is DEFERRED (not accepted) and tiers 3-4 run. The deferred match is accepted as fallback if tiers 3-4 don't find a different winner. Scope: in-memory tier-2 only in both implementations (ArtistSearch.SearchAsync + inline PlayArtistSongs Thorough mode). DB paths and Fast mode are NOT covered (cold-index trade-off, same as JF-381's Fast-mode exception).

**Containment-vs-full-name gate (JF-420 family, final shape 2026-09-01):** after the search chain returns a single containment match on a multi-word query, the handler gate (PlayArtistSongs, all tiers) compares the match against the best alternative. The comparison is SYMMETRIC: both sides scored by `FairComparisonScore` (score scaled by the length fraction in BOTH directions - the matcher's 0.5 floor is a recall device that manufactured phantom margins here). Alternatives are ranked by scoring ALL of them via `FuzzyMatcher.Score` (no early-exit: a containment-exempt 'Floyd' must not mask 'Pink Floyd'); the winner must also keep fair >= `AlternativeFullNameThreshold` (80), so exemption-only partial-word hits ('Miles' inside 'miles davis live') cannot win. Auto-select when the margin > 20 (live-verified: 'P!nk floyd' auto-plays Pink Floyd); otherwise the `DisambiguateMultipleArtists` prompt, which speaks the yes/no cycling flow the state machine supports ('Vuoi il primo? Di' no per il successivo', JF-420.2 - all 17 locales, no numbered list). Two skips: exact name equality bypasses the gate entirely (JF-420.1: 'Soul Coughing' with 'Soul Coughing & Roni Size' in the library auto-plays); a redundant shorter-form alternative ('Miles' vs 'Miles Davis') skips the comparison, guarded on the match being a word-subset of the query so superstring tier-1 matches (tribute bands) never skip. The ALBUM path handles this case independently. A blanket exclusion of the deferred candidate from tier-4 was attempted and REVERTED (it broke 'nirvana unplugged').

**Coincidental-containment downgrade (JF-377):** when a single tier-4 match is a coincidental substring containment (short common-word name inside a longer query, detected by `ArtistSearch.IsCoincidentalContainmentMatch`), the handler downgrades to a yes/no disambiguation prompt (`DisambiguationHelper.AskFirstMatch`) instead of auto-playing. Real artists still play via "yes"; nonsense resolves to not-found via "no". Bug and regression cases are string-indistinguishable (the JF-377 research), so the prompt is the only no-regression design.

**Duplicated search path (JF-382):** `PlayArtistSongsIntentHandler` still has its own inline 4-tier search (Fast/Thorough/Parallel mode selection), duplicating `ArtistSearch.SearchAsync`. The artist-SONGS query blocks have been consolidated into `BaseHandler.GetArtistSongsAsync` (shared by FindSong artist-scoped, PlaySong title fallback, and future callers), but the 4-tier SEARCH duplication remains. Do not add a third copy of the search; consolidate via JF-382.

## Cold-Start Warming Gates (JF-419 family)

After a restart (deploy or container restart), the in-memory indexes (artists, song n-grams) load in the background; a search that falls through to the cold database can exceed Alexa's ~8s window and surface as on-device INVALID_RESPONSE (live incident 2026-08-31 07:59). Two-layer protection:

- **Layer 1 (per-path entry guards)**: handlers whose request path would hit the cold DB call the shared `BaseHandler.GuardIndexReady` helper (which owns the rationale comment once and wraps `IndexWarmingGate.EnsureReady`) at entry, BEFORE the "searching" progressive response and AFTER the cancel-word escape hatch (an open Dialog.ElicitSlot flow must still cancel during warming). The gated-handler roster is NOT enumerated here: `WarmingGateCoverageTests` (test project) scans the plugin assembly IL and is the source of truth; a new gated handler must be added to its `ExpectedGatedHandlers` list or the suite fails. Per-path routing (which index a path gates): title-only FindSong/PlaySong gate the SONG index, musician-scoped paths gate the artist index, and AddToQueue/PlayNext gate both when a musician is named. A second entry gate interleaves with it: the JF-467 music gate sits BEFORE the warming gate where the handler geometry allows (FindSong, PlayMoodMusic, PlayArtistSongs) and AFTER it in PlaySong/PlayAlbum, where the warming gate must stay before the slot elicitation; the per-handler order is documented in each gate's comment, keep it when editing.
- **Layer 2 (choke points)**: `ArtistSearch.SearchAsync` and `SongNgramIndexService.Search/SearchPhonetic` re-check at entry, covering every caller including BaseHandler fallbacks and future handlers. A warming index THROWS `SkillWarmingUpException(indexName)`; the RequestPipeline translates it once into the session-ending `SkillWarmingUp` Tell (metrics/logging interceptors still run; `SkipColdLibraryWork` keeps DynamicEntities off the cold DB). Enrichment-only callers catch and degrade (MediaInfo skips artist info).

Both index services derive from `DebouncedLibraryIndexService` (Alexa/Util/): the ONE lifecycle owner (refresh lock, 5s debounce, failed-load retry, dispose ordering with volatile flag + in-lock re-checks, sticky readiness). After `MaxLoadAttempts` (10) consecutive failed loads the index DISABLES itself: gates treat a disabled index as absent (callers degrade to bounded DB paths, never an endless warming refusal); a later successful refresh re-enables. A third index service must derive from this base, not copy the lifecycle.

## Encode Gate & Cache Budget (JF-310/421/428)

- **Encode gate**: `MaxConcurrentFfmpegEncodes` (default 2) bounds concurrent ffmpeg processes across all video-audio endpoints (DoS bound). `UpdateEncodeGateCapacity` compares the CONFIGURED cap (`_encodeGateCapacity` under a lock), never `SemaphoreSlim.CurrentCount` (free slots): the old check rebuilt a fresh full semaphore whenever any encode was in flight, so the bound never bound (JF-421). In-flight holders finish on the old instance (drain-safe).
- **Pre-encode cache budget**: `StartFfmpegProcessGatedAsync` PINs the entry being written BEFORE the eviction sweep (JF-428: the creation-to-pin window let a concurrent sweep delete another request's HLS dir), reserves headroom scaled by content duration (`EstimateEncodeBytes`, 64MB/h with a 1h floor), and the eviction target floors at HALF the configured cap (an undersized config must never target zero and wipe everything). Pins are refcounted (a delayed exit-poll release cannot expose a retrying encode's re-pinned entry).

## Song Search Pipeline

`FindSongIntentHandler` uses a 3-stage search chain in `SearchAndRespondAsync()`:
1. **N-gram index** (`SongNgramIndexService.Search`) - O(1) bigram/single-token lookup, then `KeywordMatcher.Score` with 100% keyword coverage. Fast path.
2. **Phonetic index** (`SongNgramIndexService.SearchPhonetic`) - Double Metaphone phonetic code lookup, then `KeywordMatcher.ScorePhonetic` with 50% keyword coverage + 0.75 penalty. Cold path, only on exact-match miss. Protected by `PhoneticSongSearchEnabled` feature flag (default: true).
3. **DB fallback** - Jellyfin search API query. Slowest, last resort.

**Abbreviation canonicalization (JF-383):** `KeywordMatcher.Tokenize` canonicalizes common title-word abbreviations via `AbbreviationCanonicalForms` (st/saint->street, rd->road, ave->avenue, pt->part, vol->volume) on BOTH sides (title index and spoken keywords), bidirectionally. `number/no` is deliberately excluded ("no" is a real word in en/it, a Japanese particle, and a Portuguese stop word). LOAD-BEARING INVARIANT: no canonical output may be a stop word in any locale.

**Cross-locale English stop words (JF-384):** `KeywordMatcher.Tokenize` always strips the English stop-word set (`EnglishStopWords`) in addition to the locale-specific set, because English titles spoken under non-English locales carry English function words ("the") that would otherwise veto keyword coverage. The n-gram index is built with en-US, so this also repairs a pre-existing index/query asymmetry.

**Phonetic second stage (JF-384):** `KeywordMatcher.ScoreWithPhoneticFallback(songs, tokens, locale, phoneticEnabled)` runs exact `Score` first, then on miss runs `ScorePhonetic` on the same bounded candidate set. Used by FindSong artist-scoped and PlaySong title fallback paths.

**Residual keyword tiebreak (JF-388):** `KeywordMatcher.ScorePhonetic` adds a ranking bonus from the fuzzy closeness of NON-matching keyword/title-token pairs (`ResidualKeywordTiebreak`, cap 10.0). Separates candidates tied on phonetic coverage ("Decatur St." vs "St. Gregory" for "the cater street"). Ranking-only: never an admission gate (the reverted JF-337 lesson); a 100%-coverage candidate always gets residual=0.

**Disambiguation name dedup (JF-416, 2026-08-31):** the 1-4-match disambiguation list deduplicates by song NAME (case-insensitive): `scored.Take(8).GroupBy(name).First().Take(4)` keeps the highest-scoring representative per unique name (the input is always sorted descending from KeywordMatcher). Same song from different albums appears once. The `FindSongFoundMultiple` locale string already carries the candidate list via its `{1}` format arg; do NOT append the list again (the old code double-spoke it).

**Artist-scoped NameContains retry (JF-383):** when the artist-scoped `NameContains` pre-filter returns 0 candidates (spoken full word vs abbreviated tagged title), FindSong retries with ArtistIds only (Limit 500) and lets KeywordMatcher decide.

**PlaySong title fallback (JF-383/JF-384):** on exact SearchTerm miss, PlaySong falls back to (a) the artist's songs scored by `ScoreWithPhoneticFallback` (when a musician slot is present, bounded Limit 500), or (b) the n-gram index with phonetic fallback (when no musician, O(1)). `ISongNgramIndex` is an optional ctor param.

The n-gram index is a background hosted service (`SongNgramIndexService`) that loads all `Audio` items at startup, builds bigram/single-token/phonetic dictionaries, and refreshes on library changes (debounced 5s). Pre-computed phonetic codes make phonetic lookup O(1); only the user's 2-3 keywords need encoding at query time.

## Cross-Media-Type Fallback

When a handler's primary search finds no results (e.g., PlaySongIntent finds no song), `BaseHandler.BuildArtistSongsResponseAsync` falls back to artist search. This is shared across PlaySong, PlayAlbum, and PlayVideo handlers via `BaseHandler`.

`BaseHandler.TryEntityFallbackAsync` extends this to **greedy free-text slots that misroute an artist query** (PlayMoodMusic's `mood` slot captures "di miles davis"; FindSong's `titleKeywords` when no artist was given; PlayByGenre's `genre` slot captures a bare title in the es locales, JF-463). On a confirmed miss it strips locale stop-words (`KeywordMatcher.Tokenize` — covers all 11 language prefixes of the 17 locales since JF-389), runs the phonetic `ArtistSearch`, gates on `Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaArtistThreshold)` **and** the word-count guard `CrossMediaArtistMaxWords` (=2, shared in BaseHandler — a >2-word slot is a poor artist query), then plays via `BuildArtistSongsResponseAsync` with a `FoundArtistInstead` announcement. Returns null when no confident match so the caller falls through to its own not-found. Known tradeoff: a real single-word mood that coincidentally matches an artist name (>=85) substitutes it — announced, so the user knows. Do NOT remove the word-count guard (a PlaySong lesson: long queries match wrong short artists).

## Cross-Media Artist Suggestion (JF-363)

When PlaySong/PlayAlbum finds no exact match but the cross-media artist fallback scores a plausible artist in the **[normalThreshold, 85) band** (the gap that previously failed silently — e.g. a mispronounced name scores 63), the behavior is now configurable via `CrossMediaArtistSuggestion` (Off/Confirm/AutoServe, default **Confirm**):
- **Confirm**: offer the single best artist for yes/no via `BuildCrossMediaArtistOfferAsk` (sets `disambig_type=artist` + `crossmedia_notfound_query`/`crossmedia_notfound_type` session attrs). "Yes" routes through `YesIntentHandler.PlayArtist`; "No" returns the clean song/album not-found (via the `crossmedia_notfound_*` attrs in `NoIntentHandler`, NOT the generic "no more matches").
- **AutoServe** (opt-in): play the artist directly with `FoundArtistInstead`.
- **Off**: today's clean not-found.

Scores >= 85 always auto-play (unchanged); < normalThreshold (60) always not-found (unchanged). Single candidate only. Both PlaySong and PlayAlbum apply the word-count guard (`CrossMediaArtistMaxWords=2`). Per-user override (`User.CrossMediaArtistSuggestion`, nullable) → global default (`PluginConfiguration.DefaultCrossMediaArtistSuggestion`).

## Romance Phonetic Synonyms (JF-362)

`PhoneticSynonymGenerator` generates Italian/German/Spanish/French/Portuguese phonetic synonym variants for English artist/album names so Alexa's ASR recognizes them when spoken by non-English speakers. Shared tail rules live in `PhoneticSynonymGenerator.ApplyRomanceTailRules` (called from each generator's `TransformWord`):
- **`-ing` → `-in`**: Italian/Spanish/French/Portuguese L1 speakers all lack the velar nasal `/ŋ/`, so they realize English "-ing" as `/in/`. German and Dutch are Germanic and **DO have `/ŋ/`** (Ding, singen, zingen) — they deliberately do NOT call this helper.
- **`soul` → `sol`** (override map) + consonant-doubler (`GetRomanceConsonantVariants`): ASR's Italian-locale model doubles single intervocalic consonants (the "coffin" loanword mapping). The doubler emits `Cofin` → `Coffin` variants for coverage.
- Per-name cap raised 3 → 5 to fit the coverage variants. Device-captured forms are ordered first to survive the cap.

The goal is COVERAGE (emit enough plausible variants that one matches), not precision — extra near-miss synonyms are harmless to entity resolution.

## Catalog Sync (JF-335)

`LibrarySyncService.SyncUserLibraryAsync` uploads the user's library to SMAPI catalog slot types (`JellyfinArtist`, `AlbumName`) with locale-specific phonetic synonyms. Configured by `PluginConfiguration.CatalogSyncLocales` (string): the CODE DEFAULT is `*` (all active locales EXCEPT ar-SA, whose Amazon-side full build rejects catalog-wired models: JF-543, live-bisection evidence in CatalogManager.CatalogWiringUnsupportedLocales; verified 2026-09-12 - the older "empty = it-IT only (default)" note described the empty-string behavior, not the default); `"de-DE,en-US"` = it-IT + listed. `CatalogManager.UploadCatalogValuesAsync` creates a catalog version by providing a hosted URL; SMAPI fetches it once (the plugin's `CatalogController` serves the payload from a 10-min-TTL single-fetch cache).

**Catalog 503 retry**: `UploadCatalogValuesAsync` retries the version build on transient `GATEWAY_ERROR`/503/502/504 (when SMAPI couldn't fetch the source URL, e.g. the reverse proxy was still warming up after a Jellyfin restart) with a fresh source URL per attempt (the old URL is consumed on fetch). Non-transient failures (real validation errors) throw immediately. This makes the startup catalog-sync race self-healing.

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

17 locale files, ALL generated from YAML templates (JF-316 complete, 2026-09-11): one `Alexa/InteractionModel/templates/<locale>.yaml` per locale. Do NOT hand-edit any `model_*.json`; the JSONs are build output.

**Key vocabulary** (it-IT template, expanded via Cartesian product):
- `imperative`: [Riproduci, Suona, Metti, Pleia, Ascolta]
- `infinitive`: [Di riprodurre, Di suonare, Di mettere, Di pleiare, Di ascoltare]
- `artist_carrier`: [la band, il gruppo, il cantante, la cantante] — disambiguates artist from radio/genre
- `song_noun`: [il brano, la canzone, il pezzo, la traccia]
- `media_noun`: [brani, canzoni, musica, un brano, una canzone, un pezzo, una traccia]

**Model generator** (ALL 17 locales): `python3 scripts/generate_interaction_model.py <locale>`
- Templates: `Alexa/InteractionModel/templates/<locale>.yaml` (one per locale; it-IT and en-US use `vocabulary` Cartesian products, the other 15 transcribe their samples verbatim, each template header documenting that locale's divergences and key-order contracts)
- Output: `Alexa/InteractionModel/model_<locale>.json`
- Adding or changing an intent, slot, sample, or slot-type value is a YAML edit + regenerate per affected locale, in ALL 17 locales for cross-locale changes; NEVER hand-edit the JSON. Enforcement: the generator's template guards (unknown key, typo'd `{ref}`, dict type-value keys, dual-form check) fail the build on authoring mistakes, and `scripts/validate_interaction_models.py` Phase 5 auto-discovers every template, rebuilds the model in memory with the generator's own serializer, and warns on any committed JSON that drifted from its template (plus the JellyfinArtist en-family seed-equality warning on the same walk).
- Mood words live IN the templates everywhere now (the former `scripts/generate_mood_slot.py` and its `LOCALE_MOODS` table are deleted): to add or change a mood in any locale, edit that locale's `Mood` type in its template and regenerate. Every value AND synonym must resolve via `LocalizedMoodMap`/`MoodGenreMap` (see Mood feature architecture below).

**Mood feature architecture (JF-354/355/356):**
- The `mood` slot is a **custom `Mood` type** in ALL 17 locales (NOT `AMAZON.SearchQuery` — that caused the "music by X" misroute; see anti-pattern #3). Custom values restrict matching to mood words so artist queries route to PlayArtistSongs.
- The handler reads `moodSlot.Value` (raw spoken text, NOT entity-resolved canonical), so **every slot value AND synonym must independently exist in `LocalizedMoodMap`** (or be an English `MoodGenreMap` key) to resolve to genres.
- `MoodGenreMap` (English mood→genres, incl. Spotify-aligned `sleep`) + `LocalizedMoodMap` (localized word→English key) live in `PlayMoodMusicIntentHandler.cs`.
- Admin overrides: `PluginConfiguration.MoodGenreOverrides` (`Collection<MoodGenreOverride>`) merges into `ResolveGenres` at resolve time; on "Rebuild models" the words are injected into the Mood slot type via `SkillInteractionModel.InjectMoodSlotValues`. XmlSerializer-safe (Collection, not Dictionary).

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
- **Stop/Pause/session routing during playback**: ALL of it (AudioPlayer vs VideoApp routing, the 30-second session window, JF-299 event restrictions, stop/cancel/pause response shapes, workarounds) lives in the "Stop / Session Routing During Playback (THE REFERENCE)" section near the top of this file. Read that before touching `shouldEndSession` on any play/stop/event path.
- **Session attributes must NOT ride on session-ending responses (JF-387)**: `SessionAttributesInterceptor` skips copying when `ShouldEndSession == true`. Copying dead session data onto a terminal play response made the interactive-session play differ from the byte-equivalent one-shot play, and "alexa stop" was misrouted after interactive-session playback. Attributes are preserved only on multi-turn (open-session) responses.
- **Response interceptors run in REVERSE registration order**: `RequestPipeline.ExecuteAsync` iterates `_responseInterceptors` from `Count-1` down to 0. `ResponseBodyLoggingInterceptor` (registered last) runs FIRST and its snapshot does NOT include mutations by later interceptors (`DynamicEntitiesInterceptor`, `SessionAttributesInterceptor`). Trust Amazon's error messages over the logged body for post-logging mutations.
- **Dialog.UpdateDynamicEntities cannot coexist with other Dialog.* directives**: `DynamicEntitiesInterceptor` skips injection when the response already carries `Dialog.ElicitSlot`/`ConfirmSlot`/`Delegate`. Amazon rejects the combination with `INVALID_RESPONSE: "No other directives are allowed to be specified with a Dialog directive"`. Live incident: skill open into FindSong failed audibly on every entry (2026-08-21).
- **FindSong session routing is IntentRequest-only**: `AlexaSkillController` routes to `FindSongIntentHandler` when `FindSongSessionData` is in session attributes, but ONLY for `IntentRequest`. A `SessionEndedRequest` arriving with FindSong attributes must fall through to `SessionEndedRequestHandler`; routing it to FindSongIntentHandler crashes with `InvalidCastException` (live incident 2026-08-21, ErrorRef f1ff87c1).
- **Resume item resolution**: Prefer `context.AudioPlayer.Token` over `session.FullNowPlayingItem`. Jellyfin's `PlaybackStopped` event clears `FullNowPlayingItem` before the resume request arrives, but `AudioPlayer.Token` survives.
- **NLU competition**: Ambiguous utterances between intents need concrete (non-slotted) samples to disambiguate.
- **SMAPI rate limits**: Space NLU tests with `SMAPI_DELAY=1.5`.
- **ValueTuple serialization**: Never store `ValueTuple` in session attributes — Newtonsoft.Json serializes as Item1/Item2. Use named DTOs.
- **Config.Users in API responses**: Never send `config.Users` via `updatePluginConfiguration` — it can wipe skill config entries. Use dedicated endpoints.
- **[JsonIgnore] token fields are not API-readable**: `JellyfinToken` and `SmapiDeviceToken` are `[JsonIgnore]` (`Entities/User.cs`) — they persist via XmlSerializer to the on-disk XML, NOT the JSON config API. The config JSON API always shows them as null/EMPTY. **Do NOT treat an EMPTY JSON read as data loss** or a deploy casualty. Read the on-disk XML (`/config/data/plugins/configurations/Jellyfin.Plugin.AlexaSkill.xml`) to verify they persisted.
- **AudioPlayer event restrictions (JF-299)**: fully covered in the "Stop / Session Routing During Playback (THE REFERENCE)" section near the top of this file (event responses: only `AudioPlayer.Play` or keep-alive ack, `shouldEndSession` never `false`).
- **Invocation name (JF-297/JF-300)**: An empty `UserSkill.InvocationName` means "use locale defaults" (`Config.LocaleInvocationNames` → it-IT "mia collezione"; other locales → `Config.InvocationName` "jellyfin player"). A non-empty custom name applies to ALL 17 locales incl. it-IT. `LocaleInvocationNames` is default-only (NOT an unconditional override). Changing the name in settings triggers a redeploy to Amazon via `IInteractionModelRedeployer` (build + `UpdateSkillAsync` + poll, ~15–30s, longer if the SMAPI access token needs refresh) — no Alexa-console edit or re-auth needed. A one-time migration in the `Plugin` ctor clears legacy stored defaults so existing users keep locale defaults.
- **profile-nlu vs on-device divergence**: `ask smapi profile-nlu` (Utterance Profiler) tests intent/slot routing against the saved model in isolation; a real Echo adds ASR + competition from other installed skills, so routing can differ. `AMAZON.MusicRecording`/`Musician` slots capture the spoken text regardless of catalog match (PlaySong works for non-catalog titles). Trust profile-nlu for model routing; verify behavior on-device or via the plugin Simulator endpoint. (JF-298)
- **Entity resolution for slot synonyms**: `slot.Value` always contains the raw spoken text (e.g. "gli album"). To get the canonical value ("album"), extract from `slot.Resolution.Authorities[0].Values[0].Value.Name` when `Status.Code == "ER_SUCCESS_MATCH"`. See `BrowseLibraryIntentHandler.GetCanonicalSlotValue()` for the pattern.
- **Dialog.ElicitSlot requires model registration**: Any intent that uses `Dialog.ElicitSlot` directives MUST be listed in the interaction model's `dialog.intents` array. Without this registration, Alexa **silently ignores** the directive — the session stays open (`ShouldEndSession=false`) but the user's follow-up goes through general NLU, which routes music queries to Amazon Music instead of back to the skill. Set `elicitationRequired: false` on slots when controlling dialog manually from code. This must be done in ALL 17 locales: add to the locale's YAML template's `dialog` section and regenerate.
- **Slot values ≤ 140 chars (Alexa hard limit)**: Alexa rejects slot values and synonyms longer than 140 characters with `InvalidResponse`, crashing **every** skill request (e.g. libraries with long artist fields like musical cast lists). Any code building catalog/dynamic-entity slot values MUST cap length via `SlotValueHelper.Truncate` (applied in `CatalogPayload` and `DynamicEntityBuilder`).
- **After a DLL hot-swap, verify the ACTIVE dll**: Jellyfin migrates the plugin to a versioned dir (`AlexaSkill_<Version>`) when the AssemblyVersion changes and may install the catalog release there, displacing your hot-swapped dev DLL. Always deploy into the CURRENT versioned dir (`ls /config/data/plugins/ | grep AlexaSkill`) and verify the **running** DLL (`podman cp` it out → compare size + `strings | grep` for a unique identifier), not just the file you pushed.
- **Signed stream tokens (JF-309)**: All 4 video-audio endpoints (`VideoAudioController`) require a signed, item-scoped HMAC token (`?token=`) minted by `StreamTokenHelper` using `PluginConfiguration.StreamTokenSecret` (auto-generated). A bare item GUID returns 401. Tokens are 10h TTL, item-scoped (not user-scoped). The token flows: skill mints it in the playlist URL → controller reads it from the query string → `WriteAudiobookPlaylist` embeds it in segment lines / `RewritePlaylistWithToken` post-processes ffmpeg-written playlists → `GetSegment` validates it. Single-chapter audiobooks re-mint a chapter-scoped token via `StreamHlsVideoAudioCore(chapterId, overrideToken)` because segments are keyed by chapterId, not parentId.
- **MediaTypes vs IncludeItemTypes (JF-358)**: `MediaTypes=Audio` does NOT filter `ArtistIds` queries on Jellyfin 10.11.11, it returns the entire audio library for every artist, causing sort-over-thousands NREs + 8-12s retry loops. Always use `IncludeItemTypes=BaseItemKind.Audio` for `ArtistIds`-filtered queries.
- **Jellyfin has no podcast type (JF-373)**: Jellyfin 10.11.x has NO `Podcast`/`AudioPodcast` item type. A `Series` is ALWAYS `MediaType=Unknown` (a container rollup, never `Audio`), so any `IncludeItemTypes=Series` + `MediaTypes=Audio` query returns zero results unconditionally. Podcasts are stored as a **MusicAlbum of Audio tracks** in a Music library (confirmed: all 3 community podcast plugins store episodes this way). `PlayPodcastIntentHandler` queries `MusicAlbum` by name, then plays the newest `Audio` child (`ParentId=album.Id`, `OrderBy DateCreated Desc`). Do NOT re-introduce the Series/MediaTypes=Audio query.
- **AnnounceAudioPlays (JF-353 ext)**: The now-playing announce on MUSIC plays is a separate opt-in flag (`PluginConfiguration.AnnounceAudioPlays`, default `false`) — NOT the same as `DefaultAnnounceNowPlaying` (which gates video/book launches only, default `true`). `AttachAnnounceIfEnabled` in `BaseHandler` reads the audio flag via `GetAnnounceAudioPlays(user)` (per-user override → global default). Both builders (`BuildAudioPlayerResponse` + `BuildVideoAppAudioResponse`) call it.
- **PlayBook disambiguation routing (JF-361)**: `PlayBookIntentHandler` uses `DisambiguationHelper.MediaTypeAlbum` for audiobook disambiguation. When `YesIntentHandler` confirms, it must check `item is AudioBook` and route to `PlayBook()` (audiobook HLS path), NOT `PlayAlbum()` (which would say "Non ci sono canzoni nell'album"). Single-file AudioBooks (no child chapters) are treated as their own track.
- **config.html is an embedded resource; clean-build or edits don't ship**: The plugin config page is served from `config.html` compiled INTO the DLL (`<EmbeddedResource>`), NOT from the `config.html` file extracted on disk under `/config/data/plugins/AlexaSkill_<ver>/`. `dotnet build` does NOT always re-embed an updated config.html if the output DLL already exists. After editing config.html: (1) delete the output DLL before building (`rm Jellyfin.Plugin.AlexaSkill/bin/Release/net9.0/Jellyfin.Plugin.AlexaSkill.dll && dotnet build -c Release`); (2) verify the new code embedded with `strings <dll> | grep -c "<unique-new-string>"`; (3) after deploy+restart, verify the SERVED page — `curl -s 'http://localhost:8096/web/configurationpage?name=AlexaSkill&_t=<rand>' | grep -c "<unique-new-string>"` — not just the active-DLL byte-compare (active==local is useless if local itself was stale). The `_t=` cache-buster defeats HTTP caching.
- **config.html `<style>` block unreliable under emby view injection (JF-365)**: On the live admin page, element-scoped rules in config.html's `<style>` block (e.g. `.userSkillsTable tr.user-details-row { display:none }`, grid layouts, transforms) are often NOT applied, though they work in a standalone render. Apply layout-critical CSS as INLINE `style="..."` (set via `el.style.x = ...` in JS), not the `<style>` block. Unscoped global rules (`.pill`, `.fieldDescription`) are fine.
- **emby-input/emby-select component contract**: `createdCallback` (jellyfin-web 10.11.11) uses the presence of the `emby-input`/`emby-select` class as its init guard. If you pre-add that class in JS or static HTML, `createdCallback` returns early without setting `this.labelElement`, and `attachedCallback` throws `TypeError: this.labelElement is undefined` — which aborts the whole webcomponents upgrade pass. Set the `label` ATTRIBUTE on the control and let emby add the class itself. Don't pre-add it.

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

**Detection**: After regenerating a model from its template, search the JSON output for samples without `{`:
```bash
grep -rn '"[A-Z][a-z].*"' model_*.json | grep -v '{' | grep samples
```

**Boundary (JF-403 audit)**: the rule applies to intents whose slots are REQUIRED for the handler to act. Static samples on OPTIONAL-slot intents are correct Alexa design and are NOT violations: the static variant must deliver a working default (handler prompts or falls back). Verified optional-slot intents carrying static samples (do not "clean these up"): FindSongIntent (conversation openers), MediaInfoIntent ("what's playing"), PlayFavorites/PlayLastAdded/PlayRandom/Recommend (media_type defaults), GoToChapter ("next/previous chapter"), SkipForwardBack ("skip forward" = default 30s).

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

**Concrete instance (2026-07):** an intent with a greedy `AMAZON.SearchQuery` slot + a generic carrier — PlayMoodMusic's `"musica {mood}"` / `"play {mood} music"` — captured "music by X" and routed it to the mood intent (`mood="di miles davis"`) instead of PlayArtistSongs, in ALL 17 locales. **FIXED in JF-354/356:** the `mood` slot is now a custom `Mood` slot type (populated with locale mood vocabulary) in all 17 locales, so non-mood phrases no longer match it. The fix IS at the model layer — do NOT revert `mood` to `AMAZON.SearchQuery` (that reintroduces the misroute in every locale). To add/change Mood values: edit the locale's `Mood` type in its YAML template and regenerate (`python3 scripts/generate_interaction_model.py <locale>`), in every locale (all 17 are templated; the old `generate_mood_slot.py` is deleted). `TryEntityFallbackAsync` (see Cross-Media-Type Fallback) remains as the handler-side recovery for any residual mood-miss. When you see "X found nothing but a sibling entity query works," check whether a greedy SearchQuery slot on *some other* intent stole it before assuming a search bug.

### 4. Cross-Locale Drift (8+ incidents)

**Always add new intents/slots to ALL 17 locales simultaneously.** Every locale is templated: edit `templates/<locale>.yaml` and regenerate (`python3 scripts/generate_interaction_model.py <locale>`) per locale; never edit the JSON.

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

**Known deliberate exception (JF-402)**: `AMAZON.StopIntent` in it-IT carries 6 custom samples ("ferma", "ferma tutto", ...) from the YAML template (May 2026 era), so the Italian imperative "ferma" routes one-shot. No incident documented against it. Do not "fix" it blindly: removal requires first verifying on-device that "ferma" still routes without the samples.

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

### 10. Replacing Catalog-Backed Custom Slot Types With AMAZON Built-Ins (1 incident — reverted 2026-07-12)

**NEVER replace a catalog-backed custom slot type (`AlbumName`, `SeriesName`, `AudiobookTitle`, `JellyfinArtist`) with an AMAZON built-in (`AMAZON.MusicRecording`, `AMAZON.Album`, etc.) to "fix" one-shot routing for arbitrary library items.** These custom types are a deliberate architecture (JF-96.2): they are populated from the user's Jellyfin library by `CatalogSyncTask` (a weekly `IScheduledTask`) with **Italian phonetic synonyms for English names**, which is exactly how the skill stays robust when an Italian user speaks English/foreign titles. Built-in AMAZON types are English-biased and discard that phonetic matching — the cross-language limitation this project explicitly moved away from (see commit `7de7e24`, "Fix AMAZON.Series/MusicGroup slot types").

```
# ❌ WRONG — fixes "jazz cafe" one-shot but abandons the catalog/phonetic architecture
PlayAlbumIntent.album type: AlbumName  →  AMAZON.MusicRecording
#    - loses Italian phonetic synonyms for English album names
#    - blocks the catalog-sync path (CatalogSyncTypeNames writes to AlbumName, now unused)
#    - FACT CORRECTION 2026-08-29: the old "inconsistent with the other 16 locales"
#      bullet was INVERTED - only it-IT declares AlbumName; the other 16 locales have
#      used AMAZON.MusicRecording all along. The it-IT revert stands on the
#      phonetic-synonym merits alone. Consequence (JF-332 extension): the static
#      album catalog upload reaches a declared type ONLY in it-IT; in the other 16
#      locales it is inert. Do NOT "restore" a uniform-AlbumName assumption.

# ✅ RIGHT — fix the catalog population, keep the architecture
#    Investigate why CatalogSyncTask isn't filling AlbumName with the user's real
#    albums (+ phonetic synonyms). One-shot routing for arbitrary items comes from
#    the catalog populating the custom type, not from a built-in free-text type.
```

**Detection / memory hooks**:
- If one-shot routing works for famous albums (in the static seed: Thriller, Abbey Road, …) but fails for arbitrary user-library albums, the custom slot type is under-populated → fix catalog sync, do NOT swap the type.
- `CatalogSlotTypes.Names` (dynamic-entity runtime target, turn 2+) vs `CatalogSlotTypeNames` (static model slot type) must agree per entity. Album currently mismatches (`AMAZON.Album` vs `AlbumName`) — tracked in JF-332.
- `AMAZON.MusicRecording` (used by PlaySongIntent) is free-text and captures any title regardless of catalog — that is why song routing works one-shot and looks tempting to copy. Don't.

**Verified 2026-07-12**: changing `PlayAlbumIntent.album` from `AlbumName` to `AMAZON.MusicRecording` did make "jazz cafe" route one-shot (profile-nlu confirmed), but it was the wrong direction — reverted. The `album_noun` article fix (adding `"l'album"`, `"il disco"` forms to the it-IT vocabulary) is correct and kept; it fixes routing for in-catalog albums on all article forms.

### 11. Bare Album Carriers on Free-Text Album Slots (PR #15 + JF-459, all 16 free-text locales)

**NEVER add a PlayAlbumIntent sample whose carrier does not name the media** (`play {album}`, `Spiele {album}`, `Lis {album}`, `{album} を再生して`, `stream {album}`) in a locale whose `album` slot is free-text. That is every locale EXCEPT it-IT (it-IT's slot is the catalog-backed `AlbumName`, a different architecture where the catalog, not the carrier, constrains matching). A bare carrier makes PlayAlbumIntent greedily compete with PlaySongIntent for every "verb + title" utterance, turning album-vs-song routing into a per-locale coin flip. PR #15 (commit 135de9c8) trimmed the 5 English models; JF-459 (2026-09-03) trimmed the other 11 free-text locales. Recall does NOT depend on the bare carriers: the song-to-album cascade (JF-345, `TryAlbumFallbackAsync`) recovers the album AFTER a confirmed song miss. Indefinite forms that name the media (`ein Album von {musician}`) are fine.

```
# WRONG (all removed): "play {album}" / "Spiele {album}" / "stream {album}" / "{album} を再生して"
# RIGHT: every carrier names the media noun:
#   en: play the album {album}, play album {album}, play the record {album}
#   de: Spiele das Album {album}, Spiele die Platte {album}
#   es: Reproduce el álbum {album}, Pon el disco {album}
#   fr: Lis l'album {album}, Mets le disque {album}
#   it-IT: every sample is built through the album_noun placeholder (YAML template)
```

**Detection** (strip placeholders first: `{album}` itself contains "album", so a naive grep always passes):

```bash
python3 - <<'PY'
import json, glob, re
NOUNS = {'en': ['lbum', 'record'], 'de': ['lbum', 'Platte'], 'es': ['lbum', 'disco'],
         'fr': ['lbum', 'disque'], 'pt': ['lbum'], 'nl': ['lbum'], 'ar': ['لبوم'],
         'ja': ['アルバム'], 'hi': ['एल्बम']}
for f in sorted(glob.glob('Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json')):
    loc = f[-10:-5]
    if loc == 'it-IT':
        continue  # catalog-backed AlbumName slot, different architecture
    nouns = NOUNS[loc[:2]]
    d = json.load(open(f)); m = d.get('interactionModel', d)
    for i in m['languageModel']['intents']:
        if i['name'] == 'PlayAlbumIntent':
            for s in i.get('samples', []):
                carrier = re.sub(r'\{[^}]*\}', '', s)
                if '{album}' in s and not any(n in carrier for n in nouns):
                    print(f'{loc}: BARE CARRIER: {s}')
PY
```

The same detection runs as warning check #10 in `scripts/validate_interaction_models.py` (JF-460), with the noun table pinned to this section as the source of truth. It is warning-level BY DESIGN: the CI validate-models job is advisory and a false positive must not break it. If that job is ever promoted to blocking, revisit the check's severity in the same change.

**When PlayAlbumIntent samples change in ANY locale, update the mirrors or they go stale** (they went stale twice: PR #15 orphaned the 5 English rows, JF-459 initially orphaned 11 more): `VOICE_COMMANDS.md` (hand-maintained utterance tables), `docs/playback-lifecycle-<locale>.md` (the `Idle -->|"<sample>"| PlayAlbum` edge label), `docs/graphs.json` + `docs-site/graphs.json` (identical mirrors; after ANY docs md change, re-run `python3 docs-site/parse_mermaid.py` from the repo root and copy the output to both paths. The JF-459 rule "do NOT re-run, hand-edit the labels" was correct for its time, and JF-462 explains why it flipped: the regen's nulled edge targets came from a stray `]` typo in all 17 library-browsing mds (mermaid-invalid since ec417c59, so GitHub rendering was broken too), not from a parser regression; with the typo fixed, a regen is a byte-identical no-op for every in-sync diagram and prints a WARNING for each md edge endpoint it would drop (true curly-brace refs are exempt; a curly brace inside a square-bracket label is not, so the typo class itself warns), so prefer re-running over hand-editing), `docs-site/data.json` (embedded mermaid strings; no generator script exists, so update them from the md sources in the same change: they lagged JF-303's FindSong flow for two months before JF-462 synced them), and `tests/integration/fixtures/<locale>.yaml` (an NLU expectation referencing a removed sample goes stale silently: profile-nlu still routes via other samples, so only the live suite fails, never the dry-run).

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
