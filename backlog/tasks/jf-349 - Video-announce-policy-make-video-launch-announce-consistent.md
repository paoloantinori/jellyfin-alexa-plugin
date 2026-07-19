---
id: JF-349
title: Video announce policy - make video-launch announce consistent
status: Done
assignee: []
created_date: '2026-07-18 14:27'
updated_date: '2026-07-19 06:52'
labels: []
milestone: m-1
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by /code-review high (finding C1). PlayRandom's video path (movies/episodes) now speaks a now-playing SSML announce — commit 1318c2c attached `OutputSpeech` to fix a dead store — but the sibling video-launch handlers (PlayVideoIntentHandler, PlayEpisodeIntentHandler) launch silently. Even PlayRandom's own audio branch doesn't announce. Inconsistent UX.

Decide the policy: (a) all video launches announce (add to PlayVideo/PlayEpisode — recommended, consistent with audio + accessibility for voice-only devices), (b) none do (revert PlayRandom video announce), or (c) keep as-is. VideoApp+OutputSpeech is a valid standard pattern (precedent: RecommendIntentHandler:200). Verify on-device.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A recorded decision on the video-launch announce policy (all / none / per-handler)
- [x] #2 Implementation matching the decision across PlayRandom, PlayVideo, PlayEpisode video paths
- [x] #3 On-device verification: video launch either announces consistently or stays silent consistently
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-18 20:27
---
DECISION (AC#1): all PRIMARY direct video launches announce. IMPLEMENTED (AC#2): PlayVideo fresh-launch + PlayEpisode now speak now-playing via BuildOutputSpeech (was silent); PlayRandom's hand-rolled GetSsml/plain announce collapsed onto the same BuildOutputSpeech call (output-identical, also closes JF-350's latent plain-fallback for that path). PlayVideo's resume branch unchanged (still 'ResumingVideo'). ON-DEVICE VERIFIED (AC#3, 2026-07-18, minix simulator it-IT): PlayVideoIntent 'Aladdin' -> VideoApp.Launch + '<speak>In riproduzione...Aladdin</emphasis></speak>' (announce present, well-formed SSML). SCOPE NOTE: secondary video-launch paths (PlayChannel, ContinueWatching, SearchMedia, StartOver, Resume, AplUserEvent) still launch silently -- a follow-up if full coverage is wanted; this task's stated scope (PlayRandom/PlayVideo/PlayEpisode) is covered. REVIEW: builds on JF-350's code-reviewed BuildOutputSpeech; unit tests (PlayVideo fresh-launch + PlayEpisode announce + PlayRandom reserved-chars DRY) + on-device verified; /code-review high not separately run for this small DRY change -- available on request.
---

created: 2026-07-19 06:51
---
FULL COVERAGE + REVIEW (2026-07-19): Extended to all remaining video-launch paths and re-reviewed. Secondary handlers added: PlayChannel, SearchMedia (PlayItem video branch), AplUserEvent (Movie branch, selectItem + carouselTap). /code-review high then found a real gap (F1): YesIntentHandler.PlayVideo -- the disambiguation-confirmed Movie launch ('play movie X' -> multiple matches -> 'yes') -- was still silent; fixed (thread locale into PlayVideo + announce). All video launches now announce via a shared BaseHandler.BuildNowPlayingSpeech(name, locale) helper (DRYs 6 duplicated BuildOutputSpeech calls; review finding C1). Removed the per-site narration comments (finding C2 -- CLAUDE.md comment rule). Commits: c258535 (primary), 790ca72 (secondary), a720169 (review fixes). On-device verified (minix simulator it-IT, build a720169): PlayVideo 'Aladdin' + PlayChannel '100% News (576p)' both -> VideoApp.Launch + 'In riproduzione <name>' announce. Full suite 2527/2527. /code-review high: 1 bug (F1) + 2 cleanup (C1/C2) applied; 4 findings noted as OUT-OF-SCOPE follow-ups (tracked in JF-353): C4 resume-position awareness for AplUserEvent/SearchMedia; F2 audiobook fresh-start (NativeControlsForBooks); F3 audio-over-VideoApp (GetVideoAppForAudio); F4 AudioPlayer audio-branch announce.
---

created: 2026-07-19 06:52
---
Correction: the follow-up residuals task was created as JF-352 (not JF-353 as written in comment #2/final summary above). Reference: JF-352.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
All video launches now speak a now-playing announce for consistency + voice-only-device accessibility. PlayVideo (fresh launch), PlayEpisode, PlayChannel, SearchMedia (video branch), AplUserEvent (Movie tap), and the YesIntent disambiguation-confirmed Movie path all announce via a shared BaseHandler.BuildNowPlayingSpeech(name, locale) helper; PlayRandom collapsed onto the same helper. StartOver/Resume/ContinueWatching already announced (unchanged). /code-review high applied: fixed the YesIntent disambiguation gap (F1), extracted the helper (C1), removed narration comments (C2). On-device verified (PlayVideo 'Aladdin', PlayChannel '100% News'). Full suite 2527/2527. Residual announce gaps (resume-position awareness, audiobook/audio-over-VideoApp) tracked as JF-352.
<!-- SECTION:FINAL_SUMMARY:END -->
