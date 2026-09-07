---
id: JF-517
title: >-
  Untested JF-515 levers: encode source via localhost instead of public https
  (Cloudflare hairpin), and -readrate cap; measure before adopting
status: Done
assignee: []
created_date: '2026-09-07 19:42'
updated_date: '2026-09-07 21:03'
labels:
  - performance
  - video
  - hls
  - resource-contention
  - needs-measurement
dependencies: []
references:
  - JF-515
  - JF-498
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From JF-515's measurement session (2026-09-07): two candidate levers for the residual 1-2s skill-request spike class (Jellyfin items-query stalls under concurrent encode I/O + segment serving, at box load 3+, even with plugin logging at Information):

(a) LOCALHOST ENCODE SOURCE. The video-audio encodes currently read the source over the PUBLIC https URL (observed ffmpeg args: `-i https://jellyfin.casanande.mywire.org/Videos/{id}/stream?static=true`), i.e. every encode hairpins through Cloudflare + TLS while the plugin runs IN-PROCESS with Jellyfin, so `http://127.0.0.1:8096/...` is always reachable and skips both. The concurrent segment fetching (device traffic) also uses the public URL and CANNOT be changed (that is the device's path), but the encode source is fully ours. Expected effect: removes ~250MB+ of TLS+CF round-trip per movie encode from the box's load budget.

(b) THROUGHPUT CAP on the encode (`-readrate`): bounds the encode's instantaneous read I/O at the cost of a slower cache warm. Only worth it if (a) is insufficient, since it trades warm time for contention.

HOW TO MEASURE (reuse JF-515's harness): kick the encode via the episode endpoint with a minted token, run a device-like segment fetcher against the public URL, fire PlayArtistSongs simulator probes, compare load + latency distribution vs the 2026-07 evening baselines recorded in JF-515's notes (Information-logging run: median 0.47s max 1.39s at load ~1-1.6; priority run: spikes to ~2s at load 3-4). For a pure URL A/B without code changes, run the same ffmpeg command line manually inside the container (`podman exec jellyfin /usr/lib/jellyfin-ffmpeg/ffmpeg ...`) with only the -i URL differing, and probe during both runs.

CAUTION: if implementing (a) in code, the change must stay scoped to the ffmpeg source URL used by the encode paths in VideoAudioController (the URLs handed to the DEVICE in directives must keep using the configured public ServerAddress). Mind the Jellyfin port binding (ServerAddress port may differ from the local listener; read the local http port from the server configuration, not by string-munging ServerAddress).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Controlled A/B on minix: same movie encode with source URL public-https vs http://127.0.0.1:8096 (manual ffmpeg runs are acceptable for the measurement), measuring box load and PlayArtistSongs simulator latency under encode + segment fetch traffic
- [ ] #2 Decision recorded in the task notes with numbers: adopt (implement in the encode URL builder, all video-audio encode paths) or reject with evidence
- [ ] #3 If rejected on evidence: note what was measured so the lever is not re-proposed without new facts
- [ ] #4 If adopted: code change scoped to the ffmpeg -i source URL only (device-facing URLs stay public), full suite green, gates run, deployed and verified on the box
- [ ] #5 -readrate cap: either measured in the same session or explicitly deprioritized with a one-line rationale
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MEASURED AND REJECTED (2026-09-07 night, controlled A/B on minix; probe = PlayArtistSongs via Simulator; per-artist baselines measured first to kill the query-cache confound that invalidated the first attempt): (1) THROTTLED encode (-readrate 30) public-https source vs localhost source: ALL probes at per-artist baseline in both conditions (pub deltas +0.02-0.06s vs its artists' baselines; loc probes IMPROVED vs baseline = warm cache). (2) UNTHROTTLED encode (natural ~30x, 300+ segments written, encode verified alive): pub 0.24-0.27s, loc 0.23-0.28s, both at baseline, load <=1.55. Neither the source URL hairpin (openresty+TLS, nginx ~27% during heavier encodes) nor the read pace measurably hurts skill-request latency in the controlled single-encode warm-cache regime. FIRST-ATTEMPT PITFALL (documented so it is not repeated): the initial A/B was invalid twice over - output dirs created on the HOST while ffmpeg runs in the CONTAINER (encodes died instantly) and reused artists in the same order (second condition rode Jellyfin's warm query cache; its 'spikes' were the first condition's cold-query costs).

BOUNDARY CONDITIONS for the contention that motivated this task: the 6-19s stall class was observed ONLY with the concurrence of post-restart COLD indexes + a TrueHD-source encode (139% CPU, 375s wall) + real device traffic + ~2.1GB swap in use. A controlled single encode with warm caches reproduces NO degradation under any source/throttle combination tested. Conclusion: no single encode-side lever (source URL, readrate) is worth shipping; the shipped levers that DO address the class are the stderr-flood removal (JF-515, merged) and the existing cold-start warming gates (JF-419). If the 6-19s class recurs on-device, capture box state (iowait, swap, index readiness) at that moment rather than pre-emptively changing encode plumbing.

Incidental security observation for the server admin (not plugin scope): the encode source URL /Videos/{id}/stream?static=true is served by this Jellyfin WITHOUT authentication (the plugin's ffmpeg reads it with no api_key; verified from the captured command line). That is a server configuration matter worth a look.
<!-- SECTION:NOTES:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
