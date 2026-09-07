---
id: JF-516
title: >-
  Device test battery: "chiudi" during VideoApp playback + documented it-IT
  transport words during AudioPlayer playback; then FAQ + reference updates
status: To Do
assignee: []
created_date: '2026-09-07 19:16'
updated_date: '2026-09-07 19:16'
labels:
  - device-test
  - stop
  - playback
  - docs
  - platform-behavior
dependencies: []
references:
  - claudedocs/research_alexa-videoapp-stop-routing_2026-09-07.md
  - claudedocs/research_alexa-audioplayer-transport-stop_2026-09-07.md
documentation:
  - CLAUDE.md#stop--session-routing-during-playback-the-reference
  - README.md#faq
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two exhaustive research passes (2026-09-07, dossiers committed) left exactly two claims unverified on a real device. This task runs ONE ~5-minute Echo Show session that closes both, then documents the results. Device steps require Paolo (real ASR + on-screen observation); log analysis and documentation are autonomous.

BACKGROUND (from the dossiers):
1. Video/chiudi: `claudedocs/research_alexa-videoapp-stop-routing_2026-09-07.md`. The VideoApp docs list "Alexa, stop/close" as the player's voice controls, and there is NO VideoApp.Stop directive (the skill cannot dismiss the player programmatically). Whether "Alexa, chiudi" (close) natively dismisses the video player on an Echo Show is UNTESTED. Stop inside the documented 30s session window routes to the skill (+19s arrival, log-verified 2026-09-07); outside it nothing arrives.
2. Audio transport words: `claudedocs/research_alexa-audioplayer-transport-stop_2026-09-07.md`. The Standard Built-in Intents table documents that during AudioPlayer playback (or while we are the most recent audio skill) Next/Previous/StartOver/Repeat/Cancel route to the skill WITHOUT the invocation name, even if absent from our schema. Our 2026-07-02 negative test used "avanti", which may not be a canonical it-IT NextIntent word ("successivo" is). Pause/resume matched the docs; stop/next did not arrive. Open confound: wording.

TEST BATTERY (one session; record for EVERY step: exact words spoken, rough time, what the device did: video closed / audio stopped / spoken reply / "Qualcosa è andato storto" / nothing):

Part A, VIDEO (Echo Show):
- A1: "Alexa, chiedi a mia collezione di mettere il film Inside Out 2" (one-shot, no conversation before). Wait for video to start.
- A2: "Alexa, chiudi" WITHIN ~20 seconds of the video appearing (inside the 30s window). Does the video close? Does Alexa speak?
- A3: relaunch A1, wait 60+ seconds, then "Alexa, chiudi" (outside the window). Same observations.
- A4 (bonus): while video plays outside the window: "Alexa, pausa" then "Alexa, riprendi". Does the video pause natively (docs claim PauseIntent works with VideoApp)? Does PauseIntent reach the skill?

Part B, AUDIO (any Echo; documented words, NOT "avanti"):
- B1: "Alexa, chiedi a mia collezione di suonare i radiohead" (music starts via AudioPlayer).
- B2 (KEY): "Alexa, successivo" ~10s into playback. Does the track change? Does AMAZON.NextIntent arrive?
- B3: "Alexa, stop" during playback. Audio stops? What arrives (StopIntent / PlaybackStopped / nothing)?
- B4: "Alexa, ferma" during playback. Same observations.
- B5 (bonus): "Alexa, annulla" during playback (CancelIntent is in the documented no-invocation-name list).

LOG VERIFICATION (autonomous, after Paolo reports): ssh minix `podman logs jellyfin --since <window>`, extract per request: timestamp, type/intent (Processing IntentRequest lines), session new:true/false (Request body lines), and match each spoken step to arrivals/deltas; compare A2/A3 against the 14:11/14:23 timeline shape in the video dossier. A no-log-line result for a step = utterance never reached the skill (platform claim), not a plugin bug.

OUTCOME HANDLING:
- A2/A3 chiudi results: add the "How do I stop/close video playback" FAQ entry to README.md (FAQ section, ~line 418) with the verified instruction (back button; chiudi if it works; the 30s window for wake-worded commands), and update the "UNTESTED device probe" line in the CLAUDE.md section "Stop / Session Routing During Playback (THE REFERENCE)".
- B2 result: if "successivo" routes, update the README FAQ "Why doesn't stop or next work while music is playing" entry (next becomes usable with documented words; stop remains pause-or-invocation-name), and resolve the "Divergence to keep honest" note in THE REFERENCE's audio-transport subsection. If it does not route, the default-music-service claim stands as the final answer for this device: record the negative in both places.
- Record the full results table in this task's notes (step, utterance, time, device behavior, log evidence) before closing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Part A (chiudi) executed on device: A1-A3 minimum, A4 optional; results recorded with per-step device behavior
- [ ] #2 Part B (audio transport words) executed: B1-B4 minimum, B5 optional; results recorded
- [ ] #3 Log evidence pulled and matched for every step (intent/type arrivals, session.new, timestamps vs launch); no-arrival explicitly distinguished from arrival-with-wrong-response
- [ ] #4 README FAQ updated in the same turn as the results: video stop/close entry added (chiudi verdict), stop/next music entry revised per the successivo verdict
- [ ] #5 CLAUDE.md 'Stop / Session Routing During Playback (THE REFERENCE)' updated: UNTESTED chiudi probe line resolved, audio divergence note resolved (doc claim vs device claim settled with evidence)
- [ ] #6 Results table appended to this task's notes before closing
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DoD applicability (2026-09-07, creator note): this is a device-test + documentation task, no code changes expected. Items 1-3, 7-8 are N/A unless the outcomes reveal a code fix (then spin a separate task for the fix and keep this one to test+docs). Items 9-10 apply only to the README/CLAUDE.md edits if a future executor considers them substantive; doc-only edits are gate-exempt per project rules.
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
