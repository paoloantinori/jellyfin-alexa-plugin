---
id: JF-735
title: >-
  JF-735 - AskFirstMatch truncates its STATE list at Take(3): ranks past 3
  unaskable and unannounced (the stricter sibling of the JF-729 fix)
status: To Do
assignee: []
created_date: '2026-10-03 19:35'
labels:
  - ux
  - disambiguation
dependencies:
  - JF-729
references:
  - >-
    backlog/tasks/jf-729 -
    JF-729-the-capped-multi-artist-ask-presents-its-3-spoken-names-as-an-exhaustive-found-set-no-and-others-hint-in-the-17-locale-strings.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the orchestrator gate-marker review of the JF-729 merge
(commit 8b77909e, finding 2 of 2; the JF-729 worker surfaced the observation in its
task file's Final Summary, and the global review-recommendation rule requires a tracker
entry the moment an out-of-scope finding is reported - the summary mention is not
tracking). AskFirstMatch (the JF-377 coincidental-containment downgrade prompt) truncates
its STATE list at Take(3) exactly as its speech does: ranks past 3 are unaskable AND
unannounced, the stricter sibling of the exhaustive-found-set defect JF-729 just fixed
for AskMultipleArtists (which now speaks 3 but cycles the full list and hints "Plus N
more").

The documented-deliberate defense (the MultipleArtistsSpeakCap doc): AskFirstMatch's
callers pass progressively weaker fuzzy candidates, where a rank-4+ tail is noise, while
the ER gate's candidates are exact library-name resolutions. Weighed against: the JF-377
design says bug-vs-carrier-bleed cases are string-indistinguishable, so a genuine rank-4
EXACT match can exist in the fuzzy candidate list too (a containment match on a
multi-word artist whose rank-4 form is the real artist), and the user gets no signal it
exists. THE WORK: decide whether AskFirstMatch's state should keep the full list (with
the cycling reaching rank 4+ the way AskMultipleArtists now does, plus the hint), or
whether the weaker-candidates defense holds and the truncation stays deliberate with
the cap doc's rationale strengthened. If changed: pin the rank-4 reachability and the
hint's absence-or-presence per the decision.
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
