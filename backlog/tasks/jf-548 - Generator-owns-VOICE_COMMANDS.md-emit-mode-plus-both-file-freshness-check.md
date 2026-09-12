---
id: JF-548
title: >-
  Generator owns VOICE_COMMANDS.md: emit mode + both-file freshness check (the
  JF-513.1 item-8 strategic follow-up)
status: Done
created_date: '2026-09-12'
labels: [docs, tooling, drift-prevention]
references:
  - JF-513.1 (item 8)
  - scripts/generate_voice_reference.py
---

## Final Summary
DONE 2026-09-12 (commit e2da6b6a): scripts/generate_voice_reference.py now writes BOTH mirrors - docs/VOICE_COMMANDS_BY_LOCALE.md (complete) and VOICE_COMMANDS.md's 17 locale sections (up to 6 samples per intent; first of each distinct slot SIGNATURE then model order, because plain model order showed six static openers and dropped the slotted working forms; regional heading qualifiers preserved). The hand prose above the first emitted heading is preserved verbatim and the splice point must be an EXACT emitted heading line (a locale-shaped anchor in prose can no longer silently truncate it). --check validates both files with distinct MISSING/STALE messages; the write mode refuses an absent table (its prose is hand-owned). The Phase 6 lint stays as the independent title-mapping guard (_row_intent_candidates documented as the exact inverse of intent_display_title). CLAUDE.md's mirror rule updated to regenerate-never-hand-edit. Gates: /simplify + code-review high, all findings applied; determinism byte-stable; suite 3665/3665.
