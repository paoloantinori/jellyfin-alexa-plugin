---
id: JF-699
title: >-
  JF-699 - JF-693 declined altitude findings: pipeline-level refusal
  translation, the locale-threading long tail, and a structural scan pin for the
  delivered-launch gates
status: Done
assignee: []
created_date: ''
updated_date: '2026-10-02 15:03'
labels:
  - streaming
  - robustness
  - code-quality
dependencies: []
references:
  - >-
    backlog/tasks/jf-693 -
    JF-687-residuals-handler-level-deliveries-and-refusal-Tell-overwrites-outside-the-builder.md
  - >-
    backlog/tasks/jf-687 -
    Launch-side-empty-secret-mints-dead-URLs-the-five-PlaybackLaunchBuilder-sites-mint-with-unchecked-StreamTokenSecret.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-693 /simplify altitude round. JF-693 fixed the four
dispatched residuals per-site (the smallest-change-per-site mandate); this task carries the
deeper mechanisms the altitude review proposed and JF-693 deliberately declined as out of
scope:

1. PIPELINE-LEVEL REFUSAL TRANSLATION (the big one): the JF-687/JF-693 refusal contract is
   enforced per builder delivery point (return the Tell) and per handler tail (gates on
   HasLaunchDirective, locale threading, state-write ordering, the sleep-timer guard).
   The deeper alternative the repo already has precedent for: make the builder's five
   StreamTokenSecretRefusal call sites throw a typed StreamTokenNotConfiguredException and
   translate it ONCE in RequestPipeline exactly like SkillWarmingUpException (RequestPipeline
   already translates warming; every request funnels through it). Handler tails then never
   run on a refusal, which deletes the announce-overwrite gates, the phantom-state orderings,
   the locale threading seam, and the sleep-timer handler-level guard as CODE. Cost of the
   current shape: the invariants are conventions each new handler must know; nothing
   structural stops the next handler from reintroducing the defect classes. Mind: this
   re-architects JF-687's deliberate response-return contract (5 builder sites + the 11
   JF-687 pins + the JF-693 pins), so it is a design task, not a refactor.
2. LOCALE-THREADING LONG TAIL: only ~10 of ~53 BuildAudioPlayerResponse call sites pass a
   locale after JF-693; the refusal's language still depends on each caller remembering the
   param. Item 1 deletes the seam; short of it, a validator/scan pin (the WarmingGateCoverageTests
   or JF-689 IlCallScanner idiom) should fail on any guarded-builder call without a locale.
3. STRUCTURAL SCAN PIN for the delivered-launch contract: an assembly-scan pin asserting
   (a) AudioPlayerPlayDirective construction exists ONLY in PlaybackLaunchBuilder and the
   guarded SleepTimer re-issue sites (the "ONE out-of-builder mint" invariant is prose
   today), and (b) every post-builder OutputSpeech write in handler code sits behind
   HasLaunchDirective / AttachAnnounceIfLaunched (known ungated-but-benign-today sites:
   FollowMeIntentHandler ~182, RecommendIntentHandler ~219, PlayRadioIntentHandler ~397, all
   static-URL responses).
4. DIRECTIVE-SNIFF FOLDS: AlbumPlayService ~732 and PlaySongIntentHandler's
   SwapOntoAnnounceVehicleAsync each hand-roll a VideoApp-only variant of the
   HasLaunchDirective predicate (deliberately VideoApp-scoped for the JF-625 progressive
   vehicle swap, so the fold must keep that scoping explicit); consider a shared
   HasVideoAppLaunchDirective beside HasLaunchDirective so the four idioms collapse to two
   named ones. Code-review finding 6 (same round): the announce adapters could collapse
   further - a string-taking AttachAnnounceIfLaunched overload would absorb
   CrossMediaFallback.ApplyAnnouncement's adapter role and the ~5 hand-rolled
   PlainTextOutputSpeech constructions at the gated announce sites (declined in JF-693 as
   ceremony: the IOutputSpeech form serves the SSML-bearing callers, e.g. YesIntent's
   BuildOutputSpeech paths).
