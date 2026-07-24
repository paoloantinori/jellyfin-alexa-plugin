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
