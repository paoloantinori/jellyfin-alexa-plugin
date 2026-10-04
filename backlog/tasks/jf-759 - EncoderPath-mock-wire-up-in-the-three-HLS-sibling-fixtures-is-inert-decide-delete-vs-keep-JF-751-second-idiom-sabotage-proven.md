---
id: JF-759
title: >-
  EncoderPath mock wire-up in the three HLS sibling fixtures is inert: decide
  delete-vs-keep (JF-751 second idiom, sabotage-proven)
status: Done
assignee: []
created_date: '2026-10-04 19:29'
labels:
  - test-hygiene
dependencies: []
references:
  - >-
    backlog/tasks/jf-751 -
    family-agnostic-deleting-logger-factory-micro-helper-collapse-the-15-site-file-wide-idiom.md
  - >-
    backlog/tasks/jf-765 -
    file-wide-EncoderPath-inline-setup-sweep-gated-on-the-one-live-consumer-the-single-chapter-redirect-test.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-04 same-turn from JF-751's execution (the filing's SECOND IDIOM; JF-751's reserved out-of-scope number).

THE FINDING: the `_mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg")` wire-up, triplicated across the three sibling fixtures SetupEpisodeForHls / SetupSongVanishFixture / SetupAudiobookVanishFixture (Jellyfin.Plugin.AlexaSkill.Tests/Controller/VideoAudioControllerTests.cs), is INERT at every consumer today, so the proposed shared helper fails the JF-692-family non-vacuity bar (the helper's wiring broken once must red the consumers).

EVIDENCE (all verified live 2026-10-04 in the JF-751 worktree):
1. VideoAudioController.ResolveFfmpegPath (Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs, the FfmpegPath-override-first arm) returns the controller's explicit FfmpegPath before ever consulting _mediaEncoder.EncoderPath; EncoderPath is a fallback-only source with a further PATH scan behind it.
2. All 32 fixture call sites in the file pass an explicit fake ffmpeg (a CreateController ffmpegPath argument, positional fake, or a controller.FfmpegPath assignment next to the call); none resolves through the mock.
3. The decisive sabotage: the three fixtures were converted to a shared SetupEncoderPathMock helper and the helper broken (nonexistent path returned); ALL 266 VideoAudioControllerTests stayed GREEN on BOTH TFMs. A load-bearing wire-up would have red its consumers (compare the same round's logger-helper sabotage: 14 of 15 consumers red).

WHY NOT DONE IN JF-751: the JF-692 mandate for this family requires arrange-only consolidation with non-vacuity proven by shared sabotage; a helper whose wiring provably cannot red any consumer is exactly the vacuous-helper shape the JF-751 filing itself warns against, so the EncoderPath conversion was reverted there and this decision filed instead.

FIX DIRECTION (a decision, not a mechanical consolidation): either DELETE the wire-up from the three fixtures (a small semantic change beyond arrange-only: it only matters for a future consumer that constructs a controller without the FfmpegPath override on a machine with no ambient and no PATH ffmpeg, where deletion changes the failure shape from wrong-fake-path to empty-path), or KEEP it as a fallback-only convention and document that at one site. NOTE for whoever takes this: the file carries ~60 further INLINE EncoderPath setups outside the three fixtures with the same inertness question (all encode-running tests also pass an explicit fake); sample a few before deciding whether the cleanup is fixture-scoped or file-wide.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors: solution Release --no-restore -warnaserror 0 Warnings 0 Errors (both
      TFMs); test project rebuilt clean after every gate edit
- [x] #2 dotnet test passes: full suite 5194/5194 net9.0 AND net10.0 on the final state (= main baseline 5194;
      only comment edits postdate the run, rebuild-verified); class neutrality 266/266 on BOTH TFMs before AND
      after the deletion
- [x] #3 No new compiler warnings introduced: -warnaserror together with TreatWarningsAsErrors ran clean
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code
      touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: test-hygiene decision, no handler logic)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new strings)
