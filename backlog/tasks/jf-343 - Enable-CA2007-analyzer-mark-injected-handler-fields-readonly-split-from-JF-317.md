---
id: JF-343
title: >-
  Enable CA2007 analyzer + mark injected handler fields readonly (split from
  JF-317)
status: To Do
assignee: []
created_date: '2026-07-15 18:40'
labels:
  - code-quality
  - tech-debt
milestone: m-7
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Split from JF-317 (the other 3 parts closed there: SessionTokens removal, WaitAsync, Task.Run removal — all shipped in eea9cea).

Scope (2 remaining parts):
1. Enable the CA2007 (ConfigureAwait(false)) analyzer in jellyfin.ruleset and fix the ~87 non-compliant awaits concentrated in handlers/controllers (PlayArtistSongsIntentHandler, ConfigurationController, PlaySongIntentHandler, etc.). Warnings-as-errors is on, so enabling CA2007 surfaces every site — mechanical and self-verifying.
2. Mark all injected DI fields readonly across ~41 handlers (e.g. NextIntentHandler, PlayAlbumIntentHandler; BaseHandler._config is currently `private protected` non-readonly). This also hardens against the singleton-handler mutable-state concurrency finding.

Independently small changes; group as one cleanup PR. Verify: dotnet build -warnaserror (0 warnings), full test suite passes.
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
