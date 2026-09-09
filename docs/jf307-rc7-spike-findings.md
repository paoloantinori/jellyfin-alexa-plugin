# JF-307 Phase-1 Spike: Jellyfin 12.0.0-rc7 (net10.0) validation

**Date:** 2026-09-07
**Worktree:** `/home/pantinor/data/repo/personal/jellyfin-alexa-plugin-jf307` (branch `spike/jf307-12rc7`, cut from main HEAD)
**Status: COMPLETE. All three probes ran. Zero source-file changes; only the two csprojs were edited.**

## Verdict line

**UNCHANGED-COMPATIBLE at the compile and unit-test level, and the multi-target co-existence strategy is PROVEN viable.** The plugin source compiles against `Jellyfin.Controller`/`Jellyfin.Model` 12.0.0-rc7 under `net10.0` with zero compiler errors, the full test suite (3391 tests) passes on BOTH TFMs, net9.0 still builds and tests clean under the system SDK 9.0.120, and no file needed an `#if NET10_0` branch. Every break found was an SDK-age artifact (NuGet audit, new analyzers), not a Jellyfin API break. Runtime behavior on a real 12.0 server remains unverified (see open questions).

This is the hoped-for outcome from the PRIORITIZED note: the source compiles under both TFMs unchanged; a hard fork is not needed.

## Environment

- Dedicated SDK: `~/.dotnet-jf307` (SDK **10.0.400**, runtime 10.0.11 only). Installed 2026-09-07 with approval; reversible via `rm -rf ~/.dotnet-jf307`. Never shadowed the system dotnet (verified: system still reports only 9.0.120). The spike was initially blocked on the missing SDK; the dedicated install unblocked it without touching the system toolchain or the main checkout.
- Probe builds used `DOTNET_ROOT=$HOME/.dotnet-jf307`, `PATH=$HOME/.dotnet-jf307:$PATH`, and a fresh package cache (`NUGET_PACKAGES=/tmp/nuget-pkgs-jf307`, `NUGET_HTTP_CACHE_PATH=/tmp/nuget-http-jf307`).
- Package facts: 12.0.0-rc7 exists on NuGet (the 12.0.0 tail ends rc1..rc7 plus a stray `12.0.0-rcrc3`; pin rc7). The rc7 packages ship **net10.0-only** libs, depending on the 12.0.0-rc7 family (Jellyfin.Common/Model/Naming/MediaEncoding.Keyframes), BitFaster.Caching 2.6.0, and Microsoft.Extensions.* 10.0.11. So net9.0 and net10.0 cannot share one Jellyfin package version; the Condition'd PackageReference split is mandatory.

## Probe 1: net10-only (plugin + tests on net10.0, rc7 packages)

First build: **0 compiler (CS) errors**, 524 analyzer/audit errors escalated by `AnalysisMode=AllEnabledByDefault` + `TreatWarningsAsErrors`. After three mechanical spike-local suppressions (NoWarn in the plugin csproj), the build is **0 errors, 0 warnings**, and both DLLs are produced.

### Break catalog by surface (all Jellyfin surfaces: zero)

| Surface | CS errors | Notes |
|---|---|---|
| DB / entities (`IUserDataManager`, User entities) | 0 | No rename or signature drift hit our call sites |
| Library manager (`ILibraryManager.GetItemList` etc.) | 0 | Return types still compatible |
| Session / playback state | 0 | |
| DTO / `BaseItemDto` / `BaseItemKind` | 0 | |
| ASP.NET routing / controllers | 0 | Attribute routing unchanged |
| Analyzers (SDK-driven, not Jellyfin) | 524 diagnostics | CA1873 x519, CA2025 x5; details below |
| Other | 0 | |

### The three non-Jellyfin breaks and their dispositions

1. **NU1904, Refit 4.7.51 critical advisory (GHSA-3hxg-fxwm-8gf7, CRLF injection in Refit attributes; vulnerable < 7.2.22).** `Alexa.NET.Management` 5.10.0 pins Refit **exactly** 4.7.51, so the net9.0 graph resolves the same package and this is NOT TFM-specific: it is advisory-age drift, and the next `dotnet restore` with auditing on main (TreatWarningsAsErrors) can fail CI the same way. We use Refit directly in 3 files (`Alexa/RetryHelper.cs`, `Alexa/SmapiManagement.cs`, `EntryPoints/SkillStartup.cs`), so lifting Refit to >= 7.2.22 (a 4-to-7 major jump) or updating Alexa.NET.Management is a real code change with SMAPI runtime risk, deferred to Phase 2. Spike unblock: NoWarn NU1904.
2. **CA1873 (expensive log-argument evaluation), 519 unique sites.** New analyzer in the .NET 10 SDK set. Recommended Phase-2 disposition: suppress project-wide with justification rather than churn 519 guarded-call rewrites; most sites are `LogDebug` interpolations, and the project's debug-logging policy deliberately keeps those unconditional.
3. **CA2025 (tasks using IDisposable must complete before disposal), 5 unique sites: `BaseHandler.cs(524,59)`, `VideoAudioController.cs(281,48)`, `(440,43)`, `(664,43)`, `(974,39)`.** This is a real disposal-order bug class, not noise; Phase 2 should inspect and fix these 5 sites (the fire-and-forget encode paths are exactly where this matters). Spike unblock: NoWarn CA2025.

