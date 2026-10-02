---
id: JF-718
title: >-
  JF-718 - residuals of the JF-714 phantom-now-playing sweep: the channel
  builder's writes-before-capability-gate, the remaining writes-before-build
  sites, and the AttachNowPlayingIfLaunched extraction
status: To Do
assignee: []
created_date: '2026-10-02'
labels:
  - playback
  - refusal-contract
  - tech-debt
dependencies:
  - JF-714
references:
  - >-
    backlog/tasks/jf-714 -
    JF-714-gate-the-five-screenless-Tell-video-sites-NowPlaying-writes-on-HasLaunchDirective.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-714 worker's /simplify (4 angles) and
census rounds, satisfying the review-recommendation discipline: the findings
below are real but outside the JF-714 surface (five intent handlers + their
tests) or blast-radius-beyond-diff, so they were skipped with reasons and filed
here instead.

Finding 1 (the same bug class, LIVE and unreachable by the handler-side
pattern): `PlaybackLaunchBuilder.BuildChannelLaunchResponseAsync`
(Jellyfin.Plugin.AlexaSkill/Alexa/Util/PlaybackLaunchBuilder.cs:1361-1362)
writes `session.NowPlayingQueue` / `session.FullNowPlayingItem` BEFORE the
`DeviceSupportsVideoApp` capability check that returns the VideoRequiresScreen
Tell (:1369-1372). A screenless Dot asking for a live channel ("play channel
CNN", via PlayChannelIntentHandler and PlayRadioIntentHandler's channel tier)
records a phantom channel for a launch that will not happen; the JF-699
comment inside that block even documents the early-refuse rationale (a refused
channel must not be recorded as last-played) while the session writes above it
violate the same principle. The handler-side HasLaunchDirective gate
structurally cannot fix this: the write happens inside the builder, above any
gate the handler could add. Fix is a two-line move below the capability check
in the builder; extend the existing `PlayChannel_ScreenlessDevice_VideoRequiresScreenTell`
pin (VideoAppCapabilityGateTests.cs) with the no-phantom-state assertions in
the same pass.

Finding 2 (JF-699 item 5 ordering leftovers, the refusal-throws phantom
class): `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs`
(~:169-171) and `Jellyfin.Plugin.AlexaSkill/Alexa/Handler/TvNextUpService.cs`
`LaunchEpisodeAsync` (~:365-367) write NowPlayingQueue/FullNowPlayingItem BEFORE
`BuildEpisodeLaunchResponseAsync`. Both are episode arms, so the screenless
capability Tell is not reachable there (an episode degrades to the audio-only
launch, which carries a directive), but the JF-687 token refusal THROWS after
the writes, leaving phantom now-playing for a refused launch; the JF-699
reorder moved the writes after the build at five sites and missed these two.
Same shape, smaller blast: `AplUserEventHandler.HandleSelectItem` (~:163-164,
:269-275) writes before the movie/folder launch build; practically unreachable
(APL taps require a rendered APL document, i.e. a screen), so it can ride the
same reorder or be explicitly allowlisted with that reasoning.

Finding 3 (extraction, /simplify reuse+simplification angles converged): the
4-line `if (PlaybackLaunchBuilder.HasLaunchDirective(response)) {
session.NowPlayingQueue = ...; session.FullNowPlayingItem = ...; }` gate now
exists at 8 handler-side sites (BaseHandler:1259, StartOverIntentHandler:219,
ResumeIntentHandler:539, plus JF-714's five), four of which stamp near-verbatim
rationale comments. A static `AttachNowPlayingIfLaunched(SkillResponse,
SessionInfo, queue, item)` beside `AttachAnnounceIfLaunched`
(PlaybackLaunchBuilder.cs:140) would collapse all eight, single-home the
NOT-tautological rationale (the VideoRequiresScreen capability Tell), and stop
the ninth site copying the block; the builder already writes session state
itself at BuildChannelLaunchResponseAsync, so the home is consistent. Skipped
in JF-714 because the task file prescribes the per-site StartOver wrap and the
extraction touches three pre-existing shipped sites (the JF-702-to-JF-715
precedent for exactly this class).

Finding 4 (structural pin, /simplify altitude angle): the delivered-launch
OUTPUT-SPEECH contract has a machine-check (DeliveredLaunchOutputSpeechRosterTests,
IlCallScanner idiom, empty allowlist) but the delivered-launch STATE-WRITE
contract rests on 8 hand-gates plus one behavior pin covering one site
(VideoAppCapabilityGateTests PlayVideo pair, JF-714). The next write site can
silently reintroduce the phantom with a green suite, and the four sibling JF-714
sites (Recommend, SearchMedia.PlayItem, PlayRandom, YesIntentHandler.PlayVideo)
carry no state assertions at all, so a gate reverted at any of them keeps every
test green (code-review high round, 2026-10-02; note also the existing pin only
proves no-write on a FRESH session, not that pre-existing state survives the
Tell). An assembly-scan pin in
the same IlCallScanner idiom (every method calling a VideoApp-family launch
builder and writing NowPlayingQueue/FullNowPlayingItem must reference the gate
or sit on an explicit allowlist), or at minimum behavior pins on the remaining
four JF-714 sites, would close it. Standard-raising beyond the JF-714 ONE-pin
spec, hence filed not taken.

AUDIT ADDENDUM (2026-10-02, JF-714 gate-marker round, three corrections):
(1) FINDING 1'S FIX SHAPE IS INSUFFICIENT AS WRITTEN - the channel builder has a
SECOND directive-less return below the capability check (the resolver-null
MediaTypeNotAvailable Tell at PlaybackLaunchBuilder.cs:1375-1378), so moving the
session writes merely below the capability gate still leaves them before that Tell,
and the final BuildVideoAppLaunchResponseAsync (line 1389, EnsureStreamTokenDeliverable)
can also throw after the relocated writes. Correct shape: writes below the
resolver-null return, gated on the delivered directive (or the handler-side
HasLaunchDirective pattern). Also: the builder has a THIRD caller the original list
missed (StartOverIntentHandler.cs:132, alongside PlayChannel:107 and PlayRadio:170;
harmless since the fix is builder-side, but the caller census should be complete).
(2) FINDING 2'S SITE LIST GAINS SkillConnectionHandler.HandlePlayFavoritesTask (~152):
the NowPlayingQueue write precedes BOTH the MediaNotFound early Tell (156) and the
refusal-throwing BuildAudioPlayerResponse (168) - the JF-699 policy comment at 166-168
covers only the FullNowPlayingItem write at 169, and a later bare "open the skill"
resumes from the phantom queue. This site was missed by both the JF-714 census and
JF-718's original sweep (found by the gate-marker's full-codebase NowPlayingQueue scan).
(3) The arrow chain in this file's earlier prose is replaced by sentences per the
prose rules.

<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Channel builder writes moved below the capability gate (or a reasoned skip documented)
- [ ] #2 PlayEpisode / TvNextUp (+ AplUserEvent decision) writes moved after the launch build
- [ ] #3 AttachNowPlayingIfLaunched extraction decided (done or skipped with reason)
- [ ] #4 State-write roster pin decided (IlCallScanner scan or behavior pins on the remaining sites)
- [ ] #5 dotnet test passes both TFMs
- [ ] #6 /simplify + /code-review high passed
<!-- DOD:END -->
