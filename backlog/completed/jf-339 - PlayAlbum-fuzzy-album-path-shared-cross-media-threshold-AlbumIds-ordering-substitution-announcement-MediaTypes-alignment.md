---
id: JF-339
title: >-
  PlayAlbum fuzzy/album path: shared cross-media threshold, AlbumIds ordering,
  substitution announcement, MediaTypes alignment
status: Done
assignee:
  - claude
created_date: '2026-07-13 09:54'
updated_date: '2026-07-16 21:27'
labels:
  - album
  - fuzzy
  - tech-debt
  - consistency
  - follow-up
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayAlbumIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/QueueContinuationFetcher.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Locale/ResponseStrings.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Deferred from /code-review high round 2 (2026-07-13). Four lower-risk refinements to the PlayAlbum fuzzy/album path that were NOT applied with the critical fixes (commit b12cf5c fixed the continuation truncation, min-length guard, RankMatches disambiguation, test comment):

1. Cross-media artist threshold consistency (HIGHEST of the four — altitude issue): PlayAlbumIntentHandler.cs:180 uses FuzzyMatcher.ContainmentScore (=90, a scoring constant) as the cross-media artist-fallback threshold, while PlaySongIntentHandler.cs:61/257 uses a named CrossMediaArtistThreshold=85 via Math.Max(GetDefaultThreshold(user), 85). Same concept, two handlers, two values (85 vs 90), and PlayAlbum ignores the per-user FuzzyMatchThreshold config. Lift CrossMediaArtistThreshold to BaseHandler, reconcile 85/90, use in both. The 60→90 change in JF-336 was a bandaid that borrowed a scoring constant for a policy decision.

2. AlbumIds ordering: the AlbumIds fallback (initial fetch in PlayAlbumIntentHandler + continuation in QueueContinuationFetcher) has no OrderBy, so multi-disc albums may play out of order.

3. Substitution announcement: the fuzzy album fallback plays a substituted album silently (no spoken signal), unlike the artist fallback's FoundArtistInstead announcement. Voice-only devices can't tell a substitution from a correct match.

4. MediaTypes alignment: the ParentId path uses MediaTypes=Audio, the AlbumIds path uses IncludeItemTypes=Audio — latent divergence (only differs for AudioBook-tagged children).

Verified context in commit b12cf5c + the /code-review high findings (Finding 4/5/6/8). These are polish/robustness, not blockers — the critical correctness bugs (continuation truncation, false-positive guard, disambiguation) are fixed.

Related: JF-336/338 (the PlayAlbum fuzzy + tolerant work), JF-337 (phonetic fallback for 9 other handlers — same cross-media-threshold + AlbumIds-ordering concerns apply there once adopted).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Lift the cross-media artist-fallback threshold to a single shared named constant (e.g. BaseHandler.CrossMediaArtistThreshold) used by BOTH PlayAlbumIntentHandler and PlaySongIntentHandler (PlaySong already has CrossMediaArtistThreshold=85; PlayAlbum currently uses ContainmentScore=90). Reconcile the value (85 vs 90) — both must reject the weak Uazz/Lamb @75 case.
- [x] #2 Respect the user's FuzzyMatchThreshold config: the cross-media threshold should be Math.Max(GetDefaultThreshold(user), CrossMediaArtistThreshold) as PlaySong already does — PlayAlbum's hardcoded 90 ignores the per-user setting.
- [x] #3 AlbumIds fallback (initial + continuation): add OrderBy so multi-disc albums play in disc/track order, not arbitrary. Document the ordering assumption.
- [x] #4 Fuzzy album substitution: add a spoken announcement (like the artist fallback's FoundArtistInstead) so the user knows a different album was played (esp. for voice-only devices). Add the locale string to all 17 locales.
- [x] #5 Align MediaTypes=Audio (ParentId path) vs IncludeItemTypes=Audio (AlbumIds path) so the two paths can't diverge for AudioBook-tagged children.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
## Progress 2026-07-14 — AC#1/#2/#4/#5 implemented + verified; AC#3 deferred

DONE (build 0-warn, 2490 tests green incl. 1 new, locale validator green, interaction-model + version validators green):

- AC#1/#2: Added BaseHandler.CrossMediaArtistThreshold=85 (doc comment). PlayAlbum cross-media gate changed from hardcoded ContainmentScore(90) to Math.Max(GetDefaultThreshold(user), CrossMediaArtistThreshold); PlaySong now references the shared BaseHandler const (private duplicate removed). Reconciliation = 85 (PlaySong's deliberate value); PlayAlbum's 90 bandaid retired. Net: PlayAlbum loosens 90->85 (accepts strong 85-89 artist substitutions, all announced), both reject the known 75 FPs (Uazz/Lamb), both now respect per-user FuzzyMatchThreshold. FLAGGED for review in case stricter is preferred.

- AC#4: Added FoundAlbumInstead to all 17 locale JSONs (ResponseStrings.cs is dynamic, no const needed). PlayAlbum fuzzy fallback captures fuzzyAlbumAnnouncement and sets response.Response.OutputSpeech at the AudioPlayer return (same mechanism as BuildArtistSongsResponseAsync). New test PlayAlbum_ExactMiss_FuzzyAlbumMatch_PlaysAndAnnouncesAlbumName locks it in; existing PlayAlbum_AlbumsFound_NoFallbackTriggered still asserts exact matches stay announcement-free.

