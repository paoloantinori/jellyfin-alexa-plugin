---
id: JF-307
title: Migrate plugin to Jellyfin 12.0 (net10.0) — ABI 12.0.0.0 port
status: To Do
assignee: []
created_date: '2026-07-03 21:10'
updated_date: '2026-07-13 20:17'
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
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Jellyfin.Plugin.AlexaSkill.csproj
  - manifest.json
  - build.yaml
  - Directory.Build.props
  - .github/workflows/ci.yml
priority: medium
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