## Probe 2: multi-target co-existence (net9.0;net10.0, Condition'd refs)

Final csproj shape (in this worktree): `<TargetFrameworks>net9.0;net10.0</TargetFrameworks>` with `Jellyfin.Controller`/`Jellyfin.Model` at 10.11.8 under `Condition="'$(TargetFramework)'=='net9.0'"` and 12.0.0-rc7 under net10.0 (ExcludeAssets=runtime preserved on the plugin's refs).

- **SDK 10.0.400 building BOTH TFMs: 0 errors, 0 warnings.**
- **System SDK 9.0.120 (main's toolchain) building the net9.0 TFM: 0 errors, 0 warnings.** Recipe required: single-TFM restore (`restore -p:TargetFramework=net9.0`) then `build -f net9.0 --no-restore`. Regression gate PASSED: net9.0 is not compromised by the conversion.
- **`#if NET10_0` branches needed: 0.** No source file was touched; grep confirms the plugin source contains no TFM conditionals at all.
- Two caveats for Phase-2 CI design:
  - An SDK older than the highest TFM cannot restore the multi-target project at all (`NETSDK1045`). CI (all three workflows currently pin `dotnet-version: 9.x` / `9.0.x`) must move to SDK 10, which builds both TFMs fine.
  - The dedicated install dir held only runtime 10.0.11, so net9.0 testhosts could not start there; to test both TFMs in CI, both runtimes (9.x and 10.x) must be present.

## Probe 3: tests

- **net10.0 (dedicated SDK, rc7 packages): 3391 passed, 0 failed, 0 skipped** (42s).
- **net9.0 (system SDK, 10.11.8 packages): 3391 passed, 0 failed, 0 skipped** (41s).
- Identical counts on both TFMs: no TFM-conditional test divergence, and zero runtime test failures on either TFM (the only failures during the spike were the compile-gate artifacts resolved before the runs).
- Note: CLAUDE.md's "~2800 unit tests" figure is stale; the suite is 3391 today.

## Phase-2 size estimate: SMALL

1. Land the multi-target csproj shape exactly as proven here (both files already in this state in the worktree).
2. CA1873: suppress project-wide with a written rationale (519-site churn contradicts the debug-logging policy).
3. CA2025: inspect and properly fix the 5 named sites.
4. Refit NU1904: lift Refit >= 7.2.22 or update Alexa.NET.Management, then runtime-verify SMAPI calls. This also de-breaks main's own CI (independent of 12.0).
5. CI: pin SDK 10 in ci.yml / dev-build.yml / release-build.yml; ensure both runtimes for both-TFM test runs.
6. Manifest: second version entry with `targetAbi: 12.0.0.0` per the release flow (manifest/build.yaml/Directory.Build.props consistency via `validate_versions.py`).
7. Live 12.0 container verification (below).

## Open runtime questions (only a live 12.0 container answers these)

- **Auth:** 12.0's deprecated-authorization policy vs our `X-Emby-Token` controllers and simulator. RC temporarily re-enabled legacy auth because official clients lagged, but enforcement is the direction. AC #5.
- **Perf PR query reworks** (music, playlists, collections, books): re-test the audiobook HLS/resume path (JF-292) and playlist member reads. AC #4, #8.
- **Database:** 12.0 stacks further EF Core changes on 10.11; no rollback without full restore. User resolution, `IUserDataManager`, and index builds need verification against real DB state. AC #3, #4.
- Unit tests mock the Jellyfin interfaces, so contract compatibility is proven but server behavior is not.

## Recommendation

Proceed with the multi-target plan on the strength of this spike: compile-level compatibility is proven for both TFMs, the regression gate holds, and the test suite passes identically. Keep the Refit advisory fix and the CA2025 sites as named Phase-2 work items (the Refit one independently affects main's CI today). Stand up the 12.0 container next to answer the runtime questions before cutting the 12.0 manifest entry.
