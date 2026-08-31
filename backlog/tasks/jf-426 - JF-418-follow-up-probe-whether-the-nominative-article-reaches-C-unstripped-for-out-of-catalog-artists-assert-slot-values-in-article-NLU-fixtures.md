---
id: JF-426
title: >-
  JF-418 follow-up: probe whether the nominative article reaches C# unstripped
  for out-of-catalog artists; assert slot values in article NLU fixtures
status: To Do
assignee: []
created_date: '2026-08-31 15:02'
labels:
  - code-review
  - probe-first
  - nlu
dependencies: []
references:
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayArtistSongsIntentHandler.cs:117
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Util/KeywordMatcher.cs:70'
  - 'Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs:2513'
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-up from the JF-418 /simplify altitude review (2026-08-31). PlayArtistSongsIntentHandler.cs:117 reads musicianSlot.Value raw.

CONTEXT: JF-418 added nominative-article samples ("Suona i {musician}") to the it-IT model; all profile-nlu probes show AMAZON.Musician stripping the article (musician=queen for "suona i queen"). BUT every probe artist was in Amazon's catalog (Queen, Radiohead, Pink Floyd, Mina): the stripping may come from entity resolution against Amazon's catalog rather than the slot type itself. An out-of-catalog artist spoken with an article could deliver "gli xyzzy foo" raw, and a leading article poisons all 4 search tiers (every Contains/StartsWith shape fails).

The strip vocabulary already exists: KeywordMatcher.cs:70 StopWords["it"] opens with the same six articles, and BaseHandler.cs:2513 already strips this way for the cross-media fallback. The it-IT YAML vocabulary comment cross-references this twin.

PROBE BEFORE CODE (agent's explicit judgment): zero observed failures, so a preemptive C# strip would be speculative. One on-device probe decides it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 On-device (or simulator with raw slot passthrough) probe: speak an artist NOT in Amazon's catalog with an article ('suona gli xyzzy foo') and record whether musician arrives as 'xyzzy foo' or 'gli xyzzy foo'
- [ ] #2 If the article leaks: leading-article strip added in the artist entry path reusing KeywordMatcher's Italian article set (single definition, cross-referenced from the it-IT YAML comment), with unit test
- [ ] #3 If the article does not leak: finding documented as not reproducible, task closed
- [ ] #4 NLU article fixtures upgraded to assert the slot VALUE (article stripped) for at least one in-catalog case, so Amazon-side stripping drift fails the suite
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
- [ ] #10 /code-review high passed (no blocking findings remaining
- [ ] #11 or findings applied/tracked)
<!-- DOD:END -->
