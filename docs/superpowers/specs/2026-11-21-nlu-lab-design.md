# NLU Lab: design

Date: 2026-11-21. Status: approved in brainstorming with the maintainer (visual companion session, mockups in .superpowers/brainstorm/4063171-1795238988/). Motivating case: JF-933 (issue #46, the MediaInfoType vocabulary collision in de/en/fr), and the it-IT tuning history where every fix risked breaking another route.

## Problem

Iterating on an interaction model today is slow and blind. One iteration means: edit the YAML template, regenerate the JSON, deploy via SMAPI, wait for the build, run utterance probes one by one, and compare results by eye. Regressions surface only on a real device, after shipping. The maintainer wants a precise, fast loop: load a voice model from an external file (no plugin update involved), probe utterance routing, and SEE how routing changes between iterations, before anything reaches users. Users of the plugin need the same class of insight self-service, inside the plugin settings, before they ever open an issue.

## Decisions from the brainstorming

1. Operator split: the maintainer's agent drives the iterations; the webapp is the validation dashboard the maintainer reads, phone-first.
2. Iteration target: a DEDICATED lab skill on the vendor account, created once. The household skill is never touched by the lab. Accepted caveat: the lab skill carries no Jellyfin catalog wiring, so catalog-backed slots behave differently there than live; intent-selection fidelity holds for static-vocabulary and free-text utterances (the JF-933 class), while catalog-wired types can gate selection per JF-684, so lab greens on catalog-wired rows under-predict live no-selections (the bench's reporter cross-check guards the JF-933 dataset specifically).
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
- `init-skill`: one-time provisioning of the lab skill via SMAPI from the committed manifest template with endpoint+version substitutions; the dedicated invocation name is applied later, at `run`'s model deploy. Stores the lab skill id and pinned name in a local config file (`scripts/nlu-lab/lab.json`).
- `run`: the whole iteration cycle. Inputs: `--model` (path to an external model file, or `--template <locale>` to build from the YAML IN MEMORY via the generator's `build_model`/`serialize_model`, never invoking its `main()`, which always writes `model_<locale>.json` into the repo tree and would dirty the working tree), `--label`, `--locale`, optional `--workingset <path>`, optional `-k` substring filter. Preflight imports the validator's `validate_single_model(locale, lm)` ONLY (the module's `main()` scans the repo's models directory and cannot take an external path; the cross-locale and template-drift checks would produce false positives against an external file). Steps: preflight, wrap into the SMAPI payload, deploy to the LAB skill only (hard guard below), poll the build, run the battery via `ask smapi profile-nlu` with the standing SMAPI_DELAY, save the run as a JSON file under `scripts/nlu-lab/runs/`.
- `export`: produce a deploy-ready custom model JSON (the `{"interactionModel": ...}` wrapping) from any source, with preflight validation and a recommended filename carrying locale and label. INVOCATION-NAME POLICY: export requires an explicit `--invocation-name` and never passes the source's name through; the plugin's custom-model import deploys the file's `invocationName` verbatim onto the importing user's skill, so a pass-through export would silently rename every importer to the household's or the lab's name. This is the authoring side of the publish flow.
- `serve`: start the read-only webapp on the LAN (phone-friendly), serving the runs directory.

Hard safety guard: the deploy step refuses any skill id that is not the lab skill id from `lab.json`. `lab.json` is the guard's trust root: a missing or unparseable file makes `run` refuse rather than re-init, and the file pins the lab skill NAME alongside the id; at deploy time the tool fetches the live skill via SMAPI and refuses if its name no longer matches the pinned name, so a stale id pointing at a renamed or foreign skill is caught before anything is written. The household skill and any other skill are out of reach by construction. Deploys toward the live skill remain the existing rebuild-endpoint path, never the lab.

Reuse over reimplementation: `init-skill` reads the committed `Alexa/Manifest/manifest.json` template from the repo tree in Python and applies the endpoint and version substitutions, which are exactly the two the C# generator applies (the committed manifest carries no invocation-name field; publishing locales carry the display name; the drift risk is accepted and the lab skill's manifest needs locales and a valid endpoint far more than the full interface set, since profile-nlu never invokes the endpoint). The invocation name lives in `languageModel.invocationName` inside the interaction MODEL, so the dedicated lab invocation name is substituted by `run`'s model-deploy step, not by init-skill: a committed model deployed to the lab skill carries the lab's name, never the household's. `run` reuses the profile-nlu invocation and payload parsing that already exist in `scripts/nlu_wrapper_battery.py` (parameterized by `--skill-id`, SMAPI_DELAY aware, including the top-level `selectedIntent` shape quirk) and `tests/integration/smapi_client.py`, extracting rather than copying.

Battery: the committed NLU fixtures for the locale (tests/integration/fixtures/<locale>.yaml, same format; the `e2e_*.yaml` siblings in that directory are excluded) plus an optional working-set YAML per case under investigation, following the fixture format with `expected_intent` optional; the issue #46 table becomes `workingset-jf933.yaml`.

Phase 1 acceptance bench: issue #46. Noise control first: an A/A run (the same STOCK model deployed and probed twice) must show ZERO changed cells before any before/after diff is trusted; the acceptance sequence is A/A clean, then the steal reproduction. The reporter's Utterance Profiler table (his vendor, his dev skill, de-DE) is the ground truth: the lab must reproduce his observed steals against the STOCK de-DE model on the lab skill (MediaInfoIntent for "spiele das lied die streithörnchen", PlayRadioIntent for "den titel/den song", no selection for "spiele die streithörnchen"), which validates the lab's fidelity against a real observed dataset, and then carry the fix candidate from JF-933 through the matrix, showing the steal rows flipping green with no red appearing elsewhere in the battery. The bench working set pins `expected_intent: PlaySongIntent` on the steal rows (the desired end state, so the fix run's flips judge GREEN rather than AMBER; the stock run's reproduction shows through its recorded selections and failure count). The reporter offered to run further Profiler tests: cross-check any surprising lab result with him before trusting either side. Tight-iteration note: the battery accepts a `-k` substring filter (it-IT alone is about 286 fixture rows at 1.5s spacing, roughly 7 minutes plus build, versus about 2 minutes for de-DE's 83; a filtered working-set-only pass is the fast loop during a fix iteration).

Run JSON schema: `{id, timestamp, locale, label, model_source, model_sha256, skill_id, status, error?, intent_roster: [{intent, sample}], results: [{utterance, source, status, expected_intent, selected_intent, slots, considered_intents, raw}]}`. Each result carries a per-row `status`: `ok`, `no_selection`, or `error` (profile-nlu returns sporadic 500s; tests/integration/smapi_client.py documents them as JF-406). ERROR ROWS ARE EXCLUDED FROM JUDGMENT: they render as error cells in the matrix, never as changed or red, and they do not count in the expectation-failure count; without this a transient 500 would masquerade as a routing change. `expected_intent` is always present and nullable (null for expectation-less working-set rows): each result SNAPSHOTS its resolved expectation at run time and the matrix colors exclusively from the run's own snapshot, never from the fixture or working-set files, which can drift or be deleted between runs. `intent_roster` snapshots the model's full intent list with one sample each, so the legend is buildable from the run alone under the same self-containment rule. The `raw` field keeps the full profile-nlu payload so views can be rebuilt without re-probing. Runs are plain files: diffable, reviewable in chat, and the webapp holds no state of its own.

Webapp views:
- Run list per locale with labels, timestamps, and each run's expectation-failure count (the matrix is change-driven: a row failing its expectation in BOTH runs is not highlighted there; the count makes those standing failures visible).
- The routing matrix: rows are the UNION of the two chosen runs' batteries, keyed by utterance plus source (a working-set row duplicating a fixture utterance stays a distinct row), a row present in only one run shows absent in the other column (a fresh working set against an older baseline is the expected first use, not an edge case), comparison is same-locale only (guard). Cells carry the intent short name. Judgment is INTENT-SELECTION level only: slots are displayed in the row detail but never judged, because catalog-backed slot types have no catalog wiring on the lab skill (decision 2's caveat) and would go permanently red otherwise. A cell whose selected intent differs from the older run is highlighted; it is RED when the newer run's intent fails the row's snapshotted expectation and AMBER when it changed with no expectation to judge against.
- Row detail: filled slots, entity resolution status, and the considered intents list.
- Intent legend: the full intent roster of the model under test (short name, full intent name, one sample each), collapsible.
- Multi-language chrome from a strings file; en and it first; content stays in the tested locale.

The run file is created at run start; a failed deploy or build timeout marks it `failed` with the reason, and any interruption at any point mid-run (Ctrl-C included) marks it `interrupted` with whatever results were already collected; no ghost runs. `scripts/nlu-lab/runs/` is gitignored (raw profile payloads, churn); runs are working artifacts, not repo content.

## Direction: later phases (each gets its own spec when planned)

The sections below are DIRECTION, not part of this spec's implementation scope. Phase 1 is the only plannable scope here; the following phases get their own specs, each starting from the caveats recorded below.

Diagnostics tab (was Phase 2). The plugin reads its own Jellyfin log files in-process (ApplicationPaths log directory; no credentials, no upload) and renders the field view in a settings tab: arrivals grouped by correlation id, misrouting suspects, error census by class including the JF-929 PlaybackFailed types, each entry expandable to its debug chain; the lab-side consumer (`lab.py logs`) shares the parser. CAVEATS that reshape it when specced: (a) the platform does not deliver the spoken text to the skill, so "play-shaped utterances arriving as MediaInfo or PlayRadio" is NOT detectable from logs; the detectable suspects are what actually arrives: PlayRadio with an empty station slot, MediaInfo with an unresolved media_info_type, FallbackIntent arrivals (the issue #46 class is visible only to the in-plugin probe of the later phase); (b) the debug chain depends on the log level: Serilog defaults to Information, so for most users the chain is empty until the documented per-plugin override is enabled, and the tab must detect this and point at the override; (c) the parser turns log line formats into a contract: a test must pin every consumed line shape, or a wording tweak in a log message silently breaks the tab.

In-plugin probe and custom model lifecycle (was Phase 3). Single-utterance probe through the plugin's SMAPI integration (read-only against the user's live model); multi-locale custom models (locale-to-URL map as a Collection DTO, never a Dictionary, per the XmlSerializer crash gotcha; the single-pair migration is specced then); provenance and status surface; live-model export; guided import and restore; live-skill loading as a lab run source. CAVEAT: the FIRST task of that spec is a spike verifying that profile-nlu is reachable from Alexa.NET.Management and that the plugin's LWA token scopes permit it; nothing else in that phase is committed until the spike answers.

## Sequencing inside Phase 1

The CLI (`init-skill`, `run`, `export`) and the run JSON land BEFORE the webapp: the #46 acceptance bench does not depend on `serve`, and the bench is the reason this exists now. The webapp follows once runs exist to render.

## What each phase needs

Phase 1: new scripts directory, one-time lab skill creation, zero plugin changes. The later phases: plugin code with full repo gates, each under its own spec.

## Testing

Phase 1: unit tests for run serialization and battery assembly; the battery itself is the functional test; webapp verified with rendered screenshots (house rule). The log-parser grammar belongs to the diagnostics-tab spec (its first consumer), with the line-format contract test recorded there as a caveat; the later phases carry their own testing specs.

## Non-goals (v1)

No model editing UI. No upload button in the webapp (file loading is CLI). No shared hosted lab for users (a future extension with credential problems of its own). No catalog wiring in the lab skill.
