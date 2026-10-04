---
id: JF-765
title: >-
  JF-765 - file-wide EncoderPath inline-setup sweep, gated on the ONE live consumer (the single-chapter redirect test)
status: To Do
assignee: []
created_date: '2026-10-05'
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
- [ ] #1 Gate applied: the single-chapter redirect test drives an explicit fake ffmpeg (its ambient-binary
      dependence gone), or a keep decision recorded at the site with the reason
- [ ] #2 If sweeping: every remaining inline EncoderPath setup deleted in one pass, marker probe re-run clean
      (zero consumers), class 266/266 green on BOTH TFMs before/after
- [ ] #3 ResolveFfmpegPath's doc comment states the real precedence (override, then encoder, then PATH)
<!-- DOD:END -->
