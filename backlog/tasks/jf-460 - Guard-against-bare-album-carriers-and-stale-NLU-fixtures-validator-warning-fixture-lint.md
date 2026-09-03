---
id: JF-460
title: >-
  Guard against bare album carriers and stale NLU fixtures (validator warning +
  fixture lint)
status: In Progress
assignee: []
created_date: '2026-09-03 05:32'
updated_date: '2026-09-03 06:44'
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