5. MUSIC-PATH PHANTOM STATE (JF-693 code-review finding 3, declined for scope): the modal
   MUSIC play paths still write session.NowPlayingQueue/FullNowPlayingItem, SetQueue, and
   QueueContinuationStore.Set BEFORE a builder call that CAN refuse: BuildAudioPlayerResponse's
   native-controls delegation redirects plain GetStreamUrl launches to the token-gated
   video-audio URL when NativeControlsForAudio/VideoAppForAudio is on, so a seek-mode user
   with an empty secret gets the full phantom-state defect class on "play artist/album/song/
   playlist". Sites: CrossMediaFallback.BuildArtistSongsResponseAsync (~372-383, SetQueue at
   376), CrossMediaFallback.BuildSingleSongResponse (~449-456; the JF-440 ONE single-song
   shape shared with PlaySongIntentHandler - coordinate with that handler's owner),
   AlbumPlayService album path (~670-714, SetQueue at 674) and playlist path (~980-1035,
   SetShuffledQueue 988 / SetQueue 999). NOTE: the reordering is only safe AFTER the JF-693
   CopySurvivingStores fix (SetQueue now carries the last-played record); reordering without
   it reintroduces the finding-1 ledger wipe on every music launch.
6. CORRECTED PREMISE for item 3(b)'s site list: FollowMeIntentHandler ~182,
   RecommendIntentHandler ~219, and PlayRadioIntentHandler ~397 are NOT benign: their
   static-URL launches reach the same native-controls delegation, so their ungated
   OutputSpeech writes can speak over a JF-687 refusal Tell for seek-mode users. The same
   reachability applies to any future "static URL so it never refuses" assumption - only
   the token marker in the DELIVERED url (what StreamTokenSecretRefusal checks) decides.

GATE-MARKER TAIL (2026-10-02, orchestrator review of commit 65821618, 5 findings): all
six scrutiny axes verified clean (the pipeline catch sits below every swallowing catch
with the controller and simulator both dispatching through ExecuteAsync; event requests
get the speechless keep-alive and StreamTokenNotConfigured exists in all 17 locales;
happy-path response shapes byte-identical, the deleted verdict wrappers tautologies;
the FollowMe reorder safe via CopySurvivingStores carrying the maps; the F3 ledger move
correct; the roster scans enumerate the state-machine bodies). F3 APPLIED in the tail
(the progressive qualifier block regained the HasLaunchDirective gate JF-693 had around
it; unreachable today but the belt the comment claims); F4 APPLIED as a comment (the
PlaybackController CommandIssued classification documented as deliberate, same response
shape the happy path produces there); F1 FILED as JF-712 (PlaybackNearlyFinished
advances recovery/queue state before the token-gated build - the reachable event
refusal - leaving a phantom pointer and zeroed position; derive-then-commit vs
compensate-on-refusal needs its own design and pin); F2 FILED as JF-714 (five
screenless-Tell video sites still write NowPlaying state for launches that will not
happen on Dots; the StartOver gate pattern plus one pin); F5 FILED as JF-713 (the
PlayPlaylist shuffle commit-before-build residual closed via snapshot derive-then-commit).
Independent suite 4931/4931 both TFMs on the worker commit; filtered classes 254/254
after the tail edits.
<!-- SECTION:DESCRIPTION:END -->

## Item 1 Design (written BEFORE coding, per the sequencing mandate)

**Contract change.** The five builder delivery points (BuildVideoAppLaunchResponse sync,
BuildVideoAppLaunchResponseAsync, BuildAudioPlayerResponse, BuildVideoAppAudioResponse,
BuildAudiobookResumeResponse) and the sleep-timer re-issue arm stop RETURNING the refusal
Tell and start THROWING a new typed `StreamTokenNotConfiguredException`
(Alexa/Exceptions/, beside SkillWarmingUpException; sealed; message names the condition;
carries NO url so no log path can ever leak a minted token). The one translation site is
RequestPipeline.ExecuteAsync, mirroring the SkillWarmingUpException catch that already
sits there: log one error line, set the response, let the response interceptors run.
`StreamTokenSecretRefusal(sourceUrl, locale)` (returns SkillResponse?) becomes
`EnsureStreamTokenDeliverable(sourceUrl)` (void, throws) in the same class, same
predicate (empty secret AND the ApiBaseUri-derived marker), same placement at each
delivery decision; the JF-682 no-snapshot rule is unchanged (one read in the method tail
that assembles the response).

**JF-687 response-return shape.** Replaced, not kept: the refusal Tell's construction
moves from six per-site returns to the single pipeline site. The no-overblock half of
JF-687 (static Jellyfin URLs, live-TV resolver URLs never refused) is unchanged: same
predicate at the same points. The 11 JF-687 builder pins and the JF-693 handler pins
change shape accordingly (pins section below).

**Locale.** The pipeline recovers the locale from the request exactly like the warming
translation: `BaseHandler.GetLocalePublic(requestContext.SkillRequest)`. Verified: that
is the same source the warming catch uses (RequestPipeline.cs:100), and both entry
points (AlexaSkillController and SimulatorController) funnel every request through
`_pipeline.ExecuteAsync`, so the request locale is always in hand. This is at least as
good as the per-call-site threading, which in production always threaded the request
locale or the announce locale (the JF-687 F2 fold's own comment: the announce is already
spoken in the request's language).

**Event requests (found during design; the JF-507 INVALID_RESPONSE lesson applied).**
The refusal CAN fire on an AudioPlayer EVENT request: PlaybackNearlyFinished's
rate-continuity enqueue mints atempo (token-gated) URLs through
BuildAudioPlayerResponse. A Tell with outputSpeech on an event response is rejected by
Amazon ("Response may not contain an outputSpeech", the 2026-09-06 incident), so the
translation answers `BaseHandler.BuildKeepAliveResponse()` when
`BaseHandler.IsEventRequest(requestContext.SkillRequest)`, Tell otherwise (the same
event-aware degrade AlexaSkillController.DegradeForEventRequest applies). This also
fixes a latent JF-687-era defect: today a builder-returned refusal Tell on an event
request would be INVALID_RESPONSE'd on-device.

**What item 1 deletes as code (verified against the merged tree, post JF-690/JF-695):**
1. The four refusal-only `locale` params (BuildAudioPlayerResponse,
   BuildVideoAppAudioResponse, BuildAudiobookResumeResponse,
   BuildAudiobookVideoAppLaunchResponseAsync) + every call-site `locale:` arg feeding
   them + the `locale ?? announceLocale` folds (item 2's seam; builders whose locale
   serves OTHER strings (the capability Tell on BuildVideoAppLaunchResponse/Async,
   BuildEpisodeLaunchResponseAsync, BuildChannelLaunchResponseAsync) keeps theirs.
2. The sleep-timer arm's refusal-response handling (`is { } refusedReplay` return):
   becomes the throwing guard call; the cancel branch and RecordReissueLedger split from
   JF-693's F2 rework are untouched.
3. The JF-693 verdict wrappers (`if (!HasLaunchDirective(response)) return response;`
   and the state-gate positives) at PlayBook x3, YesIntent x2+state-gate, SetPlaybackSpeed,
   StartOver, ResumeIntent book arm, LaunchRequestHandler queue-resume, BaseHandler
   HandleFuzzyMiss qualifier block: the builder families these sites consume
   (AudioPlayer/VideoAppAudio/AudiobookResume) are THROW-OR-LAUNCH post-item-1 (each
   either refuses by throwing or always emits exactly one launch directive (verified by
   reading all three bodies), so the wrappers are tautologies and flatten to
   straight-line build-then-write. The announce writes at flattened sites route through
   AttachAnnounceIfLaunched (which KEEPS its internal HasLaunchDirective check as the
   free belt against future non-launch builder returns, e.g. a capability Tell added
   inside BuildAudioPlayerResponse later).
4. The JF-693 refusal-response pins' Tell assertions at handler level (pins now assert
   the throw + skipped tails).

**What item 1 does NOT delete:** the in-builder announce gates
(BuildEpisodeAudioLaunch, BuildAudiobookVideoAppLaunchResponseAsync) stay as the same
free belt (one shared predicate each, zero cost, protects future builder shapes);
ApplyAnnouncement's gate semantics ride into the item-4 string overload.

**Pins (red-green throughout):**
- Builder pins (PlaybackLaunchBuilderStreamTokenSecretTests): the seven ConfigTell pins
  become Throws pins on the new exception at the same five delivery points + the
  sleep-timer arm; the four no-overblock/control pins stay unchanged; the four JF-693
  ThreadedLocale twins MOVE to pipeline level (request locale now drives the Tell).
- Pipeline pins (new, in SkillWarmingUpTests' ExecuteViaPipelineAsync idiom or
  PipelineTests): intent-request refusal -> localized Tell (it-IT and en-US), event
  request -> keep-alive (no speech), handler tail after the builder call never runs,
  response interceptors still run after the translation.
- Handler pins (JF-693 refusal pins): flip to `Assert.ThrowsAsync` + the no-phantom-state
  / no-persist assertions (which now prove tails skipped structurally).
- Red proofs: pipeline catch removed -> pipeline pins flip (exception escapes); a
  state-write reorder reverted -> phantom-state pin flips; the structural scan pin is
  self-red (removing a gate reference flips it).

## Item dispositions after the item-1 re-read (mandated re-read of items 2-6)

- ITEM 2 (locale-threading long tail): DELETED BY ITEM 1 as predicted: the seam is the
  four refusal-only locale params, all removed; the ~10 handler-side `locale:` args
  feeding them go with them. The proposed call-site validator pin is MOOT (no seam left
  to validate). Recorded here rather than silently dropped.
- ITEM 3(a) (AudioPlayerPlayDirective construction only in builder + sleep sites):
  ALREADY EXISTS as AudioPlayerPlayConstructionRosterTests (JF-631), discovered during
  design: the construction-site roster + RecordLastPlayed/RecordLaunchBase presence
  checks are exactly this invariant. NOT reimplemented; the moot note records the
  existing pin as the owner.
- ITEM 3(b) + ITEM 6 (post-builder OutputSpeech writes; corrected premise): the scan pin
  lands NEW (DeliveredLaunchOutputSpeechRosterTests, the IlCallScanner idiom): every
  plugin method that BOTH calls a launch-builder family member AND writes
  ResponseBody.OutputSpeech must reference a gate token (HasLaunchDirective /
  AttachAnnounceIfLaunched / the item-4 HasVideoAppLaunchDirective) directly or via a
  same-type helper, or sit on an explicit allowlist with a reason. The item-6 sites
  (FollowMe ~182, Recommend ~219, PlayRadio StartRadioPlayback ~397) are safe by
  construction post-item-1 (their builders throw-or-launch) AND get the belt gate added
  (writes routed through AttachAnnounceIfLaunched), so the allowlist stays EMPTY for
  handler code: a future ungated site fails the scan loudly. Both facts (by-construction
  safety + belt) recorded.
- ITEM 4 (directive-sniff folds): applies. HasVideoAppLaunchDirective lands beside
  HasLaunchDirective (VideoApp scoping explicit in the name); AlbumPlayService's album
  vehicle swap and PlaySongIntentHandler.SwapOntoAnnounceVehicleAsync fold onto it; the
  string-taking AttachAnnounceIfLaunched overload absorbs
  CrossMediaFallback.ApplyAnnouncement (deleted; its 3 call sites call the builder
  overload directly).
- ITEM 5 (music-path phantom state): STILL NEEDED post-item-1 (the exception fires AT
  the builder call; writes BEFORE the call still land). Reorder = builder call first,
  then the session/SetQueue/QueueContinuation writes, safe because of JF-693's
  CopySurvivingStores carry (LastPlayed id+route+stamp). Sites: CrossMediaFallback
  .BuildArtistSongsResponseAsync, .BuildSingleSongResponse, AlbumPlayService album path,
  playlist path (non-shuffle fully; the shuffle branch keeps SetShuffledQueue before the
  builder because firstItem is derived FROM the shuffled queue; recorded residual), and
  the adjacent same-class sites found in the merged tree: PlaySongIntentHandler's inline
  single-song path (session writes before the builder) and Recommend/PlayRadio's
  pre-builder session writes (Recommend's queue/FullNowPlayingItem, PlayRadio's
  queue/FullNowPlayingItem/RadioModeState.Enable - RadioModeState.Enable is the worst
  phantom: a refused radio start must not arm radio continuation).

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (plugin + test projects, both TFMs, 0 errors 0 warnings on the final state)
- [x] #2 dotnet test passes (full recipe, both TFMs: 4931/4931 net9.0 AND net10.0; baseline at the merge commit measured 4925/4925 via a tagged-stash run, so the delta is exactly +10 new pins − 4 moved builder ThreadedLocale twins)
- [x] #3 No new compiler warnings introduced (grep-clean build output; the CS0168 in a REDPROOF shape was fixed before the final state)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A: no session-attribute shape touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU fixtures updated if interaction model changed (N/A: no interaction model change; the refusal reuses the JF-687 key)
- [x] #7 E2E added for new intent/handler logic (N/A: no new intent; pipeline translation + reorders pinned by unit pins, empty-secret state not safely reachable on the live server)
- [x] #8 Locale strings in all 17 locales (N/A: reuses StreamTokenNotConfigured, already in all 17 from JF-687)
- [x] #9 /simplify passed (4 agents: reuse/simplification/efficiency/altitude; dispositions in the Final Summary)
- [x] #10 /code-review high passed (6 findings: 4 applied in-code, 1 applied as a documented boundary, 1 filed as JF-708; dispositions in the Final Summary)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 65821618 (base corrected early by merging main per the coordinator's instruction) + gate-marker tail d45f2fb1, merged as f87c89d6, plus the simplify-round doc-home fix d5c691a1. The refusal contract re-architected: StreamTokenNotConfiguredException at the five builder delivery points plus the sleep-timer re-issue arm, translated once in RequestPipeline (locale from the request; event requests get the speechless keep-alive per JF-507, fixing a latent INVALID_RESPONSE in the old builder-returned Tell); handler tails never run on a refusal (the locale-threading seam, announce-overwrite gates, and phantom-state orderings deleted as code across 13+ reordered sites); HasVideoAppLaunchDirective folded beside HasLaunchDirective; the string-taking AttachAnnounceIfLaunched absorbed CrossMediaFallback.ApplyAnnouncement; two empty-allowlist roster pins. Worker gates green (simplify 13 applied; code-review high all 6 applied incl. the 13-site reorder extension and the RecordLastPlayed-after-delegation move; JF-708 filed). Orchestrator gate-marker verified all six scrutiny axes clean; 5 findings dispositioned same-turn (F3 gate restore + F4 classification note applied in tail d45f2fb1; JF-712 the NearlyFinished phantom pointer, JF-713 the shuffle commit, JF-714 the five screenless-Tell sites, all filed with fix directions and pin requirements). The closure-gate /simplify round over the tail applied the IsEventRequest doc-home fix. Suites: worker 4931/4931 both TFMs, orchestrator independent 4931/4931 on the worker commit, filtered 254/254 after the tail, merged-tree 4933/4933 both TFMs exit 0. Production surface changed (pipeline, builder, 13+ handlers): deploying in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