- [x] #9 /simplify passed: 4 angles (reuse, simplification, efficiency, altitude); efficiency CLEAN; applied the
      audiobook-fixture doc reduced to the pointer form (dedup with the episode fixture's canonical JF-759 note)
      and the protective comment tightened; declined with reason the canonical ResolveFfmpegPath doc-comment fix
      (production file, outside the task's test-file surface) - filed as JF-765 Finding 3; ci.yml comment
      adjudicated by direct read (two agents disagreed): "several test fixtures mock ..." remains true for the 54
      survivors, no edit
- [x] #10 /code-review high passed: two independent high-effort reviews, both verdict "no correctness bugs";
      findings adjudicated - applied: the episode-fixture doc's wiring-form parenthetical dropped (all 23 of ITS
      callers use only the CreateController argument form) and the protective comment now names BOTH consult sites
      (entry ~VideoAudioController.cs:2962 + the redirected core ~:4482; the marker counts executions, not reads);
      tracked in JF-765: the ResolveFfmpegPath doc precedence (Finding 3), the redirect test's ambient real-ffmpeg
      dependence (Finding 2), and the protective comment's eventual staleness once the JF-765 gate lands (fold
      clause in Finding 1)
<!-- DOD:END -->

## Final Summary

DECISION (2026-10-05): DELETE the EncoderPath wire-up from the three sibling fixtures; KEEP the one live inline
consumer; the file-wide sweep of the 54 residual inline setups is REFUSED for this round and filed as JF-765,
gated on first giving the consumer test an explicit fake.

FRESH VERIFICATION, STRONGER THAN THE FILING'S: the decisive sabotage was re-run as a MARKER PROBE - all 57
EncoderPath setups (the three fixtures' included) were pointed at an EXECUTABLE marker script and the full
266-test class ran on BOTH TFMs: green 266/266 with the marker executing exactly TWICE (once per TFM), both hits
from ONE test. A broken-VALUE sabotage (the JF-751 method) can never see this consumer because its assert
(IsNotType<NotFoundObjectResult>) tolerates encode failure; the marker makes consumption observable. Attribution
confirmed by a filtered re-run of the single test (exactly one hit). The one consumer is
StreamHlsAudiobook_SingleChapter_RedirectsToSingleItemHls (~line 3368), an INLINE setup, not a fixture: it builds
the only controller in the class with no FfmpegPath override that still reaches an encode (the single-chapter
redirect falls into the song core). This REFUTES the file-wide reading of the JF-751 evidence while CONFIRMING it
for the fixtures: zero of the three fixtures' 31 callers consumed the mock (static audit: every one wires an
explicit fake via the CreateController ffmpegPath argument, a .FfmpegPath assignment, or the fake-writer helpers;
dynamic: zero marker hits). The mock is loose, so deletion is a no-op: un-setup EncoderPath returns null and
ResolveFfmpegPath null-guards it into the PATH scan - and every fixture caller short-circuits on the override
before the mock is read at all, on every machine. Production has exactly ONE read site
(VideoAudioController.ResolveFfmpegPath ~:5766) behind the override; no other test file touches EncoderPath.

EXECUTED: the three setup lines deleted; the three fixtures' doc comments now record the no-EncoderPath contract
(the episode fixture carries the canonical JF-759 note, the two siblings point at it); the kept consumer's site
carries a protective comment (the ONE consumed setup; both consult sites; do-not-blind-delete; JF-765 pointer).

BLAST RADIUS: zero. All 31 fixture callers stay green unedited (class 266/266 both TFMs before AND after; full
suite 5194/5194 both TFMs = main baseline). The 54 inline setups and the one live consumer are untouched.

FILED AS JF-765 (the round's reserved number): the gated file-wide sweep, the redirect test's ambient
real-ffmpeg dependence (pre-existing, the ci.yml seatbelt class), the ResolveFfmpegPath doc-comment precedence
bug, and the executions-vs-reads marker methodology note.

Gates: Skill simplify (4 angles, 2 applied / 1 declined-filed / 1 adjudicated no-edit) + Skill code-review high
(two completed high-effort reviews; 0 correctness bugs; 2 comment-precision fixes applied; 3 findings tracked in
JF-765). A third dispatched reviewer did not report within its window; two completed reviews satisfy the gate.

One transient observation, not a regression: a single simultaneous dual-TFM class run had 1 net10.0 failure in
MonitorHls_HungEncode_KilledAfterOneStallBudget (300ms stall-budget timing test); it passed alone on net10.0, in
both sequential per-TFM class runs, and in both full-suite runs (the documented cross-test-sweep timing class,
same family as JF-751's observation). TEST-ONLY change: no deploy.
