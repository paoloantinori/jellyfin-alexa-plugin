---
id: JF-693
title: >-
  JF-693 - JF-687 residuals: handler-level token-gated deliveries and refusal-Tell
  overwrites that sit outside the launch builder
status: Done
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-02 (worker on the JF-693 dispatch). ALL FOUR RESIDUALS plus both gate-marker
extensions and four code-review fixes:

(1) SLEEP-TIMER: the re-issue (the ONE AudioPlayer.Play minted outside the guarded chokepoint)
routes its resolved replay URL through the ONE shared guard, now internal
(PlaybackLaunchBuilder.StreamTokenSecretRefusal): a token-gated replay source (speed/transcode)
with an empty secret answers the localized configuration Tell instead of a dead directive, and
the check sits BEFORE the JF-628 ledger writes (RecordLaunchBase/RecordLastPlayed), so a refused
re-issue never touches the scope store or the device ledger (static replay URLs still re-issue).

(2) OVERWRITES: two shared members in PlaybackLaunchBuilder (both internal): HasLaunchDirective
(the JF-687 private predicate, now the handler-side verdict signal) and
AttachAnnounceIfLaunched(response, speech) (the task's suggested shape). Every post-builder
OutputSpeech overwrite in scope now rides a delivered launch: PlayBook (tracked-resume announce,
fresh-launch rides the builder's own gate, standard-chapter announce), YesIntent
(resume-playlist announce, standard resume announce, PlayBook confirmation), ResumeIntent
(position announce x2 incl. the progressive-announce swap, book-resume announce),
SetPlaybackSpeed (the success speech), SkipForwardBack (both skip confirms), JumpToPosition,
CrossMediaFallback.ApplyAnnouncement (the JF-345 ONE override site, now delegating to the
helper - covers AlbumPlayService's seek-mode concat path), and BaseHandler.HandleFuzzyMiss
(the auto-play qualifier block, code-review finding 4).

(3) LOCALE: BuildAudiobookVideoAppLaunchResponseAsync gained the optional locale param (threaded
into BuildVideoAppAudioResponse); threaded from every caller that holds one: PlayBook (all three
branches), YesIntent (both), ResumeIntent (audiobook resume + fresh book + audio tail + the
server-progress audio branch), StartOver (book + audio branches), and the refusal-reachable
chokepoint calls (SetPlaybackSpeed, Skip x2, Jump, PodcastEpisodeResolver,
LaunchRequestHandler.HandleSessionQueueResume, Yes resume-confirm).

(4) PERSIST-BEFORE-REFUSE: SetPlaybackSpeed's PodcastSpeedPerMille persist + SaveConfiguration
moved behind the delivered-launch verdict (a refused speed ask keeps the standing rate and its
own Info log line). Same class applied beyond the dispatch: StartOver's durable position clears
(server PlaybackPositionTicks + the book tracker) ride a delivered restart only (code-review
finding 2), and PodcastEpisodeResolver/LaunchRequestHandler/YesIntent session writes moved
behind the verdict.

GATE-MARKER (a): four localized twins (one per builder family) added to the JF-687 pin file, and
the handler pins assert the it-IT string end-to-end (the flagship path is now localized).
GATE-MARKER (b): the phantom now-playing state is gated at PlayBook (the queue/session/
SetQueue/QueueContinuation writes moved into a post-verdict ApplyBookPlaybackState local),
YesIntent (both shapes), PodcastEpisodeResolver (+ its APL screen attach), and
LaunchRequestHandler's queue-resume else branch.
CODE-REVIEW FINDING 1 (a happy-path regression the reordering would have introduced): the
builder records the launch ledger DURING its call and the post-verdict SetQueue replaced the
DeviceQueue without carrying it, so every successful book launch would have wiped its own
last-played record. Fixed at the manager: CopySurvivingStores carries LastPlayedItemId/
LastPlayedLaunchRoute (a launch fact, not queue content); old-order callers are unaffected
(their builder record still lands last).

PINS (12 new): SleepTimer refusal (dead-directive shape + ledger/scope untouched),
SetPlaybackSpeed refusal (localized Tell, no success speech, no persist), PlayBook tracked-resume
refusal (localized Tell survives, no phantom state/QueueContinuation), PlayBook success-path
ledger survival, DeviceQueueManager SetQueue_PreservesTheLastPlayedRecord, StartOver refusal
keeps the saved position, ApplyAnnouncement no-directive no-op (+ the override fixture updated
to the delivered-launch contract), the fuzzy qualifier refusal pin (+ two fixtures updated to
carry the directive a real delegate returns), and the four localized builder twins. Shared
oracle hoisted: TestHelpers.AssertStreamTokenRefusalTell (the JF-687 file's twins fold onto it).
RED PROOFS run and read (each guard disabled in isolation, the pin flipped, guard restored):
sleep guard off -> dead directive ships; speed gate off -> "Velocità uno e mezzo." over the
refusal; PlayBook gate off -> "eccoci Riprendo The Hobbit..." over the refusal; locale dropped
alone -> en-US string; builder locale ignored -> all four localized twins flip; SetQueue carry
removed -> both ledger pins flip to null; StartOver gate off -> SaveUserData invoked despite the
refusal.

GATES: Skill simplify (4 agents; APPLIED: ApplyAnnouncement delegates to the ONE gate, the
shared refusal-Tell oracle in TestHelpers + the production predicate in tests, the private
predicate twin deleted, YesIntent PlayBook single gate, PlayBook/Resume early-return shapes,
queueItems built only when applied, the ApplyAnnouncement fixture split; SKIPPED with reasons:
the double directive-scan on audiobook flows (nanoseconds vs builder-signature churn), the
sleep gate's post-resolution placement (forced: token-gating is only knowable from the resolved
URL), the local-function closure (no delegate, request-scoped). The altitude angle's pipeline
redesign was declined as re-architecting JF-687's contract: filed as JF-699) + Skill code-review
high (6 findings: 4 APPLIED - the SetQueue ledger carry (finding 1), the StartOver durable
clears (finding 2), the HandleFuzzyMiss qualifier gate (finding 4), the four in-function locale
asymmetries (finding 5); 2 FILED in JF-699 - the music-path phantom-state sweep (finding 3,
safe only after the finding-1 fix that landed here) and the announce-adapter overload collapse
(finding 6)). Nothing cut at a cap.

SUITES: 4868/4868 BOTH TFMs (net9.0 + net10.0; baseline 4856 + 12 pins), 0 warnings. No new
locale strings (the refusal reuses the JF-687 StreamTokenNotConfigured key in all 17 locales);
no interaction model, session-attribute, or HttpClient changes (DoD 4-8 N/A as annotated). No
deploy; do not push. Residuals filed: JF-699.
<!-- SECTION:FINAL_SUMMARY:END -->
