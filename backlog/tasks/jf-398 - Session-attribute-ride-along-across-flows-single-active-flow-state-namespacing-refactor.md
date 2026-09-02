---
id: JF-398
title: >-
  Session attribute ride-along across flows: single active-flow state
  namespacing refactor
status: Done
assignee: []
created_date: '2026-08-23 05:56'
updated_date: '2026-08-23 10:53'
labels:
  - refactor
  - multi-turn
  - session-state
milestone: m-15
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Structural finding (2026-08-23 multi-turn audit). SessionAttributesInterceptor merges incoming attributes onto every non-terminal response, so keys of DIFFERENT flows coexist (e.g. a resume offer issued while pagination_state is active carries both; a stale disambig_matches rides along into any subsequent Ask). The Yes/No handlers resolve collisions with a hard-coded priority (resume > pagination > disambiguation), which works today but scales badly as flows are added and produces stale-state surprises (see the resume_state bug JF-394 for the concrete instance).

Direction: namespace conversational state under a single active-flow key (e.g. active_flow: {type, payload}) written atomically by each flow's responses, cleared on flow exit, so only one flow is ever live per session and the priority chain becomes unnecessary. This is a refactor; sequence AFTER JF-394 and the FindSong fixes so those land on current behavior.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented incrementally in c7d9f47 (instead of the key-format rewrite): ConversationalFlows helper + MarkOthersInactive wired at every flow START (FindSong BuildElicitSlotResponse, DisambiguationHelper AskFirstMatch x2/AskNextMatch, BaseHandler cross-media offer, ListPaginationHelper BuildNextPageResponse, LaunchRequestHandler resume offer). Only one conversational flow can be live per session; key formats unchanged so sessions open across a deploy survive. The Yes/No priority chain stays as defense-in-depth, now unreachable in steady state. TDD: helper test + 2 handler/interceptor integration tests; 2721 passing. Deploy pending (rides the next /deploy; on-device sanity is JF-405 item 8).
<!-- SECTION:FINAL_SUMMARY:END -->
