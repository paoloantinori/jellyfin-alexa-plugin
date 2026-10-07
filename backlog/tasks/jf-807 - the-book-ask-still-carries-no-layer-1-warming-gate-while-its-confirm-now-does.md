---
id: JF-807
title: >-
  JF-807 - the book ask still carries no Layer-1 warming gate while its confirm now does
status: In Progress
assignee: []
created_date: '2026-10-07'
labels:
  - tech-debt
  - playback
dependencies:
  - JF-806
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-806 round (2026-10-07, same-turn per the
review-recommendation rule; the /simplify altitude review surfaced it).

JF-806 folded the warming axis onto the YesIntent confirm legs per the
JF-805 gate marker, including the BOOK confirm leg (the marker's explicit
instruction, protective on the JF-795 composition's widened surface: the
paged chapter fetch plus per-chapter UserData reads). But the book ASK,
PlayBookIntentHandler, still carries NO Layer-1 warming gate: it is absent
from WarmingGateCoverageTests.ExpectedGatedHandlers and makes no
GuardIndexReady call, so during the post-restart index-load window the ask
runs the same cold-database composition the confirm now refuses. The
confirm is more protected than the ask: the residual asymmetry the marker's
fold introduced deliberately.

FIX SHAPE: the PlayAlbumIntentHandler precedent is the pattern: the coarse
GuardIndexReady(_artistIndex) entry gate with the "album paths have no
in-memory index of their own to gate on, so the artist index stands in for
the shared cold database" rationale (PlayBookIntentHandler would need the
IArtistIndex ctor param threaded). Add the ask to
WarmingGateCoverageTests.ExpectedGatedHandlers when gated, and decide
whether the JF-806 book-confirm gate's ordering comment (which today
documents the ask's ungated state) needs its premise updated in the same
change. Optional companion: a SkillWarmingUpTests-style reachability pin
for the ask entry.
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
