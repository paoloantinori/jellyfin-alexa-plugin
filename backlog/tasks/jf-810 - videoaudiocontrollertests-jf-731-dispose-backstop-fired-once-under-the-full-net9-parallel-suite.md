---
id: JF-810
title: >-
  JF-810 - VideoAudioControllerTests' JF-731 Dispose backstop fired once under the full net9.0 parallel suite
status: Done
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - testing
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-808 round (2026-10-07, same-turn per the
every-failure-tracked rule; the JF-809 twin shape).

OBSERVED ONCE: the FIRST full-suite run at the JF-808 final state on
net9.0 failed exactly one test class teardown:
VideoAudioControllerTests.Dispose (line 84), the JF-731 backstop's
Assert.Fail ("the JF-731 Dispose backstop had to clean up at this
test's teardown"). The specific test whose teardown observed the leak
was not captured in the run tail (the failure surfaces at class
Dispose, aggregated by xUnit).

REPRODUCTION MATRIX (no root-cause claim):

- Full suite, JF-808 tree, net9.0, run 1: FAILED (the Dispose
  backstop; 5463/5464 passed).
- VideoAudioControllerTests in isolation, net9.0: PASSED 174/174.
- Full suite, JF-808 tree, net9.0, run 2: PASSED 5464/5464.
- Full suite, JF-808 tree, net10.0: PASSED 5464/5464.
- Full suite, base tree 1ea40cd4 (pre-JF-808), net9.0: PASSED
  5458/5458.

KNOWN DOCUMENTED RACE (in-code, not diagnosed here): the backstop's
own coverage note names the window ("an encode born after a PRIOR
test's sweep completed lands here"), i.e. a gated encode outliving the
sweep of the test that started it can land in a LATER test's teardown
and trip the backstop. Whether this observation is that window or
something else is undetermined.

NEXT STEP if it recurs: capture the full failure message including the
`detail` string and the owning test name (run with
`--logger "console;verbosity=detailed"` or a trx), then bisect the
encode-starting tests against the sweep ordering.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

CLOSURE (2026-10-07, the JF-808 gate-marker): this filing is a DUPLICATE of JF-772 (the same JF-731 dispose-backstop flake class, already filed 2026-10-05 with base-reproducibility matrices and candidate mechanisms). The one genuinely new datum (the first net9.0 occurrence, weakening the net10.0-specific candidate) is folded into JF-772's addendum. Closed as a duplicate; no separate work.
<!-- SECTION:NOTES:END -->
