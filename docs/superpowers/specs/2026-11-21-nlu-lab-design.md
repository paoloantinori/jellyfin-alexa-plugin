# NLU Lab: design

Date: 2026-11-21. Status: approved in brainstorming with the maintainer (visual companion session, mockups in .superpowers/brainstorm/4063171-1795238988/). Motivating case: JF-933 (issue #46, the MediaInfoType vocabulary collision in de/en/fr), and the it-IT tuning history where every fix risked breaking another route.

## Problem

Iterating on an interaction model today is slow and blind. One iteration means: edit the YAML template, regenerate the JSON, deploy via SMAPI, wait for the build, run utterance probes one by one, and compare results by eye. Regressions surface only on a real device, after shipping. The maintainer wants a precise, fast loop: load a voice model from an external file (no plugin update involved), probe utterance routing, and SEE how routing changes between iterations, before anything reaches users. Users of the plugin need the same class of insight self-service, inside the plugin settings, before they ever open an issue.

## Decisions from the brainstorming

1. Operator split: the maintainer's agent drives the iterations; the webapp is the validation dashboard the maintainer reads, phone-first.
2. Iteration target: a DEDICATED lab skill on the vendor account, created once. The household skill is never touched by the lab. Accepted caveat: the lab skill carries no Jellyfin catalog wiring, so catalog-backed slots behave differently there than live; intent SELECTION fidelity (what JF-933 needs) is unaffected.
3. Core view: the routing matrix, utterance rows by run columns, changed rows highlighted, red in the newer run meaning regression, row detail with filled slots and the intents Amazon considered.
4. The UI chrome is multi-language (strings file, en + it first, browser language with manual override); probe content stays in the locale under test.
5. Publish flow for power users: guided, static, multilingual; the plugin's existing URL-based custom model import is the consumption point.
6. Field diagnostics from Jellyfin logs join the family: parse arrivals, handler decisions, and error classes with full debug context, grouped by correlation id.
7. The whole thing ships as a plugin settings page over time, so users self-debug their own instance and locale before opening issues; the full lab stays on the workstation.
8. Custom models are first-class: any source (embedded, regenerated template, external file, the live skill's model fetched via SMAPI) loads, probes, and diffs against any other.

## Architecture

Three delivery phases, one design.

### Phase 1: the lab (workstation, serves JF-933 now)

`scripts/nlu-lab/lab.py`, Python, no new runtime dependencies beyond what the repo already uses.

Subcommands:
- `init-skill`: one-time provisioning of the lab skill via SMAPI, reusing the plugin's manifest generator with a dedicated invocation name and harmless endpoint. Stores the lab skill id in a local config file (`scripts/nlu-lab/lab.json`).
- `run`: the whole iteration cycle. Inputs: `--model` (path to an external model file, or `--template <locale>` to regenerate from the YAML), `--label`, `--locale`, optional `--workingset <path>`. Steps: preflight validation with the repo's own validator (scripts/validate_interaction_models.py, same checks CI runs), wrap into the SMAPI payload, deploy to the LAB skill only (hard guard below), poll the build, run the battery via `ask smapi profile-nlu` with the standing SMAPI_DELAY, save the run as a JSON file under `scripts/nlu-lab/runs/`.
- `export`: produce a deploy-ready custom model JSON (the `{"interactionModel": ...}` wrapping) from any source, with preflight validation and a recommended filename carrying locale and label. This is the authoring side of the publish flow.
- `serve`: start the read-only webapp on the LAN (phone-friendly), serving the runs directory.

Hard safety guard: the deploy step refuses any skill id that is not the lab skill id from `lab.json`. `lab.json` is the guard's trust root: a missing or unparseable file makes `run` refuse rather than re-init, and the file pins the lab skill NAME alongside the id, refusing on mismatch. The household skill and any other skill are out of reach by construction. Deploys toward the live skill remain the existing rebuild-endpoint path, never the lab.

Reuse over reimplementation: `init-skill` reads the committed `Alexa/Manifest/manifest.json` template from the repo tree in Python and applies the locale/endpoint/version/invocation-name substitutions (the C# generator's real payload is that template; the drift risk is accepted and the lab skill's manifest needs locales and a valid endpoint far more than the full interface set, since profile-nlu never invokes the endpoint; profile-nlu also matches raw text against the model and ignores the invocation layer, but the manifest's publishing locales carry the invocation name and it must be substituted). `run` reuses the profile-nlu invocation and payload parsing that already exist in `scripts/nlu_wrapper_battery.py` (parameterized by `--skill-id`, SMAPI_DELAY aware, including the top-level `selectedIntent` shape quirk) and `tests/integration/smapi_client.py`, extracting rather than copying.

Battery: the committed NLU fixtures for the locale (tests/integration/fixtures/<locale>.yaml, same format; the `e2e_*.yaml` siblings in that directory are excluded) plus an optional working-set YAML per case under investigation, following the fixture format with `expected_intent` optional; the issue #46 table becomes `workingset-jf933.yaml`.

Run JSON schema: `{id, timestamp, locale, label, model_source, model_sha256, skill_id, status, error?, results: [{utterance, source, expected_intent?, selected_intent, slots, considered_intents, raw}]}`. Each result SNAPSHOTS its resolved expectation at run time (null for expectation-less working-set rows) and the matrix colors exclusively from the run's own snapshot, never from the fixture or working-set files, which can drift or be deleted between runs. The `raw` field keeps the full profile-nlu payload so views can be rebuilt without re-probing. Runs are plain files: diffable, reviewable in chat, and the webapp holds no state of its own.

Webapp views:
- Run list per locale with labels, timestamps, and each run's expectation-failure count (the before/after matrix is change-driven and cannot surface a row failing its expectation in BOTH runs; the count makes standing failures visible).
- The routing matrix: rows are the UNION of the two chosen runs' batteries (a row present in only one run shows absent in the other column; a fresh working set against an older baseline is the expected first use, not an edge case), comparison is same-locale only (guard). Cells carry the intent short name. Judgment is INTENT-SELECTION level only: slots are displayed in the row detail but never judged, because catalog-backed slot types have no catalog wiring on the lab skill (decision 2's caveat) and would go permanently red otherwise. A cell whose selected intent differs from the older run is highlighted; it is RED when the newer run's intent fails the row's expectation (a fixture's `expected_intent`, or a working-set row's optional expectation) and AMBER when it changed with no expectation to judge against.
- Row detail: filled slots, entity resolution status, and the considered intents list.
- Intent legend: the full intent roster of the model under test (short name, full intent name, one sample each), collapsible.
- Multi-language chrome from a strings file; en and it first; content stays in the tested locale.

A failed deploy or build timeout marks the run `failed` with the reason and saves it; a mid-battery interruption saves a partial run with an `interrupted` status; no ghost runs. `scripts/nlu-lab/runs/` is gitignored (raw profile payloads, churn); runs are working artifacts, not repo content.

### Phase 2: the diagnostics tab (plugin settings page)

The plugin reads its own Jellyfin log files in-process (ApplicationPaths log directory; no credentials, no upload) and renders the field view in a settings tab:
- Arrivals grouped by correlation id: request type, intent, locale, session.
- Misrouting suspects: FallbackIntent arrivals, play-shaped utterances arriving as MediaInfo or PlayRadio, fuzzy-miss and refusal lines from the handler debug policy.
- Error census by class, including PlaybackFailed error types (the JF-929 log line).
- Each entry expandable to the full debug chain for that correlation id.

The log parser is shared logic: same grammar in the lab (for our workstation logs) and in the tab (for user instances). Phase 2 keeps it in-process and read-only.

### Phase 3: in-plugin probe and custom model lifecycle

- Single-utterance probe in the tab: an additive profile-nlu call through the plugin's existing SMAPI integration, read-only against the user's own live model. The user types an utterance and sees the selected intent, slots, and considered intents. This is the self-service debugger that would have auto-diagnosed issue #46.
- Custom model support completed: multi-locale custom models (a locale-to-URL map in configuration instead of the single CustomModelUrl pair; the map MUST be a Collection DTO, never a Dictionary, per the XmlSerializer IDictionary crash gotcha; the single-pair to map migration is specced when Phase 3 is planned), provenance and status surface (which model is live, stock or custom, hash and date, drift versus current stock), export of the live model for modification, guided import, and the restore path. Live-skill model loading as a run source lands in this phase too (Phase 1's `run` takes `--model` files and `--template` regeneration only). The publish guide lives in the tab and mirrors to the docs site as a static page.

## What each phase needs

Phase 1: new scripts directory, one-time lab skill creation, zero plugin changes. Phase 2: plugin code (new controller endpoints + config page tab), full repo gates. Phase 3: plugin code (SMAPI profile-nlu additive, configuration schema extension), full repo gates.

## Testing

Phase 1: unit tests for run serialization and battery assembly; the battery itself is the functional test; webapp verified with rendered screenshots (house rule). The log-parser grammar lands with Phase 2 (its first consumer) and is unit-tested there. Phase 2 and 3: unit tests for the endpoints and the parser shared with the lab; on-instance verification per the deploy checklist.

## Non-goals (v1)

No model editing UI. No upload button in the webapp (file loading is CLI). No shared hosted lab for users (a future extension with credential problems of its own). No catalog wiring in the lab skill.
