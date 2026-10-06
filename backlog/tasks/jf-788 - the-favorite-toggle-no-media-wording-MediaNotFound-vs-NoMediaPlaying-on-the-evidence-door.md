---
id: JF-788
title: >-
  The favorite-toggle no-media wording: MediaNotFound where the other three
  guarded families say NoMediaPlaying (the evidence-door shape)
status: To Do
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