- AC#5: Aligned both album-track paths to IncludeItemTypes=Audio in PlayAlbumIntentHandler (was MediaTypes=Audio on the ParentId path) and QueueContinuationFetcher.FetchAlbumTracks.

DEFERRED (AC#3 — AlbumIds disc/track ordering): ItemSortBy.ParentIndexNumber/IndexNumber are not used anywhere in the repo and the Jellyfin SDK source path did not resolve via zread — I will not guess an enum member for DB-level sort. Correct multi-page ordering needs verified DB sort fields. The ParentId path likely already returns Jellyfin's natural child order; AlbumIds is a rare malformed-folder fallback. Keep AC#3 open; investigate Jellyfin album-child ordering separately (candidate: in-memory sort on fetched items via confirmed BaseItem.ParentIndexNumber/IndexNumber properties).

WORKING-TREE CAUTION: the diff also contains the REJECTED ContainsMediaNounCarrier guard in PlayArtistSongsIntentHandler.cs (+31, from the jazz-cafe work, JF-342 territory) and .claude/.serena/backlog config files. JF-339 must be staged/committed SEPARATELY from those.

REMAINING for DoD: /simplify + /code-review high on the JF-339-only diff before marking the task Done (AC#3 stays unchecked regardless).

## DoD reviews 2026-07-14 — PASSED clean

- /code-review high (independent fresh-context superpowers:code-reviewer on the JF-339 src diff + test): NO correctness findings. Confirmed: (a) the OutputSpeech-on-AudioPlayer announcement mirrors the shipped BuildArtistSongsResponseAsync pattern and Alexa speaks-then-plays; (b) fuzzyAlbumAnnouncement is set in exactly one branch and cannot fire on exact-match / artist-fallback / disambiguation paths; (c) the 90->85 threshold loosening has no known false-positive in the 85-89 band (both known FPs score 75); (d) the MediaTypes->IncludeItemTypes alignment is a strict improvement (the AlbumIds path already used it); (e) the new test genuinely exercises the fuzzy path and cannot pass for the wrong reason.

- /simplify: diff is 32 insertions / 12 deletions across 4 src files + 17 one-line locale additions + 1 test; reuses the existing announcement mechanism and the shared CrossMediaArtistThreshold; build is 0-warning. No reuse/simplification/efficiency/altitude cleanup warranted.

STATE: AC#1/#2/#4/#5 + DoD build/test/no-warnings/locale-strings/simplify/code-review all done. AC#3 (AlbumIds disc/track ordering) remains DEFERRED (unverified Jellyfin ItemSortBy sort fields — separate investigation needed). Task stays In Progress until AC#3 is resolved or formally descoped. JF-339 src changes are ready to commit (stage separately from the .claude/.serena/backlog config files).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-16 20:26
---
2026-07-16 reconciliation (CORRECTION): an earlier draft overclaimed this as fully done. Reality: 4 of 5 ACs shipped in commit 7e8e4b9 (on main, part of v0.10.0.0) — AC#1/#2/#4/#5 (shared cross-media threshold reconciled 85-vs-90 + per-user config, FoundAlbumInstead substitution announcement, MediaTypes alignment). AC#3 (AlbumIds multi-disc disc/track ordering) remains DEFERRED — Jellyfin ItemSortBy sort fields unverified, refused to guess an enum member. Per the 2026-07-14 note, the task stays In Progress until AC#3 is resolved or formally descoped. Left open. (The commit message's 'AlbumIds ordering' wording is misleading — that AC did not ship.)
---

created: 2026-07-16 21:27
---
2026-07-16: AC#3 RESOLVED (was deferred). Implemented DB-level disc/track ordering across all album-track query sites; see finalSummary. /code-review high (superpowers:code-reviewer, opus) passed: it surfaced a 5th album-track query site I had missed — YesIntentHandler.PlayAlbum (the disambiguation 'yes, play that album' path) — which is the same defect class; fixed with the same OrderBy. Two low findings: (a) SortName tie-breaker for pathological duplicate disc+track — skipped per reviewer (not a real-world issue; well-formed albums have unique disc+track); (b) AlbumIds-fallback test coverage — added a 3rd test for symmetric coverage. DoD /simplify: no cleanups warranted (reviewer confirmed 'done well'). All 5 ACs now met. Residual (not blocking): on-device/E2E confirm that Jellyfin honors ItemSortBy.ParentIndexNumber/IndexNumber end-to-end — not unit-testable (mocks control return order).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
AC#3 (AlbumIds multi-disc disc/track ordering) resolved 2026-07-16. Added a shared DB-level OrderBy — (ParentIndexNumber, Ascending), (IndexNumber, Ascending) — to all 5 album-track query sites: PlayAlbumIntentHandler initial + AlbumIds fallback; QueueContinuationFetcher continuation primary + AlbumIds fallback; and YesIntentHandler.PlayAlbum (album-disambiguation confirm path, surfaced by /code-review). DB-level sort is required because the progressive queue paginates (initial 5 + continuation 10/batch), so a per-page in-memory sort cannot keep global order; the shared AlbumTrackOrder constant guarantees the initial page and every continuation page concatenate into one global order. ItemSortBy.ParentIndexNumber/IndexNumber confirmed present via a throwaway reflection test (the SDK DLL restores into the build container, not the host). 3 contract tests added (fetcher primary, handler initial, fetcher AlbumIds fallback). Release build 0-warning; 2515 tests pass; /code-review high passed (5th-site finding fixed). All 5 ACs met. Changes in working tree, uncommitted.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [x] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
