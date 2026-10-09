---
id: JF-822
title: >-
  Consolidate the guarded evidence door into one shared owner (considered and
  rejected unless a third drift lands)
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - tech-debt
dependencies:
  - JF-788
priority: low
---

## Description

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-08 from the JF-788 /code-review high round (its finding 3,
same-turn filing rule), recording a REJECTED alternative so the decision is
durable and re-openable, the JF-785 LEG 4 / JF-789 precedent.

The observation: the guarded evidence door (HasCurrentPlaybackEvidence guard +
tail-refused ResolveCurrentPlayingItem + the NoMediaPlaying tell) is hand-copied
per family (PlaylistEditHandlerBase.ResolveCurrentItem, MediaInfoIntentHandler,
ProgressReporter.ApplyRepeatModeAsync, FavoriteToggleIntentHandler). JF-785
unified the mechanism and JF-788 the wording, both per copy; a shared owner of
guard + resolve + tell would make a third drift structurally impossible.

WHY REJECTED (verified during JF-788's simplify altitude round):

- The mechanism layer is ALREADY shared and machine-enforced where it matters:
  the ONE predicate (exactly the four guarded call sites) plus the resolver's
  allowLedgerTailAnswers:false tail refusal, with GuardedResolverTailRosterTests
  (the IL-scan precedent, mutation-proven in JF-785) failing the build if a
  guarded family regresses. What drifts is only the tell wording, and wording
  is now test-pinned per family (JF-788 added the favorite pins; the siblings
  already had theirs).
- The four doors differ structurally, so one helper cannot cover them without
  forced rewrites: PlaylistEditHandlerBase.ResolveCurrentItem returns BaseItem?
  with the tells living in its two leaf handlers; MediaInfoIntentHandler
  inserts the DTO display fallback between resolver-null and the tell (its
  resolver-null branch can still answer the informational DTO, the exact
  behavior the JF-788 filing cites as correct); ProgressReporter logs via
  _logger + label; favorite wraps in Task.FromResult. A helper covering only
  the guard branch would not even deliver the no-drift guarantee, since the
  resolver-null legs are the divergent ones.
- The house stance on the strings is documented (PlaybackLaunchBuilder,
  BuildVideoAppTransportRefusal doc): plain-text-only keys stay hand-written
  per site; the shared-shape/per-family-wording split is deliberate.

RE-OPEN TRIGGER: a third unification task against these four families (after
JF-785 mechanism and JF-788 wording), or a new fifth guarded family
copy-pasting the door. At that point weigh the forced rewrites above against
the drift cost; the likely shape is a guarded-door helper returning a
result discriminated union (item / refused-with-tell) rather than a tell
emitter, so MediaInfo's DTO fallback survives.
<!-- SECTION:NOTES:END -->
