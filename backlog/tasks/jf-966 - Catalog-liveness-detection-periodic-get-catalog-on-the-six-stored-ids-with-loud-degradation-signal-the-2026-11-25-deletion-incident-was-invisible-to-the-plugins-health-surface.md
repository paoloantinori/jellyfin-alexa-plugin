---
id: JF-966
title: >-
  Catalog liveness detection: periodic get-catalog on the six stored ids with
  loud degradation signal (the 2026-11-25 deletion incident was invisible to the
  plugin's health surface)
status: To Do
assignee: []
created_date: '2026-11-25 15:13'
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
