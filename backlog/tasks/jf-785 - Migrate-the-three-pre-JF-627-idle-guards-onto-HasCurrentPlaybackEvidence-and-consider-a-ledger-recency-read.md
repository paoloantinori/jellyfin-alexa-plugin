---
id: JF-785
title: >-
  Migrate the three pre-JF-627 idle guards (FavoriteToggle, MediaInfo,
  ApplyRepeatModeAsync) onto HasCurrentPlaybackEvidence; consider a ledger
  recency read as the root fix
status: Done
assignee: []
created_date: '2026-10-06 00:00'
updated_date: '2026-10-06 14:30'
labels:
  - tech-debt
  - refactor
dependencies:
  - JF-627
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/FavoriteToggleIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/MediaInfoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/ProgressReporter.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-627 /simplify + altitude reviews (2026-10-06), same-turn filing rule.

JF-627 introduced `PlaybackLaunchBuilder.HasCurrentPlaybackEvidence(context, session)` as the ONE current-evidence predicate for the JF-629 idle-guard family, defined as the resolver's own read set (an AudioPlayer token, OR the session's held FullNowPlayingItem, OR its NowPlayingItem DTO). Only the new playlist-edit guard consumes it so far. Three older inline guards remain hand-rolled in a NARROWER two-condition shape (token OR the NowPlayingItem DTO only):

- FavoriteToggleIntentHandler.HandleAsync (~line 92; serves Mark/UnmarkFavorite)
- MediaInfoIntentHandler.HandleAsync (~line 96)
- ProgressReporter.ApplyRepeatModeAsync (~line 821; serves LoopOn/LoopOff/LoopSongOn)

The delta is behavioral, not stylistic: on a session holding FullNowPlayingItem with NO NowPlayingItem DTO and no token, the three DTO-only guards answer their no-media tell even though the shared resolver WOULD resolve the held item (it resolves the full item before the DTO), and the playlist-edit family (post-JF-627) now acts on it. The divergence is an artifact of migration order (those families migrated from DTO-only readers, so their JF-629 guards mirrored their own pre-migration behavior), the same accident class JF-627 was opened to close.

The migration changes those three families' behavior on the full-item-without-DTO shape, so it needs its OWN red proof per family (a pin asserting the handler proceeds and resolves the held item on that shape), not a silent fold. No existing pin covers that shape (verified by grep over the favorite/media/loop suites: only LoopIntentHandlerTests touches FullNowPlayingItem, setting it to null).

ROOT-FIX OBSERVATION (the JF-627 altitude review): the per-family guard layer as a whole is compensation for ONE missing primitive, the ledger's absent recency read. Three families make three compromises over the same missing signal (RateItem acts on the unbounded tail; loop/sleep/speed refuse on it through the belt; favorite/media/playlist-edit answer no-media through the evidence guard). Recording a timestamp beside the route in the DeviceQueueManager last-played ledger and bounding the tail would dissolve the entire guard layer; consider it here before adding any fifth guard.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->

### Design record (2026-10-06)

LEG 1 (the migration), shape: the three inline guards now call the ONE
predicate (`!PlaybackLaunchBuilder.HasCurrentPlaybackEvidence(context, session)`),
each keeping its own log line and idle REACTION string (MediaNotFound for
favorite; NoMediaPlaying for media info and the loop toggle). Behavioral
delta exactly as filed: on the full-item-without-DTO shape (the session holds
FullNowPlayingItem, no DTO, no token) the three families now proceed and the
resolver's held-item leg answers. Production reality of that shape, for the
record: every delivered launch writes FullNowPlayingItem through
AttachNowPlayingIfLaunched (its directive gate admits the VideoApp directive),
while the DTO is only written by a Jellyfin playback report, so the window is
the pre-PlaybackStarted interval after any launch, plus the whole duration of
a VideoApp launch (which reports nothing, VideoApp emits no events).

