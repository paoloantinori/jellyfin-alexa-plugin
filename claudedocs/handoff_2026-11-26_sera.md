# Handoff: orchestrator run 2026-11-26 sera

Read this first on resume, then `git log --oneline -8` and `backlog.task_list` for the live state. Everything decided here is settled; do not re-litigate.

## Where we are (updated 2026-11-27 morning: EIGHT tasks closed across the run)

- JF-964 CLOSED (no code): en-US HEALTHY, no conjunction disease. Run 20261126-175416-jf964-en-conjunction-r2.
- JF-967 DONE + DEPLOYED LIVE (verified on-page).
- JF-936 DONE: nlu-lab battery resilience.
- JF-975 DONE: ERROR-row diagnostics on all five human surfaces.
- JF-974 DONE + DEPLOYED LIVE on the maintainer's 'Vai' (2026-11-27 06:25-06:40): DLL + all-17-locale rebuild; live proof run 20261127-063304 (both album-shuffle rows route, sentinels stable); handler live via simulator; catalog wiring intact.
- JF-933 CLOSED live-verified (2026-11-27): the issue-46 steal is CURED (baseline run 20260627... 20261127-064247); fresh audit reduced the collision class to de 'titel' (benign) + fr 'titre'.
- JF-940 CLOSED: the architectural fix (twin fold + JF-946 catalog) confirmed delivered and live; corroborated by the JF-933 baseline.
- JF-955 CLOSED verified-benign (2026-11-27): fr baseline run 20261127-065633 shows the 'titre' collision routing all carriers to PlaySongIntent with the slot filled; the phrase-valued conversion recommendation RETIRED as over-engineering; fr-CA covered by family evidence (strict sample subset).
- FILED: JF-976 (disambiguation hoist), JF-977 (15-locale twin pins, live-verify-before-commit discipline).

## In flight at handoff time

- JF-770 (HIGH, In Progress) is the NEXT SESSION'S FIRST MOVE, by the maintainer's explicit choice (2026-11-27, "la seconda"): open the LAB ABLATION ARC, not the device round. The eighth-rebuild data point (run 20261127-070921) changed the failure's shape for the first time: the anchor misroutes to PlayEpisodeIntent with an unfilled slot (not Fallback), and फिल्म-headed rows never consider PlayVideoIntent while the वीडियो family is fully healthy. The Gate-5 card is outlined in the task notes: arms = (a) drop/reword the PlayEpisode samples carrying फिल्म/देखो-family tokens, (b) densify the फिल्म-headed PlayVideo anchor family, (c) both; sentinels = the वीडियो family + PlayEpisode's healthy rows; A/A per Gate 4; a Fable consult on the considered-set burial pattern is indicated (the 2026-11-25 pattern). The terminal condition (no third LIVE template patch) stands: the lab arc is an experiment, any live change it motivates is a NEW decision with the maintainer. The hassio device round stays the fallback path, unfired.

## Queue (voice-model next)

- JF-770 ablation arc (above) FIRST.
- JF-963 (MEDIUM): the alignment meta-task (its twelfth row JF-974 is done; the remaining probe-first list stays out per the settled decisions).
- JF-976/JF-972/JF-973 (low) and JF-977 (fixture maintenance, batch onto future locale batteries).
- Older device-test checklist items (JF-405 HIGH, JF-595 HIGH) predate the incident era; reassess before working them.

## Operational notes for resume

- The deploy-gate PreToolUse hook blocks EVERY git push until the gate answer is stated in the record (name the task, confirm gates); retrying the same command then passes. Same pattern for docs-only pushes (state the gate-exempt class).
- The JF-964 Gate-5 card + verdict live in the task file; the workingset was /tmp/jf964_workingset_en-US.yaml (may be gone).
- Two locked harness worktrees linger (agent-af4ad0963aa7e5359 merged at 68bedde3, agent-a809d0a047aee0b65 merged at 47330c5b): harmless, `git worktree remove -f -f` if they bother anyone.
- The untracked claudedocs/research_herdr-cross-pane-identity_2026-11-20.md is another session's; leave it.
- Test invocations unchanged: PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 /usr/bin/python3 -m pytest scripts/nlu-lab/tests tests/scripts -q; dotnet with NUGET_PACKAGES=/tmp/nuget-pkgs NUGET_HTTP_CACHE_PATH=/tmp/nuget-http.

## Settled decisions (do not reopen)

- MalformedResponseError stays a sibling of SmapiError, never a subclass (the NO_SELECTION overload).
- The all-ERROR rc-1 gate is at cmd_run level; row semantics in run_battery_rows are final.
- The vendor term stays OUT of the task loop's credential gate (vendor-less users must reach the 12h branch where the JF-966 liveness piggyback lives); CanSyncUser is the reporting aggregate only.
- The webapp/app.js surfacing of row.error is JF-975's scope, not a JF-936 retrofit.
