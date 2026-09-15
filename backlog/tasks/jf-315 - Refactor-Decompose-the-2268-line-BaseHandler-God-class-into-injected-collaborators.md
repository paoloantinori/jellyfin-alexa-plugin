---
id: JF-315
title: >-
  Refactor: Decompose the 2268-line BaseHandler God class into injected
  collaborators
status: To Do
assignee: []
created_date: '2026-07-12 14:58'
updated_date: '2026-09-15 14:02'
labels:
  - refactor
  - maintainability
  - tech-debt
milestone: m-7
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
`Alexa/Handler/BaseHandler.cs` is 2268 lines with 60+ protected members spanning response building, fuzzy search, ASR fallback, APL directives, playback progress reporting, radio, playlists, resume-index math, XML/SSML escaping, and URL building. All 61 handlers inherit ALL of it. This is the single biggest maintainability risk in the codebase (architecture review 2026-07-12): any change ripples to every handler and the class is impossible to reason about in isolation.

This is strategic debt, not a bug — scope it as a deliberate, incremental refactor. Extract cohesive collaborators (e.g. ResponseFactory, SearchService, ProgressReporter, UrlBuilder, SsmlBuilder) and inject them, migrating handlers in batches while keeping the test suite green. Do NOT attempt in one big-bang change. Consider doing this as a parent task with per-collaborator subtasks. The large existing test suite (159 test files, ~2360+ cases) is the safety net that makes this feasible.

Related: BaseHandler currently forces the singleton stateless-by-luck constraint on every handler (see the concurrency milestone) — extracting state-free collaborators reduces that surface too.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A target decomposition is documented (which responsibilities move to which collaborator) before code changes
- [ ] #2 At least the response-building and search responsibilities are extracted into separately-testable, injected services
- [ ] #3 BaseHandler line count is materially reduced and no longer mixes unrelated responsibilities
- [ ] #4 All existing unit tests pass after each extraction batch (no behavior change)
- [ ] #5 New collaborators have direct unit tests
- [ ] #6 Handlers consume collaborators via constructor injection (readonly fields)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
METHOD DISCIPLINE (2026-09-11, from the TDD question): this is a characterization-first refactor, NOT classic TDD - behavior must not change, so tests are written against CURRENT behavior and are green on arrival. Per extraction step: (1) census the behaviors of the member(s) about to move; identify thin coverage in the ~3610-test suite (BaseHandler is tested mostly indirectly through handler tests); (2) write characterization tests for the gaps FIRST, green on the current code; (3) extract the collaborator; (4) suite stays green with ZERO expectation edits; (5) red-green applies ONLY to new invariants the refactor introduces (the JF-539 Times.Once precedent: graft the new test onto pre-refactor code and watch it fail). No big-bang decomposition: one collaborator per dispatch, one merge per gate.

DECOMPOSITION CENSUS (batch 1, 2026-09-15; BaseHandler was 5569 lines / ~88 protected+private-protected members + 6 consts + 2 enums before batch 1). Line spans are pre-batch-1. Cluster table:

A. SPEECH/SSML OUTPUT - 8 members (TellSsml, AskSsml x2, GetSsml, BuildOutputSpeech, AskLocalized, BuildNowPlayingSpeech, EscapeXml + private EscapeStringArgs), was lines ~2203-2361 + ~4110-4122. ALL STATIC, zero instance state, pure functions of (ResponseStrings, args). Called from BaseHandler itself (~27 refs) + 16 non-BaseHandler production files (14 intent handlers + DisambiguationHelper + ListPaginationHelper) + tests. STATUS: EXTRACTED in batch 1 (2026-09-15) to Alexa/Util/SpeechBuilder.cs (191 lines, public static class); 17 production files + 2 test files migrated mechanically; BaseHandler 5569 -> 5394 lines. Already had SsmlResponseTests (15 facts); AskLocalized was the one thin-coverage member and got 2 characterization tests BEFORE the move. Static-class choice (not ctor injection) deliberate: members were already static and pure, so injection would change every handler ctor for zero testability gain; AC#6 ctor injection deferred to when a STATEFUL collaborator lands (see clusters B/C/H).

B. ANNOUNCE GATING - 6 members: AttachAnnounceIfEnabled (~2372), BuildVideoLaunchSpeech x2 (~2396/2410), SpeakVideoLaunchAnnounceAsync (~2651), GetAnnounceNowPlaying (~3192), GetAnnounceAudioPlays (~3210). Mix: per-user/global config resolution (Plugin.Instance + _config fallback) + speech composition. Candidate collaborator: AnnouncePolicy (instance state = config access; split config getters from speech assembly - speech side already delegates to SpeechBuilder).

