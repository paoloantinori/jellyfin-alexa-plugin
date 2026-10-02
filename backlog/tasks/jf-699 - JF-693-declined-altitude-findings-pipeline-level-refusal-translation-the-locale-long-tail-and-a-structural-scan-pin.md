---
id: JF-699
title: >-
  JF-699 - JF-693 declined altitude findings: pipeline-level refusal translation,
  the locale-threading long tail, and a structural scan pin for the delivered-launch gates
status: Done
priority: low
labels:
  - streaming
  - robustness
  - code-quality
references:
  - backlog/tasks/jf-693 - JF-687-residuals-handler-level-deliveries-and-refusal-Tell-overwrites-outside-the-builder.md
  - backlog/tasks/jf-687 - Launch-side-empty-secret-mints-dead-URLs-the-five-PlaybackLaunchBuilder-sites-mint-with-unchecked-StreamTokenSecret.md
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
Landed 2026-10-02 (worker on the JF-699 dispatch, base corrected to include the
JF-695/JF-690/JF-700 merges per the coordinator's mid-run instruction).

ITEM 1 (the spine): the JF-687 empty-secret refusal is now a TYPED exception,
StreamTokenNotConfiguredException (Alexa/Exceptions/, the SkillWarmingUpException
precedent; carries no URL so no log path can leak a minted token), thrown by the
guard renamed EnsureStreamTokenDeliverable at the FIVE builder delivery points plus
the sleep-timer re-issue arm, and translated ONCE in RequestPipeline's second
catch: log (intent/eventType/corr), SkipColdLibraryWork, then the localized Tell
with the locale from BaseHandler.GetLocalePublic(requestContext.SkillRequest) (the
same source the warming translation uses; both entry points, controller and
simulator, funnel through ExecuteAsync). EVENT REQUESTS get the speechless
keep-alive (BaseHandler.IsEventRequest), because the refusal is reachable on
AudioPlayer events (PlaybackNearlyFinished's rate-continuity enqueue mints atempo
token-gated URLs) and outputSpeech on an event response is INVALID_RESPONSE'd (the
JF-507 lesson; the pre-JF-699 builder-returned Tell was latently invalid there).
Deleted as code, as the task predicted: the four refusal-only locale params and
every call-site arg feeding them; the sleep-timer arm's refusal-response handling;
and the JF-693 verdict wrappers (the builder families handlers consume are
throw-or-launch: verified each of BuildAudioPlayerResponse / BuildVideoAppAudioResponse
/ BuildAudiobookResumeResponse refuses by throwing or always emits exactly one
launch directive). The wrappers flattened at PlayBook x3, YesIntent x2 + the
PlayBook state gate, SetPlaybackSpeed (incl. the JF-693 F3 no-directive log),
StartOver kept its gate NON-flattened with the documented reason (its episode arm
routes through BuildEpisodeLaunchResponseAsync, which CAN answer the screenless
capability Tell), ResumeIntent, LaunchRequestHandler, PodcastEpisodeResolver, and
BaseHandler.HandleFuzzyMiss (the qualifier write now routes through
AttachAnnounceIfLaunched). The in-builder announce gates stayed as the free belt,
with HasLaunchDirective's doc re-framed from refusal protection to belt.

ITEM 2 (locale long tail): DELETED BY ITEM 1 as predicted, explicit note here
rather than silent deletion: the seam WAS the four refusal-only locale params;
all removed, all call-site args with them; the proposed builder-call validator pin
is moot (no seam left). The builders whose locale serves OTHER strings (the
capability Tell: BuildVideoAppLaunchResponse/Async, BuildEpisodeLaunchResponseAsync,
BuildChannelLaunchResponseAsync) keep theirs. The four JF-693 ThreadedLocale
builder twins moved to pipeline level (request locale drives the translated Tell;
pinned it-IT and en-US).

ITEM 3(a): ALREADY EXISTED, discovered during design: AudioPlayerPlayConstruction
RosterTests (JF-631) is exactly this invariant (construction-site roster +
RecordLastPlayed/RecordLaunchBase presence). Not reimplemented; recorded as the
owner.

ITEM 3(b) + ITEM 6: the new DeliveredLaunchOutputSpeechRosterTests (the
IlCallScanner idiom) flags any plugin method that BOTH calls a launch-builder
family member AND writes ResponseBody.OutputSpeech unless it references a gate
token (HasLaunchDirective / HasVideoAppLaunchDirective / either
AttachAnnounceIfLaunched overload) directly or via a one-level same-type helper;
the allowlist is EMPTY BY POLICY (a safe-by-construction certification would rot
when a builder learns a new non-launch return; a runtime gate cannot). The item-6
sites (FollowMe ~182, Recommend ~219, PlayRadio StartRadioPlayback ~397) are safe
by construction post-item-1 (their builders throw-or-launch) AND got the belt
added; both facts recorded in-code. SELF-RED proven: reverting FollowMe's write to
the raw assignment flips the scan by name, both TFMs.

ITEM 4: HasVideoAppLaunchDirective landed beside HasLaunchDirective (VideoApp
scoping explicit in the name; AlbumPlayService's album vehicle swap and PlaySong's
SwapOntoAnnounceVehicleAsync fold onto it, zero hand-rolled sniffs remain); the
string-taking AttachAnnounceIfLaunched absorbed CrossMediaFallback.ApplyAnnouncement
(deleted; its three call sites call the builder overload directly).

