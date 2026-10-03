---
id: JF-732
title: >-
  JF-732 - the AudioPlayer-family now-playing belt: extending the JF-718 state
  roster beyond the VideoApp-Tell family, and the roster's write-detection depth
status: To Do
assignee: []
created_date: '2026-10-03'
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
- [ ] #1 AudioPlayer-family belt decision made and landed (roster extension + gate sweep, or documented thinner-belt decision)
- [ ] #2 PlayRadio write gate + helper-deep write detection landed together (or the boundary re-documented with the reason)
- [ ] #3 dotnet test passes both TFMs
- [ ] #4 /simplify + /code-review high passed
<!-- DOD:END -->
