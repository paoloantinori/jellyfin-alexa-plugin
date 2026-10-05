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
DONE 2026-10-05 by the JF-758 worker; reworked the same day per the
orchestrator's gate-marker round (four findings, all applied).

THE BELT: `PlaybackLaunchBuilder.EnsureItemPairsWithLaunchId(itemId, item)`,
an internal static guard in the EnsureLaunchResponse style (doc owns the
contract, the three construction idioms, the JF-750 history, and the skip
constraints), called at the entry of BOTH builder members that accept an
independent (itemId, item) pair: the `BuildAudioPlayerResponse` chokepoint
(all three overloads funnel into the belted main one; the belt runs first,
before the JF-687 refusal, the ledger record, the launch-scope write, and the
native-controls VideoApp delegation, so both delivery routes inherit the
verdict) and `BuildVideoAppAudioResponse` (the /simplify altitude round's
catch: the sibling accepts the same pair and two direct call sites bypass the
chokepoint, namely the builder-internal audiobook launch composition carrying
PlayBook/YesIntent/StartOver, and Resume's book arm). The compare is
Guid-based exactly per the filing: null item skips (PlayIntentHandler);
the id resolves through `StreamTokenCodec.TryGetItemId` (the ONE token parser,
per the gate-marker rework), so composite `{guid}|launch:n` /
`{guid}|sleep:t` tokens are JUDGED by the item they name and only a genuinely
unparseable id (unknown suffix, future non-GUID token) skips; dashless
"N"-format ids compare equal, and the APL carousel tap path makes that
constraint live, since the carousel list items are BUILT with ToString("N")
and the dashless form genuinely rides the launch.

THE BELT CAUGHT TWO LIVE CLASSES AT FIRST FIRE (the first full-suite run with
the belt, before any call-site fix): (1) a REAL production wrong-item launch:
`ResumeIntentHandler`'s tail launched a DISPLACED token's stream with the STALE
session item's metadata. The JF-563 guard deliberately treats the displaced
token as authoritative for ROUTING while the tail kept feeding the session
pointer as the metadata item, the exact JF-750 class the filing said could not
happen ("every non-null site satisfies the invariant" was false for this
runtime-state-dependent site; the audit had verified code idioms, not runtime
pairing). Fixed at the site: the tail now resolves the token's item through
the library (StreamTokenCodec.TryGetItemId per the file's one-parser rule, so
composite tokens resolve and are followed too), degrading to the null-item
contract when unresolvable. The pinned test's DELIBERATE routing behavior
(token item keeps the flat AudioPlayer path) is unchanged and now also pins
the metadata delta both ways (empty title on the unresolvable degrade, the
token item's name on the resolved twin the review round added). (2) Four
VideoAppForAudioPerUserTests harness sites seeded
`NewSong().Id.ToString(), NewSong()` (two different songs); aligned to the
derive idiom.

THE DISPLACED-TOKEN OFFSET (the gate-marker rework of the review round's drop):
the first rework zeroed the session-sourced offset (fallback 2) on the swap;
the gate-marker verified at source that the progress writers report the
ACTUALLY-PLAYING item with item-absolute ticks (JF-522), so with a displaced
token the PlayState row most plausibly belongs to the TOKEN item and zeroing
regressed position-correct resumes to 0:00. The honest re-derivation now in
place: on the swap, when the offset is not device-derived, the tail resolves
the offset from the TOKEN item's own sources through the ONE UserData-first
resolver (`DeviceQueueManager.ResolveResumeTicks`: UserData, then the plugin
store, played items at 0), which keeps the common displaced-with-own-position
shape resuming where it left off and reads 0 for a genuinely foreign stale
row; only the device-derived stream-relative offset (fallback 1) is kept
as-is, since it already counts the token item's own stream timeline.

THE CENSUS (the ~45 sites, task-mandated verification): all 45 production
`BuildAudioPlayerResponse` call sites re-derived mechanically (argument-pair
extraction per site) and classified into the three idioms (derive
`item.Id.ToString()` beside the same item, fetch `GetItemById(itemId)`, and
indexed, both args reading one `[startIndex]` / `[0]`), with the
state-dependent tail shapes (token-vs-session) read to their derivations; the
one divergence found is the ResumeIntentHandler displaced-token site above.
The suite green on the final state is the bulk proof; the JF-750 family audit
remains the recorded census. The code-review round independently re-audited
every production call site of both belted builders and every direct-builder
test file with no false-fire path found.

PINS: `PlaybackLaunchBuilderLaunchPairingPinTests` (8 behavioral facts plus
the structural roster): mismatch throws via the string overload (both ids and
the JF-758 tag in the message), via the AudioLaunchSource overload (the
funnel), via BuildVideoAppAudioResponse's own branch (VideoApp-capable context
so the throw can only come from that member's belt), via a composite token
naming another item, and the silent shapes (null item, dashless "N" id with a
self-shape guard, composite token naming the same item, unknown-suffix
unparseable id). STRUCTURAL: the roster pin (the ThrowOrLaunch deep-construct
analogue) walks the builder's declared methods, discovers every LAUNCH member
accepting the pair by shape (itemId + item parameters on a SkillResponse or
Task<SkillResponse> return; the pair-accepting RESOLVER helpers are outside
the rule by design because they take the pair to resolve it), and forces each
discovered member to be rostered and to belt-or-delegate, with an async
kickoff-stub exemption and staleness on the roster.

RED PROOFS: deleting the chokepoint's belt call flips exactly the two
chokepoint mismatch pins (both TFMs); deleting the VideoApp sibling's call
flips exactly its behavioral pin; the gate-marker rework sabotages were
re-run and each flipped exactly its own pin (raw Guid parse instead of the
codec flip the composite-mismatch pin; zeroing instead of the re-derivation
flips the offset-arm test; removing the sibling call flips the roster pin and
the VideoApp behavioral pin). Sabotage cycles run and restored in the worker
transcript.

Gates: worker Skill simplify (4 agents: reuse applied the TryGetItemId swap
and adjudicated the belt's own non-use of the codec CORRECT under the original
raw-parse design, superseded by the gate-marker; simplification applied the
dead queueItem==null conjunct drop and a local-var reorder, skipped the
pin-comment dedup on the repo's comment-heavy test convention; efficiency
CLEAN: the belt is one token parse against a method that already does config
reads, redaction, and URL builds, and the displaced-token fetch provably
cannot fire on the common path; altitude applied the sibling-member belt and
upheld the chokepoint depth with the filing's reasoning verified in code).
Skill code-review high (5 findings, dispositions on the DoD above; no
false-fire path found). Orchestrator gate-marker round (all seven axes
re-verified at source with independent re-execution): four findings, ALL
APPLIED, GM-F1 the offset re-derivation above, GM-F2 the codec-resolving
compare above, GM-F3 the structural roster above, GM-F4 the prose-hyphen
forms fixed. Reserved JF-764 UNUSED: no real out-of-scope finding survived
the rounds.

Suites: measured on the reworked final state (see the DoD). Release
--no-restore -warnaserror: 0 warnings 0 errors. No locale, model, or speech
surface changed; no deploy (the orchestrator's).
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: 0 warnings 0 errors)
- [x] #2 dotnet test passes (5204/5204 net9.0 AND net10.0 on the reworked final state; one full run. Derived arithmetic: pre-change tree 5193 = the JF-551 tip's 5191 + JF-752's 2, plus the first cycle's net +7 and the rework's net +4: 8 behavioral pairing facts + the structural roster + the resolved-arm and offset-arm displaced twins, minus the one replaced composite pin)
- [x] #3 No new compiler warnings introduced (0 warnings under -warnaserror)
- [x] #4 /simplify passed (4 angles via 4 agents: 4 findings applied, namely the dead queueItem==null conjunct drop, the resolve-log-assign reorder, the StreamTokenCodec.TryGetItemId swap in the handler guard, and the BuildVideoAppAudioResponse sibling belt plus its pin; 2 skipped with reasons, namely the pin-file site-local rationale, which follows the repo's comment-heavy test convention, and the parse-then-fetch idiom, which hoists on the third copy; efficiency CLEAN)
- [x] #5 /code-review high passed (5 findings: 3 applied, namely the displaced-token offset handling, whose first shape the gate-marker round then reworked into the honest re-derivation, the resolved-arm twin test, and the caller-count comment correction; 1 applied as the scratch-runner deletion; 1 DECLINED with reason: the plain InvalidOperationException throw shape is the filing's recorded EnsureLaunchResponse design, the controller catch turns it into an error response plus an errorRef, and a refusal-Tell or null-metadata degrade would resurrect the silent-drift failure mode JF-758 exists to kill)
- [x] #6 RED-PROVEN, per site: seeding a mismatched pair throws through the string overload, the AudioLaunchSource overload, BuildVideoAppAudioResponse's own VideoApp branch (VideoApp-capable context, so no chokepoint re-entry masks it), and a composite token naming another item, with both ids in the message; the null-item, dashless "N"-format, composite-same-item, and unknown-suffix shapes stay silent (each pinned). Sabotage cycles (run and restored, both TFMs): removing the chokepoint belt call flips exactly the two chokepoint mismatch pins; removing the VideoApp sibling's call flips its behavioral pin AND the roster pin; a raw Guid parse instead of the codec flip flips the composite-mismatch pin; zeroing instead of the offset re-derivation flips the offset-arm test.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Worker cycle complete 2026-10-05, including the orchestrator gate-marker rework:
the launch-pairing belt (codec-resolving, Guid-based, null-item and
unparseable-id skips) is live at both builder entries that accept an
independent (itemId, item) pair, structurally rostered against future
pair-accepting launch members, and red-proven at each entry by sabotage. At
first fire the belt caught one real production wrong-item launch
(ResumeIntentHandler's displaced-token tail, fixed at the site with the
codec-based resolve, the null-item degrade, and the honest offset
re-derivation from the token item's own sources) plus four sloppy test
harnesses (aligned to the derive idiom). Gates simplify (4 agents), code
review high (5 findings dispositioned), and the orchestrator gate-marker
round (4 findings, all applied) are closed with reasons recorded; JF-764
unused. 5204/5204 both TFMs; Release 0/0; awaiting the orchestrator's merge.
<!-- SECTION:FINAL_SUMMARY:END -->
