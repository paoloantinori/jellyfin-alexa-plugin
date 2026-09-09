---
id: JF-528
title: >-
  JF-527 review follow-ups: four below-threshold nits (token whitespace gate,
  redundant log field, test DeviceQueueManager dispose, hi-IN/de-DE locale copy)
status: To Do
assignee: []
created_date: '2026-09-09 09:19'
labels:
  - code-review
  - nitpick
  - jf-527
dependencies: []
references:
  - JF-527
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-527 code review (2026-09-09, worktree jellyfin-alexa-plugin-jf527) so the below-threshold review notes are not lost. All four are cosmetic/hardening; none blocks the JF-527 merge.

1. BaseHandler.BuildSessionMissResponse: the token gate uses !string.IsNullOrEmpty(user.JellyfinToken) while the project convention for Alexa-supplied strings is IsNullOrWhiteSpace. A whitespace-only JellyfinToken cannot co-occur with a recorded previous play (a play requires the token to have resolved a session), so no failure scenario exists today; switching to IsNullOrWhiteSpace is pure hardening.

2. Same method: the Information log line includes the structured field "previous play on record: {HadPreviousPlay}", which is always true inside that branch. Redundant field in triage output; drop it or move the line to cover both discriminator outcomes.

3. EventHandlerTests.RecordPreviousPlayOnHarnessDevice creates a DeviceQueueManager that is never Disposed; its 2s debounce timer fires after the test ends and may write into a temp dir already swept by PluginTempDirCleanup (PersistToDisk catches and logs a warning, so it is harmless). Dispose the manager, or call FirePersistForTest before returning, to make the lifecycle explicit.

4. Locale copy nits in the new AccountRelinkRequired strings: hi-IN "Alexa स्किल की सेटिंग्स पेज" should agree with the masculine head noun पेज ("की" -> "का", or drop the izafat: "Alexa स्किल सेटिंग्स पेज"); de-DE new strings use real umlauts (Öffne, verknüpfe) while the older keys in the same file use the ae/oe transliteration convention. Both speak correctly; fix only if locale copy is touched again.
<!-- SECTION:DESCRIPTION:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
