# Handoff: session state before context clean (2026-11-25 sera)

Read this first on resume, then `git log --oneline -8` and `backlog.task_list` for the live state. Everything decided here is settled; do not re-litigate.

## Where we are

The tooling chain agreed with the maintainer and Fable is COMPLETE and pushed (HEAD 0b61362a). Four tasks closed through the full discipline (worker in worktree, orchestrator review, /simplify 4-agent round, code-review high, findings applied, merge): JF-970 (carriers subcommand), JF-968 (consideredIntents full-hypothesis capture), JF-969 (multiTurn probe, closed not-applicable), JF-971 (catalog ER canary + flips gating + CATALOG_STATE triage verdict). The nlu-debug-protocol skill (five gates, Fable-authored, two amendments applied) governs all voice-model work now. 674 python tests green on merged main; suite collection 1498 intact.

CI at handoff time: the JF-971 push run was IN_PROGRESS (45670769277); the two previous runs green. On resume, verify it finished green (`gh run list --workflow=ci.yml --limit 2`) before any push (the ci-status-push-gate enforces it anyway).

## The next move (awaiting the maintainer's explicit go)

The JF-965 alignment batch: TWELVE ABSENT rows measured with the carriers tool (17 candidates run, zero SAME_CARRIER; the table and the full rationale live in the task description). Scope: fr 2 (Joue des chansons de / Joue de la musique de), de 4 (three carrier-noun by-order rows + Starte das Album), en 3 (start the album / start the record / shuffle the album), es 3 (canciones del artista/cantante/grupo, LINT-FIRST: check the MediaInfoType synonym collision before adding). Execution discipline per the task: graph diff on a branch -> token lint + classify-fixtures -> lab round with the Gate-5 card -> template edit + regenerate in all affected locales -> fixtures in the same change -> live verify after deploy. The baseline mechanism EXISTS (two `nlu-lab run --live` + `compare --expect-zero-changes` immediately before deploy; considered-hypothesis rows now captured; canary certifies catalog state). The maintainer said the lab arc can start autonomously; the LIVE deploy + rebuild step takes his explicit vai, as every deploy today did.

## Open backlog (not started, in priority order)

- JF-966 (HIGH): plugin-side catalog liveness detection (periodic get-catalog on the six stored ids, loud degradation signal). Complements the landed JF-971 (test-side).
- JF-967 (MEDIUM): one-click catalog recovery admin action + incident runbook (the manual XML surgery must not stay the recovery of record).
- JF-964: formal closure with the incident-correlated verdict (it was answered incidentally: no conjunction in en-US; the NONE readings were the catalog incident).
- JF-972 (low), JF-973 (low): test-builder consolidation; the pre-existing --live deploy fall-through double-run.
- JF-955, JF-960 later slices: as in their task files.

## Operational notes for resume

- KNOWN_FLAKY is DORMANT until `NLU_CANARY_SNAPSHOT` is armed: every pre-JF-971 session reads catalog UNKNOWN, so flips_same_model counts nothing. Arming the snapshot is a deliberate choice to make with the maintainer (it costs up to six canary calls per locale per session).
- The catalog incident is fully closed (all six catalogs live; artist propagation landed 17:16-17:18, ~1h45 from version creation, per-value). The deletion cause is still unknown; JF-966/967 are the protective follow-ups.
- Fable consults: pane w2Q:p3 on the bird herdr daemon (`display_agent fable/jellyfin-alexa-plugin`, same repo cwd). Pattern: write a self-contained brief to /tmp, `herdr agent prompt w2Q:p3 "$(cat ...)"`, arm `herdr agent wait w2Q:p3 --timeout ...` (NO --until) in background, read the pane on wake. He applies his own edits to .claude/skills/nlu-debug-protocol/SKILL.md; commits are the coordinator's.
- One locked worktree remains (agent-a518e1df568a2c878, fully merged at c6bf5cd6; harness lock): harmless. If it lingers: `git worktree remove -f .claude/worktrees/agent-a518e1df568a2c878 && git branch -d worktree-agent-a518e1df568a2c878`.
- Test invocations: `PYTEST_DISABLE_PLUGIN_AUTOLOAD=1 /usr/bin/python3 -m pytest tests/scripts scripts/nlu-lab/tests -q` (the system python3 lacks pytest; HA's plugin autoload breaks runs without the env var).
- Untracked leftover NOT ours: claudedocs/research_herdr-cross-pane-identity_2026-11-20.md (another session's; leave it).

## Settled decisions (do not reopen)

- The extractor home: module-level in tests/integration/smapi_client.py; the lab re-exports it. No lazy imports, no shims.
- The ledger considered column is annotation-only; triage automation on it waits for 2-3 live suites of evidence.
- canary semantics follow Gate 2's asymmetry strictly (fail disqualifies; pass certifies only that value; failed-but-identical pairs also excluded from flips).
- JF-965's scope is the measured twelve rows; five research premises already fell to the carriers tool (en by-order, en artist-shuffle, fr start-album all PRESENT). The probe-first list and the scartati stay out.
