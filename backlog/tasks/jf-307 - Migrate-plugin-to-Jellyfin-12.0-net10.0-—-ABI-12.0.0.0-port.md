---
id: JF-307
title: Migrate plugin to Jellyfin 12.0 (net10.0) — ABI 12.0.0.0 port
status: To Do
assignee: []
created_date: '2026-07-03 21:10'
updated_date: '2026-09-09 09:54'
labels: []
dependencies: []
references:
  - 'https://jellyfin.org/posts/state-of-the-fin-2026-05-24/'
  - 'https://github.com/jellyfin/jellyfin/releases'
  - 'https://www.nuget.org/packages/Jellyfin.Controller/12.0.0-rc2'
  - >-
    https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Server/Jellyfin.Server.csproj
documentation:
  - >-
    Project memory: jellyfin-10.11-migration (the prior 10.8→10.11 port — same
    shape of breakage expected)
  - >-
    CLAUDE.md → Release (manifest/targetAbi/version flow) and Project Layout
    (handler/DB surfaces)
  - >-
    CLAUDE.md → Key Gotchas (stream endpoints, IUserDataManager usage, plugin
    container file access)
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Jellyfin's next server release is **12.0** (not 10.12 — they dropped the "10" prefix). It is currently at **RC2**; stable is imminent. Every released version of this plugin is pinned to `targetAbi: 10.11.0.0`, so the moment a user upgrades their Jellyfin server to 12.0, **the plugin will stop loading** until a 12.0-compatible line ships. This task lands that line.

