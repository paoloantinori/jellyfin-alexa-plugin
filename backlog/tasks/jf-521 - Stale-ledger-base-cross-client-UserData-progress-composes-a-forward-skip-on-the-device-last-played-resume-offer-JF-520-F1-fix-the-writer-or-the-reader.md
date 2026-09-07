---
id: JF-521
title: >-
  Stale ledger base + cross-client UserData progress composes a forward skip on
  the device-last-played resume offer (JF-520 F1); fix the writer or the reader
status: To Do
assignee: []
created_date: '2026-09-07 23:45'
labels:
  - resume
  - transcoding
  - known-issue
  - follow-up
dependencies: []
references:
  - JF-520
  - JF-514
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-520 code review (2026-09-08), finding F1 - CONFIRMED mechanism, live-reachable on this deployment (NativeControlsForAudio=true verified in the container config):

BuildDeviceLastPlayedOffer classifies a UserData position as stream-relative whenever the device ledger has a base for the item AND the item routes to the transcode. But UserData is CROSS-CLIENT: after the device did an audio-shaped transcode launch (base recorded, UserData stream-relative), a LATER play of the same item on the phone or via VideoApp advances UserData with an ITEM-ABSOLUTE position while the device ledger base stays stale. The offer then flags it stream-relative and the confirm composes base+position = a FORWARD skip of exactly the stale base (walk: base 20min + phone-watched 40min mints ?start=60min on a 40min-true position; no runtime clamp, so the mint can exceed the item length). The residual was documented and accepted when JF-520 shipped the seed classification as the cheapest fix for the common audio-shaped case; this task owns the real fix.

Also record from the same review, finding F2 (PLAUSIBLE, narrow, one-sentence note; no code change requested): a resolve whose directive never plays advances the ledger while the offset source stays frozen from the previous cycle (device offline / response lost, no AudioPlayer events), so each resume retry walks the mint forward by the stale offset; the same shape has existed on the offer path since JF-514.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Decision between fix shapes recorded with reasoning: (a) fix the WRITER (event handlers persist item-absolute positions by adding the launch base; could eventually delete the flag) or (b) fix the READER (clear/ignore the base when UserData was written by a non-audio-shaped source)
- [ ] #2 If (a): writer sites updated with tests; the seed classification re-evaluated; the residual re-checked end to end
- [ ] #3 If (b): invalidation implemented with tests; the F1 walk re-verified (stale base no longer composed)
- [ ] #4 Regression guard: the screenless-Dot common path (context seed, flag=true, rebase) unaffected
- [ ] #5 Full suite green; /simplify + code-review high gates before merge
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
