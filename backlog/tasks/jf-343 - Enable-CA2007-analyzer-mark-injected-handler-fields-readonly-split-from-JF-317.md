---
id: JF-343
title: >-
  Enable CA2007 analyzer + mark injected handler fields readonly (split from
  JF-317)
status: Done
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

## Implementation Notes

COMPLETED 2026-09-13 (both parts verified against the CURRENT tree, three months after filing):
1. CA2007 enabled as Error in jellyfin.ruleset. The filed "~87 non-compliant awaits" had already been fixed by the intervening months of ConfigureAwait discipline: with the rule live, the full solution builds -warnaserror with ZERO violations (verified by a deliberate bare-await probe file firing the error, then its removal). A statement-level grep for "missing ConfigureAwait" produces only FALSE positives from lambdas (the call's ConfigureAwait sits after inner semicolons) - the analyzer is the truth, not greps.
2. Readonly fields: every injected DI field across the 61 intent handlers was ALREADY readonly (sweep: 0 non-readonly in handlers). The 7 remaining non-readonly private fields in the plugin (timers, CTS, running Task, nullable factory, test override) are all legitimately-reassigned mutable state and must stay writable. The one filed exception fixed here: BaseHandler._config (assigned once in the ctor, never reassigned - verified by grep) is now `private protected readonly`.
Suite 3701/3701 both TFMs; full-solution -warnaserror build clean.

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
