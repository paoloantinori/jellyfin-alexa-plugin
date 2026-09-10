---
id: JF-534
title: >-
  Transcode-tier cache reserve (3072MB/h) exceeds the default
  VideoAudioCacheSizeMB (2048MB): E2 cache dir vanished post-play; decide cap
  default vs budget clamp + find what deleted it
status: Done
assignee: []
created_date: '2026-09-10 06:38'
updated_date: '2026-09-10 11:10'
labels:
  - bug
  - cache
  - video
  - hls
  - jf500-followup
dependencies: []
references:
  - JF-500
  - JF-428
  - corr=c0c21c6a
  - 'Jellyfin.Plugin.AlexaSkill/Configuration/PluginConfiguration.cs:231'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs:2497'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-531 device session (2026-09-09, corr=c0c21c6a), secondary observation: the Adolescence E2 transcode cache dir was ABSENT ~10 min post-play (0 segments on disk). Config facts on main: PluginConfiguration.VideoAudioCacheSizeMB defaults to 2048 MB, while EstimateEpisodeTranscodeEncodeBytes (JF-500) reserves 3072 MB/h - a 51-min HEVC episode reserves ~2.6 GB, EXCEEDING the whole default cap before any other content counts. The JF-428 pre-encode budget pins the in-flight entry (refcounted) so in principle the sweep cannot delete an encoding dir, yet the observed dir is gone with 0 segments. Candidate explanations to distinguish: (a) a LATER sweep for a subsequent play evicted the completed E2 cache (fair eviction of completed content, cap simply too small for the tier); (b) post-completion validation failed (segment count < chapters check) and deletion followed; (c) the JF-500 monitor kill + cleanup raced; (d) the pin did not actually protect (regression in the JF-428 pin path under reserve > cap). Decide and implement: either raise the default cap to cover a typical transcode-tier episode (e.g. 4096 MB, remembering disk usage is user-visible) or make the budget path WARN + clamp sensibly when the reserve exceeds the configured cap, and fix whichever mechanism actually deleted the dir (reproduce locally with a small cap + a fake encode). Keep JF-428's invariant intact: an in-flight encode's dir is never evicted.
<!-- SECTION:DESCRIPTION:END -->

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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
DIAGNOSIS (live-log verified, /config/log/log_20260909.log): mechanism (a), fair eviction of completed content under a too-small cap. Timeline: 18:09:27 user launches Adolescence E2 (item 0268dd4e, HEVC, transcode tier); 18:09:30 pre-encode sweep evicts E1's completed 2396MB dir (6c205103) to reach the 2048 target; encode runs 18:09:30-18:24:41 (15.2 min, ~3.4x realtime, inside the 36-min scaled monitor timeout so (c)'s kill never fired); playback stops ~18:11:43 (last playlist serve); 18:24:41 encode completes (772 segments), pin released by design at ffmpeg exit, and the monitor's post-encode sweep (headroom 0, full-cap target) finds 2781.6MB > 2048MB with the E2 dir's last serve 12m58s past the 10-min PlaybackEvictionExemptionTtl, so it evicts abb7d668 (34MB) AND E2 (2747MB). Zero 'invalidated'/'TIMED OUT'/stub lines in the day's logs: (b) and (c) never fired. (d) refuted: the pin held through the whole 15-min encode (nothing touched the dir mid-encode); its scope is the WRITE window only, exactly as the JF-428 design comment states.

KEY MEASURED FACT: the completed 51-min episode dir is 2747MB (~3.2GB/h, confirming the 3072MB/h reserve), so warn+clamp on the pre-encode reserve would NOT have saved it: the deletion was the POST-encode sweep, and a 2.7GB completed dir exceeds the 2048 cap by itself. Only the cap raise fixes it.

DECISION: option (i), default VideoAudioCacheSizeMB 2048 -> 4096. Also updated: config.html label ('Album Art' -> 'Video-Audio Cache Size (MB)'), field description now names the ~3GB/h transcode tier cost, both JS 2048 fallbacks, the EvictIfNeededCore ?? fallback, and the stale doc comments (VideoAudioCache JF-431 threshold notes, EstimateEpisodeTranscodeEncodeBytes default-cap interaction). Residual (documented, not fixed): a completed transcode of >~80min (2h movie ~6.5GB) still outgrows the 4096 cap and relies on the playback-recency window plus oldest-first eviction.

Worker (worktree jf534, branch fix/jf534-transcode-cache-budget, uncommitted): Release build both TFMs 0 warnings; net9.0 suite 3562/3562 (incl. 2 new: Constructor_InitializesVideoAudioCacheSizeMBTo4096, EstimateEpisodeTranscodeEncodeBytes_OneHourReserve_FitsUnderDefaultCacheCap). NOTE: config.html is an embedded resource; deploy needs a clean rebuild (delete output DLL first).

ADDENDUM (coordinator live check): the production XML persists <VideoAudioCacheSizeMB>2048</VideoAudioCacheSizeMB> explicitly, so the raised default alone was inert on every existing install. Added the stale-default migration per the JF-297/300 precedent: Plugin.MigrateStaleCacheCapDefault (internal static, called from the ctor right after MigrateDefaultInvocationNames, same shape/SaveConfiguration-best-effort pattern) raises a stored 2048 to 4096; a stored value equal to the old default IS the old default, not a deliberate choice (a re-lowered value sticks until next load, same property as the JF-300 name migration). Tests: CacheCapMigration_StoredOldDefault_RaisedOnceAndIdempotent + CacheCapMigration_UserChosenValue_Untouched(8192,512). Final: Release both TFMs 0 warnings; net9.0 suite 3565/3565.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed 2026-09-10 with merge 93596948 + deployed (clean rebuild for the embedded config.html; active DLL md5 42ba72ca verified) + live-verified: the migration fired on the production box ('Cache cap migrated 2048 -> 4096' log line at 13:08:49, XML now <VideoAudioCacheSizeMB>4096), and the served config page carries the corrected 'Video-Audio Cache Size (MB)' label + the ~3GB/h description (the old 'Album Art' label gone). Diagnosis was live-log-proven (mechanism (a): post-encode sweep, headroom 0, full-cap target, last serve 12m58s past the 10-min exemption TTL; pin held through the encode; monitor-kill and validation refuted). Gates: /simplify (2 combined angles; findings applied: named consts, incident narrative canonicalized on the field doc - 5 retellings to 1 + pointers; grep-verified no missed 2048 hardcodes), code-review high (ONE finding applied: the ~80min reserve-fit boundary contradicted the round-up estimator - reserve fits ONE rounded hour, 61-120min reserves 6144MB and runs the half-cap-floor regime; ~80min kept only as the measured-bytes boundary, now disambiguated; below-threshold migration log line added - it paid off immediately as the deploy's migration evidence), tests 3565/3565 net9.0 final (5 new: migration x3, default pin, estimator pairing). Follow-up JF-537 filed (oversize content churns invisibly).
<!-- SECTION:FINAL_SUMMARY:END -->
