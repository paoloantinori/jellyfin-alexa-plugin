---
id: JF-386
title: >-
  FAQ batch 2: remaining mined candidates (transient invalid-response, re-link
  after update, play-order, announcements, native-controls-for-audio, invocation
  tips per locale)
status: Done
assignee: []
created_date: '2026-08-21 06:28'
updated_date: '2026-09-18 23:28'
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
- [x] #1 #1 Write the remaining mined FAQ candidates (list in description) in a second batch, same style/placement rules as 2cb9112
- [x] #2 #2 Keep the deferred list updated as entries ship
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-10: the 're-link after update' candidate LANDED in the README FAQ (commit bdb2d53e), prompted by external issue #23 (v12 update, generic 'Sorry, I'm having trouble', Test connection green) plus the JF-527 relink UX landing. Entry covers the symptom/mechanism (update invalidates the stored per-user token; Test connection checks the server, not the link), the dashboard re-link steps, the version-dependent spoken message (generic up to 0.12.1.0, explicit re-link message from newer releases), and the log lines to attach when opening an issue. Batch 2's remaining candidates unchanged.

2026-09-19: batch 2 landed. Remaining deferred: 5 (native-controls standalone, only if reports recur), 7 (carrier-words table, merge later), 8 (upgrade-notes, release-gated). 2 landed earlier (bdb2d53e).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Batch 2 shipped (README FAQ, docs-only commit): (1) transient 'skill was unable to respond' entry (8s window, retry-first guidance, pointer to the warm-up entry); (3) same-artist-same-order entry, corrected after review to the two mechanisms that actually exist (the Shuffle Artist Songs config flag and the during-playback built-in 'shuffle'; the first draft's 'ask for shuffle in the same breath' advice was wrong: no slot-content shuffle detection exists on the artist path, and the 'in modalita casuale' samples belong to the playlist intent) with the popularity wording fixed to favorites/most-played-then-rating (PopularitySort's real key order); (4) now-playing-music announce entry (Announce Music Plays opt-in default off, video/book default on, both toggles located); (6) invocation-name entry extended with the foreign-brand-under-non-matching-locale pitfall (French 'Jellyfin' report). Also fixed in passing: 'Two common pitfalls' now says Three, and a pre-existing duplicated FAQ heading (mood/genre) removed. Deferred list now: (5) native-controls standalone entry only if reports recur, (7) carrier-words table merge later, (8) upgrade-notes class only when a release ships such a fix. Candidate (2) had landed earlier in bdb2d53e. Review: code-reviewer verified all claims against source; 2 of 4 draft entries needed corrections, both applied.
<!-- SECTION:FINAL_SUMMARY:END -->

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
