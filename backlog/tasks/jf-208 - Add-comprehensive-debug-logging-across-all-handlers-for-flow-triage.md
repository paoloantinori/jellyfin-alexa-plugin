---
id: JF-208
title: Add comprehensive debug logging across all handlers for flow triage
status: Done
assignee: []
created_date: '2026-05-23 09:15'
updated_date: '2026-05-23 10:45'
labels:
  - observability
  - logging
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Goal
Add `Logger.LogDebug(...)` calls throughout the codebase so that enabling `Jellyfin.Plugin.AlexaSkill: Debug` in `logging.default.json` gives a complete picture of every request lifecycle — from intent resolution through Jellyfin queries to response building.

## Why
Debug logs have been critical for diagnosing real-device issues (e.g., the PlaybackStopped displacement bug that corrupted resume positions). Currently logging is uneven — some handlers have it, most don't. A systematic pass ensures every flow is observable without code changes.

## Scope

### 1. Intent Handlers (Alexa/Handler/Intent/)
For **every** intent handler, add debug logs at:
- **Entry**: intent name, resolved slot values, locale
- **Jellyfin query**: search term, item types, result count
- **Key decisions**: fuzzy match outcomes, disambiguation path taken, feature flag checks
- **Exit**: response type (AudioPlayer/SSML/tell/ask), item ID played, offset if resuming

Priority handlers (media playback):
- `PlayArtistSongsIntentHandler` — artist search fallback tier used, match score, song count
- `PlayAlbumIntentHandler` — album match, track count, resume offset
- `PlayPlaylistIntentHandler` — playlist match, track count
- `PlayEpisodeIntentHandler` — series/episode match
- `SearchMediaIntentHandler` — search term, result types
- `BrowseLibraryIntentHandler` — category resolved, item count, truncation
- `ListQueueIntentHandler` — queue size, items returned
- `InProgressMediaListIntentHandler` — item count, truncation
- `QueryArtistLibraryIntentHandler` — artist match, album list
- `QueryRecentlyAddedIntentHandler` — item count, truncation
- `YesIntentHandler` / `NoIntentHandler` — what was being confirmed (disambiguation context)

### 2. Event Handlers (Alexa/Handler/Event/)
Already partially covered. Ensure:
- `PlaybackNearlyFinishedEventHandler` — token, queue advance result, next item ID
- `PlaybackStartedEventHandler` (if exists) — token, offset

### 3. BaseHandler shared utilities
- `HandleFuzzyMiss()` — query, candidate count, best match name/score, outcome (auto-play/disambiguate/not-found)
- `FuzzyMatch()` — query, best candidate, score
- `BuildAudioPlayerResponse()` — item ID, stream URL, offset
- `ApplyLibraryFilter()` — libraries included/excluded
- `SendProgressiveResponse()` — when sent, what text

### 4. Pipeline (Alexa/Pipeline/)
- Request interceptor: already logs intent name — add slot values
- Response interceptor: already logs body — ensure it's at debug level

### 5. Playback state (Alexa/Playback/)
- `DeviceQueueManager.SetQueue()` — device ID, item count, first item
- `DeviceQueueManager.Advance()` — device ID, from index → to index, next item
- `QueueContinuationStore.Set/Get` — device ID, parent ID, start index

### 6. APL (Alexa/Apl/)
- `AplHelper.BuildListDirective()` — item count, hasMore
- `AplUserEventHandler` — tap action, resolved item ID

## Guidelines
- Use `Logger.LogDebug(...)` — filtered at Information level by default, no performance impact
- Include identifiers that link to Jellyfin entries: item IDs (short form OK), names
- Log slot values as received from Alexa (before any transformation) to help triage NLU issues
- Don't log API keys, tokens, or user credentials
- Follow structured logging with named placeholders: `"Processing {IntentName} with slots {Slots}"`

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every intent handler has entry and exit debug logs
- [x] #2 Every play handler logs Jellyfin query params and result count
- [x] #3 FuzzyMatch/HandleFuzzyMiss log match score and outcome
- [x] #4 PlaybackNearlyFinished logs queue advance
- [x] #5 dotnet build 0 errors and dotnet test passes
- [ ] #6 Verified on minix with debug logging enabled
<!-- SECTION:DESCRIPTION:END -->

<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added comprehensive Logger.LogDebug() calls across 48 files covering all 57 intent handlers, event handlers, BaseHandler utilities (FuzzyMatch, HandleFuzzyMiss, BuildAudioPlayerResponse, SendProgressiveResponse), pipeline interceptors, DeviceQueueManager, and APL user event handling. 

Key decisions:
- Pipeline interceptor logs all slot values at debug level with IsEnabled guard; per-handler slot logging removed as redundant
- FuzzyMatch changed from static to instance method for Logger access (all callers are instance methods)
- LibraryFilter.ApplyLibraryFilter gained optional ILogger param for the most important call paths

Build: 0 errors, 0 new warnings. Tests: 1862 passed, 0 failed.
AC#6 (minix verification) skipped — requires manual deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
