---
id: JF-966
title: >-
  Catalog liveness detection: periodic get-catalog on the six stored ids with
  loud degradation signal (the 2026-11-25 deletion incident was invisible to the
  plugin's health surface)
status: Done
assignee:
  - zai
created_date: '2026-11-25 15:13'
updated_date: '2026-11-26 15:34'
labels:
  - catalog
  - reliability
  - incident-followup
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the 2026-11-25 catalog-deletion incident (all six interactionModel catalogs 404'd on Amazon between ~15:00 and ~15:45; the plugin reported everything healthy — LocaleModelStatuses SUCCEEDED, sync success at 13:28 — while the live skill's artist/series/audiobook/movie/song selection was dead; the diagnosis took an hour because the symptom pointed at NLU first). PROTECTION: a catalog-liveness check the plugin owns. (1) A scheduled task (or a piggyback check inside the existing CatalogSyncTask's skip path: when the 12h gate skips the user, do a cheap get-catalog HEAD on each of the six stored ids — the skip path currently costs zero SMAPI calls, so this adds at most six reads per skip) that detects a 404 and: logs a LOUD error naming the dead catalog(s), records a durable ledger caveat (the JF-951 OversizeSkipped caveat-bit pattern: CatalogIdsDead), and surfaces it on the config page's locale-status dump. (2) Optional auto-heal behind a config flag (default OFF): on dead ids, null them and force a sync (the documented recovery). (3) The triage rule codified in the check's doc comment and the plugin memory: a whole slot-type family dying cross-locale is an infrastructure signature (catalog liveness), not an NLU hypothesis — check this BEFORE graph/labbattery/conjunction theories (the incident's first hour went to JF-964 conjunction probing while the catalogs were already dead).
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
APPROVED FOR EXECUTION (maintainer "prosegui" 2026-11-26, delegation on record: "se funziona per te funziona per me").

1. SDK surface verification FIRST: check Alexa.NET.Management for a get-catalog call (GET /v1/catalogs/{id} is what `ask smapi get-catalog` uses); if absent, add a minimal Refit endpoint in SmapiManagement following the file's existing client patterns. 404 vs 200 discrimination must be explicit.
2. New CatalogLivenessCheck (Alexa/Catalog/CatalogLivenessCheck.cs): reads the six stored ids (Entities/User.cs: ArtistCatalogId, AlbumCatalogId, SeriesCatalogId, AudiobookCatalogId, MovieCatalogId, SongCatalogId), one GET each, returns per-id alive/dead (404 or empty = dead; 200 = alive). Bounded: only non-null ids, max 6 reads per invocation.
3. Piggyback in CatalogSyncTask's skip path (EntryPoints/CatalogSyncTask.cs:90-99, the <12h gate): on skip, run the check. On dead ids: LOUD LogError naming them; set the new CatalogIdsDead bit on the ledger caveat (mirror the OversizeSkipped writer/survival semantics, LibrarySyncService.cs:831-890 and the CatalogLedgerCaveats enum at PluginConfiguration.cs:884+); clear the bit when all alive again.
4. Surface the caveat in the config page's locale-status dump (find where LocaleModelStatuses/the ledger rows render in ConfigurationController + config.html) so the admin sees CATALOG IDS DEAD without logs.
5. Auto-heal behind a new PluginConfiguration flag (default FALSE): on dead ids, null them + backdate LastCatalogSync (the documented recovery from the incident), letting the next scheduled sync recreate; OFF = detect-and-signal only.
6. Triage rule as the check's doc comment: a whole slot-type family dying cross-locale is an infrastructure signature (catalog liveness), check BEFORE graph/lab/conjunction theories (the incident's first hour went to JF-964 probing while catalogs were dead).
7. Tests: check dead/alive/mixed; caveat bit set/clear/survival; skip-path piggyback (skipped user with dead ids gets caveat + loud log; all-alive gets neither; auto-heal OFF leaves ids intact, ON nulls + backdates); flag-file-per-flag convention for the new config flag.
8. Gates: dotnet build + full test, /simplify, /code-review high; NLU fixtures and locale strings untouched (no model/handler-string change). Worker in own worktree per house pattern; orchestrator reviews and merges.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
MERGED 6a07d52b and pushed (worker commit 6d61a938 on its worktree branch, reviewed by the orchestrator). Worker gates: /simplify 4-agent round (findings applied) + code-review high (6 findings: 4 applied, 2 skipped with reasons in the commit message). ORCHESTRATOR VERIFICATION (my own runs, this transcript): dotnet build 0 errors 0 warnings; dotnet test 6384 passed / 0 failed on BOTH net9.0 and net10.0. The worker's SDK-surface answer verified in its report against the shipped DLL AND upstream source: the correct get-catalog is ManagementApi.ReferenceCatalogManagement.Get on /v1/skills/api/custom/interactionModel/catalogs/{id} (the create/delete family), NOT ICatalogManagementApi's /v0/catalogs (would 404 a live catalog); composition tests pin the route empirically. Three small deviations from the recorded plan, all documented in the report and acceptable: LastCatalogSync cleared to null (the JF-946 force-next-sync precedent) instead of a fake past timestamp; no new Refit interface needed (handler-seam ctor for testability instead); the ledger stamps existing locale rows only, never fabricates rows.

REVIEW FINDING (orchestrator): the worker ALSO compacted the coordinator's memory index while adding the triage-rule memory, dropping 12 entries from MEMORY.md (including load-bearing lessons) - outside its authority; ALL 12 restored from each file's frontmatter, zero orphans verified. The triage-rule memory itself is kept (correct content).

STATUS: code merged and CI will verify; the LIVE deploy (DLL hot-swap) awaits the maintainer's go - until then the protection exists only in the repo. The auto-heal flag ships default OFF; enabling it on minix is a maintainer config decision after the deploy.

DEPLOYED LIVE 2026-11-26 16:34 (maintainer 'via'): config backed up (1 user), DLL swapped into AlexaSkill_1.1.2.0, md5 9b4b5050 local==active, server up, config survived, served config page carries the CatalogAutoHealDeadIds checkbox (4 embedded occurrences, the served-page curl check per the config.html rule). END-TO-END EVIDENCE: CatalogSyncTask fired on the StartupTrigger at 16:34:24, took the skip path (user synced 10.4h ago), and the liveness piggyback ran quiet (zero liveness/dead lines in the logs = all six catalogs answered alive; the alive path is silent by design, the unknown arm is debug-only). AUTO-HEAL LEFT OFF per the stated recommendation, maintainer-acquiesced: enable only after the signal has been seen live at least once. Task closed: all DoD items satisfied (build 0/0, tests 6384 x2 TFMs, gates in the worker round, deploy verified).
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->