C. PLAYBACK LAUNCH + MEDIUM RESOLUTION - ~16 members: ResolveVideoAppStreamDecision (769), PlayingMedium enum + IsVideoAppMedium + ResolvePlayingMedium (808-930), BuildVideoAppTransportRefusal (931), BuildVideoAppLaunchResponse(Async) (985/1045), BuildChannelLaunchResponseAsync (1089), BuildAudioPlayerResponse (1625), AudioLaunchSource record + ResolveAudioLaunchSource + ResolveResumedAudioLaunch + RoutesToAudioTranscode + GetActiveLaunchBaseMs (1751-1960), IsActivelyPlaying (2453), GetVideoAppForAudio (3230). Heavy user-mix coupling: reads DeviceQueueManager, session state, config; hands off to SpeechBuilder for speech. Candidate: ResponseFactory/LaunchResponseBuilder (largest cluster, needs its own dispatch; some members read queue state so design ctor-injected).

D. RESUME/POSITION MATH - 12 members: SortAndFindResumeIndex (300), FindResumeTrackIndex x2 (4039/4052), FindLastPlayedItemWithProgress (3756), ComposeItemAbsolutePosition (1961), ComposeEventPositionTicks (2002), TryGetRuntimeTicksForGuard (2027), GetAudiobookBookKey/GetAudiobookStartTicks (2077/2089), FormatPosition (4295), FormatTimeSpan (4310), BuildPositionDisplay (4329). Nearly all STATIC pure math over (items, ticks); InvariantCulture-sensitive formatting. Candidate: ResumeMath/PositionFormatter static helper - batch-2-ready (pure, easily characterized).

