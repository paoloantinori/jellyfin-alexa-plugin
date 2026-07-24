---
id: JF-331
title: >-
  Platform risk: Track Alexa+ impact on custom AudioPlayer skills (monitoring
  task)
status: To Do
assignee: []
created_date: '2026-07-12 15:01'
updated_date: '2026-07-13 20:18'
labels:
  - platform-risk
  - monitoring
milestone: m-11
dependencies: []
references:
  - >-
    https://developer.amazon.com/en-US/docs/alexa/ask-overviews/deprecated-features.html
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Medium-term platform risk to monitor (competitor research, 2026-07-12, sourced with explicit uncertainty flags):

Confirmed: existing custom skills keep working on "original Alexa" and can still be updated/published (Amazon's own Feb 2025 post developer.amazon.com/.../2025/02/new-alexa-announce-blog). The Music/Radio/Podcast Skill API remains partner-only and is NOT deprecated (developer.amazon.com deprecated-features page, fetched 2026-07-12) — so the native-scrubber/queue ceiling is category-wide, not fixable here. But Amazon has stated new developer tooling focuses on Alexa+ (LLM assistant) via new Action/Web-Action/Multi-Agent SDKs.

UNVERIFIED (flagged, do not treat as fact): a single secondary blog claimed Amazon began auto-upgrading some Prime Echo devices to Alexa+ starting Jan 2026, non-opt-out but revertible. Not confirmed against an Amazon source.

This is a watch-item, not actionable work today. Task: periodically re-verify (a) whether custom AudioPlayer invocation-name routing still behaves on Alexa+-upgraded devices, (b) whether Amazon opens an Alexa+ path for custom skills / self-hosted media, (c) the auto-upgrade claim against a primary source. Record findings; escalate to a real migration task only if on-device behavior degrades. Verify all claims live before acting — training memory on Alexa+ is presumptively stale.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A short living note (docs or task comments) records the current verified state of Alexa+ vs custom AudioPlayer skills with source URLs and dates
- [ ] #2 The unverified auto-upgrade claim is either confirmed against an Amazon-owned source or kept explicitly labeled unverified
- [ ] #3 On-device (or simulator) check of invocation-name routing behavior on any Alexa+ device is recorded if such a device is available
- [ ] #4 A concrete trigger is defined for when this becomes a real migration task (e.g. routing observed to break)
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
