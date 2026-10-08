---
id: JF-821
title: >-
  The favorite-toggle data==null branch speaks MediaNotFound for a verifiably
  playing item (open wording question)
status: Done
assignee: []
created_date: '2026-10-08'
labels:
  - ux
  - tech-debt
dependencies:
  - JF-788
priority: low
---

## Description

Filed 2026-10-08 from the JF-788 /code-review high round (its finding 2,
same-turn filing rule).

JF-788 unified favorite-toggle's two evidence-door branches (idle guard,
resolver-null) onto NoMediaPlaying and deliberately STOPPED at the door: the
third no-media branch, the GetUserData null check after the item has already
resolved (FavoriteToggleIntentHandler, the branch carrying the JF-821 pointer
comment), keeps MediaNotFound. The review verified why that keep is not a
settled answer, only a scope boundary:

- On this branch the item IS resolved and IS playing (MediaInfo on the same
  session names it), so BOTH existing no-media strings are semantically false:
  "Nothing is currently playing" is wrong (something is playing) and "Sorry, I
  could not find the media" is wrong (the media was found; only its user-data
  row is missing). The review's scenario: "metti questa nei preferiti" during
  such a track answers "could not find the media" for an item the skill just
  named, the exact misleading sentence JF-788 was filed to remove from the two
  adjacent shapes.
- The in-code parallel the original JF-788 keep-comment cited ("the RateItem
  sibling keeps its own family word on this same shape") does not decide the
  question: RateItem's word on the data==null shape is RatingNoItem ("There is
  nothing playing right now for me to rate."), which is semantically a
  NOTHING-PLAYING sentence, so the sibling precedent actually argues toward
  NoMediaPlaying here, not MediaNotFound. The comment was corrected in JF-788
  to state the scope boundary instead.

THE DECISION TO MAKE: give this branch a semantically honest answer. Options:
(a) a dedicated key shaped like RatingNoItem's apology ("nothing playing for me
to favorite"), which reads naturally even though the item is technically
playing (the user cannot tell the difference; the failure is ours); (b)
NoMediaPlaying (the sibling-consistent least-wrong word); (c) keep MediaNotFound
and document it as the defensive-branch word. The branch is pinned by
HandleAsync_ItemResolvedButNoUserData_KeepsMediaNotFound_JF788
(FavoriteToggleIntentHandlerTests), so any change flips that pin; whichever
way it lands, move the pin and the handler comment's JF-821 pointer together.

Note: the same shape exists in RateItemIntentHandler (its own data==null
branch); if the decision mints a dedicated key, consider whether RateItem's
RatingNoItem already IS that key's answer for its family (it is, which is why
(a) mirrors it) and whether any other userData-family handler has the same
branch.

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no strings or session shapes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: locale response wording only, no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: wording change on an existing handler branch; the moved/added unit pins ARE the coverage; the branch itself is a defensive GetUserData-null shape not reachable via SMAPI fixtures)
- [x] #8 Locale response strings added to all 17 locales (FavoriteNoItem minted in all 17; scripts/validate_locales.py PASS, no new gaps)
- [x] #9 /simplify passed (2 comment-weight findings applied, 2 angles CLEAN, 1 non-blocking note)
- [x] #10 /code-review high passed (4 findings: 1 applied via direction-neutral wording + twin pin, 1 applied comment reword, 1 rejected as the decided trade-off, 1 filed as JF-824)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Worker round 2026-10-09 (worktree agent-a3e33020f268307db).

