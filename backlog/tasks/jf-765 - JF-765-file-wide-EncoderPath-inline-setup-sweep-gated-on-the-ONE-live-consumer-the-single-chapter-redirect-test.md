---
id: JF-765
title: >-
  JF-765 - file-wide EncoderPath inline-setup sweep, gated on the ONE live
  consumer (the single-chapter redirect test)
status: Done
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-05 02:21'
labels:
  - test-hygiene
dependencies: []
references:
  - >-
    backlog/tasks/jf-759 -
    EncoderPath-mock-wire-up-in-the-three-HLS-sibling-fixtures-is-inert-decide-delete-vs-keep-JF-751-second-idiom-sabotage-proven.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-759 gates (2026-10-05, the round's reserved out-of-scope number): the file-wide half of the
EncoderPath cleanup, which JF-759 deliberately did not take (its surface was the three sibling fixtures + their
call sites), plus two adjacent findings the round's marker probe surfaced.

## FINDING 1: the 54 residual INLINE EncoderPath setups, and the gate any sweep must pass first

After JF-759 deleted the three sibling fixtures' copies, ~54 inline
`_mediaEncoderMock.Setup(m => m.EncoderPath).Returns("/usr/bin/ffmpeg")` lines remain in
VideoAudioControllerTests.cs. Fresh classification by MARKER PROBE (stronger than the JF-751 broken-value
sabotage, which cannot see consumers whose asserts tolerate encode failure): point ALL 57 sites at an executable
marker script, run the full 266-test class on BOTH TFMs, count marker executions. Result: exactly TWO hits (one
per TFM run), all from ONE test: `StreamHlsAudiobook_SingleChapter_RedirectsToSingleItemHls` (~line 3368). It is
the ONLY test in the class that consumes the mock, because it is the only one that constructs a controller with no
FfmpegPath override and still reaches an encode (the single-chapter redirect falls into the single-item song core,
a cache miss starts the encode, ResolveFfmpegPath consults IMediaEncoder). NOTE the marker counts binary
EXECUTIONS, not mock reads: StreamHlsAudiobook resolves ffmpeg at entry (~VideoAudioController.cs:2962) AND the
redirected core again (~:4482), so the mock is read at least twice per request while the marker logs once. The
test stayed green under the JF-751
value-sabotage AND under the marker because its only assert is `IsNotType<NotFoundObjectResult>` (encode failure
tolerated) - which is exactly why the JF-751 evidence ("inert at every fixture consumer") was correct for the
fixtures but would have been WRONG as a file-wide claim.

Every OTHER inline site is inert by one of three mechanisms (verify locally before sweeping; the marker probe is
the cheap re-proof):

1. The test passes an explicit fake ffmpeg (CreateController ffmpegPath arg / .FfmpegPath assignment /
   WriteFakeFfmpeg / WriteRecordingFakeFfmpeg / WriteBlockingFakeFfmpeg / CreateEpisodeController): the override
   short-circuits ResolveFfmpegPath before the mock is ever read.
2. The test sets `.FfmpegPath = "/usr/bin/ffmpeg"` itself (8 sites: ~lines 500, 684, 847, 962, 987, 1256, 3315,
   3341): doubly bypassed, the mock value is never read.
3. The flow never reaches ResolveFfmpegPath (400/401/404 validation tests, pure builder/cache/playlist unit
   tests, planted-cache GetSegment tests).

THE GATE, in order: (a) give the redirect test an explicit fake ffmpeg (WriteFakeFfmpeg, like its MultiChapter
sibling a few tests below), which makes its inline setup inert AND removes its ambient-binary dependence
(FINDING 2); (b) delete all remaining inline EncoderPath setups in ONE pass; (c) neutrality proof = the class
266/266 green on both TFMs before/after (the JF-759 proof shape). The live site now carries a protective comment
(committed with JF-759) so a blind sweep is already warned off; keep or fold that comment into the test's arrange
when the fake lands.

## FINDING 2: the redirect test's ambient-binary dependence (pre-existing)

Today, on any machine with /usr/bin/ffmpeg (dev box, CI runners since ci.yml installs ffmpeg), the redirect test
runs the REAL ffmpeg: a lavfi black-frame encode against an unreachable `http://localhost:8096/...` input URL,
which fails fast and is tolerated by the assert. On a machine without it, the encode resolves an empty path. Same
ambient-binary class as the CLAUDE.md CI seatbelt note ("a test that depends on the ambient ffmpeg binary passes
locally and 503s on CI"); the ci.yml comment itself (".github/workflows/ci.yml" ~line 29, "Install it so the
ambient-path tests resolve") documents that at least one such test NEEDS the ambient binary on CI. Gate (a) above
fixes this for free.

## FINDING 3: ResolveFfmpegPath's doc comment states the wrong precedence (adjacent, doc-only)

`VideoAudioController.ResolveFfmpegPath`'s summary says "Tries IMediaEncoder first, then falls back to PATH
lookup", but the code checks the explicit `FfmpegPath` override FIRST, then IMediaEncoder (gated on
File.Exists), then a PATH scan. The wrong precedence story is the misconception that plausibly seeded 57
copy-paste setups; fix the comment when next touching the file.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Gate applied: the single-chapter redirect test drives an explicit fake ffmpeg (its ambient-binary
      dependence gone), or a keep decision recorded at the site with the reason
      (WriteRecordingFakeFfmpeg "fake-ffmpeg-single-chapter-redirect", 3-digit seg_000.ts = the redirected
      song core's poll shape, threaded via CreateController(parentId, ffmpegPath:); assert upgraded from
      IsNotType<NotFound> to IsType<ContentResult> + the exact m3u8 content type so a failed encode reds
      (the weak assert is why no sabotage could see this consumer). Verification: single-test run with the
      EncoderPath setup sabotaged to an executable MARKER script = GREEN with ZERO marker executions (the
      override short-circuits both consult sites); PLUS the direct ambient proof = green with PATH stripped
      of every ffmpeg (only the fake's shell tools symlinked into a scratch bin). The protective JF-759
      comment folded into the arrange as the gate required.
- [x] #2 If sweeping: every remaining inline EncoderPath setup deleted in one pass, marker probe re-run clean
      (zero consumers), class 266/266 green on BOTH TFMs before/after
      (all 53 residual setups pointed at the marker script in ONE pass: full class 266/266 green on BOTH
      TFMs with ZERO marker executions across both runs; then all 53 deleted; class 266/266 green on BOTH
      TFMs before AND after (baseline re-taken first). Sweep bonus, beyond the filing: a no-ffmpeg-on-PATH
      class run caught ONE marker-invisible consumer the execution-counting probe structurally cannot see
      (StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock: entry validation 503s on an UNRESOLVED
      ffmpeg before the in-lock probe runs, so the flow RESOLVES but never EXECUTES ffmpeg); it now carries
      an explicit exit-0 fake and the WHOLE class is 266/266 green with no ffmpeg anywhere, both TFMs. Full
      suite 5205/5205 on BOTH TFMs, normal PATH AND no-ffmpeg PATH (= main baseline).)
- [x] #3 ResolveFfmpegPath's doc comment states the real precedence (override, then encoder, then PATH)
      (VideoAudioController.cs ~5752; verified against the code by direct read BEFORE editing. The simplify
      round also found and fixed the SECOND copy of the same misconception on the FfmpegPath property's own
      summary ~:203 ("Resolved from Jellyfin's IMediaEncoder service"), the site a test author greps first.)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
THE GATE, then the sweep, exactly in the filing's order. (1) The redirect test
(StreamHlsAudiobook_SingleChapter_RedirectsToSingleItemHls) now drives an explicit
WriteRecordingFakeFfmpeg with the 3-digit seg_000.ts (the redirect lands in the
single-item SONG core, whose first-segment wait polls the 3-digit name) via
CreateController(parentId, ffmpegPath:), and its assert was upgraded from the
failure-tolerating IsNotType<NotFoundObjectResult> to IsType<ContentResult> + the
exact application/vnd.apple.mpegurl content type, the shape that makes the hermeticity
check NON-VACUOUS (with the weak assert, "green under sabotage" proves nothing; that is
precisely how the JF-751 value-sabotage missed this consumer). Marker red-check: with the
setup sabotaged to an executable marker script, the test is GREEN with ZERO marker
executions, and it is green with every ffmpeg removed from PATH: the override
short-circuits both consult sites (entry ~VideoAudioController.cs:2962 and the redirected
core ~4482), so neither the mock nor the PATH scan is ever reached. The ambient-binary
dependence (Finding 2) is gone. (2) All 53 remaining inline setups (54 in the filing
minus the gate site's deleted one) pointed at the marker in ONE pass: class 266/266 green
on BOTH TFMs with ZERO executions; then all 53 deleted; class 266/266 green on BOTH TFMs
before AND after; full suite 5205/5205 on BOTH TFMs (= main baseline). (3) The
ResolveFfmpegPath doc comment now states the real precedence (override, then IMediaEncoder
gated on File.Exists, then PATH scan), verified against the code by direct read first.

BEYOND THE FILING, two strengthenings earned by the same methodology: (a) a no-ffmpeg-
on-PATH run of the whole class exposed ONE consumer the marker probe STRUCTURALLY cannot
see (it counts executions; StreamHlsVideoAudio_ThrowingInLockProbe_ReleasesTheItemLock
RESOLVES ffmpeg but never EXECUTES it, because entry validation 503s on an unresolved
ffmpeg before the in-lock probe runs); it now carries an explicit exit-0 fake, and the
WHOLE class is green with no ffmpeg anywhere on PATH on both TFMs. The JF-759
classification's mechanism 3 ("flow never reaches ResolveFfmpegPath") had a hole for
resolve-but-not-execute consumers; the restricted-PATH run is the tool that closes it.
(b) The full 5205-test suite was additionally verified green on BOTH TFMs under the same
no-ffmpeg PATH, so the ci.yml ffmpeg install is now provably unnecessary for the suite;
the install is KEPT as a seatbelt with its comment rewritten to the post-sweep reality
(the old comment's stated reason, "several test fixtures mock IMediaEncoder.EncoderPath",
is falsified by the sweep).

DOCUMENTATION consolidated: the never-configured contract now lives once, as a comment
on the _mediaEncoderMock field itself (the first thing a future setup-writer touches,
the regrowth vector that cost the JF-751 to JF-759 to JF-765 chain); the three per-fixture
"No EncoderPath setup" clauses (JF-759) became vacuous non-distinctions post-sweep and
were trimmed. The simplify round also fixed the SECOND copy of the precedence
misconception on the FfmpegPath property's summary ("Resolved from Jellyfin's IMediaEncoder
service"), the doc a test author greps first when injecting a fake.

Gates: Skill simplify (4 angles: reuse CLEAN, efficiency CLEAN; applied 3 blank-line
removals + the probe-comment tighten (simplification), the FfmpegPath property doc fix +
the field-level contract replacing the three vacuous fixture clauses (altitude); skipped
with reason: the redirect-test historical sentence (task-ID history convention) and the
efficiency trivia on the shared WriteFakeFfmpeg helper (pre-existing shape, not
diff-introduced)) + Skill code-review high (0 correctness bugs; its one low-severity
finding, that the field comment overstated hermeticity and CI would mass-red without
ambient ffmpeg, was adjudicated: the mass-red scenario REFUTED by the direct experiment
(full suite green both TFMs with no ffmpeg on PATH, because the 404 families' plain-string
.FfmpegPath overrides pass the non-empty gate with no File.Exists), and the legitimate
wording half APPLIED). NOT FILED (noted, below the JF-761 noted-not-filed bar):
VideoAudioCacheTests' two EvictIfNeeded_Unreadable* tests shell out to external
chmod via PATH (Process.Start("chmod")), an ambient-binary dependence of the same class
but with zero real-world risk (every Linux runner and dev box ships chmod; it only
surfaces under an artificial PATH restriction).

TEST-ONLY plus doc comments and the ci.yml comment: no DLL deploy (the controller change
is comment-only, verified by hunk inspection).

CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker commit 97411fbb, --no-ff) under the scaled verification (the comment-only controller hunk, the sweep count, and the hermetic fake read directly); 5205/5205 both TFMs both PATH states. TEST-ONLY: no deploy. JF-768 unused; the chmod ambient-binary note recorded in the task file below the filing bar.
<!-- SECTION:FINAL_SUMMARY:END -->
