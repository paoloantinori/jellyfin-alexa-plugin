---
id: JF-761
title: >-
  ja PlayVideo bare "{title} を見たい" steals the 見たい episode form (SearchQuery
  absorption, catalog-independent)
status: Done
assignee: []
created_date: '2026-10-04 20:05'
updated_date: '2026-10-05 00:33'
labels:
  - interaction-model
  - nlu
  - ja-JP
dependencies: []
references:
  - JF-551
  - JF-459
  - JF-684
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-551 closure probe (2026-10-04, live profile-nlu, 4x-stable).

The ja-JP episode-addressing form "{series_name} のシーズン {season_number} エピソード {episode_number} を見たい" (a COMMITTED PlayEpisodeIntent sample) is stolen by PlayVideoIntent's bare carrier "{title} を見たい" (AMAZON.SearchQuery): the whole span up to 見たい lands in title (live: "breaking bad のシーズン 1 エピソード 3 を見たい" and "the bear のシーズン 1 エピソード 3 を見たい" both SELECT PlayVideoIntent with title="... のシーズン 1 エピソード 3", 4/4 repeats). This is the one REMAINING stable episode misroute in the 8 JF-551 locales: it survives even with a catalog-matched series (ER_SUCCESS_MATCH), so unlike the pt/es sibling steals it is NOT the catalog NO_MATCH confidence class but pure SearchQuery absorption.

Why it is filed rather than fixed in JF-551: every fix shape lives OUTSIDE PlayEpisode's own samples (which already carry the exact losing form; anchors cannot outrank a slot that absorbs the entire utterance):
1. Qualify or drop PlayVideo's bare "{title} を見たい" (keep "映画 {title} を見たい" etc.). Risk: it is the most natural bare video-by-name request in ja, so removing it likely regresses video routing; needs the JF-459-style live A/B (deploy candidate, probe video one-shots + episode forms, revert on regression).
2. Accept the steal and de-duplicate: drop PlayEpisode's 見たい sample (it never wins; today it is dead weight) and keep 再生して as the only episode carrier. Cheapest, but removes 見たい episode addressing entirely rather than fixing it.
3. Handler-side: let PlayVideoIntentHandler detect the episode-shaped title (シーズン N エピソード N pattern) and delegate to the episode path. No model change, but adds a parsing convention to a handler.

Context for whoever picks it up: the 再生して forms (both word orders) route PlayEpisodeIntent cleanly since the 2026-09-26/27 ja rebuilds, so this is a single-form residual, not a broken locale. Probe protocol and the nondeterminism discipline (4x repeats, selectedIntent-first parsing) are in the locale-routing-probe skill and tests/integration/smapi_client.py.

## Implementation (2026-10-05, worktree agent)

