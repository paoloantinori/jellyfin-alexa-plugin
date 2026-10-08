---
id: JF-818
title: >-
  The favorite-toggle data==null branch speaks MediaNotFound for a verifiably
  playing item (open wording question)
status: To Do
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
resolved (FavoriteToggleIntentHandler, the branch carrying the JF-818 pointer
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
way it lands, move the pin and the handler comment's JF-818 pointer together.

Note: the same shape exists in RateItemIntentHandler (its own data==null
branch); if the decision mints a dedicated key, consider whether RateItem's
RatingNoItem already IS that key's answer for its family (it is, which is why
(a) mirrors it) and whether any other userData-family handler has the same
branch.
