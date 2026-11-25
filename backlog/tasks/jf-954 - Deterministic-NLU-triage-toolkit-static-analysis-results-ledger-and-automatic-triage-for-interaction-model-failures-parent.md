---
id: JF-954
title: >-
  Deterministic NLU triage toolkit: static analysis, results ledger and
  automatic triage for interaction-model failures (parent)
status: Done
assignee: []
created_date: '2026-11-23 21:09'
updated_date: '2026-11-25 05:24'
labels:
  - nlu
  - tooling
  - determinism
dependencies: []
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Why

Triage of NLU failures currently costs hours of live probing. On 2026-11-23 a full profile-nlu suite (about 1450 rows, 2h08m) reported 45 red NLU rows and the maintainer could not tell in less than a day which were (a) trainer coin flips on marginal rows, (b) fixture artifacts, or (c) real model defects. Three of the costly discoveries were predictable with deterministic local code and no Amazon call:

1. Issue #46 (de-DE): the word "Lied" was at the same time a carrier word in PlaySongIntent samples and a synonym of the MediaInfoType slot type. A token index would have shown the collision.
2. The es/fr episode rows ("reproduce breaking bad temporada uno episodio tres"): the fixture uses a series that exists in the static seed of the SeriesName slot type but NOT in the live catalog that replaces that seed after catalog sync (Series seeds are deliberately not merged, see `CatalogSeedEnrichment.GetSeedNames`). A vocabulary check would have flagged the row before the suite ran.
3. The es-ES rows where `Pon {song}` (PlaySongIntent, free-text slot) can swallow `pon {series_name} temporada {season_number} episodio {episode_number}` (PlayEpisodeIntent). A sample-subsumption analysis marks such rows as contested by construction.

Amazon's trainer is a black box, so none of this predicts routing. The goal is narrower and achievable: detect NECESSARY conditions for a break at build time, and classify every red row in seconds with a reproducible rule instead of a human guess.

## What this parent delivers (through its subtasks)

A Python package `scripts/nlu_static/` with one CLI entry point and one doc page, containing:
- an effective-model builder and a catalog snapshot format (subtask 1);
- a token-ownership collision lint (subtask 2);
- a fixture-versus-vocabulary check (subtask 3);
- a sample-subsumption graph and an utterance matcher (subtask 4);
- a persistent ledger of every live NLU suite outcome with flip statistics (subtask 5);
- a triage report that classifies each red row with a fixed precedence rule (subtask 6);
- model identity hashes and change-driven fixture selection (subtask 7);
- an authoritative catalog payload export from the plugin (subtask 8, optional producer for the snapshot format).

Suggested order: 1, then 2, 3, 4 and 5 in any order, then 7, then 6 last. Subtask 8 is independent.

## Rules that apply to EVERY subtask (repeat-read before coding)

- Python 3.12, standard library plus `pyyaml` only. No new dependencies.
- Unit tests live in `tests/scripts/` and must pass with `python3 -m pytest tests/scripts -q` (this exact command runs in CI, see `.github/workflows/ci.yml`). Tests never call the network, SMAPI, the `ask` CLI or Jellyfin; use small synthetic models and files built inside the test.
- NEVER hand-edit any `model_*.json` and do not edit templates under `Alexa/InteractionModel/templates/`. These tools only READ models, templates and fixtures.
- NEVER run `ask smapi delete-skill`. No subtask needs any SMAPI write.
- Determinism contract for every artifact a tool writes (JSON, markdown, text): sorted keys, explicitly sorted lists, no timestamps, no absolute paths, no dependence on set or dict iteration order, no randomness. Each tool gets a test that runs it twice and asserts byte-identical output.
- Text normalization is ONE shared function (defined in subtask 1): Unicode NFC, casefold, strip leading/trailing punctuation per token, collapse whitespace. Do not write a second normalizer.
- New validator phases are WARNING level, never errors. `scripts/validate_interaction_models.py` is a blocking CI job whose exit code fails on errors only; a false positive must not break CI. Use the existing `run_warning_phase(title, check, all_clear, sink)` helper; a check that cannot run returns `None` after printing its own SKIP line with the reason.
- A locale a tool cannot handle is reported with an explicit `SKIP <locale>: <reason>` line. Silent skips are forbidden.
- Prose in comments and docs: never use the em-dash character and never use "word - word"; write a new sentence or use a semicolon. In markdown files keep each paragraph on one line (no hard wraps).
- Do not claim a subtask complete without running its proving commands in the same session and pasting the output into the task notes.