CHOSEN: Option 1, the minimal removal. The bare `{title} を見たい` sample is deleted from PlayVideoIntent in `templates/ja-JP.yaml` (with a JF-761 comment carrying the rationale and the deliberate keep of `{title} を見せて`); the qualified `映画 {title} を見たい` survives untouched, so the watch-verb carrier stays anchored per JF-459 discipline. No new samples were added (every addition is an unprobed surface; the filing's option 1 reads "qualify or drop", and qualification already exists via the 映画 form).

Why option 1 over 2 and 3: it is the exact operation JF-551 already proved live on this intent pair in this locale (removing bare `{title} を再生して` closed the 再生して steals; both orders route PlayEpisodeIntent since the 09-26/27 rebuilds), so the post-removal win of the anchored PlayEpisode 見たい sample is precedent-backed, not speculative. Option 2 accepts a guaranteed-miss user experience (the stolen shape can never resolve: the handler searches Movie+Episode by SearchTerm, and the absorbed span "... のシーズン N エピソード M" is not a title). Option 3 adds a fragile free-text parsing convention to a handler and leaves the model-level ambiguity in place.

Changed: templates/ja-JP.yaml, regenerated model_ja-JP.json, VOICE_COMMANDS.md + docs/VOICE_COMMANDS_BY_LOCALE.md (generator), tests/integration/fixtures/ja-JP.yaml (exact-value anti-pollution pin on the 見たい episode form, red until deploy per the JF-551 es-ES convention; the pin series is the committed static SeriesName value 'Game of Thrones' so the pin is deterministic against the saved model). No .cs changes. Validators PASS with zero new warnings (294 = main baseline).

RECORDED CONFLICT (sources disagree, surfaced not averaged): the JF-551 closure probe reported 'the bear' resolving ER_SUCCESS_MATCH on the LIVE ja skill, but the committed ja SeriesName type is STATIC with 8 fixed values (Breaking Bad, Game of Thrones, Stranger Things, Money Heist, Peaky Blinders, The Walking Dead, Dark, Sherlock) and no valueSupplier, so 'the bear' is not in it. Either the live model carries more than the committed seeds or the probe's ER reading was loose; unresolved here, which is why the fixture pin uses the committed-type value instead.

### LIVE-PROBE INSTRUCTION FOR THE ORCHESTRATOR (after the rebuilt ja model is deployed via the rebuild endpoint)

Run each utterance through `ask smapi profile-nlu --locale ja-JP`, 4 repeats, selectedIntent-first parsing (the nlu_trainer_nondeterminism discipline):

1. `game of thrones のシーズン 1 エピソード 3 を見たい` → EXPECT PlayEpisodeIntent with series_name="Game of Thrones", season_number="1", episode_number="3". This is the fixed steal; 4/4. If it still selects PlayVideoIntent, the removal did not reach the live model (check the deploy), not a template bug. If it selects PlayEpisodeIntent but series_name carries ER_SUCCESS_NO_MATCH, that is the committed-vs-live type conflict above, not this bug.
2. `the bear のシーズン 1 エピソード 3 を見たい` (real-library variant) → EXPECT PlayEpisodeIntent; also note what series_name's ER status reads (this doubles as the conflict check above; 'the bear' resolved ER_SUCCESS_MATCH on 2026-10-04 per JF-551).
3. `breaking bad のシーズン 1 エピソード 3 を見たい` → EXPECT PlayEpisodeIntent (uncataloged series may land in series_name with ER_SUCCESS_NO_MATCH; that is acceptable, the JF-551 NO_MATCH confidence class, not the absorption bug; the intent selection is what this fix owns).
4. REGRESSION GUARD, bare movie watch: `インセプション を見たい` → EXPECT either no PlayVideoIntent selection (Fallback/NO_SELECTION is the accepted JF-459-class recall cost) or a clean PlayVideoIntent. What is FORBIDDEN is a PlayEpisodeIntent selection; if the bare form now routes PlayEpisode, that is a new competition to file, not a fixture bug to weaken.
5. REGRESSION GUARD, qualified carriers unchanged: `映画 インセプション を見たい` → EXPECT PlayVideoIntent, title="インセプション" (4/4). `game of thrones のシーズン 1 エピソード 3 を再生して` → EXPECT PlayEpisodeIntent (the already-clean sibling must stay clean).
6. REGRESSION GUARD, bare 見せて survivor: `インセプション を見せて` → EXPECT PlayVideoIntent (the deliberately kept sibling carrier must still work).
7. After the probes pass, run the NLU suite for ja (`./scripts/run_nlu_tests.sh -k "ja-JP"`, or the full suite): the new fixture pin (the Game of Thrones 見たい row) must go GREEN on the first live run; that green is the task's close evidence.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (dotnet build: 0 Warnings, 0 Errors, dual-TFM)
- [x] #2 dotnet test passes (5194/5194 net9.0 AND net10.0 = main baseline; no .cs changed, neutrality run)
- [x] #3 No new compiler warnings introduced (build 0 warnings; validate_interaction_models 294 warnings = main baseline)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no C# touched, model-only change)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no C# touched, model-only change)
- [x] #6 NLU test fixtures updated if interaction model changed (tests/integration/fixtures/ja-JP.yaml: exact-value PlayEpisode 見たい pin added, red until model deploy; dry-run green)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent or handler logic; the NLU pin in #6 is the routing regression device; the orchestrator's post-deploy probe matrix is in the Implementation section above)
- [x] #8 Locale response strings added to all 17 locales (N/A: no new response strings; ja sample removal only)
- [x] #9 /simplify passed (no blocking cleanups remaining) (4 angles: reuse + simplification findings APPLIED, comment blocks trimmed per the JF-551 block convention; efficiency CLEAN; altitude CLEAN for ja, the hi-IN cross-locale finding filed as JF-766 same-turn)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked) (0 correctness bugs; 3 findings: ER-class claim fixed by switching the pin to the committed-type series 'Game of Thrones' with the committed-vs-live conflict recorded in the task file; ar-SA/nl-NL audit disposition recorded in JF-766; all applied same-turn)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit a6e89d31, --no-ff; direct verification: the removal, the regenerated model, and the fixture pin), combined-tree suite 5205/5205 both TFMs; the ja model rebuild + the 7-step live-probe matrix follow the batched DLL deploy (the model JSONs embed in it). JF-766 filed by this task.
<!-- SECTION:FINAL_SUMMARY:END -->
