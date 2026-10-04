---
id: JF-732
title: >-
  JF-732 - the AudioPlayer-family now-playing belt: extending the JF-718 state
  roster beyond the VideoApp-Tell family, and the roster's write-detection depth
status: To Do
assignee: []
created_date: '2026-10-03'
labels:
  - playback
  - refusal-contract
  - tech-debt
dependencies:
  - JF-718
references:
  - >-
    backlog/tasks/jf-718 -
    JF-718-residuals-of-the-JF-714-phantom-now-playing-sweep-channel-builder-writes-before-gate-writes-before-build-sites-and-the-gate-extraction.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-718 /simplify (4 angles) and /code-review
high rounds plus the task's own scoping census, carrying the findings judged real
but outside the JF-718 granted surface. Same review-recommendation discipline:
filed the turn they were cut.

Finding 1 (the AudioPlayer-family belt asymmetry, scoping census + code-review
finding 1): the JF-718 state roster
(DeliveredLaunchStateWriteRosterTests) covers ONLY the four Tell-capable
VideoApp-family builders, so roughly two dozen post-build now-playing writes that
call the AudioPlayer family carry NO delivered-launch gate: the handler-side
PlayFavorites/PlayByDecade/PlayLastAdded/PlayByGenre/PlayMoodMusic/PlaySong/
PlayBook intents, most YesIntent confirm arms, AlbumPlayService,
CrossMediaFallback, PodcastEpisodeResolver (the site the code-review round named:
the APL podcast arm delegates to PlayLatestEpisodeAsync, whose post-build writes at
PodcastEpisodeResolver.cs ~133 are the one remaining ungated tail a JF-718-touched
handler reaches), and PlayRadioIntentHandler.StartRadioPlayback. All are
safe-by-construction TODAY (BuildAudioPlayerResponse is throw-or-launch since
JF-699 item 1: a refusal throws, every other return carries a directive), exactly
the belt-not-load-bearing posture the SPEECH roster codifies the other way
(DeliveredLaunchOutputSpeechRosterTests includes BuildAudioPlayerResponse in its
builder family with an EMPTY allowlist by policy, because a certification rots the
moment a builder learns a new non-launch return while a runtime gate cannot). The
state side holds none of that belt: the moment the AudioPlayer chokepoint grows a
capability Tell or any directive-less return, every one of these sites re-opens
the phantom-now-playing class JF-714/JF-718 closed, with a green suite. The work:
either extend the roster family to BuildAudioPlayerResponse plus the
audio-degrading builders and gate every flagged site through
PlaybackLaunchBuilder.AttachNowPlayingIfLaunched, or decide the state belt is
deliberately thinner than the speech belt and document that decision on the
roster's doc.

