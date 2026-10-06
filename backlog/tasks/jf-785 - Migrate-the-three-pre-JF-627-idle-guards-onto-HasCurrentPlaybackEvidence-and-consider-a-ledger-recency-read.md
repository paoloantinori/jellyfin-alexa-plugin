---
id: JF-785
title: >-
  Migrate the three pre-JF-627 idle guards (FavoriteToggle, MediaInfo,
  ApplyRepeatModeAsync) onto HasCurrentPlaybackEvidence; consider a ledger
  recency read as the root fix
status: In Progress
assignee: []
created_date: '2026-10-06 00:00'
updated_date: '2026-10-06 12:00'
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
entry, a session-lookup miss). The only remaining VideoApp-shaped signal there
is the device ledger, which is unbounded in recency: admitting it as evidence
would reopen the exact JF-629 idle hazard the guard exists to block (an idle
device's days-old launch passing the guard). So the boundary is deliberate:
guarded families answer NoMediaPlaying there while unguarded RateItem still
acts on the same ledger entry (its documented JF-626 stance, not a parity
target). Pinned both ways (below); the predicate's doc carries the parity
story.

LEG 4 (the root-fix consideration), DECISION: REJECTED for this task, filed
as JF-787. The finding's premise needs one correction first: the timestamp
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
branch verbatim. JF-787 carries the implementable shape now that the
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

JF-627 GATE-MARKER FINDINGS (2026-10-06): LEG A (the unresolvable-evidence door, finding 1): the idle guard gates on evidence PRESENCE not RESOLVABILITY, so an unresolvable now-playing DTO (item deleted mid-play, or Id == Guid.Empty) lets the resolver's unbounded ledger tail through and 'add this to playlist' ADDS a days-old unrelated item where pre-JF-627 answered NoMediaPlaying; fix shape: guard on resolvable evidence or have guarded families reject ledger-tail answers (displacement-arm answers only). LEG B (the VideoApp-no-token parity, finding 2): HasCurrentPlaybackEvidence has no VideoApp leg, so a video-first device (no AudioPlayer history, no token ever) refuses 'add this' during movies while 'rate this' acts on the same ledger entry; not a regression but the parity boundary is undocumented and unpinned, and video-first 1.0 users hit it on every movie-time 'add this'.