E. SEARCH/FUZZY CORE - ~10 members: SafeGetItemsResult (2783), SearchWithAsrFallbackAsync (2810), CachedSearchAsync (2852), FuzzyMatch/FuzzyMatchPhonetic (2890/2915), GetArtistSongsAsync (2956), SearchItemsFuzzyAsync (3003), GetSearchResponseMode (3087), HandleFuzzyMiss + FuzzyMissOutcome enum (3246-3404). Uses Logger + config thresholds; HandleFuzzyMiss is the big multi-turn decision block. Candidate: SearchService (AC#2's second named responsibility). NOTE JF-382: the inline 4-tier search duplication in PlayArtistSongsIntentHandler must consolidate INTO this, not the reverse.

F. CROSS-MEDIA FALLBACK - 9 members: FindBestNonEmbeddedMatch (125), PassesCrossMediaWordGuard (173), GetCrossMediaArtistSuggestion (3121), BuildCrossMediaArtistOfferAsk (3149), BuildArtistSongsResponseAsync (4369), BuildSingleSongResponse (4495), TrySongFallback (4534), PassesArtistMatchAcceptance (4649), TryEntityFallbackAsync (4709). All instance (Logger + config thresholds + search coupling). Candidate: CrossMediaFallback service, AFTER E exists (it is E's biggest consumer).

G. ALBUM/PLAYLIST PLAY - 5 members: MinFuzzyAlbumQueryLength const, TryStripLeadingAlbumCallingWord (4965), BuildAlbumQuery (5017), TryAlbumFallbackAsync (5103), BuildAlbumPlayResponseAsync (5221), BuildPlaylistPlayResponseAsync (5362). Instance (session/library/config); candidate: AlbumPlayService or fold into C/F dispatch.

H. QUEUE/RADIO/SHUFFLE/PROGRESS REPORTING - ~10 members: FavoritesAndRatingsFirst (248), Shuffle/ShuffleCopy/ShuffleAndCap (3405-3436, static), MirrorQueueToSession (3457), ReportPlaybackProgress (3513), ApplyRepeatModeAsync (3544), FindRadioTracksAsync/FindRadioTracksByGenreAsync (3583/3612), GetPostPlayBehavior (3103), ReportStopOrderedAsync (2725), RunFireAndForget (2693). Candidate: ProgressReporter + RadioSource; ReportPlaybackProgress/ReportStopOrderedAsync touch SessionManager (stateful, ctor-inject).

I. APL DIRECTIVES - 3 members: TryAttachListDirective/TryAttachCarouselDirective/TryAttachNowPlayingDirective (4159-4280, private protected). Instance (Logger); candidate AplDirectiveFactory, small standalone batch.

J. ELICIT/DIALOG RESPONSES - 3 members: BuildElicitSlotResponse (1322), BuildDialogElicitResponse (1367), BuildCancelDuringOpenElicit (1394). First two STATIC + pure; BuildCancelDuringOpenElicit reads session state. Could join SpeechBuilder's neighborhood or a DialogResponses static helper.

K. SESSION/CONFIG GATES + INFRA - ~16 members: GuardIndexReady x2 (218/225, static one-liners), GetLocale (2423, static), IfFeatureDisabled (2464), FilterByContentAccess (2486, static), IfMediaTypeDisabled (2528), IsMusicEnabled (2554), ApplyLibraryFilter (2565, static), SendProgressiveResponse (2595, virtual - pipeline coupling), RetryAsync (2771), ResolveJellyfinUser (4136, static), ResolveSeriesForPlaybackAsync/GetNextUpEpisodesAsync/PlayNextUpEpisodeAsync (3827-4038, TV flow - arguably cluster L). Config gates (IfFeatureDisabled/IfMediaTypeDisabled/IsMusicEnabled + the Get*Behavior getters) belong with the future ConfigPolicy collaborator; TV-nextup trio is its own mini-domain.

L. UNCLASSIFIED SINGLES: GetArtistSubtitle (4280, static, 1-purpose), CheapDtoOptions (5001, static one-liner), PopularitySort (71), CrossMedia* consts. Leave near consumers unless a cluster claims them.

COVERAGE NOTES: A was already directly tested (SsmlResponseTests); D (resume math) and the static parts of H (Shuffle*) have partial direct tests; B/C/E/F are covered only indirectly via handler tests - characterization FIRST for those in their own batches. BATCH 2 RECOMMENDATION: cluster D (ResumeMath/PositionFormatter) - static, pure, InvariantCulture-sensitive (matches the batch-1 risk profile), or cluster I (APL directives) if a smaller step is wanted before the stateful ResponseFactory (C).

BATCH-1 GATE FINDINGS APPLIED (2026-09-15, /simplify 2 combined agents covering 4 angles + code-review high opus, verdict CLEAN with P3s): SpeechBuilder dead using dropped + formatting fixed; AskLocalized test strengthened (exactly-once escape: Contains 'Rock &amp; Roll' + DoesNotContain raw + XDocument.Parse); census count corrected (16 non-BaseHandler files). CENSUS CORRECTIONS for the NEXT batches: (1) cluster D purity was OVERSTATED - FindLastPlayedItemWithProgress is static but I/O (ILibraryManager+IUserDataManager), TryGetRuntimeTicksForGuard is instance+I/O+Logger, ComposeItemAbsolutePosition/ComposeEventPositionTicks are queue-state-coupled: batch 2's ResumeMath must be scoped to the GENUINELY PURE subset only (SortAndFindResumeIndex, FindResumeTrackIndex x2, GetAudiobookBookKey/GetAudiobookStartTicks, FormatPosition, FormatTimeSpan, BuildPositionDisplay) and the manager-touching members deferred to the stateful H/C batches; (2) cluster E carries the JF-408-protected auto-play predicates in HandleFuzzyMiss (FairComparisonScore symmetric comparison, AlternativeFullNameThreshold, the JF-377 downgrade) - two prior relocation attempts were REVERTED (fuzzy_recall_vs_judgment_layers memory): the SearchService dispatch MUST keep the judgment predicates at their decision points or it repeats the reverted mistake; (3) cluster J (BuildDialogElicitResponse/BuildElicitSlotResponse) is NAME-COUPLED to validate_interaction_models.py Phase 8's method-name scan: keep the names or patch the validator in the same change; (4) FOLLOW-UPS filed: batch-1b TellLocalized (the Tell-shape GetSsml-then-wrap pattern survives at ~7 sites + 8 hand-rolled <speak> wraps; SpeechBuilder is the natural home) and cluster-B's BuildNowPlayingSpeech announceOn=true DEFAULT (a policy default inside the util; make the parameter required when cluster B lands - no production site relies on it today). AC#2/AC#6 status: batch 1 delivered separately-testable but NOT injected (static); they close with the cluster-C ctor-injected ResponseFactory and cluster-E SearchService. DO NOT close JF-315 on batch 1's strength.

BATCH 2 STATUS (2026-09-15): cluster D extracted to Alexa/Util/ResumeMath.cs (312 lines, public static; the 8 gate-scoped pure members incl. the exclusively-owned private helpers; the 4-tuple SortByRating stayed with cluster H). BaseHandler 5394 -> 5110 lines. Characterization-first honored: 33 tests written green on pre-move code via a probe subclass, then proven multiset-identical on the move; 26 call sites migrated; zero expectation edits mechanically proven. Purity caveats documented in the ResumeMath class doc: GetAudiobookStartTicks reads the Plugin.Instance tracker singleton, FindResumeTrackIndex creates-on-read via GetOrCreateQueue (parameterization pointer to clusters H/C). Suite 3861/3861 both TFMs; Release 0 warnings; validators PASS. Gates: /simplify 4-angle pass (dead using, blank lines, sorted usings, WithDeviceQueue dedup, CreateJellyfinUser helper replacing the 44th test-user copy) + review-local 5-reviewer pass (nothing >=80 on the code; byte-identity independently re-verified). Filings: JF-570 (twin SortByRating split, lands with cluster H) and JF-571 (the CreateJellyfinUser dedicated batch). BATCH 3 recommendation: cluster I (APL directives, 3 members) or batch-1b TellLocalized; cluster C (ctor-injected ResponseFactory, closes AC#2/#6) is batch 4.
<!-- SECTION:NOTES:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
