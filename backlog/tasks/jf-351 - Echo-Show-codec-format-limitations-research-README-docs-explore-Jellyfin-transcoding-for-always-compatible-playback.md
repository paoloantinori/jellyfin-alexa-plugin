---
id: JF-351
title: >-
  Echo Show codec/format limitations - research + README docs; explore Jellyfin
  transcoding for always-compatible playback
status: Done
assignee: []
created_date: '2026-07-18 14:29'
updated_date: '2026-07-18 19:35'
labels: []
milestone: m-9
dependencies: []
references:
  - 'issue #12 (Live TV channels only display artwork on Echo Show)'
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Util/ILiveTvStreamResolver.cs (Live TV
    resolver)
  - 'CLAUDE.md: Live TV Channel Playback'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From issue #12 (Live TV on Echo Show). RUBIKOF reported (2026-07-18) that beyond HTTPS, IPTV channels need codecs the Echo Show supports; channels with unsupported codecs show a black screen / never play — an Echo Show limitation, not a plugin bug. @paoloantinori committed (issue comment 2026-07-18) to deep-research the limitations (official + unofficial docs) and document them in the main README.

This task captures that + adds an exploration:

1. **Research + docs** (the committed work): deep-research Echo/Echo Show codec + format support (video H.264 baseline — H.265?; audio AAC — AC3?, container + HLS constraints) from official Amazon docs + unofficial/developer findings. Document the supported matrix + the limitation (HTTPS + Echo-supported codec required for IPTV/Live TV) in the main README.

2. **Exploration** (added): whether Jellyfin itself can transcode streams to an always-Echo-compatible format. Today `ILiveTvStreamResolver` plays IPTV/M3U remote HLS DIRECTLY (H.264/AAC) — so an unsupported-codec channel fails. Only hardware-tuner sources take Jellyfin's transcoded-HLS fallback. Question: can the IPTV path ALSO route through Jellyfin transcoding when the source codec isn't Echo-compatible (transcode to H.264/AAC), so unsupported-codec channels play? Assess Jellyfin's transcoding API for live/remote sources, latency + server-load tradeoff, and whether it's feasible + worth implementing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Deep-research complete: documented matrix of Echo Show / Echo device codec+format support (video: H.264 baseline, H.265?; audio: AAC, AC3?; container + HLS requirements), sourced from official Amazon docs + unofficial/developer findings
- [x] #2 Main README documents the limitation: IPTV/Live TV channels require HTTPS + Echo-supported codecs (channels with unsupported codecs show a black screen — Echo limitation, not a plugin bug), with a link/anchor from the Live TV section
- [x] #3 Exploration recorded: feasibility of Jellyfin transcoding for unsupported-codec IPTV/remote streams — does Jellyfin's transcoding API accept a live/remote source and emit H.264/AAC the Echo plays? Latency + server-load tradeoff assessed. Recommendation (implement / document-and-accept) with rationale.
- [ ] #4 If the transcoding path is feasible + recommended: a follow-up implementation task is created (this task stays research/docs unless the decision is to implement here)
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
created: 2026-07-18 14:42
---
2026-07-18 autonomous research + exploration (Bash classifier flaky today, so recording findings + a README draft here; commit the README when tooling is stable):

