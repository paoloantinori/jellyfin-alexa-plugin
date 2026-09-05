---
id: JF-324
title: 'Feature: Next/latest-episode playback and TV binge auto-advance'
status: In Progress
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-09-05 10:28'
labels:
  - feature
  - tv
  - video
milestone: m-9
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayNextEpisodeIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Event/PlaybackNearlyFinishedEventHandler.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Video/TV is the shallowest surface (functional review 2026-07-12). Today `PlayEpisodeIntentHandler` REQUIRES an explicit season AND episode number (else it responds "didn't catch the episode number"); there is no way to say "play the next / latest episode of {series}", and there is no video PostPlay — `PlaybackNearlyFinishedEventHandler`/`PlaybackFinishedEventHandler` enqueue only music radio tracks, so a finished episode just stops. This is the highest-value gap a TV user hits in the first week.

Deliver two capabilities using Jellyfin's NextUp API:
1. New utterances/intent: "play the next episode of {series}", "play the latest episode of {series}", "continue watching {series}" resolving to the correct next unwatched episode via NextUp (respect per-user watched state and library/content gating).
2. Auto-advance: when an episode finishes and the user is bingeing a series, enqueue the next episode via the VideoApp launch path — mirroring the existing music AutoPlay mechanism in PlaybackNearlyFinished, but for video. Within-session auto-advance is reliable; cross-session continuation relies on relaunch/resume (note the platform limits).

Respect platform constraints already documented in CLAUDE.md: VideoApp.Launch for video, AudioPlayer-event handlers must never set shouldEndSession=false, Next/Stop during playback may be claimed by the default service. Add samples to all 17 locales (it-IT via YAML template) and locale response strings, plus unit + NLU tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 User can say 'play the next episode of {series}' and get the correct next unwatched episode via Jellyfin NextUp
- [ ] #2 User can say 'play the latest episode of {series}' and get the most recent episode
- [ ] #3 PlayEpisode still supports explicit season+episode, and no longer hard-fails when only a series is given (falls back to next-up)
- [ ] #4 When a video episode finishes during a session, the next episode auto-advances via VideoApp without a manual command
- [ ] #5 Per-user watched state and library/content-access gating are respected in episode selection
- [ ] #6 Samples added to all 17 locales (it-IT via YAML template) with locale response strings
- [ ] #7 Unit and NLU tests cover next/latest-episode routing and the auto-advance path
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
### PART 1 (2026-09-05): next/latest-episode resolution + PlayEpisode series-only fallback (AC#1, #2, #3, #5)

DONE, verified: `dotnet build` Debug + Release 0 errors 0 warnings; full `dotnet test` green at 3257 passed (baseline 3243, +14 new tests); `validate_interaction_models.py` PASS at the 90-warning baseline (no new warnings); `validate_locales.py` PASS no new gaps; NLU dry-run 8 passed / 904 skipped (fixture syntax only; the new routing expectations need a deployed model before the live profile-nlu suite can confirm them).

**Design choices:**

