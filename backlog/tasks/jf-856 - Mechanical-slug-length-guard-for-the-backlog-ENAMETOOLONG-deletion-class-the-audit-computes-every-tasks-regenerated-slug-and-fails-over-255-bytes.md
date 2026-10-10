---
id: JF-856
title: >-
  Mechanical slug-length guard for the backlog ENAMETOOLONG-deletion class: the
  audit computes every task's regenerated slug and fails over 255 bytes
status: In Progress
assignee: []
created_date: '2026-10-10 07:57'
updated_date: '2026-10-10 08:03'
labels:
  - tooling
  - backlog-hygiene
  - hardening
dependencies: []
references:
  - >-
    backlog/tasks/jf-853 -
    Backlog-marker-pathologies-normalize-jf-637-643-781-then-fold-fold-first-PROVEN-destructive-guard-the-4-ENAMETOOLONG-files-draft-the-upstream-Backlog.md-issue.md
  - scripts/audit_backlog_orphans.py
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-10 by the orchestrator from the JF-853 completion round (its same-turn rule item 1; the worker's dispatch forbade the MCP).

JF-853's guard notes on jf-724/771/774/782 are ADVISORY TRIPWIRES ONLY: the Backlog.md CLI/MCP never reads the file before editing it, so a note inside the file structurally cannot prevent the deletion. The deletion class is mechanical and predictable: when the CLI regenerates a task's slug from its title and the result exceeds the 255-byte filename limit, the edit fails with ENAMETOOLONG and the file is ALREADY GONE (rename-before-write ordering; both bugs reproduced live on v1.44.0, see JF-853's Implementation Notes for the reproducers and the upstream issue draft).

Deliverable: a mechanical guard in the JF-854 audit tool (or beside it) that computes, for every file in backlog/tasks/, the slug the CLI would regenerate from the title (the naming rule derivable from the observed slugs; JF-853's notes carry the observed lengths: jf-724 268, jf-771 >255 Devanagari, jf-774 277, jf-782 337 bytes) and FAILS --check naming any file whose regenerated slug exceeds 255 bytes, so a new long-titled task cannot land without either shortening the title or knowingly accepting the hand-edit-only regime (the four existing files become the allowlist's founding members, the KNOWN_RAW_PARKED pattern). Also extend the audit's test with the new shape. This closes the class: the tool that already guards orphans also guards the deletion hazard.
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
