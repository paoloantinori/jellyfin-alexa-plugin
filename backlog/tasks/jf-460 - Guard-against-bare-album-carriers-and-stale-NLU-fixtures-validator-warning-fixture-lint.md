---
id: JF-460
title: >-
  Guard against bare album carriers and stale NLU fixtures (validator warning +
  fixture lint)
status: Done
assignee: []
created_date: '2026-09-03 05:32'
updated_date: '2026-09-03 07:29'
labels: []
dependencies: []
references:
  - 'CLAUDE.md anti-pattern #11'
  - scripts/validate_interaction_models.py
  - tests/integration/conftest.py
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From the JF-459 /simplify review (2026-09-03): the no-bare-album-carrier invariant is enforced nowhere machine-readable. CLAUDE.md anti-pattern #11 now documents it with a working detection snippet (placeholder-stripping + per-locale noun table), but two mechanical guards are still missing:

1. VALIDATOR WARNING in scripts/validate_interaction_models.py: port the CLAUDE.md #11 detection into the validator as a WARNING-level check (never FAIL: the CI validate-models job is advisory and a false positive must not break it). Design: for each locale except it-IT (catalog-backed AlbumName slot; skip it by reading PlayAlbumIntent's album slot type from the model itself rather than hardcoding the locale list, so a future locale switch is handled automatically), flag any {album}-containing sample whose carrier text (all {placeholders} stripped) lacks the locale's media noun. Noun table: en album/record, de Album/Platte, es álbum/disco, fr album/disque, pt álbum, nl album, ar الألبوم/ألبوم, ja アルバム, hi एल्बम. Keep the table next to the check with a comment pointing at CLAUDE.md #11 as the source of truth so the two cannot drift silently.

2. FIXTURE-PRESENCE LINT (warning-level, same script or a new one wired into run_nlu_tests.sh --dry-run): cross-check NLU fixture utterances against the interaction-model samples. Scope it to a heuristic, NOT an oracle: profile-nlu legitimately matches utterances that are not literal samples, so this can only WARN when a fixture utterance shares a carrier shape (same non-slot prefix) with samples that no longer exist. The concrete case it must catch: JF-459 deleted 113 samples and the one fixture referencing a removed sample (fr-CA "Lis la musique de the beatles") was caught only by a manual profile-nlu probe.

Acceptance criteria:
- validate_interaction_models.py emits a warning (and exits PASS) when a bare album carrier is injected into a free-text locale in a scratch copy; emits nothing on the current tree.
- The check skips a locale whose album slot type is not an AMAZON.* free-text type.
- The fixture lint warns on a fixture utterance whose carrier prefix matches a removed sample shape (test with the pre-JF-459 fr-CA fixture on the post-JF-459 models).
- Both checks documented in the script header.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both guards implemented in scripts/validate_interaction_models.py (commit 7da19466, single-file code change + CI job dependency + CLAUDE.md note).

1. Check #10 (validate_single_model, warning-level): flags PlayAlbumIntent samples containing {album} whose carrier (placeholders stripped, _lint_normalize'd) lacks the locale's media noun, only when the album slot type read from the model is an AMAZON.* free-text type. it-IT's catalog-backed AlbumName is exempt automatically (no hardcoded locale list; the 'it' noun-table gap is documented as deliberate belt-and-braces with the slot-type exemption). Noun table ALBUM_CARRIER_NOUNS pinned to CLAUDE.md anti-pattern #11 with a same-commit drift comment.

2. lint_fixture_carriers (Phase 3, warning-level): for fixture tests expecting PlayAlbumIntent, warns when the utterance matches no current sample via subsequence-of-fragments containment. Scoped to PlayAlbumIntent deliberately (song-intent fixtures intentionally exercise NLU generalization, documented in-code).

Hardening applied from the code-review pass: pip install pyyaml added to the validate-models CI job (the lint was silently inert there: the runner image ships no PyYAML, and the sibling validate-build-yaml job already installs it); the all-clear line can no longer print after a SKIP (lint returns None on not-run); malformed fixture entries (non-string utterance, non-list tests) no longer crash the advisory validator; --verbose is now a real flag (the summary previously hinted at a nonexistent flag while capping at 20 warnings; Phase 1 warnings were invisible beyond the cap).

Verification: 90-warning baseline unchanged and exit 0; four controls reproduced independently by the orchestrator (injected 'Spiele {album}' fires exactly one warning; a case/whitespace nouned variant stays silent; it-IT with real AlbumName stays silent with an injected bare sample; the pre-JF-459 fr-CA fixture warns on exactly 'Lis la musique de the beatles'; current fixtures all clean). Suite 3056/3056; locales/versions validators PASS; NLU dry-run unchanged; ci.yml valid YAML. No deploy needed (repo-side script only, no DLL change).

Gates: /simplify (4 parallel angle agents; applied the --verbose fix, unified normalization, hoists, scope notes; skips documented with reasons); code-review via pr-review-toolkit:code-reviewer (3 findings P2@88 + P3@85 x2, all applied same-turn; below-threshold notes: verbose double-print cosmetic, noun-table substring coupling documented as deliberate).
<!-- SECTION:FINAL_SUMMARY:END -->