**Confirmed upstream facts (verified against primary sources, July 2026):**
- Jellyfin 12.0 runs on **.NET 10** — the server's own `Jellyfin.Server/Jellyfin.Server.csproj` on `master` declares `<TargetFramework>net10.0</TargetFramework>`. This plugin is currently `net9.0`, so the TFM must bump too. This is NOT just a version-string edit.
- SDK packages already published as prerelease: `Jellyfin.Controller` / `Jellyfin.Model` **12.0.0-rc2** on NuGet (current refs are `10.11.8`).
- 12.0 builds **further database/EF Core changes** on top of the 10.11 SQLite→EF Core migration. Jellyfin's official guidance: repository plugins should be **removed before migrating** and re-added after, and DB changes **prevent rollback without a full restore**.
- 12.0 **disables deprecated authorization mechanisms by default** (temporarily reverted during RC because official clients lag — but it is coming). Our controllers + simulator use `X-Emby-Token`; confirm this scheme survives.
- 12.0 includes a big Performance PR (#16062) that reworks queries for music, playlists, collections, and books — re-test the audiobook (JF-292) and playlist paths.

**Why this is shaped like the 10.8→10.11 migration (which became the 0.2.0.0 line):** the breakage is expected in the server-facing layer, not the Alexa-facing code. The surfaces that broke last time (see project memory `jellyfin-10.11-migration`) are the prime suspects again:
- `IUserDataManager` (was `IUserDataRepository`), `Jellyfin.Database.Implementations.Entities.User`
- `ILibraryManager.GetItemList()` return types, `BaseItemDto.Type` / `BaseItemKind`
- Anything reading `BaseItem` collections, session/playback state, playlist members

**Approach:** Phase 1 is a spike — retarget TFM + SDK in a throwaway branch, build, and **catalog every compile/runtime break** with a written findings list. Phase 2 applies fixes; if the breakage is large, spin off follow-up subtasks from the findings rather than ballooning this task. The 12.0 simulator/E2E verification needs a 12.0 Jellyfin container (the minix box currently runs 10.11) — stand one up via the unstable Docker tag.

This task is intentionally queued ahead of stable so it's ready to execute the moment 12.0 ships; the spike (Phase 1) can start now against RC2.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 `dotnet build -c Release` passes with 0 warnings against `Jellyfin.Controller`/`Jellyfin.Model` 12.0.x with `<TargetFramework>net10.0</TargetFramework>` in the plugin .csproj
- [ ] #2 Full unit test suite passes against the 12.0 SDK (no tests skipped, disabled, or commented out)
- [ ] #3 Plugin loads on a Jellyfin 12.0 server (RC2 or stable) with no startup errors in `podman logs jellyfin`
- [ ] #4 Database-layer surfaces verified working on 12.0: user resolution, `IUserDataManager` (favorites + played progress), playlist member reads (the 0.9.1.0 playlist-API path), and the artist/song index builds — the surfaces that broke in the 10.8→10.11 migration
- [ ] #5 Controller + simulator authentication confirmed still accepted by 12.0 (`X-Emby-Token`), or migrated to whatever 12.0 enforces under its deprecated-auth policy
- [ ] #6 `manifest.json` carries a new version entry with `targetAbi: 12.0.0.0`; `python3 scripts/validate_versions.py` reports all 3 sources (Directory.Build.props, build.yaml, manifest.json) consistent
- [ ] #7 CI pipelines (`ci.yml`, `dev-build.yml`, `release-build.yml`) build and test under net10.0 and produce net10.0 artifacts
- [ ] #8 Simulator + E2E verification passes on a 12.0 server for at least: play song, play artist, play playlist (incl. shuffle-at-start), and audiobook seek/resume (JF-292 path)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PRIORITIZED 2026-09-07 by Paolo: validation against jellyfin 12.0.0-RC7 (verified on NuGet: the 12.0.x tail is rc3..rc7; the task's RC2 references are stale) in a DEDICATED WORKTREE. Co-existence is the explicit goal: 'nella speranza che il codice sia compatibile con entrambe le versioni, o dobbiamo avere una strategia per la co-esistenza'. The candidate strategy (to be PROVEN by the spike, not assumed): multi-target net9.0+net10.0 with Condition'd PackageReference (10.11.8 for net9.0, 12.0.0-rc7 for net10.0) and a second manifest entry targetAbi 12.0.0.0 - the pattern the official plugin ecosystem uses. The spike's first question: does the source compile under BOTH TFMs unchanged (the hoped-for outcome), or which files need #if/net10 branches, or is a hard fork unavoidable (the dis-preferred outcome).

PRODUCTION REALITY CHANGE (2026-09-09 ~07:20): the minix server AUTO-UPDATED to Jellyfin 12.0.0 (linuxserver/jellyfin:latest pulled the major; /System/Info/Public confirms Version 12.0.0). LIVE FINDINGS from the boot logs: (1) plugin 0.12.1.0 (net9.0, built against the 10.11 ABI) LOADS AND STARTS CLEANLY on 12.0 - 'AlexaSkill plugin loaded v0.12.1.0', PluginManager 'Loaded plugin: AlexaSkill 0.12.1.0', DeviceQueueManager restored 10+ device queues from disk, ZERO FTL/ERR lines. Assembly-level compat holds at startup. (2) The ADMIN API KEY WAS INVALIDATED by the migration: the previously-working key gets 401 on every auth shape (X-Emby-Token, X-MediaBrowser, Bearer, api_key query) - a fresh key must be generated in the 12.0 dashboard (needs Paolo or dashboard access). (3) CONSEQUENT RISK, UNVERIFIED: the skill's per-user JellyfinTokens (plugin XML config) may be equally dead -> stream-URL auth for playback could fail until users re-link; the simulator/e2e verification paths are all key-based and blocked until a fresh key exists. (4) The JF-508B deploy LANDED on the 12.0 boot (md5 b96db630, PassesShortQueryFullCoverageGate symbol present, clean start). NEXT STEPS (morning, mostly blocked on a fresh admin key): new key -> plugin config Users token check (re-link if dead) -> full unit+e2e battery against 12.0 -> then this task's spike becomes the structured compat validation with a REAL 12.0 production box (better than any container probe). The .NET 10 SDK question (the original blocker) may be moot for 12.0 if the 10.11-built plugin passes the battery: single-codebase compat is now the live hypothesis with supporting evidence.

PHASE-1 SPIKE COMPLETE (2026-09-07/09, findings doc landed on main as docs/jf307-rc7-spike-findings.md, commit 3c7a97f4): UNCHANGED-COMPATIBLE at compile+unit level on BOTH TFMs - net10.0 + 12.0.0-rc7 packages: 0 compiler errors (the only breaks were SDK-age artifacts: NU1904 Refit advisory, CA1873 x519, CA2025 x5 - all dispositions enumerated), 3391/3391 tests pass identically on net10.0 (SDK 10.0.400, dedicated ~/.dotnet-jf307) and net9.0 (system 9.0.120), ZERO #if branches, ZERO source-file changes. The multi-target co-existence strategy (Paolo's explicit goal) is PROVEN: <TargetFrameworks>net9.0;net10.0</TargetFrameworks> with Condition'd refs (10.11.8 / 12.0.0-rc7) builds clean under both SDKs; the net9.0 regression gate held. The proven csproj shape lives in the spike worktree (kept standing for Phase-2). PHASE-2 IS SMALL (7 enumerated items in the findings doc; note Refit>=7.2.22 independently de-breaks main's CI). COMBINED WITH THE PRODUCTION AUTO-UPDATE: the live 12.0.0 box + the boot-clean plugin + the proven compile compat make SINGLE-CODEBASE DUAL-TARGET the confirmed strategy; the runtime questions (auth policy vs X-Emby-Token, perf reworks on audiobook/playlist, DB surfaces) are answerable on the live box once a fresh admin key exists.

PHASE-2 LANDED (2026-09-09, merge af820dbb, branch b50adfa6, DEPLOYED as the net9.0 artifact md5 43433a4d, clean boot on the production 12.0 box: plugin loaded, 24 device queues restored, zero FTL/ERR): the dual-target csproj shape (net9.0;net10.0, Condition'd refs), CA1873+NU1904 suppressed with written rationales (Refit update remains the tracked follow-up), CA2025 fixed FOR REAL at all 6 sites via the monitor-ownership helpers + scope narrowing (the review's independent warnaserror proof: 0 warnings with the rule active = genuinely fixed, not suppressed; three real races closed where a post-handoff serve exception disposed a process under a running WaitForExitAsync), CI dual-runtime (9.0.x+10.0.x) in all three workflows. Both TFMs verified 3526/3526 twice. AC #1/#2/#3/#7 now DONE (#3 via the production 12.0 boot). REMAINING for closure: #4 (DB surfaces live battery), #5 (X-Emby-Token acceptance on 12.0 - the old key's 401 may be key-rotation, not scheme rejection), #8 (simulator+e2e on 12.0) - all blocked on a fresh admin API key from the 12.0 dashboard (Paolo), then the battery; #6 (manifest targetAbi 12.0.0.0) at release time. ALSO LANDED SAME BATCH: JF-527 (the dead-token re-link UX, merged a02ff67c, in the same deployed DLL).
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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