LEG A (the unresolvable-evidence door), CHOSEN SHAPE: guarded families reject
ledger-tail answers (the filing's second option). A new optional parameter
`allowLedgerTailAnswers = true` on ResolveCurrentPlayingItem (after logLabel,
so every existing positional call site compiles unchanged) turns the final
non-displacement tail off for the four guarded callers (favorite, media info,
loop, playlist-edit) while the default true keeps RateItem's JF-626 unbounded
stance, Repeat and SetPlaybackSpeed byte-identical. WHY NOT guard on
resolvable evidence (the first option): that pushes a library resolve into the
guard, duplicating the resolver's own work, and the JF-626 finding 7 decision
already rejected per-id existence re-resolves for session-held items;
presence is the right concept for the guard (the session SAYS something is
playing), and the door is that the resolver SUBSTITUTES a different item (the
days-old tail) when the evidence itself does not resolve, so the fix belongs
where the substitution happens. The displacement arm above the tail still
answers for guarded callers: it requires a live mismatched token, so it is
evidence-backed (pinned at the resolver level).

LEG B (VideoApp parity), DECISION: document and pin the boundary as
deliberate; NO new predicate leg. The investigation the task asked for: a
VideoApp evidence source DOES exist in the session, and the predicate already
reads it. Every delivered VideoApp launch writes the launched item into
session.FullNowPlayingItem (AttachNowPlayingIfLaunched at the eight video
sites: PlayVideo, PlayEpisode, TvNextUp, the APL movie/episode taps,
Recommend, SearchMedia video arm, YesIntent video confirm, the podcast
resolver; plus the channel site's raw gated block), and the request path
resolves the SAME SessionInfo per (token, deviceId) through
GetSessionByAuthenticationToken + SessionReferenceCache, so during a movie
the held-item leg IS the VideoApp evidence leg: 'add this' adds the movie and
'favorite this'/'what's playing' act on it (three of the four families gain
this parity from Leg 1 alone, since their DTO-only guards were what refused
the held item). The residual boundary: a session that has LOST the held item
(a server restart mid-movie, the movie ended and the server cleared the
entry, a session-lookup miss). GATE-MARKER CORRECTION (the code-review F1
round refuted the second loss shape's mechanism, keep this corrected when
reading this record alone): the server NEVER clears the entry on movie end;
an ended movie is indistinguishable from a playing one and KEEPS acting until
the session object is dropped or a later playback report replaces the item
(the predicate doc's boundary (a) is the corrected taxonomy; this paragraph's
original wording predates it). The only remaining VideoApp-shaped signal there
is the device ledger, which is unbounded in recency: admitting it as evidence
would reopen the exact JF-629 idle hazard the guard exists to block (an idle
device's days-old launch passing the guard). So the boundary is deliberate:
guarded families answer NoMediaPlaying there while unguarded RateItem still
acts on the same ledger entry (its documented JF-626 stance, not a parity
target). Pinned both ways (below); the predicate's doc carries the parity
story.

LEG 4 (the root-fix consideration), DECISION: REJECTED for this task, filed
as JF-789. The finding's premise needs one correction first: the timestamp
primitive ALREADY EXISTS. DeviceQueue.LastPlayedWrittenAt (JF-619) is stamped
by RecordLastPlayed on every write (including the short-circuit relaunch
path), persists with the queue (null on pre-JF-619 files), so there is no
persisted-state migration in the way; what is missing is only the read
(GetLastPlayedSnapshot returns id+route) and a policy. The rejection rests on
blast radius, not feasibility: dissolving the guard layer means bounding the
tail GLOBALLY, and the unbounded tail is a documented DELIBERATE stance of
families outside this task, namely RateItem (JF-626: rate-this on an idle
device rates the days-old item) and the JF-632 medium gate family
(loop/sleep/speed), whose shipped comment records the tradeoff and warns that
bounding "would fork the family's ledger semantics"; Repeat and SetPlaybackSpeed
ride the same tail unguarded. Bounding it changes all of their idle behavior
(each needing its own red proofs and, for the refusal-vs-no-media flips, a
device round), the window value itself has no device evidence behind any
number, and the pre-JF-619 null stamps need their own policy decision
(legacy-tie keeps the days-old answer for old files; stale-as-refused changes
upgrade behavior). That is the task's "expands scope, risky blast radius"
branch verbatim. JF-789 carries the implementable shape now that the
primitive's existence is on record.

### Red proofs (both TFMs, run on the UNMODIFIED tree before any production change)

Battery: 9 handler-level pins added first, built against the unmodified
production tree. Result 7 RED + 2 GREEN on both TFMs (net9.0 and net10.0):
the 2 green are the Leg B pins, which pin the CURRENT (deliberate) semantics
and are drift guards, not bug proofs. The failing assertion strings are the
bug symptoms verbatim:

- Leg 1 (migration), one pin per family, all RED:
  FavoriteToggleIntentHandlerTests.HandleAsync_FullItemHeldWithoutDto_TogglesHeldItem_JF785
  ("the held full item is the toggle target": data.IsFavorite false; the
  DTO-only guard had answered MediaNotFound);
  MediaInfoIntentHandlerTests.Handle_FullItemHeldWithoutDto_ReportsHeldItem_JF785
  (String "Nothing is currently playing." / Not found "Held Movie");
  LoopIntentHandlerTests.HandleAsync_FullItemHeldWithoutDto_AppliesModeToHeldItem_JF785
  (Assert.NotNull failure: no PlaybackProgressInfo, the no-media tell had won).
- Leg A (the door), four family pins, all RED:
  FavoriteToggle...HandleAsync_UnresolvableDtoStaleLedger_NoWrite_JF785
  (String "Media added to favorites list." / Not found "could not find the
  media": the days-old ledger item got favorited);
  MediaInfo...Handle_UnresolvableDtoStaleLedger_SpeaksDtoNotLedgerItem_JF785
  (String "now playing Days Old Song" / Not found "Ghost Track": the answer
  spoke the days-old item);
  Loop...HandleAsync_UnresolvableDtoStaleLedger_NoModeWrite_JF785
  (String "Repeat all enabled." / Not found "Nothing is currently playing.":
  the mode landed on the days-old item);
  PlaylistEdit...AddCurrent_UnresolvableDtoStaleLedger_NotAdded_JF785
  (String "Added Circles to the playlist Road Trip." / Not found "playing":
  the days-old item got added).
- Red-battery integrity note: the favorite door pin initially passed
  VACUOUSLY (no GetUserData setup, so no write could happen even though the
  tail resolved the item); it was hardened with the SetupHappyPath write
  reachability before being counted, and then failed as expected. The final
  battery counts are post-hardening.
- Post-fix: 11/11 green on both TFMs (the 9 above plus the 2 resolver-level
  pins added with the fix: CurrentItem_GuardedCaller_TailRefused_DefaultKeepsTail_JF785
  and CurrentItem_GuardedCaller_DisplacementArmStillAnswers_JF785; they pin
  the new parameter's mechanism and its preserved displacement half).
- Leg B pins (green by design on both trees, the deliberate boundary):
  PlaylistEdit...AddCurrent_VideoFirstMovieLaunch_SessionHoldsMovie_AddsMovie_JF785
  (the held-item leg carries VideoApp playback: the add carries the movie),
  PlaylistEdit...AddCurrent_VideoFirstSessionLostMovie_RefusesWithoutLedgerLeg_JF785
  (the session-lost shape answers NoMediaPlaying: the ledger is not evidence;
  fails if a VideoApp-ledger leg ever sneaks into the predicate).

### Gates

/simplify (4 parallel angles: reuse, simplification, efficiency, altitude):
efficiency CLEAN (the flag genuinely skips the tail's GetItemById for guarded
callers; everything else nanoseconds). APPLIED: the Leg A rationale
consolidated to its ONE home (the resolver doc owns the contract; the
predicate doc links instead of restating; the four call sites keep
family-specific one-liners, the verbatim migration-history paragraphs dropped);
the belt/supersession note on the predicate doc (with the tail refused, the
resolver's answering arms are exactly the predicate's legs, so guard + flag
encode one policy; the fold is JF-789 step 5); CreateAddCurrent gained the
optional ledger param with the three new inline constructions repointed (the
file's factory convention); the two new Movie constructions repoint to
TestHelpers.CreateMovie (the JF-781 as-touched rule); the pin docs cut to a
few lines with pointers. SKIPPED with reasons: the structural fold (delete
the four guards, keep the flag, delete the zero-caller predicate; it
contradicts the filed task shape whose core mandate is the migration ONTO the
predicate, rewrites the JF-627/JF-629 contracts the task declares load-bearing,
and is deliberately JF-789 step 5); the ghost-DTO TestHelpers factory (three
one-line initializers with per-test Name flavor, below the ceremony threshold;
noted as the 4th-copy trigger); the two pre-existing JF-627 inline
constructions left alone (no-drive-by).

/code-review high (8 angles, 5 findings): F1 APPLIED as a doc fix (the
boundary claim "the movie ended and the server cleared the entry" had no
mechanism: the plugin never nulls FullNowPlayingItem and a VideoApp launch
reports nothing, so the ENDED movie is indistinguishable from playing and
keeps acting until the session is dropped or a later report replaces the
entry; the predicate doc now states both boundary facts and names the true
loss shapes: server restart, inactivity drop, session-lookup miss). F2
APPLIED (the belt invariant was enforced by nothing machine-checkable:
GuardedResolverTailRosterTests, the WarmingGateCoverageTests IL-scan
precedent, scans the plugin assembly for HasCurrentPlaybackEvidence callers
and fails unless their ResolveCurrentPlayingItem sites pass the flag, plus a
roster-equality test on the unflagged callers {RateItem, Repeat,
SetPlaybackSpeed}; MUTATION-PROVEN load-bearing: dropping the flag at
FavoriteToggle reddens both roster tests with the finding's own message).
F3 APPLIED (the touched PlaylistEditHandlerBase resolver call omitted
logLabel, so its displacement/no-resolve log lines printed the generic
CurrentItem label; now passes IntentName like the siblings). F4 APPLIED (the
two new playlist pins asserted only Contains("playing"), too weak to identify
the tell; now assert the actual NoMediaPlaying string like the Loop/MediaInfo
siblings; the two PRE-EXISTING weak asserts in the same file are out of the
diff and left alone). F5 FILED as JF-788, not changed (the four families'
mechanism is now uniform but favorite's wording still says MediaNotFound
where the other three say NoMediaPlaying on the door and idle shapes; the
split predates JF-785 and unifying it is a user-facing change on two shapes
that needs both favorite branches moved together, a product decision recorded
in the filing).

<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: 0 warnings 0 errors)
- [x] #2 dotnet test passes (5352/5352 BOTH TFMs on the final state; baseline 5339 + 13 new: 11 pins + 2 roster tests)
- [x] #3 No new compiler warnings introduced (Release -warnaserror clean)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A-shaped but covered: 13 unit pins incl. handler-level red proofs per family and the IL roster enforcement; no new intent, the changed shapes are unit-pinned, and the e2e suite's simulate-skill path does not exercise the session-evidence shapes without a live device round)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings; every family keeps its existing keys)
- [x] #9 /simplify passed (4 angles: efficiency clean; applied the doc consolidation, the belt note, the factory repoints, the pin-doc cuts; 3 reasoned skips recorded)
- [x] #10 /code-review high passed (5 findings: 4 applied, 1 filed as JF-788; recorded above)
<!-- DOD:END -->

JF-627 GATE-MARKER FINDINGS (2026-10-06): LEG A (the unresolvable-evidence door, finding 1): the idle guard gates on evidence PRESENCE not RESOLVABILITY, so an unresolvable now-playing DTO (item deleted mid-play, or Id == Guid.Empty) lets the resolver's unbounded ledger tail through and 'add this to playlist' ADDS a days-old unrelated item where pre-JF-627 answered NoMediaPlaying; fix shape: guard on resolvable evidence or have guarded families reject ledger-tail answers (displacement-arm answers only). LEG B (the VideoApp-no-token parity, finding 2): HasCurrentPlaybackEvidence has no VideoApp leg, so a video-first device (no AudioPlayer history, no token ever) refuses 'add this' during movies while 'rate this' acts on the same ledger entry; not a regression but the parity boundary is undocumented and unpinned, and video-first 1.0 users hit it on every movie-time 'add this'.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All four legs closed. LEG 1 (the migration): the three pre-JF-627 DTO-only
idle guards (FavoriteToggle, MediaInfo, ProgressReporter.ApplyRepeatModeAsync)
now call the ONE predicate HasCurrentPlaybackEvidence, each with its own red
proof on the full-item-without-DTO shape (RED on the unmodified tree: the
families answered their no-media tells while the resolver would have resolved
the held item; GREEN after). LEG A: closed at the resolver with the new
optional allowLedgerTailAnswers parameter (false at the four guarded call
sites; the displacement arm, which requires a live token, still answers;
default true keeps RateItem's JF-626 stance plus Repeat and SetPlaybackSpeed
byte-identical), chosen over guard-on-resolvable-evidence because the JF-626
finding 7 decision already rejected per-id existence re-resolves and the
substitution belongs where it happens; four family door pins RED on the
unmodified tree (the failing strings were the symptoms verbatim: the
days-old item favorited, spoken, loop-mode-applied, and added), and the
belt invariant is now machine-enforced (GuardedResolverTailRosterTests, the
IL-scan precedent, mutation-proven). LEG B: decided documented-and-pinned,
no new predicate leg: the investigation found the VideoApp evidence source
already exists (every delivered VideoApp launch writes FullNowPlayingItem
through AttachNowPlayingIfLaunched and the request path resolves the SAME
session), so the held-item leg IS the VideoApp evidence leg, three of the
four families gained movie-time parity from the migration alone, and the
residual boundary (session lost the item: server restart, inactivity drop,
lookup miss) deliberately refuses because the only remaining signal is the
unbounded ledger; pinned both ways, with the code-review-corrected boundary
facts (the ended movie is indistinguishable from playing and keeps acting,
matching RateItem's stance). LEG 4 (root fix): REJECTED with the reason
recorded and FILED as JF-789 (the LastPlayedWrittenAt stamp already exists,
but dissolving the guard layer requires globally bounding the tail, flipping
documented deliberate stances of RateItem, the JF-632 gate family
(loop/sleep/speed), and the unguarded Repeat/SetPlaybackSpeed riders, plus
an evidence-free window value and a null-stamp policy).

Gates: /simplify 4 angles (efficiency clean, 5 applied, 3 reasoned skips)
and /code-review high (5 findings: 4 applied, 1 filed as JF-788, the
favorite-family wording split). Suites: 5352/5352 BOTH TFMs on the final
state (baseline 5339 + 11 pins + 2 roster tests); Release --no-restore
-warnaserror 0 warnings 0 errors. No locale strings, no interaction models,
no session-attribute shapes touched; the predicate and its three legs
byte-identical in behavior (doc only). Not deployed (worker branch only).
<!-- SECTION:FINAL_SUMMARY:END -->