## Facts about the repo an implementer needs

- Models: `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_<locale>.json`, 17 locales. Top-level keys are `languageModel` and `dialog` (some tools also accept an outer `interactionModel` wrapper; `load_model(path)` in the validator already returns the `languageModel` dict). `languageModel.intents[]` has `name`, optional `slots[] {name, type}`, `samples[]` with `{slot_name}` placeholders. `languageModel.types[]` has `name` and `values[] {name: {value, synonyms[]}}`.
- Slot type census across all locales today: custom types 331 slot uses, AMAZON.SearchQuery 256, AMAZON.NUMBER 87, AMAZON.Musician 78, AMAZON.MusicRecording 67, AMAZON.Genre 45, AMAZON.DURATION 34, AMAZON.TIME 17, AMAZON.Person 16. Total samples 9646; the largest locale is it-IT with 1860.
- Catalog-backed custom slot type names (replaced by library values after catalog sync): JellyfinArtist, AlbumName, SeriesName, AudiobookTitle, MovieTitle, SongTitle (`Alexa/Catalog/CatalogSlotTypes.cs`, `CatalogSlotTypeNames`).
- NLU fixtures: `tests/integration/fixtures/<locale>.yaml` with keys `locale`, `invocation_name`, `tests[]`; each test has `utterance`, `expected_intent` (may be `A|B`, every listed name is a correct delivery) and optional `expected_slots` where `{}` means any non-empty value and `{value: "x"}` pins the exact value. Files named `e2e_<locale>.yaml` are a different format and are out of scope everywhere.
- The live NLU test is `tests/integration/test_nlu.py`, launched by `./scripts/run_nlu_tests.sh`.
- Existing script tests to imitate for style: `tests/scripts/test_elicit_checker.py`, `tests/scripts/test_cjk_slot_glue.py`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All eight subtasks are Done or explicitly closed with a written reason
- [ ] #2 docs/nlu-static-analysis.md describes every subcommand with one runnable example each and states plainly that the tools detect necessary conditions and classify failures but do not predict Amazon's routing
- [ ] #3 python3 -m pytest tests/scripts -q passes and python3 scripts/validate_interaction_models.py exits 0 on the committed models with the new phases enabled
- [ ] #4 The CLAUDE.md Build and Test command block lists the nlu_static CLI entry point with a one-line description
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
gate-exempt: parent closure; all implementation gates ran per-subtask (worker /simplify + /code-review high or the equivalent coordinator rounds, every finding applied or tracked in the subtask records); this closure adds no code.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
PARENT CLOSED 2026-11-25: all eight subtasks Done (JF-954.1 foundation, JF-954.2 token collision lint, JF-954.3 fixture vocabulary check, JF-954.4 subsumption graph + matcher, JF-954.5 results ledger, JF-954.6 automatic triage report, JF-954.7 model identity + change-driven selection, JF-954.8 authoritative plugin export — each shipped with worker gates + an orchestrator code-review round whose findings were all applied). AC review: #1 all eight Done (this closure); #2 docs/nlu-static-analysis.md describes every subcommand with runnable examples and the does-not-predict-routing statement (verified across the subtask rounds); #3 pytest tests/scripts green (414 at final state) and the validator exit 0 with the new warning phases (Phase 11 token collisions, Phase 12 fixture vocabulary) on every round; #4 the CLAUDE.md Build & Test block carries the triage entry point (added by JF-954.6, one line as specced). The toolkit's end-to-end chain is live: snapshot --from-plugin -> identity/select -> NLU_ROW_FILTER fast loop -> ledger -> triage, with the full suite remaining the release gate.
<!-- SECTION:FINAL_SUMMARY:END -->
