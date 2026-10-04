---
id: JF-732
title: >-
  JF-732 - the AudioPlayer-family now-playing belt: extending the JF-718 state
  roster beyond the VideoApp-Tell family, and the roster's write-detection depth
status: Done
assignee: []
created_date: '2026-10-03'
updated_date: '2026-10-04 03:27'
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
Closed by the orchestrator after the full cycle including a rework round: worker commits 60e9cb53 + 2d92ce60, merged as 9c6f3215. The AudioPlayer-family throw-or-launch belt: EnsureLaunchResponse pins the family's contract at the source (the null-total guard total against a null RESPONSE too after the rework, wrapping each family member's own terminal return; a directive-less builder return throws naming the follow-up), chosen over the 30-site roster arm on a scratch IL measurement. The rework landed all 6 gate-marker findings (the deep single-definition rule - every SkillResponse-constructing builder method must call the pin unless Tell-capable, name-independent with the staleness assert - adopted over the closed array; the mid-body residual admitted at every claim site; the null-response guard; the formatting-lambda lane exemption; the attachScreen dead-wiring documented to the real JF-623 mechanism; the prose sweep). The three residuals from the filing gated (the resolver tail with its hook, the APL podcast arm via that gate, PlayRadio's writes + radio-state arm on one verdict via AttachNowPlayingIfLaunched's bool return). The refused final-shape re-proof's by-construction substitute verified SOUND by the orchestrator reviewer (the exemption narrowing strictly monotone, both proven mutations outside the exempted set). Worker gates green on both rounds (the rework's gate-skip justified: reviewer-specified mechanical edits with the new invariant read end to end against the four construction sites); the orchestrator gate-marker verified all five axes with the full member inventory. JF-741 unused. Suites: worker 5084/5084 both TFMs on the exact final state (Release -warnaserror clean), orchestrator independent 5084/5084, merged-tree 5084/5084 both TFMs exit 0 on both split legs. Production surface changed (PlaybackLaunchBuilder, PlayRadioIntentHandler, PodcastEpisodeResolver): deployed in the batched post-closure deploy with JF-721.
<!-- SECTION:FINAL_SUMMARY:END -->