- **NextUp surface**: in-process `MediaBrowser.Controller.TV.ITVSeriesManager.GetNextUp(NextUpQuery, DtoOptions)` (Jellyfin 10.11.8 SDK, verified against the package + the TVSeriesManager source semantics). Injected into the handlers via ctor DI; handlers resolve from the same host provider that serves Jellyfin.Api's ShowsController, so no new registration is needed.
- **One new intent, three phrasings**: `PlayNextEpisodeIntent` (slot `series_name: SeriesName`, the type PlayEpisodeIntent already uses, consistent in all 17 locales). "next episode", "latest episode", and "continue watching {series}" all route here. All samples are slotted (anti-pattern #1) and carry episode nouns or continuation verbs (no bare carriers, anti-pattern #3/#11).
- **Watched state**: NextUp query is scoped per-user with `EnableResumable=true`, so an episode stopped mid-way counts as the next one (that is what "next" and "continue watching" mean to a viewer mid-episode); the per-user unwatched filtering itself is Jellyfin's TVSeriesManager logic. A resumable next-up episode announces "Resuming X from Y" via the existing `BuildVideoLaunchSpeech` (VideoApp cannot honor the offset; the position is spoken only).
- **Latest fallback when NextUp is empty** (the choice the task asked to be stated): serve the most recently CREATED episode of the series (InternalItemsQuery ordered by `DateCreated` desc, `AncestorIds=[series]`, `IsVirtualItem=false`; the same ordering PlayPodcastIntentHandler uses for "newest episode") with the `PlayingLatestEpisode` announce. So "play the latest episode of X" plays the literal newest episode when fully caught up, and "play the next episode" when everything is watched degrades to the finale (announced) instead of a refusal. Only when the series has no playable episodes at all does the handler speak the new `NoNextEpisode` string.
- **Gating (AC#5)**: the shared series resolution (`BaseHandler.ResolveSeriesForPlaybackAsync`) applies `ApplyLibraryFilter` (per-user TopParentIds) and `FilterByContentAccess` (JF-466 hard-zero when videos are disabled; the new handler additionally returns the localized `MediaTypeNotAvailable` via `IfMediaTypeDisabled` before any query).
- **AC#3**: `PlayEpisodeIntentHandler` now parses season+episode; when either fails to parse it falls through to the same NextUp core (announce included). Explicit season+episode behavior is unchanged (same query, same responses; the series search is now content-gated, which was missing before).
- Locale keys added to ALL 17 locale files: `PlayingNextEpisode`, `PlayingLatestEpisode`, `NoNextEpisode` (plain only, no SSML twins; `BuildOutputSpeech` falls back to the plain key when the SSML twin is absent, so twins can be added later with zero code change).
- it-IT samples went through the YAML template + regeneration (idempotent diff: only the new intent block). DynamicEntityBuilder `TvIntents` includes the new intent so SeriesName dynamic entities update for it too.

**Known follow-ups (not blocking PART 1):**

- **Stream URL inconsistency (pre-existing, now visible)**: PlayEpisode's explicit season+episode path and PlayRandom's VideoApp directive still use `GetStreamUrl` (`/Audio/{id}/stream`) for Movie/Episode items, while PlayVideo/StartOver/ContinueWatching and the new NextUp path use `GetVideoStreamUrl` (`/Videos/{id}/stream`). The new code uses the Videos endpoint (majority + correct for video items); aligning the two older call sites is a deliberate follow-up decision, kept out of this change to avoid altering the explicit-path behavior silently.
- The it-IT `ResumingVideo` locale value is untranslated English (pre-existing gap, unchanged).
- The `DidNotCatchEpisodeNumber` locale key lost its last production code path with the AC#3 fallback (no code returns it anymore). It stays in the 17 locale files unused; remove it in the PART 2 cleanup or when PART 2 adds a phrasing that legitimately needs the prompt back.
- NLU competition risks pinned by the new fixtures: "continue watching {series}" vs the slotless `ContinueWatchingIntent` samples, and "play the latest episode of {series}" vs PlayPodcastIntent's identical carrier with `{podcast_name}`. The fixtures encode the expected winner; the live NLU suite must confirm after the model deploy.
- Docs mirrors (VOICE_COMMANDS.md, docs/playback-lifecycle-*.md, docs graphs) do not yet include the new intent's flow; nothing existing went stale because samples were only added, never removed or reworded. Extend them together with PART 2's flow documentation.

### PART 2 (outstanding): TV binge auto-advance (AC#4)

Not implemented: `PlaybackNearlyFinishedEventHandler`/`PlaybackFinishedEventHandler` still enqueue only music radio tracks. PART 2 must add the video auto-advance via the VideoApp launch path (within-session), respecting the AudioPlayer-event handler constraints documented in CLAUDE.md, plus AC#6/#7 completions (live NLU run, E2E test) and the DoD gates (#9 /simplify, #10 code-review) before this task can leave In Progress.

Simplify watch-item (2026-09-05): the ~28-line series-resolution prelude (feature gate, series_name extraction, progressive response, user resolution, series resolve) now runs verbatim in both PlayEpisodeIntentHandler and PlayNextEpisodeIntentHandler; extracting it needs 6 parameters today (heavier than the duplication). Apply the extraction when PART 2 adds a third series path, not before. Also from the same pass: the VideoApp.Launch inline-response family (~11 sites) could be retired by a generic BuildVideoAppLaunchResponse builder; repo-wide refactor, deliberately out of scope here.
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
