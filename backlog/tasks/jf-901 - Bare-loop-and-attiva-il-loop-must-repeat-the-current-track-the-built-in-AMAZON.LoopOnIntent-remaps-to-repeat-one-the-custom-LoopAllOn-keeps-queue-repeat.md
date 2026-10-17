---
id: JF-901
title: >-
  Bare 'loop' and 'attiva il loop' must repeat the current track: the built-in
  AMAZON.LoopOnIntent remaps to repeat-one, the custom LoopAllOn keeps queue
  repeat
status: Done
assignee: []
created_date: '2026-10-17 12:31'
updated_date: '2026-10-17 13:30'
labels: []
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
MAINTAINER DECISION 2026-10-17 (option a): bare 'loop' and 'attiva il loop' must repeat the CURRENT TRACK (repeat-one), not the queue. Device evidence: on the real Echo 'attiva il loop' resolves to the BUILT-IN AMAZON.LoopOnIntent (corr 6883f581/4d2f33b6, 2026-10-17 12:21Z) and LoopOnIntentHandler maps it to RepeatMode.RepeatAll (LoopOnIntentHandler.cs line ~68), so at track end the queue advanced to the next album track (corr 4e6ce000) instead of repeating; the announced 'Riproduzione in loop attivata' was RepeatAll semantics. Amazon's own convention for the built-in loop on music skills is loop-THIS-track, so the fix aligns the built-in with its platform semantics. THE CHANGE (handler-side, NO model surface): the intent that LoopOnIntentHandler claims as AMAZON.LoopOn moves to the repeat-ONE application (reuse the LoopSongOnIntentHandler path: either LoopSongOn adds AMAZON.LoopOn to its CanHandle and LoopOn drops it, with the registration-order uniqueness verified (ONE handler per intent, the Registrator order checked, handler-routing tests exist for this class), or LoopOnIntentHandler branches by intent name applying RepeatOne vs RepeatAll); the custom LoopAllOnIntent (the explicit queue-wording samples) keeps RepeatAll unchanged; confirmation wording disambiguated per leg (the song-loop line must exist already via LoopSongOn, check Locale keys; add only if missing, all 17). SIMULATOR/DIVERGENCE NOTE: in profile-nlu 'attiva il loop' still routes to the custom LoopAllOn (our samples win there, built-in wins on device) - the divergence predates this change and is structural; the device path is the decision's target. Full locale-sample consistency is a possible follow-up, NOT in this task. PINS: LoopOn(AMAZON.LoopOn) applies RepeatOne and speaks the song-loop line; LoopAllOn keeps RepeatAll byte-identical; LoopOff clears either; on-device verification after deploy: 'attiva il loop' then track end re-enqueues the SAME item id (log check on PlaybackNearlyFinished).
<!-- SECTION:DESCRIPTION:END -->

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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
EXECUTED on branch jf901 (bc1f211c), merged to main dcbb0d36, pushed, DEPLOYED to minix via LAN IP (active-DLL md5-verified, config survived, probes green: the built-in AMAZON.LoopOnIntent now speaks the RepeatSongEnabled line through the simulator, play pipeline healthy). THE CHANGE (maintainer decision option a, 2026-10-17): LoopSongOnIntentHandler claims the built-in AMAZON.LoopOnIntent beside its same-mode aliases (the shuffle-twin shape: one handler = one mode = one confirmation), applying RepeatOne with the existing RepeatSongEnabled key (present in all 17 locales, nothing minted); LoopOnIntentHandler now owns only the custom LoopAllOnIntent with its RepeatAll application byte-identical; LoopOff clears both unchanged; ApplyRepeatModeAsync and its JF-632/JF-629/JF-890/JF-892 gates untouched, shared by both legs. Exactly-one-owner enforced by the DispatchRoutingTests guard (empty multi-owner allowlist). Red proof both TFMs: with the production edits stashed the four JF-901 pins fail (routing selects the old owner, the queue owner still claims the built-in, the alias row mispairs). Gates (in bc1f211c): 0-warning build both TFMs Debug+Release -warnaserror, 6041/6041 per TFM, validate_locales belt PASS, /simplify 4 agents (3 applied), code-review high 5 findings (4 applied, 1 confirm-only), pa:reflect ALIGNED. FOLLOW-UPS the worker filed as JF-902: the model-layer divergence (bare loop rows still on LoopAllOnIntent in the 4 custom locales), the 13 built-in-only locales losing the repeat-all voice path (the direct trade of the handler-side-only scope, documented in the IntentNames KNOWN GAP comment), and the stale loop-semantics docs mirrors. DEVICE VERIFICATION pending Paolo: 'attiva il loop' then track end must re-enqueue the SAME item id (log check on PlaybackNearlyFinished). gate-exempt: gates ran in the dispatched worker's transcript; commit bc1f211c carries the full Gates record.
<!-- SECTION:FINAL_SUMMARY:END -->
