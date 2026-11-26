# Handoff: orchestrator run 2026-11-26 sera

Read this first on resume, then `git log --oneline -8` and `backlog.task_list` for the live state. Everything decided here is settled; do not re-litigate.

## Where we are (updated end-of-run: FIVE tasks closed)

The /pa:backlog-orchestrator run closed FIVE tasks through the full discipline (worker in worktree, merge, /simplify, code-review high, findings applied, push; CI green on every code push):

- JF-964 CLOSED (no code): en-US HEALTHY (H2), no conjunction disease; en alignment unblocked. Run 20261126-175416-jf964-en-conjunction-r2.
- JF-967 DONE + DEPLOYED LIVE to minix (20:45, verified: served button, guards 400/404, active-dll md5 match, tokens survived; happy path deliberately unfired). POST /alexaskill/api/catalog/recreate + config-page button + runbook.
- JF-936 DONE: nlu-lab battery resilience (MalformedResponseError -> marked ERROR rows; RowResult.error; all-ERROR fails rc 1).
- JF-975 DONE: the diagnostics surfaced on ALL FIVE human surfaces (webapp projection, badge tooltip, detail line, run-ok summary + first-error stderr, compare output via RowDiff.error, run-picker warning badge).
- JF-974 DONE through the full Gate-5 lab round (H1 CONFIRMED: control/treatment/A-A runs 20261126-220103/221255/222439; both rows PlayAlbum->ShufflePlay with album filled; twin survives; zero-noise A/A; two pre-existing fixture failures resolved as side effects). BuildAlbumShufflePlayResponseAsync + handler branch + en-US samples + all-17-locale album slot. 6417/6417 both TFMs.
- JF-976 FILED (low): hoist the four album-disambiguation copies into one helper.

PUSH-GATE RITUAL LEARNED: the deploy-gate hook keys on the EXACT command string (30-min window). A compound command with anything appended is a NEW command and blocks again; replay the identical string after stating the gate answer in the record.

PROCESS LESSON (memory reviewer-equivalence-claims-need-artifact-read): a /simplify finding claiming "equivalent, safe to remove" still needs the named code opened before applying; JF-974's removed session pre-seed regressed the mirror's preserve loop and the code-review round caught it (now pinned by a regression test).

## Awaiting the maintainer

- JF-974's LIVE model deploy (DLL deploy + custom-model/rebuild en-US) + e2e simulate rows + on-device verification: needs the explicit vai (the standing JF-965 rule). The lab round is done; only the live step remains.

## Queue (not started, next up)

- JF-976 (low): the disambiguation hoist (its own change + gate round).
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