Finding 2 (the roster's write-detection depth, /simplify altitude finding 1):
DeliveredLaunchStateWriteRosterTests detects the state WRITE only in the scanned
method's own IL (depth 0) while the builder call and the gate may sit in a
one-level same-type helper. The documented boundary exists because the deeper
write detection flags PlayRadioIntentHandler.HandleAsync (builder call direct, the
write inside its StartRadioPlayback helper, no gate reference): a false positive
whose fix (gating PlayRadio's radio-mode writes) sat outside the JF-718 granted
surface. The honest closing shape is both halves in ONE change: gate
PlayRadioIntentHandler.StartRadioPlayback's writes through the helper, then make
write detection helper-deep (WritesNowPlayingState(method) || any SameTypeHelper),
collapsing the ACCEPTED BOUNDARY paragraph to the genuinely reasonable two-level
limit. Note StartRadioPlayback also arms RadioModeState.Enable beside the writes
(the JF-699 comment there already calls an armed-but-dead radio mode the worst
phantom of the family), so the gate should cover that arm too or say why not.

Finding 3 (minor, /simplify altitude finding 2): the roster skips the whole
PlaybackLaunchBuilder owner type, so the builder-internal channel gate
(BuildChannelLaunchResponseAsync's raw HasLaunchDirective block) is pinned only
behaviorally (PlayChannelIntentHandlerTests resolver-null Tell + VideoApp
capability Tell pins). Skipping only the four BuilderMethodNames entries (and
their state machines) instead of the whole type would let the roster structurally
pin the builder-internal gate against future edits; low priority since the
behavioral pins exist and red-prove the property.

<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 AudioPlayer-family belt decision made and landed (the DOCUMENTED THINNER-BELT arm, strengthened: the throw-or-launch contract is PINNED AT THE SOURCE by PlaybackLaunchBuilder.EnsureLaunchResponse, a null-total guard (null response, null body, or no launch directive all throw the actionable contract exception) wrapping each family member's OWN CURRENT terminal returns (the master chokepoint, BuildVideoAppAudioResponse, BuildAudiobookResumeResponse, and the async audiobook wrapper); delegation edges carry no second verdict, the callee owns its return. Decision basis: a scratch IL probe measured the roster-extension arm at 30 flagged sites (~27 distinct methods, 15 files), every conversion a runtime no-op today, for belt-only value; the pin delivers the same future-proofing at one guard plus four wraps and is stronger for the JF-732 scenario (a contract break cannot ship silently; the roster extension would ship green with the gates no-opping). HONEST COVERAGE LIMIT (gate-marker F2, carried in the pin's doc, the roster's SCOPE paragraph, and here): the wraps guard the terminal returns that exist today; a directive-less return grown at a NEW MID-BODY site bypasses its member's terminal wrap and ships silently, and only the per-site gate sweep (the rejected roster-extension arm) covers that class structurally. A new launch-building member IS covered structurally (gate-marker F1's deep rule in PlaybackLaunchBuilderThrowOrLaunchPinTests: every SkillResponse-constructing builder method must call the pin unless it is one of the four documented Tell-capable VideoApp members, name-independent so a fifth or renamed constructing member cannot escape, plus a staleness assert so a rename that strands a family or exempt entry reds). The roster's SCOPE paragraph documents the decision, its reason, the residual, and points at the pin's doc for the removal policy. Two sites still got explicit gates on their own merits, not the belt: PlayRadio's StartRadioPlayback (DoD #2) and the PodcastEpisodeResolver tail, whose three callers include the APL podcast arm that returns BEFORE AplUserEventHandler's shared attach; gating it keeps every write arm of that JF-718-gated handler on a gated write (gate-marker F5 corrected the screen half of the story: the attachScreen hook is currently DEAD WIRING, no caller passes one, and the APL podcast screen actually comes from the chokepoint's JF-623 auto-attach, which only fires on a directive-carrying response; the hook is gated anyway so a future caller cannot un-gate it). JF-732 filing Finding 3 (skip only the four BuilderMethodNames entries instead of the whole owner type) NOT taken: the pin machinery now lives on the builder type too, and the behavioral pins (VideoAppCapabilityGateTests plus the throw-or-launch pin tests) red-prove the property; recorded here as the reason)
- [x] #2 PlayRadio write gate + helper-deep write detection landed together (StartRadioPlayback's queue+item writes ride AttachNowPlayingIfLaunched, which now RETURNS the delivered-launch verdict so the RadioModeState.Enable arm rides the SAME single verdict, the armed-but-dead radio mode gating with the writes; the roster's write probe is helper-deep and the depth-0 boundary collapsed to the two-level limit, matching the speech twin; red-proven: reverting StartRadioPlayback to the raw shape flips the roster naming PlayRadioIntentHandler.HandleAsync via its helper, on both TFMs)
- [x] #3 dotnet test passes both TFMs (5084/5084 net9.0 and 5084/5084 net10.0 on the final post-rework state, -m:1; baseline 5078 + 6 new tests: the 5 throw-or-launch pin tests + the null-body guard pin (which gained the null-RESPONSE line in the rework); Release --no-restore -warnaserror clean)
- [x] #4 /simplify + /code-review high passed (simplify 4 angles ALL APPLIED: the IlCallScanner.CallsAnyDirectlyOrViaHelpers snapshot-predicate hoist used by both roster twins, the scan reorder + helper dedup, the decision prose single-homed on the pin's doc, AttachNowPlayingIfLaunched's bool return collapsing PlayRadio's double verdict, the resolver comment corrected (its 'the one site' claim was FALSE: YesIntentHandler's song-confirm arm reaches CrossMediaFallback.BuildSingleSongResponse with the same ungated shape; the true rationale is the APL arm's pre-attach return), the delegation wrap dropped for the member-owns-its-return principle, Array.IndexOf to Contains; SKIPPED with reasons: unqualified VideoAppLaunchDirective (CS0104 collision with Alexa.NET's own type), single-pass two-name write probe (reorder already bounds it), nested-closure enumeration (6ms measured). code-review high 6 findings ALL APPLIED: the kickoff-stub exemption keyed on Task<SkillResponse> return type, the self-red doc claim corrected (the async wrapper's wrap is belt beyond the structural check), attachScreen gated on the same verdict, the null-total guard, the helper dedup in both twins, and the roster scan core hoisted into DeliveredLaunchWriteRosterScan at the second consumer per the JF-582/634 precedent. GATE-MARKER REWORK ROUND 6 of 6 APPLIED: F3 the guard now catches a null RESPONSE (not just a null body) with the test line added; F2 the mid-body residual admitted at every claim site (pin doc, SCOPE, this file); F1 the DEEP construct rule + staleness assert adopted (the reviewer's named single-definition shape, implemented within the diff's IlCallScanner idiom; the Tell-capable exempt set is the state roster's four VideoApp builders, and the gate-marker round itself inventoried every SkillResponse-returning builder member confirming wrap coverage complete today); F4 the lanes skip compiler-generated non-MoveNext shapes (a future formatting lambda inside a family member no longer false-reds); F5 the attachScreen dead-wiring story corrected at the param doc, the resolver comment, and here; F6 the banned hyphen forms swept from this file's prose. Nothing out of scope, so the reserved JF-741 number stays UNUSED; the rework diff judged below the gate-refresh bar (one guard term, doc honesty, and a test-invariant restructuring implementing the reviewer's own specified design), stated here as the call)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the JF-732 worker on the isolated worktree branch, including the gate-marker rework round. THE DECISION: of the three weighed shapes (roster extension + gate sweep / chokepoint pin / both), the PIN won on measurement; a scratch IL probe put the roster-extension arm at 30 flagged sites across 15 files, every conversion a runtime no-op while the contract holds, while PlaybackLaunchBuilder.EnsureLaunchResponse (a null-total guard wrapping each family member's OWN terminal return; delegation edges carry no second verdict) enforces the throw-or-launch contract the ~30 ungated write sites rest on, at one guard plus four wraps, and converts the filing's "with a green suite" scenario into a loud throw whose message names the mandatory follow-up. The state belt stays deliberately thinner than the speech belt, documented on the roster's SCOPE paragraph with the reason (the speech sweep already existed from JF-699 item 6; the state sweep would have been thirty no-op conversions), the KNOWN RESIDUAL (a directive-less return grown at a new MID-BODY site bypasses its member's terminal wrap and ships silently; only the rejected per-site arm covers that class structurally), and a pointer to the pin's doc as the single home of the coverage limit and the removal policy. THE THREE RESIDUALS: PodcastEpisodeResolver's tail gated (its single-item writes and the attachScreen hook ride one delivered-launch verdict; the APL podcast arm, which returns at the delegation before the caller's shared attach, thereby lands on a gated write like every other write arm of that handler; the rework round corrected the screen half of the story: the hook is currently dead wiring, no caller passes one, and the APL screen actually comes from the chokepoint's JF-623 auto-attach, which only fires on a directive-carrying response); PlayRadio's StartRadioPlayback gated per the DoD pair (writes plus the RadioModeState.Enable arm on ONE verdict via AttachNowPlayingIfLaunched's new bool return, an armed-but-dead radio mode being the worst phantom of the family); the APL podcast arm itself covered BY the resolver gate (it delegates; no caller-side change needed). The roster's write detection went helper-deep with the depth-0 boundary collapsed to the two-level limit, red-proven by reverting PlayRadio to raw writes (the roster names HandleAsync via its helper, both TFMs). Gates: simplify 4 angles applied (7 applied items incl. the false 'the one site' resolver-rationale correction and the IlCallScanner predicate hoist; 3 skips with reasons), code-review high 6 of 6 applied (kickoff-stub exemption keyed on the async return type, self-red doc claim corrected, attachScreen gated, null-total guard, twin dedup, the shared DeliveredLaunchWriteRosterScan core); the gate-marker rework round 6 of 6 applied (null-RESPONSE guard term + test line, the mid-body residual admitted at every claim site, the DEEP construct rule + staleness assert so a fifth or renamed constructing member cannot escape the pin, the compiler-shape lane exemption so a future formatting lambda cannot false-red, the attachScreen dead-wiring correction, and the prose hyphen sweep); JF-741 remains unused. Red proofs for the structural pin run via the Edit tool on both TFMs (a removed BuildAudiobookResumeResponse wrap and a removed master wrap each flip the test naming the member); later re-proofs were denied by the permission classifier (scripted sabotage mutations refused) and argued safe by construction instead, an argument the gate-marker reviewer examined and pronounced SOUND (the only deltas since the proofs narrow exemptions or add invariants, neither of which can un-red the proven constructing-member case; the reviewer also independently inventoried every SkillResponse-returning builder member and confirmed the wrap coverage complete today, and confirmed the resolver co-gate correct and PlayRadio's List cast preserving the by-reference install byte-for-byte). Suites: 5084/5084 both TFMs on the final post-rework state (baseline 5078 + 6), Release --no-restore -warnaserror clean. Production surface changed (PlaybackLaunchBuilder, PlayRadioIntentHandler, PodcastEpisodeResolver); NOT deployed from this branch.
<!-- SECTION:FINAL_SUMMARY:END -->
