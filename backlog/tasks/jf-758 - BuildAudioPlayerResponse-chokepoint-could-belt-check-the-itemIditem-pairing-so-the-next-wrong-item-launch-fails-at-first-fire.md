---
id: JF-758
title: >-
  JF-758 - BuildAudioPlayerResponse chokepoint could belt-check the itemId/item
  pairing so the next wrong-item launch fails at first fire
status: Done
assignee: []
created_date: '2026-10-04'
updated_date: '2026-10-05 01:05'
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

## Notes

<!-- SECTION:NOTES:BEGIN -->
DONE 2026-10-05 by the JF-758 worker.

THE BELT: `PlaybackLaunchBuilder.EnsureItemPairsWithLaunchId(itemId, item)`, a
private static guard in the EnsureLaunchResponse style (doc owns the contract,
the three construction idioms, the JF-750 history, and the skip constraints),
called at the entry of BOTH builder members that accept an independent
(itemId, item) pair: the `BuildAudioPlayerResponse` chokepoint (all three
overloads funnel into the belted main one; the belt runs first, before the
JF-687 refusal, the ledger record, the launch-scope write, and the
native-controls VideoApp delegation, so both delivery routes inherit the
verdict) and `BuildVideoAppAudioResponse` (the /simplify altitude round's
catch: the sibling accepts the same pair and two direct call sites bypass the
chokepoint - the builder-internal audiobook launch composition carrying
PlayBook/YesIntent/StartOver, and Resume's book arm). Guid-based compare
exactly per the filing: null item skips (PlayIntentHandler), unparseable id
skips (composite `{guid}|launch:n` / `{guid}|sleep:t` tokens, any future
non-GUID token), dashless "N"-format ids compare equal (the APL carousel tap
path - the carousel list items are BUILT with ToString("N"), so the dashless
form genuinely rides the launch).

THE BELT CAUGHT TWO LIVE CLASSES AT FIRST FIRE (the first full-suite run with
the belt, before any call-site fix): (1) a REAL production wrong-item launch:
`ResumeIntentHandler`'s tail launched a DISPLACED token's stream with the STALE
session item's metadata - the JF-563 guard deliberately treats the displaced
token as authoritative for ROUTING while the tail kept feeding the session
pointer as the metadata item, the exact JF-750 class the filing said could not
happen ("every non-null site satisfies the invariant" was false for this
runtime-state-dependent site; the audit had verified code idioms, not runtime
pairing). Fixed at the site: the tail now resolves the token's item through
the library (StreamTokenCodec.TryGetItemId per the file's one-parser rule, so
composite tokens resolve and are followed too), degrading to the null-item
contract when unresolvable; the code-review round additionally dropped the
foreign session-sourced offset (fallback 2) on the swap, since only the
device-derived stream-relative offset follows the token's own stream. The
pinned test's DELIBERATE routing behavior (token item keeps the flat
AudioPlayer path) is unchanged and now also pins the metadata delta (empty
title on the unresolvable degrade, the token item's name on the resolved twin
added by the review round). (2) Four VideoAppForAudioPerUserTests harness
sites seeded `NewSong().Id.ToString(), NewSong()` (two different songs);
aligned to the derive idiom.

THE CENSUS (the ~45 sites, task-mandated verification): all 45 production
`BuildAudioPlayerResponse` call sites re-derived mechanically (argument-pair
extraction per site) and classified into the three idioms - derive
(`item.Id.ToString()` beside the same item), fetch (`GetItemById(itemId)`),
indexed (both args `[startIndex]` / `[0]` of one collection) - with the
state-dependent tail shapes (token-vs-session) read to their derivations; the
one divergence found is the ResumeIntentHandler displaced-token site above.
The suite green on the final state (5200 both TFMs) is the bulk proof; the
JF-750 family audit remains the recorded census.

PINS: `PlaybackLaunchBuilderLaunchPairingPinTests` (6 facts): mismatch throws
via the string overload (both ids + JF-758 tag in the message), via the
AudioLaunchSource overload (the funnel), via BuildVideoAppAudioResponse's own
branch (VideoApp-capable context so the throw can only come from that
member's belt), and the three silent shapes (null item, dashless "N" id with
a self-shape guard, composite token). RED PROOFS: deleting the chokepoint's
belt call flips exactly the two chokepoint mismatch pins (both TFMs);
deleting the VideoApp sibling's call flips exactly its pin. Sabotage cycles
run and restored in the worker transcript.