ITEM 5 (music-path phantom state): STILL NEEDED post-item-1 (the exception fires
AT the builder call; writes BEFORE the call still land), implemented at the task's
four sites (CrossMediaFallback.BuildArtistSongsResponseAsync + BuildSingleSongResponse,
AlbumPlayService album + playlist paths; the shuffle branch keeps SetShuffledQueue
before the build because firstItem derives FROM the shuffled queue, recorded
residual) PLUS the /code-review finding-1 extension to the thirteen same-class
sites the reviewer enumerated: PlayFavorites, PlayRandom (both arms), PlayLastAdded,
PlayByGenre, PlayByDecade, PlayMoodMusic, SearchMedia (both arms),
PlayArtistSongs (queue/SetQueue/continuation), YesIntent PlayArtist/PlayVideo/
PlayPlaylist, FollowMe (target SetQueue + now-playing + the SOURCE queue Clear all
moved after the build: a refused transfer leaves both devices untouched),
PlayVideo (token-gated unconditionally via the remux URL), SkillConnection's
PlayFavorites task, and ProgressReporter's adjacent-item navigation. PINS: five
new MusicPathLaunchRefusalTests (artist, single-song with seeds surviving,
PlaySong, PlayAlbum, PlayRadio incl. RadioModeState-not-armed), each asserting
throw + untouched session/queue/continuation/ledger; the playlist twin is not
pinnable end-to-end (Playlist.GetManageableItems non-virtual, documented in the
file header). RED PROOF: reverting the single-song reorder flips its pin.

CODE-REVIEW FINDING 2 (a real regression item 1 would have introduced):
SkillConnectionHandler's task catch(Exception) swallowed the new exception and
answered MediaSearchError; now filtered (`when (ex is not StreamTokenNotConfiguredException)`) so the pipeline translation owns it. Verified the other
broad catches hold no builder calls (PlaybackStopped/Started, MediaInfo,
SetReminder, ProactiveSubscriptionChanged, ProgressReporter's runtime guard).

CODE-REVIEW FINDING 3: inside BuildAudioPlayerResponse the Audio-route
RecordLastPlayed ran BEFORE the native-controls delegation could refuse, flipping
the ledger for a refused seek-mode launch (pre-existing, but contradicting the
policy this diff states at both VideoApp guard sites); the record moved AFTER the
delegation (the delegated path records its own VideoApp route inside the callee,
past its own guard), and the artist/album pins now assert GetLastPlayedItemId
stays null.

PINS (net +10): 7 builder Throws pins (reshaped from the JF-687 ConfigTell pins)
+ 4 unchanged no-overblock/control pins; 4 pipeline pins (localized Tell it-IT via
a real refusing builder with the tail-skip proof, locale-less en-US fallback,
event-request keep-alive, interceptors-run + SkipColdLibraryWork); 1 scan pin;
5 music-path refusal pins; the JF-693 handler refusal pins flipped to ThrowsAsync
keeping their no-phantom/no-persist/no-ledger assertions; the Fuzzy belt pin
re-commented; the CrossMediaFallback fixture retargeted onto the builder string
overload. RED PROOFS run and read, all on both TFMs: pipeline catch removed ->
all 4 pipeline pins flip (exception escapes) while the 39 pre-existing pipeline
tests stay green; single-song reorder reverted -> phantom-state pin flips to the
written-state failure; guard disabled -> exactly the 7 Throws pins flip while the
4 no-overblock/control pins stay green; FollowMe gate removed -> the scan pin
fails naming the site.

GATES: Skill simplify (4 agents: reuse/simplification/efficiency/altitude).
APPLIED: the in-guard LogError deleted (the pipeline catch logs strictly more;
one line instead of two errors + a near-copy message), HasLaunchDirective doc
belt re-frame, YesIntent ternary + single attach call, Recommend single
session-write pair, using directives replacing 11 fully-qualified exception
names across six test files, the single-caller async AssertRefused inlined, six
stale _NoDirective test-name suffixes dropped, the PlayRadio pin onto
SeekModeBrokenConfig + the fixture's mocks, a CreateCrossMedia factory for the
duplicated construction, the SleepTimer stale refusal sentence, the roster scan
single-pass IL snapshot (~5x fewer walks) with a lazily-built site key,
SameTypeHelpers + CallsDirectlyOrViaSameTypeHelper + CallsNamedMethod (the
CallsGetter setter twin) hoisted into IlCallScanner with both roster tests
delegating (the JF-634 direction), the three longest reorder comments trimmed to
pointers with the policy homed on EnsureStreamTokenDeliverable's doc.
SKIPPED with reasons: the shared refusal-translation base type and the
event-aware-degrade extraction (FILED as JF-708: out-of-diff mechanism unification
that would touch the warming contract, the controller, and two BaseHandler sites);
a TestHelpers assert-throws twin (the usings already collapsed the qualified names;
the remaining inline calls are one line each); the pre-existing double
token-marker Contains on the async VideoApp path (predates JF-699, one short
string scan, noted in JF-708's context). Skill code-review high (6 findings):
F1 applied (the thirteen-site reorder extension above), F2 applied (the
SkillConnection catch filter), F3 applied (the ledger order + pin assertions),
F4 applied as a documented accepted boundary in the scan's doc (helper discovery
is one-level same-type; a miss cannot silently pass a gated site), F5 applied
(all banned word-hyphen-word instances in authored prose fixed; the one remaining
diff line is pre-existing base text, verified against HEAD), F6 applied (the
dead Allowlist conditional simplified).

SUITES: 4931/4931 BOTH TFMs (net9.0 + net10.0) on the final state; baseline
4925/4925 at the merge commit (tagged-stash measurement). 0 new warnings. No new
locale strings (the translation reuses the JF-687 StreamTokenNotConfigured key in
all 17 locales); no interaction-model, session-attribute, or HttpClient changes
(DoD 4-8 N/A as annotated). No deploy; do not push. Residuals filed: JF-708.
<!-- SECTION:FINAL_SUMMARY:END -->
