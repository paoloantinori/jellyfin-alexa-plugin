---
id: JF-788
title: >-
  The favorite-toggle no-media wording: MediaNotFound where the other three
  guarded families say NoMediaPlaying (the evidence-door shape)
status: Done
assignee: []
created_date: '2026-10-06'
labels:
  - ux
  - tech-debt
dependencies:
  - JF-785
priority: low
---

## Description

Filed 2026-10-06 from the JF-785 /code-review high round (its finding 5),
same-turn filing rule.

The JF-785 migration made the four guarded families' MECHANISM uniform (the
ONE evidence predicate plus the resolver tail refusal) but their WORDING is
still split, and the door shape makes the split audible: on a session whose
now-playing DTO does not resolve (item deleted mid-play, or Id == Guid.Empty),
"what's playing" answers the informational DTO line (MediaInfo), while
"favorite this" on the very next utterance answers MediaNotFound ("sorry, I
could not find the media"), telling the user nothing was found when the
session just said something IS playing. The same split exists on the plain
idle shape (no evidence at all), where favorite again says MediaNotFound
while loop and playlist-edit say NoMediaPlaying.

The divergence is PRE-EXISTING (favorite's tell has been MediaNotFound on both
branches since JF-629; the JF-785 diff made the mechanism uniform, not the
strings), which is why it was recorded here instead of changed inside JF-785:
swapping the string is a user-facing behavior change on two shapes (the idle
guard branch AND the resolver-null branch, which must move together or
favorite speaks two different no-media lines depending on which branch
refused).

THE DECISION TO MAKE: unify favorite's two no-media branches onto
NoMediaPlaying (the door's semantics, evidence-exists-but-unresolvable or
nothing-at-all, match "nothing is playing" better than "could not find the
media"; both keys already exist in all 17 locales so there is NO locale
surface), or keep MediaNotFound deliberately (favorite's "this" genuinely
failed to resolve as a library item) and document the split. Either way the
favorite pins asserting "could not find the media"
(FavoriteToggleIntentHandlerTests: HandleAsync_NoResolvableItem...,
HandleAsync_UnresolvableDtoStaleLedger_NoWrite_JF785) move with the decision.

VERIFICATION BAR: whichever way it lands, the two branches (idle guard,
resolver-null) must speak the SAME key, pinned in the favorite suite.

## Implementation Notes

Decision taken 2026-10-08: unify on NoMediaPlaying (the filing's first option,
the milestone's consistency direction). Both evidence-door branches in
FavoriteToggleIntentHandler (the JF-629 idle guard and the JF-785 Leg A
resolver-null refusal) moved from MediaNotFound to NoMediaPlaying; the two
debug log lines updated with them ("answering NoMediaPlaying", the sibling
phrasing).

Red-green: the three pins asserting "could not find the media"
(NoResolvableItem, renamed from ..._MediaNotFoundWithoutWriting to
..._NoMediaPlayingWithoutWriting; UnresolvableDtoStaleLedger;
DeletedMidPlayDtoStaleLedger) were updated FIRST and run RED on the unmodified
handler (exactly 3 failures per TFM, Sub-string not found), then the handler
swap made them GREEN (8/8 both TFMs).

The data==null branch KEPT MediaNotFound deliberately: the item resolved and is
playing there, only its user-data row is missing, and the filing's decision
covered the two door branches. A boundary pin
(HandleAsync_ItemResolvedButNoUserData_KeepsMediaNotFound_JF788) now locks the
keep so a future unification sweep cannot flip it as "the family's last
MediaNotFound" without a red test; the open wording question for that branch is
JF-821.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release, both TFMs: 0 warnings 0 errors)
- [x] #2 dotnet test passes (5512/5512 BOTH TFMs on the final state; baseline 5511 + 1 new boundary pin)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean; the ruleset's TreatWarningsAsErrors makes the clean build the proof)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model, template, or model_*.json change)
- [x] #7 E2E test added for new intent or handler logic (N/A-shaped but covered: 4 unit pins in the favorite suite assert the spoken strings on every changed branch; no new intent; no e2e fixture went stale, the favorite case "aggiungi ai preferiti" pins only response_type any, and the two NoMediaPlaying fixture asserts in e2e_it-IT.yaml belong to the Repeat and SetPlaybackSpeed families, both untouched)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings; NoMediaPlaying already existed in all 17 locale files, verified by grep count 17 and validate_locales.py PASS at the final state)
- [x] #9 /simplify passed (4 angles: efficiency clean, reuse clean; applied the redundant-parenthetical trim, the test-doc enumeration trim, and the altitude angle's boundary pin; 2 reasoned skips recorded in the commit)
- [x] #10 /code-review high passed (3 findings: 2 applied as comment-accuracy corrections, 1 filed as JF-822; the finding-2 behavioral residue filed as JF-821)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both evidence-door branches of the favorite toggle now speak NoMediaPlaying,
the same door word the playlist-edit, media-info, and ProgressReporter
loop-mode families already speak: on a session whose now-playing DTO does not
resolve, "what's playing" and the very next "favorite this" no longer disagree
about whether something is playing. The pins moved with the decision (red
proof first: the three failing asserts on the unmodified handler, then green),
the class grew a boundary pin locking the data==null branch's deliberate
MediaNotFound keep, and the verification bar is met: both door branches pinned
on the same key (idle door via NoResolvableItem, resolver-null door via the two
JF-785 tests).

Gates: /simplify 4 angles (efficiency clean, reuse clean, 3 applied, 2 reasoned
skips) and /code-review high (3 findings: 2 comment corrections applied; the
guarded-door consolidation question filed as JF-822 considered-and-rejected
with the re-open trigger; the data==null wording residue filed as JF-821).
Suites: 5512/5512 BOTH TFMs on the final state (baseline 5511 + the boundary
pin); Release build 0 warnings 0 errors; validate_locales.py PASS (NoMediaPlaying
verified present in all 17 locale files, no locale surface touched). No
interaction models, no session-attribute shapes, no production logic beyond the
two string keys and their log lines. Not deployed (worker branch only).
<!-- SECTION:FINAL_SUMMARY:END -->