Gates: worker Skill simplify (4 agents: reuse - the TryGetItemId swap applied,
belt-vs-codec non-use adjudicated CORRECT since codec reuse would resolve
composites the belt must skip; simplification - dead conjunct + local-var
reorder applied, pin-comment duplication skipped on the repo's comment-heavy
test convention; efficiency - CLEAN, the belt is one Guid parse against a
method that already does config reads, redaction, and URL builds, and the
displaced-token fetch provably cannot fire on the common path; altitude - the
sibling-member belt applied, chokepoint altitude adjudged right with the
filing's recorded reasoning verified in code). Skill code-review high (5
findings, dispositions on the DoD above; the reviewer independently re-audited
every production call site of both belted builders and every direct-builder
test file - no false-fire path found). Reserved JF-764 UNUSED: no real
out-of-scope finding survived the round.

Suites: 5200/5200 net9.0 AND net10.0 on the final state (one full run; derived
pre-change tree 5193 = the JF-551 merge tip's 5191 + JF-752's 2, + 7 new
tests). Release --no-restore -warnaserror: 0 warnings 0 errors. No locale,
model, or speech surface changed; no deploy (the orchestrator's).
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: 0 warnings 0 errors)
- [x] #2 dotnet test passes (5200/5200 net9.0 AND net10.0 on the final state; derived pre-change tree baseline 5193 + 7 new tests: 6 pairing pins + the resolved-arm displaced twin)
- [x] #3 No new compiler warnings introduced (0 warnings under -warnaserror)
- [x] #4 /simplify passed (4 angles via 4 agents: 4 findings applied - dead queueItem==null conjunct dropped, resolve-log-assign reorder, StreamTokenCodec.TryGetItemId swap, the BuildVideoAppAudioResponse sibling belt + its pin; 2 skipped with reasons - pin-file site-local rationale follows the repo's comment-heavy test convention, parse-then-fetch idiom hoists on the third copy; efficiency CLEAN)
- [x] #5 /code-review high passed (5 findings: 3 applied - the foreign fallback-2 offset dropped on the displaced-token swap, the resolved-arm twin test, the caller-count comment corrected; 1 applied as the scratch-runner deletion; 1 DECLINED with reason - the plain-InvalidOperationException throw shape is the filing's recorded EnsureLaunchResponse design, the controller catch turns it into an error response + errorRef, and a refusal-Tell or null-metadata degrade would resurrect the silent-drift failure mode JF-758 exists to kill)
- [x] #6 RED-PROVEN both entries: seeding a mismatched pair throws through the string overload, the AudioLaunchSource overload, and BuildVideoAppAudioResponse's own VideoApp branch (VideoApp-capable context, so no chokepoint re-entry masks it), with both ids in the message; the null-item, dashless "N"-format, and composite-token shapes stay silent (each pinned). Sabotage removing the chokepoint belt call flips exactly the two chokepoint mismatch pins on both TFMs; sabotage removing the VideoApp sibling's call flips exactly its pin.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Worker cycle complete 2026-10-05: the Guid-based launch-pairing belt is live
at both builder entries that accept an independent (itemId, item) pair (the
AudioPlayer chokepoint and the VideoApp-for-audio sibling), red-proven at each
entry by sabotage, with the three mandated silent shapes pinned. At first fire
the belt caught one real production wrong-item launch (ResumeIntentHandler's
displaced-token tail, fixed at the site with the codec-based resolve, the
null-item degrade, and the foreign-offset drop) plus four sloppy test
harnesses (aligned to the derive idiom). Gates simplify (4 agents) and
code-review high run with all findings applied or dispositioned with reasons
(one declined on the filing's recorded throw-shape design); JF-764 unused.
5200/5200 both TFMs; Release 0/0; awaiting the orchestrator's merge.
<!-- SECTION:FINAL_SUMMARY:END -->
