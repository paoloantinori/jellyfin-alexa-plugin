---
id: JF-766
title: >-
  hi-IN PlayVideo bare "{title} देखो" carries the same SearchQuery absorption
  class as JF-761 (committed episode sample shares the tail verb)
status: Done
assignee: []
created_date: '2026-10-05 00:00'
updated_date: '2026-10-05 02:27'
labels:
  - interaction-model
  - nlu
  - hi-IN
dependencies: []
references:
  - JF-761
  - JF-551
  - JF-459
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-761 /simplify altitude review (2026-10-05, cross-locale sweep of all 17 templates).

The ja-JP steal JF-761 just fixed (bare `{title} を見たい` on PlayVideoIntent absorbing the committed PlayEpisodeIntent episode address wholesale, catalog-independent, live 4/4) has ONE structural twin: hi-IN.

- `templates/hi-IN.yaml` ~line 185: PlayVideoIntent carries the BARE carrier `{title} देखो` (`title: AMAZON.SearchQuery`, lines ~194-196).
- `templates/hi-IN.yaml` ~line 465: PlayEpisodeIntent carries the committed sample `{series_name} सीज़न {season_number} एपिसोड {episode_number} देखो`, ending in the SAME tail verb देखो.

Same subsumption surface as the ja bug: the bare carrier is empty-prefix + SearchQuery + tail verb, so the whole episode address can land in `title` and route PlayVideoIntent (guaranteed miss, since PlayVideoIntentHandler searches Movie+Episode by SearchTerm and the absorbed span is not a title). NOTE: this is STRUCTURAL analysis, not a live probe; hi-IN has never been probed for this shape (the JF-551 closure probe covered ja only for this class).

Checked and CLEAR: every other locale's PlayEpisodeIntent samples end in the series name or episode_number, never a tail verb shared with their locale's bare video carrier (en-* watch/play verbs sit at the FRONT of episode samples; fr/es/nl/it/de/ar/pt checked). hi-IN's sibling bare `{title} शुरू करो` is safe, the episode sample ends देखो not शुरू करो.

OTHER BARE SearchQuery VIDEO CARRIERS, audited and DISPOSITIONED (recorded here so the class audit is never redone): ar-SA carries bare `شاهد {title}` and nl-NL carries bare `kijk {title}` (both AMAZON.SearchQuery), but neither locale's PlayEpisodeIntent samples end in a verb those carriers share (ar/nl episode forms end in the series name or episode number), so the subsumption surface of the ja/hi class is ABSENT there. Structural analysis only, no live probe; if a live ar/nl episode steal is ever observed, start from this note, not from scratch.

FIX SHAPE (per the JF-551/JF-761 playbook): remove or qualify the bare `{title} देखो` carrier; the anchored `फिल्म {title} देखो` form (~line 191) already keeps the watch-verb carrier. Requires a live profile-nlu probe first (does the steal reproduce on the live model with a library series?) and the JF-459-style regression guards after (bare movie watch must not newly route PlayEpisode).

OPTIONAL STRUCTURAL GUARD: a validator check for the class (bare SearchQuery-terminated carrier on PlayVideoIntent vs any same-locale PlayEpisode sample sharing the tail verb) would have caught both ja and hi mechanically; the JF-761 ja fix closed the only other instance, so the check's yield today is zero, file it only if the class recurs.

Evidence source: the JF-761 altitude review agent (2026-10-05), line numbers from the worktree diff review; re-verify line numbers against main before editing.

## Implementation (2026-10-05, worktree agent)

PROBE VERDICT: REPRODUCED (pre-fix, live profile-nlu, 2026-10-05, skill `amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83` discovered fresh via list-skills-for-vendor, 4x repeats each, selectedIntent-first parsing):

