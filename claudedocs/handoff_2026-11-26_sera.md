# Handoff: orchestrator run 2026-11-26 sera

Read this first on resume, then `git log --oneline -8` and `backlog.task_list` for the live state. Everything decided here is settled; do not re-litigate.

## Where we are

The /pa:backlog-orchestrator run closed THREE tasks through the full discipline (worker in worktree, merge, /simplify 4-agent round, code-review high, findings applied, push; CI green on every code push):

- JF-964 CLOSED (no code): recorded live verdict run 20261126-175416-jf964-en-conjunction-r2. en-US is HEALTHY (H2): zero Fallback on the non-famous carrier/naked/verb battery; the JF-961 conjunction disease is NOT live in en-US under the current wiring (artist + audiobook catalog-wired; the live PlaySong song slot is FREE-TEXT, raw shows only AlexaEntities ER_SUCCESS_NO_MATCH). en-US alignment rows may proceed with the standard graph-diff sequence only. 3 fixture expectation failures observed in the same run (coldplay-artist row -> PlaySong carrier-flip class; What-am-I-watching -> MediaInfo; play-dinner-music -> PlayNext): fixture hygiene for the regular triage path, NOT conjunction evidence.
- JF-967 DONE (merge 80af6f14 + gate round 00d0f403): POST /alexaskill/api/catalog/recreate + Recreate Catalogs config-page button + incident runbook claudedocs/incident_2026-11-25_catalog-deletion.md. Key verified facts baked into comments: Jellyfin's TaskManager.QueueScheduledTask<T> LOGS-AND-RETURNS (no throw) on unregistered tasks, hence the registration pre-check through IScheduledTaskWorker.ScheduledTask; the endpoint nulls six ids through the ONE shared StoredCatalogs map (now internal+unconditional, probe filter at CheckUserAsync call site), CanSyncUser/HasSyncCredentials on LibrarySyncService are the ONE predicate family; CarryForeignDiagnostic moved to LibrarySyncService with recovery-Source own-shape (consecutive recoveries replace wholesale, no nesting). RESIDUAL: not deployed to minix yet (the button flow verified via embedded markup + 6403 unit tests only; a deploy + on-page exercise is the natural follow-up).
- JF-936 DONE (worker 47330c5b + gate round 4a6a8d77): nlu-lab battery resilience. MalformedResponseError (NOT a SmapiError subclass; the runner overloads SmapiError as NO_SELECTION) maps response-shape problems to MARKED ERROR rows; per-row error diagnostics through RowResult.error (backward-compatible run JSON, all 80 old run files load); ALL-ERROR runs now fail rc 1 naming the first row's diagnostics, partial-ERROR stays ok (both boundaries pinned). 689 python tests green.

NEW TASK FILED: JF-975 (low) - surface the ERROR-row diagnostics on the human surfaces (webapp _side projection, app.js badge/detail, run-ok summary count). Filed same-turn per the landing rule from JF-936's altitude review.

## Queue (not started, next up)

- JF-975 (low, small): the surfacing task above; natural quick win.
- JF-974 (medium): album-shuffle as content-scoped play (the JF-965 twelfth row) - involves interaction-model changes in en + handler work; the nlu-debug-protocol and nlu-verify skills govern.
- JF-972 (low): consolidate the five synthetic-model test builders into tests/scripts/conftest.py.
- JF-973 (low): the --live deploy fall-through double-run (the reason full live batteries take ~40 min; see memory nlu-lab-live-full-battery-runtime).
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
