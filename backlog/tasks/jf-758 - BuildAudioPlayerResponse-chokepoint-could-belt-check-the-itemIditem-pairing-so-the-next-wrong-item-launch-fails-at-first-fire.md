---
id: JF-758
title: >-
  JF-758 - BuildAudioPlayerResponse chokepoint could belt-check the itemId/item
  pairing so the next wrong-item launch fails at first fire
status: To Do
assignee: []
created_date: '2026-10-04'
labels:
  - playback
  - hardening
dependencies: []
references:
  - >-
    backlog/tasks/jf-750 - PlayArtistSongs-resume-launch-passes-the-wrong-played-item-artistsItems0-instead-of-artistsItemsstartIndex.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-750 /simplify altitude round (2026-10-04, verdict CLEAN on the
fix itself, this is the one optional follow-up it surfaced; the
review-recommendation rule files it the same turn).

CONTEXT: every production caller of
`PlaybackLaunchBuilder.BuildAudioPlayerResponse`
(Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs ~:1972, ~45
sites) passes `itemId` and the metadata `item` as INDEPENDENT arguments; the
chokepoint cannot derive one from the other (it holds no ILibraryManager, the
deliberate null-item contract must survive, and a library fetch on the
PlaybackNearlyFinished pre-fetch hot path would cost Alexa-budget latency for
zero information). Three call-site idioms keep the pairing true by
construction (derive: `itemId = item.Id.ToString()`; fetch:
`item = GetItemById(itemId)`; indexed: both args read `[startIndex]` of the
same collection). JF-750 was the one historical site that drifted (indexed id,
first-element item), proven by a red pin.

THE HARDENING: a belt check at the chokepoint, in the existing
`EnsureLaunchResponse` style (PlaybackLaunchBuilder.cs ~:157: a belt
InvalidOperationException for a "cannot happen" contract break, "firing it is a
contract break, not a runtime condition to handle"), asserting the pairing
when both inputs are present:

  if (item != null && Guid.TryParse(itemId, out var launchedId) && launchedId != item.Id) throw ...

DESIGN CONSTRAINTS (from the JF-750 audit, keep them):
- The compare must be GUID-based, NOT string-based: most sites pass the dashed
  id form but `AplUserEventHandler` ~:316 passes `item.Id.ToString("N")`
  (dashless) as itemId; a string compare would false-fire there.
- `item == null` must skip: the null shape is deliberate
  (PlayIntentHandler ~:63 passes null when only the queue id survives).
- Guid.TryParse failure (a non-GUID token id, if any future site mints one)
  must skip, not throw: the belt guards the pairing of resolvable ids only.

Today the belt would be dead code on all ~45 sites (every non-null site
satisfies the invariant), which is exactly why JF-750 did not add it: it is
hardening against the NEXT drift of this class, not a fix for a live bug, and
the JF-750 pin already locks the one known site. Backing it with a pin that
seeds an id/item mismatch at the chokepoint and asserts the throw.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 /simplify passed (no blocking cleanups remaining)
- [ ] #5 /code-review high passed (no blocking findings remaining or findings applied/tracked)
- [ ] #6 A pin seeding a mismatched (itemId, item) pair at the chokepoint asserts the belt throws; the null-item and dashless-id ("N" format) shapes stay silent
<!-- DOD:END -->