THE DECISION (made by the orchestrator, option (a), recorded here and in the
handler's branch comment): a dedicated `FavoriteNoItem` key mirroring the
RateItem sibling's RatingNoItem apology shape. Rationale: RateItemIntentHandler
answers the SAME data==null branch shape (item resolved and playing, user-data
row missing) with RatingNoItem, so the userData family gets one answer shape
across its two handlers; the user cannot distinguish our failure modes and the
apology reads naturally either way. Rejected: (b) NoMediaPlaying loses the
apology shape the sibling set the precedent for; (c) MediaNotFound is the
misleading sentence JF-788 was filed to remove. Both rejections live in the
handler comment so a future wording sweep re-reads them before flipping.

Key naming/minting mechanics: ResponseStrings.cs is a generic loader (keys are
string literals at call sites, no constants file exists), so the key was minted
in the 17 `Alexa/Locale/*.json` files, one line each, inserted directly after
`RemovedFromFavorites` in every locale (adjacent to the favorites/rating block,
verified at a uniform relative position in all 17 by the altitude reviewer).
Each translation mirrors that locale's own RatingNoItem phrasing.

DIRECTION-NEUTRALITY (the code-review high F2 application): the branch lives on
the ABSTRACT base shared by MarkFavoriteIntentHandler AND
UnmarkFavoriteIntentHandler, so a single "add to favorites" apology would name
the wrong verb for the remove direction (RatingNoItem escapes this because "to
rate" is direction-free). The minted values therefore name BOTH verbs ("add to
or remove from favorites" / "aggiungere o rimuovere dai preferiti" / ...), and
a twin pin
(`HandleAsync_UnmarkItemResolvedButNoUserData_SpeaksDirectionNeutralApology_JF821`)
locks the Unmark side.

RED-GREEN: the pin was moved FIRST
(`HandleAsync_ItemResolvedButNoUserData_KeepsMediaNotFound_JF788` renamed to
`HandleAsync_ItemResolvedButNoUserData_SpeaksFavoriteNoItemApology_JF821`,
assertion flipped to the new spoken substring) and run against the UNMODIFIED
handler: RED on both TFMs with `Assert.Contains() Failure: Sub-string not
found / String: "Sorry, I could not find the media." / Not found: "nothing
playing right now..."`. Then the handler swap + 17 locale keys went in: the
class passed 9/9 both TFMs; after the review applications 10/10 both TFMs.
`python3 scripts/validate_locales.py`: PASS, no new locale gaps (en-US 309
keys, all 17 locales OK). Full suite after the review applications: 5527/5527
both TFMs (see Final Summary for the exact lines).

GATE DISPOSITIONS:
- /simplify (4 angles): efficiency CLEAN; reuse CLEAN with a non-blocking note
  (the pin's hardcoded English substring matches the family's own convention in
  RateItemIntentHandlerTests, vs FollowMeIntentHandlerTests' localized-assert
  style; left as-is per family convention); simplification findings applied
  (handler comment trimmed, test doc reduced to a pointer at the handler
  comment's rationale); altitude CLEAN on the diff.
- /code-review high (4 findings): F1 (the apology asserts "nothing playing"
  while an item is playing) REJECTED AS THE DECIDED TRADE-OFF: option (a)'s
  rationale explicitly accepts the nothing-playing phrasing because the failure
  is ours and the user cannot distinguish; re-litigating it here would reopen
  the settled decision. F2 direction-blindness APPLIED (see above). F3
  (FavoriteNoItem/RatingNoItem not in the ResponseStringsTests AllExpectedKeys
  ledger) FILED same-turn as JF-824 (the test file is outside this worker's
  surface; severity low: missing-from-one-locale is CI-caught by
  validate_locales.py, handler-key typos are caught by the spoken-substring
  pins; the residual all-locales gap is shared with RatingNoItem today). F4
  (muddled closing clause in the decision comment) APPLIED (reworded).
- Altitude reviewer's stale-mention finding on the JF-788 Done task (line ~73
  presents the data==null keep as final): dispositioned as immutable history;
  JF-821's own record (this file) and the handler comment carry the reversal,
  so the historical task file is left untouched per the no-errata convention.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-09 in worktree agent-a3e33020f268307db (commits on the worktree
line, not merged/pushed/deployed). The favorite-toggle data==null branch now
speaks the dedicated `FavoriteNoItem` apology in all 17 locales instead of the
misleading MediaNotFound, mirroring RateItem's RatingNoItem so the userData
family answers its shared no-user-data shape with one apology form; the values
are direction-neutral ("add to or remove from favorites") because the branch is
shared with UnmarkFavoriteIntentHandler. The old JF-788 boundary pin was moved
with red-green evidence (RED first on the unmodified handler, old string
spoken), renamed to
`HandleAsync_ItemResolvedButNoUserData_SpeaksFavoriteNoItemApology_JF821`, and
a direction twin pin covers the Unmark subclass. RateItemIntentHandler carries
a 2-line pointer comment anchoring the mirror from its side; the handler's
branch comment records the decision, the rationale, and the rejected
alternatives. Verified: red failure output captured on both TFMs; touched class
10/10 both TFMs; scripts/validate_locales.py PASS (no new gaps, en-US 309
keys); full suite 5527/5527 net9.0 + 5527/5527 net10.0 after all review
applications. Gates: /simplify (2 applied, angles otherwise clean) and
/code-review high (F2+F4 applied, F1 rejected as the decided trade-off, F3
filed as JF-824 same-turn). No deploy, no SMAPI, no server access.
<!-- SECTION:FINAL_SUMMARY:END -->