1. `game of thrones सीज़न 1 एपिसोड 3 देखो` → PlayVideoIntent, title='game of thrones सीज़न एक एपिसोड तीन' (whole address absorbed). 4/4. STEAL.
2. `the bear सीज़न 1 एपिसोड 3 देखो` → PlayEpisodeIntent, clean slots (series_name='the bear', season 1, episode 3), series_name ER=ER_SUCCESS_MATCH. 4/4. CLEAN.
3. `breaking bad सीज़न 1 एपिसोड 3 देखो` → PlayVideoIntent, title='breaking bad सीज़न एक एपिसोड तीन'. 4/4. STEAL.
4. `इंसेप्शन देखो` (bare movie watch, guard baseline) → PlayVideoIntent, title='इंसेप्शन'. 4/4.
5. `फिल्म इंसेप्शन देखो` (qualified anchor) → PlayVideoIntent. 4/4.
6. `game of thrones सीज़न 1 एपिसोड 3 चलाओ` (clean sibling) → PlayEpisodeIntent, series_name ER=ER_SUCCESS_NO_MATCH. 4/4.

hi-IN NUANCE vs ja (recorded, not averaged): the steal is ER-status-DEPENDENT here. The catalog-matched library series ('the bear', ER_SUCCESS_MATCH) routed PlayEpisodeIntent clean, while both ER_NO_MATCH legs were stolen. In ja the steal survived ER_SUCCESS_MATCH (JF-761's "catalog-INDEPENDENT" finding); in hi-IN the library-wired SeriesName catalog partially masks the absorption. The live hi-IN SeriesName behaves CATALOG-WIRED ('the bear' MATCH; committed seed values 'game of thrones'/'breaking bad' NO_MATCH), consistent with JF-493 (SeriesName is declared by all 17 locales and catalog sync targets it) and resolving the JF-761 recorded conflict the same way: the committed static 8 values are seeds, not the live matching surface. The absorption surface itself (bare carrier + shared tail verb) is identical, and any series the live catalog does not match (a new show, a typo, an unmapped library name) hits the steal, so the fix is warranted on the same grounds as ja.

CHOSEN: the JF-761 minimal removal. The bare `{title} देखो` sample is deleted from PlayVideoIntent in `templates/hi-IN.yaml` (JF-766 comment inline carrying the rationale); the qualified `फिल्म {title} देखो` survives untouched (watch-verb carrier stays anchored per JF-459 discipline); the sibling bare `{title} शुरू करो` deliberately STAYS (no committed sample shares its tail, no steal recorded, removal would be speculative recall loss). No new samples added.

Changed: templates/hi-IN.yaml, regenerated model_hi-IN.json (one sample line), VOICE_COMMANDS.md + docs/VOICE_COMMANDS_BY_LOCALE.md via the generator (362 → 361 phrases), tests/integration/fixtures/hi-IN.yaml (exact-value anti-pollution pin, red until the rebuilt hi-IN model deploys). docs/playback-lifecycle-hi-IN.md references no देखो-tailed edge (checked; the graphs.json/data.json देखो hits are the FallbackIntent response string), so no docs md regen needed.

PIN DEVIATION from the ja convention (deliberate, evidence-first): the pin value is the RAW slot value 'game of thrones', NOT the canonical committed-type 'Game of Thrones'. The suite's exact-value comparison runs against slot.value, and profile-nlu returns the spoken raw text there (4/4 on the hi-IN चलाओ sibling; one probe on the deployed ja pin utterance, which returned series_name='game of thrones' raw while the ja fixture pins 'Game of Thrones'). The ja row will therefore read RED on its next live suite run on the value comparison despite correct routing; filed as JF-769 rather than averaged or silently copied here.

OPTIONAL VALIDATOR GUARD: NOT built (yield zero per the structural-guard note above; the ar-SA/nl-NL audit disposition is in the filing body).

### LIVE-PROBE INSTRUCTION FOR THE ORCHESTRATOR (after the rebuilt hi-IN model is deployed via the rebuild endpoint, following the wave DLL deploy)

Run each utterance through `ask smapi profile-nlu --locale hi-IN`, 4 repeats, selectedIntent-first parsing. ER dimension, stated ONCE for the whole matrix: the live SeriesName is catalog-wired today ('the bear' MATCH, static seed names NO_MATCH) and rebuilds preserve wiring (the skill-recreation memory), but if the rebuild instead redeployed the static seeds the two statuses swap. Every probe below owns the INTENT SELECTION only; either ER status on series_name is acceptable and is the confidence class, never this bug.

1. `game of thrones सीज़न 1 एपिसोड 3 देखो` → EXPECT PlayEpisodeIntent with series_name='game of thrones', season_number='1', episode_number='3'. This is the fixed steal. If it still selects PlayVideoIntent, the removal did not reach the live model (check the rebuild), not a template bug.
2. `breaking bad सीज़न 1 एपिसोड 3 देखो` → EXPECT PlayEpisodeIntent (same stolen class).
3. `the bear सीज़न 1 एपिसोड 3 देखो` → EXPECT PlayEpisodeIntent (was already clean pre-fix; must stay clean). Note which ER status it reads: this doubles as the wiring check for the paragraph above.
4. REGRESSION GUARD, bare movie watch: `इंसेप्शन देखो` → EXPECT either a clean PlayVideoIntent (another sample absorbing it is fine) or NO_SELECTION/Fallback (the accepted JF-459-class recall cost). FORBIDDEN: a PlayEpisodeIntent selection; that would be a new competition to file, not a fixture bug to weaken.
5. REGRESSION GUARD, qualified carriers: `फिल्म इंसेप्शन देखो` → EXPECT PlayVideoIntent (4/4). `game of thrones सीज़न 1 एपिसोड 3 चलाओ` → EXPECT PlayEpisodeIntent (the clean sibling must stay clean).
6. After the probes pass, run the NLU suite for hi-IN (`./scripts/run_nlu_tests.sh -k "hi-IN"`): the new pin going GREEN on the first live run is the task's close evidence. Escape hatch (the nlu_trainer_nondeterminism discipline): if the intent ROUTES PlayEpisodeIntent but the row reads red on slot filling (numbers unfilled, raw value drift), do NOT weaken the pin and do NOT call it a regression on one reading; run the 6-10 probe battery and the identical-content rebuild check first. A persistent PlayVideoIntent selection is the only finding that reopens this task. NOTE: the ja JF-761 pin is expected RED on its next live run for the raw-vs-canonical reason (JF-769); do not conflate the two rows.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Live profile-nlu probe reproduces or refutes the steal (4 repeats, library series + uncataloged series) (REPRODUCED 2026-10-05: 'game of thrones' and 'breaking bad' legs stolen into PlayVideoIntent.title 4/4 each; 'the bear' library leg clean 4/4, ER-status-dependent masking recorded in the Implementation section)
- [x] #2 If reproduced: bare `{title} देखो` removed/qualified in templates/hi-IN.yaml, model regenerated, mirrors regenerated (removed with inline JF-766 comment; model_hi-IN.json one line; VOICE_COMMANDS.md + docs/VOICE_COMMANDS_BY_LOCALE.md via generator, 362→361)
- [ ] #3 Regression guards probed post-deploy (anchored फिल्म form clean; bare movie watch must not route PlayEpisode) (ORCHESTRATOR: the 6-step post-deploy matrix is in the Implementation section; rides the next wave DLL deploy + hi-IN rebuild)
- [x] #4 NLU fixture pin added per the JF-761 ja convention (red until deploy, exact-value series_name) (fixtures/hi-IN.yaml; raw-value pin 'game of thrones' with the ja raw-vs-canonical deviation documented inline and filed as JF-769)
- [x] #5 Validators pass with no new warnings (validate_interaction_models PASS 294 warnings = main baseline; validate_locales PASS no new gaps; NLU dry-run clean: all fixture files collect, the 9 hi-IN rows incl. the new pin are schema-valid and skipped pending SMAPI. The dry-run's "8 passed" line is the unrelated simulator smoke rows, not pin evidence.)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED 2026-10-05 by the orchestrator after the full cycle: merged into main (worker commit 152fb11f, --no-ff; direct verification: the removal, the regenerated model, the fixture pin, the mirrors); the hi-IN model rebuild + the 6-step probe matrix ride the NEXT DLL deploy (the wave deploy in flight predates the merge). JF-769 filed by this task.
<!-- SECTION:FINAL_SUMMARY:END -->
