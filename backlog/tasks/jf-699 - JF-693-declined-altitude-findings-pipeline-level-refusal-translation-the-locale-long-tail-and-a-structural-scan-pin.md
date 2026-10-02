---
id: JF-699
title: >-
  JF-699 - JF-693 declined altitude findings: pipeline-level refusal translation,
  the locale-threading long tail, and a structural scan pin for the delivered-launch gates
status: To Do
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
<!-- SECTION:DESCRIPTION:END -->
