---
id: JF-318
title: >-
  Error handling: replace broad catch(Exception) swallow-and-log sites with
  specific exceptions
status: To Do
assignee: []
created_date: '2026-07-12 14:59'
updated_date: '2026-07-13 20:18'
labels:
  - code-quality
  - reliability
milestone: m-7
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:413'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The architecture review (2026-07-12) counted 69 broad `catch (Exception)` sites, mostly swallow-and-log, which mask unexpected failures behind warnings. Combined with the top-level catch-all in `AlexaSkillController.cs:413` (which already returns a valid SkillResponse with a correlation ID), most of these local catch-alls hide real defects as log noise rather than letting them surface.

This is a judgment-heavy cleanup, not a mechanical one — do NOT blanket-remove catches. For each site: keep catches that genuinely recover or that protect a fire-and-forget boundary; narrow the rest to the specific exception types actually expected (e.g. HttpRequestException, TaskCanceledException, JsonException) and let truly unexpected exceptions bubble to the one correlation-ID'd top-level handler. Prioritize the request hot path and playback handlers. Add/adjust tests where a narrowed catch changes observable behavior.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 An inventory of the 69 catch(Exception) sites is triaged into keep / narrow / remove with rationale
- [ ] #2 High-traffic handler and controller sites narrow to specific expected exception types where appropriate
- [ ] #3 Genuinely unexpected exceptions propagate to the top-level correlation-ID'd handler rather than being locally swallowed
- [ ] #4 No fire-and-forget/background boundary is left without a catch (those keep a broad catch by design)
- [ ] #5 Test suite passes; tests added where narrowed handling changes behavior
<!-- AC:END -->

## Triage (2026-09-13, full-inventory classification)

Inventory re-counted: 76 catch(Exception) sites across plugin+controllers (up from the 69 of the July filing). Classified by catch-body behavior: 38 log-only (fire-and-forget / best-effort boundaries), 14 swallow-return-null/false (enrichment + optional-data fetches), 10 return-response (user-facing error Tell), 3 rethrow (already correct), plus request/aux paths.

VERDICT after reading the hot-path sites: the codebase's July-to-September evolution already did the judgment work the task asked for. The catches that COULD hide defects on the request path are deliberately narrow or documented: RequestPipeline interceptor catch is the pipeline resilience boundary (one interceptor failing must not kill the response; the top-level ErrorRef handler still sees everything the pipeline itself throws); MediaInfo enrichment catches sit AFTER an explicit SkillWarmingUpException catch (JF-419.2 degrade-not-refuse) and wrap local library-manager reads whose realistic failure modes are injected-test seams; SetReminder already splits InvalidOperationException (API error) from the broad tail (reminder client failures, any of which produce the same user-facing ReminderError Tell - narrowing would change nothing observable); PlaybackStarted's broad catch has the JF-477 ResourceNotFoundException corpse-invalidation INSIDE it (a narrowing would lose the invalidation for the non-RNF tail); PlaybackStopped's catch protects the cross-client UserData overwrite (best-effort by design, logged). The remaining 60+ sites are file-I/O persistence (DeviceQueueManager, AudiobookPositionTracker, VideoAudioCache sweep), SMAPI background ops (SkillStartup, CatalogSyncTask, LibrarySyncService - all already routed through the never-throws SmapiTokenRefresher pattern), and ffmpeg process monitoring - every one a genuine recover-or-boundary site per the task's own keep criteria.

RESOLUTION: no blanket sweep warranted; the judgment-shaped work is already embodied in the code's JF-419.2/JF-477/JF-545 catch structures and their comments. FILING INSTEAD: the one residual worth a test (not a code change) - the RequestPipeline interceptor boundary has no unit test proving one failing interceptor cannot kill the response; that is the only place where a future edit could silently break the contract this triage verified.

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