## 1. Codec matrix (confirmed facts; authoritative full matrix still wanted)
- H.264 video + AAC audio over HTTPS = confirmed working: movies/episodes (issue #12 reporter), IPTV direct-play (CLAUDE.md, LiveTvStreamResolver doc line 25), and corroborated by the hls-restream-proxy note ('most free HLS streams are H.264 + AAC, which direct-play on virtually every client').
- Other codecs (H.265/HEVC video, non-AAC audio) → black screen / no playback (RUBIKOF's finding 2026-07-18): an Echo Show limitation, not a plugin bug.
- GAP: couldn't authoritatively confirm whether H.265/AC3 play on newer Echo Show models — crw/SearXNG search returned only generic codec articles, not Amazon device-spec docs. Recommend a targeted scrape of Amazon's Echo device specs / Alexa VideoApp docs (stable tooling) to nail the full matrix before the README claims one.

## 2. Transcoding exploration (the added ask) — VERDICT: not feasible via the existing Jellyfin path
Read LiveTvStreamResolver.cs. Two paths:
- Direct-remote (line 109-115): IPTV/M3U sources where MediaSource.SupportsDirectStream + Path is http(s) → the remote HLS URL is handed to VideoApp as-is. ExoPlayer plays whatever codec that HLS carries → unsupported codec = black screen.
- Transcode fallback (line 117-126): /Videos/{id}/master.m3u8?MediaSourceId=…[&LiveStreamId=…] — Jellyfin dynamic HLS (transcode). This is for hardware tuners.
KEY FINDING (resolver's own verified doc, line 26-27): 'Jellyfin's own HLS re-wrap via master.m3u8 → live.m3u8 500s here because the source is already HLS.' So for IPTV sources (already HLS), the master.m3u8 transcode path 500s — it CANNOT be reused to transcode unsupported-codec IPTV. The direct path is the only working IPTV path.
Alternatives for transcoding unsupported-codec IPTV:
 (a) A different Jellyfin transcoding invocation for a remote live HLS source — needs research (does Jellyfin expose another endpoint/profile that transcodes a remote HLS input rather than re-wrapping it?).
 (b) The plugin running ffmpeg to transcode the remote stream itself (like the audiobook HLS concat path) — heavy: live-TV transcoding is continuous server load + significant complexity, far beyond the audiobook (one-shot encode).
RECOMMENDATION: document-and-accept. The existing Jellyfin transcode path can't help (500s on HLS sources); plugin-side ffmpeg live-transcode is a substantial feature (continuous load + complexity). User guidance: use H.264/AAC IPTV channels. If transcoding unsupported-codec IPTV is genuinely wanted, track it as a separate heavy feature (plugin ffmpeg live-transcode or an external transcoding proxy) — not a quick fix.

## 3. README limitation draft (ready to commit; place under the 📺 Echo Show & visuals section or a new 'Known Limitations'):
> ### Live TV / IPTV channels
> Live TV channels launch via VideoApp.Launch (like movies and episodes) and play the channel's stream directly. For reliable playback the channel stream must be **H.264 video + AAC audio over HTTPS** — the formats the Echo Show supports. Channels that use other codecs (e.g. H.265/HEVC video, or non-AAC audio) may show a black screen or fail to start; this is an Echo Show codec limitation, not a plugin issue. Hardware tuners (HDHomeRun/DVB) that need transcoding are served via Jellyfin's dynamic HLS. Audio-only Echo devices (no screen) can't play channels (VideoApp requires a screen, same as movies).

(Adjust the codec claim if the authoritative Amazon-docs scrape confirms H.265/AC3 support on specific models.)
---

created: 2026-07-18 19:35
---
CLOSED via commit aaf9eba (README FAQ entry). Authoritative codec matrix sourced from Amazon's official VideoApp Interface Reference (developer.amazon.com/en-US/docs/alexa/custom-skills/videoapp-interface-reference.html, last updated 2025-10-30): video = H.264/MPEG-4 ONLY (no H.265/HEVC); HLS audio = AAC ONLY (AC-3/Dolby/Dolby Digital Plus supported only on SmoothStreaming/MP4/M4A, NOT HLS); max resolution 1280x720; HTTPS required. This closes the earlier 'authoritative gap' noted in comment #1 — the matrix is now definitive, not 'may fail'. AC#3 (transcoding exploration) verdict stands: document-and-accept. AC#4 precondition (transcoding feasible + recommended to implement) was NOT met — the existing Jellyfin master.m3u8 transcode path 500s on already-HLS IPTV sources (resolver doc line 26-27), and plugin-side ffmpeg live-transcode is a heavy separate feature (continuous server load) not justified without a demand signal; that option is documented in comment #1 for future reference. No follow-up implementation task created. Docs-only change (README FAQ) — no code/build/test impact.
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Researched Echo Show codec/format limits and Jellyfin transcoding feasibility for issue #12 (Live TV black-screen on Echo Show). Documented the limitation in the main README (commit aaf9eba) as a FAQ entry with the authoritative supported-format table from Amazon's VideoApp Interface Reference: IPTV/Live TV (HLS) plays reliably only as H.264 video + AAC audio over HTTPS; H.265/HEVC video or AC-3/E-AC-3 audio = black screen (Echo codec limit, not a plugin bug). Transcoding exploration verdict: document-and-accept — the existing Jellyfin transcode path cannot help (500s on already-HLS IPTV sources), and plugin-side ffmpeg live-transcode is a heavy unjustified feature.
<!-- SECTION:FINAL_SUMMARY:END -->
