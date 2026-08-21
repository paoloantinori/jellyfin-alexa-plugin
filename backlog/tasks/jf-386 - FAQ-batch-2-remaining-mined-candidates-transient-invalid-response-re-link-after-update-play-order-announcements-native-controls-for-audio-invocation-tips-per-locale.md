---
id: JF-386
title: >-
  FAQ batch 2: remaining mined candidates (transient invalid-response, re-link
  after update, play-order, announcements, native-controls-for-audio, invocation
  tips per locale)
status: To Do
assignee: []
created_date: '2026-08-21 06:28'
labels:
  - docs
  - faq
  - onboarding
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Batch 1 of mined FAQ entries shipped in 2cb9112 (7 entries: no seek bar, next-mid-track buffering error, follow-me behavior, artist-request song-wording + carrier words + did-you-mean, mood genres on tracks, Ready-not-linked + co.jp redirect). Sources: backlog mining (410 tasks), session transcripts (8 files of live on-device reports), CLAUDE.md gotchas + GitHub issues.

DEFERRED CANDIDATES (mined and validated, not yet written):
1. Intermittent 'la skill non ha fornito una risposta valida' - Alexa ~8s window, transient, retry usually works (JF-358 fixed the main cause; 2 severe session reports).
2. After a plugin update the skill asks to re-link the account (JellyfinToken persistence; 2 session reports; partially dev-side).
3. Artist always plays the same songs in the same order - deliberate rating/popularity ordering; use shuffle ('modalita casuale') (issue #3).
4. Now-playing announcements never heard on music - AnnounceAudioPlays is opt-in, default off (1 report).
5. Native controls for audio can break audiobook playback on some devices (JF-288) - partially covered inside the new seek-bar entry; standalone entry only if reports recur.
6. Invocation name tips per locale (fr user could not say 'jellyfin'; 'mon serveur' worked) - extends the existing invocation-name FAQ.
7. Recommended-phrasings bundle (carrier words table per locale) - could merge with the existing artist-request entry later.
8. Upgrade-notes class (duplicate user rows after upgrade JF-152 etc.) - release-notes material, write only if a release ships with such a fix.

Write when a natural doc-touch moment occurs (release notes, next FAQ batch); no urgency.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 Write the remaining mined FAQ candidates (list in description) in a second batch, same style/placement rules as 2cb9112
- [ ] #2 #2 Keep the deferred list updated as entries ship
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
