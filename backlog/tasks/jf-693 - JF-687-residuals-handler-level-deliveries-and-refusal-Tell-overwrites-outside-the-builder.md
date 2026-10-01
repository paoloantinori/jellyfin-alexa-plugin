---
id: JF-693
title: >-
  JF-693 - JF-687 residuals: handler-level token-gated deliveries and refusal-Tell
  overwrites that sit outside the launch builder
status: In Progress
priority: low
labels:
  - streaming
  - robustness
references:
  - backlog/tasks/jf-687 - Launch-side-empty-secret-mints-dead-URLs-the-five-PlaybackLaunchBuilder-sites-mint-with-unchecked-StreamTokenSecret.md
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-01 same-turn from the JF-687 work (the two residual classes the builder-level
guard deliberately does not cover, both living in files outside the JF-687 task's scope):
1. HANDLER-LEVEL DELIVERY: the JF-628 sleep-timer re-issue (SleepTimerIntentHandler) hand-builds
its AudioPlayerPlayDirective from `replaySource.Url` (the ONE AudioPlayer.Play minted outside
the BuildAudioPlayerResponse chokepoint, per the JF-564 docs). When that source is
speed/transcode-routed (a token-gated /alexaskill/api/ URL) and StreamTokenSecret is empty,
the dead URL still ships: the JF-687 guard sits at the builder delivery points and cannot see
this directive. Reachability is the narrow corner: the guard refuses a minted launch at its
first play, so a queue of speed-routed streams should never form while the secret is empty;
the re-issue of a stream launched BEFORE the secret emptied dies at fetch anyway (secret
changes invalidate outstanding tokens).
2. REFUSAL-TELL OVERWRITES: several handlers overwrite the OutputSpeech of a response a
guarded builder returned (PlayBookIntentHandler ~283 and ~315, YesIntentHandler ~238/~279/~283,
ResumeIntentHandler ~371/~446/~522, AlbumPlayService ~740, and any other site writing
OutputSpeech after a guarded builder call). In each the launch itself stays refused (no
directive leaves), but the caller's announce replaces the StreamTokenNotConfigured message,
so the user hears a now-playing for a play that will not happen instead of the configuration
error. The in-file equivalents (BuildEpisodeAudioLaunch, BuildAudiobookVideoAppLaunchResponseAsync)
were gated inside JF-687 (the shared HasLaunchDirective predicate); these handler sites need
the same "only onto a response that carries the directive" gate.
3. LOCALE THREADING: the refusal Tell localizes only where a caller threads the request
locale (the PlayVideo episode route) or passes an announce locale (now folded in at the
BuildAudioPlayerResponse chokepoint). The audiobook paths, the flagship token-gated
consumers, always answer the en-US fallback: no caller passes the new optional locale to
BuildAudiobookResumeResponse (PlayBookIntentHandler ~282, YesIntentHandler ~237,
ResumeIntentHandler ~517) and BuildAudiobookVideoAppLaunchResponseAsync has no locale
parameter yet (its PlayBook/StartOver/YesIntent callers hold the locale). One-line thread
per site once JF-687's optional-param seam is in place (it is).
4. REFUSAL-TELL OVERWRITES, EXTENDED SITE LIST (code-review 2026-10-01): the same
OutputSpeech-after-build overwrite also fires at SetPlaybackSpeedIntentHandler ~246
(replaces the refusal with the PlaybackSpeedSet SUCCESS speech on the always-token-gated
speed route), SkipForwardBackIntentHandler ~150/~171, JumpToPositionIntentHandler ~138,
and the LaunchRequest/resume family (LaunchRequestHandler ~530/~544, ResumeIntentHandler
~371/~446). Suggested shape: one shared Launch.AttachAnnounce(response, speech) helper
carrying the has-launch-directive gate, consumed by every site. Related residual: the
speed handler persists PodcastSpeedPerMille BEFORE the launch build, so a refused speed
ask still mutates the persisted preference; move the persist behind a successful launch.
<!-- SECTION:DESCRIPTION:END -->

ORCHESTRATOR GATE-MARKER EXTENSIONS (2026-10-01, from the final-state review; findings 3 and 4): (a) all 11 JF-687 pins assert the en-US refusal only - when this task's locale threading lands, extend at least one pin per family to the localized assertion (the it-IT string is currently unreachable on the flagship paths, so the pins bake English in); (b) NEW ITEM, the phantom now-playing state: handlers write session.NowPlayingQueue / FullNowPlayingItem / DeviceQueueManager.SetQueue / QueueContinuationStore BEFORE the guarded builder call (PlayBook ~234-251, YesIntent ~220), so a refused launch leaves MediaInfo answering "playing <book>" with nothing playing and a stale QueueContinuation that survives - the refusal-before-ledger policy must extend to the queue/session writes (fix ordering or roll back on refusal detection via the HasLaunchDirective predicate).
