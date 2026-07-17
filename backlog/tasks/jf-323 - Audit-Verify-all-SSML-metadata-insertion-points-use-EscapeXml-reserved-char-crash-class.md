---
id: JF-323
title: >-
  Audit: Verify all SSML metadata insertion points use EscapeXml (reserved-char
  crash class)
status: To Do
assignee: []
created_date: '2026-07-12 15:00'
updated_date: '2026-07-13 20:18'
labels:
  - reliability
  - ssml
  - audit
milestone: m-8
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:1723'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Comparative finding from the 2026-07-12 competitor review: AskNavidrome shipped a fix adding SSML/XML reserved-character sanitization for track/artist/album names, a real bug class (classical music and non-English catalogs commonly contain `&`, `<`, `>`, quotes). This plugin ALREADY has `BaseHandler.EscapeXml` (`BaseHandler.cs:1723`) and uses it in ~8 places (ListPaginationHelper, YesIntentHandler, BrowseLibraryIntentHandler, SetReminderIntentHandler, QueryRecentlyAddedIntentHandler, FollowMeIntentHandler). So this is NOT a from-scratch gap — it's a coverage audit.

Task: audit every code path that inserts library-derived metadata (item names, artist, album, playlist names) into SSML/`<speak>` output and confirm EscapeXml (or equivalent) is applied. Any unescaped insertion of a name containing `&`/`<`/`>` produces invalid SSML and an Alexa `InvalidResponse` → user hears an error. Add a unit test with a metadata name containing reserved characters flowing through the main play/announce responses.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every SSML/<speak> output path that includes library-derived names is confirmed to escape reserved XML characters
- [ ] #2 Any unescaped insertion found is fixed to use EscapeXml
- [ ] #3 A unit test feeds a metadata name containing & < > \" through the primary play/announce responses and asserts valid, non-crashing SSML
- [ ] #4 No double-escaping regressions on already-escaped paths
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
