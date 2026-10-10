---
id: JF-770
title: >-
  JF-770 - hi-IN: the qualified फिल्म {title} देखो anchor lost routing after the
  JF-766 bare removal (exact sample present in the live model; identical-content
  rebuild does NOT recover: not the trainer-nondeterminism class)
status: In Progress
assignee: []
created_date: '2026-10-05'
updated_date: '2026-10-09 23:14'
labels:
  - interaction-model
  - nlu
  - hi-IN
  - regression
dependencies:
  - JF-766
references:
  - >-
    backlog/tasks/jf-766 -
    hi-IN-PlayVideo-bare-title-देखो-carries-the-same-SearchQuery-absorption-class-as-JF-761-committed-episode-sample-shares-the-tail-verb.md
  - backlog/tasks/jf-459*.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Definition of Done
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-05 by the orchestrator from JF-766's post-deploy probe matrix (its
DoD #3 regression guard FAILURE, the file-not-weaken discipline).

EVIDENCE (all live profile-nlu, hi-IN, 4x repeats per step, selectedIntent-first,
skill discovered fresh):

1. PRE-REMOVAL (the JF-766 worker's probe on the old model):
   `फिल्म इंसेप्शन देखो` → PlayVideoIntent, title='इंसेप्शन', 4/4. CLEAN.
2. POST-REMOVAL model 1 (after the JF-766 DLL deploy + hi-IN rebuild):
   `फिल्म इंसेप्शन देखो` → AMAZON.FallbackIntent, 5/5 readings (1 in the matrix
   + 4 in the stability re-probe). REGRESSED.
3. The exact sample `फिल्म {title} देखो` IS in the live model's PlayVideoIntent
   (verified via get-interaction-model after the rebuild; the only देखो sample
   remaining, 11 total samples). The model content is CORRECT.
4. IDENTICAL-CONTENT REBUILD (the nlu_trainer_nondeterminism discrimination
   step): rebuilt hi-IN again, SUCCEEDED; re-probed 4x: Fallback 4/4. The
   classic trainer flip RECOVERS on identical-content rebuild; this does not.

The JF-766 steal fix itself is verified working (the lead/library/sibling probes
all route PlayEpisodeIntent clean), so this is NOT a revert case: it is a RECALL
LOSS on the qualified video-watch form, and per the matrix's own guard language
a new finding to file, not a fixture to weaken. Both video-watch forms are now
unreachable in hi-IN (bare इंसेप्शन देखो reads NO_SELECTION, the qualified form
reads Fallback), a larger JF-459-class recall cost than the filing anticipated.

HYPOTHESIS (for whoever picks it up): removing the bare sample removed the
trainer's only other देखो-tailed PlayVideo example, and the remaining single
qualified sample is not enough signal for the intent to win Fallback
competition. Candidate remediations to weigh: (a) strengthen the qualified
family (add 2-3 more qualified देखो variants: मूवी {title} देखो, फिल्म देखो
{title} word-order twin, a titled literal example), (b) re-add the bare sample
with a NARROWER slot type than SearchQuery if the model layer allows, (c) accept
the recall cost and document the workaround (the शुरू करो sibling still routes
video). Option (a) is the JF-459-discipline shape. Probe-first: verify the
family-strength hypothesis by probing the OTHER qualified PlayVideo forms
(फिल्म {title} चलाओ etc.) before choosing.

VERIFICATION BAR: the chosen shape deploys, the फिल्म anchor routes PlayVideo
4/4, the JF-766 steal fix stays closed (the episode forms keep routing
PlayEpisode), and the bare-movie guard still passes (no PlayEpisode steal).

## Implementation (2026-10-05, worktree agent)

HYPOTHESIS PROBE VERDICT (live profile-nlu 2026-10-05, skill
`amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83` discovered fresh via
list-skills-for-vendor; the live hi-IN model re-fetched and verified
sample-for-sample equal to the committed post-JF-766 model BEFORE probing; 10
hi-IN utterances plus 3 ja cross-locale controls, 4 repeats each,
selectedIntent-first parsing):

The family-strength hypothesis is CONFIRMED IN ITS GENERAL FORM and REFINED:
single-member tails are NOT universally weak and the देखो subfamily is not
uniquely sick. The failure localizes to the फिल्म-carried SHORT form (the one
exception outside that class: the bare `इंसेप्शन देखो` shape itself reads
NO_SELECTION, the JF-766-accepted recall cost recorded in the filing; every
OTHER non-फिल्म short form probed routes clean). Map:

ROUTES PlayVideoIntent clean 4/4 (title='इंसेप्शन'):
- `वीडियो इंसेप्शन चलाओ` and `वीडियो इंसेप्शन शुरू करो` (multi-member tails)
- `वीडियो इंसेप्शन लगाओ` (SINGLE-member लगाओ tail: the strong carrier routes it)
- `चलो इंसेप्शन देखते हैं` and `क्या तुम इंसेप्शन चला सकते हो` (SINGLE-member
  tails: the long preambles route them)
- `इंसेप्शन शुरू करो` (the kept bare शुरू करो sibling)
- `मैं फिल्म इंसेप्शन देखना चाहता हूँ` (the long मैं preamble rescues the फिल्म
  carrier; nuance: it matched the BARE `मैं {title} देखना चाहता हूँ` sample with
  title='फिल्म इंसेप्शन' whole-absorbed, so the intent is right but the slot
  value is suboptimal)

FAILS (all three फिल्म-led short forms, 4/4 each):
- `फिल्म इंसेप्शन देखो` → AMAZON.FallbackIntent (the JF-770 anchor, re-confirmed)
- `फिल्म इंसेप्शन खोजो` → AMAZON.FallbackIntent (the OTHER single-member
  qualified tail; NEW finding, filed JF-771)
- `फिल्म इंसेप्शन चलाओ` → PlaySongIntent steal, song='फिल्म इंसेप्शन' (the bare
  slot-initial `{song} चलाओ` absorbs it; NEW finding, filed JF-771)

ja CROSS-LOCALE CONTROL (the 映画 family, live post-JF-761 model):
`映画インセプションを見たい` (the EXACT structural analog: single qualified member
of the watch-verb tail under the movie-carrier noun) routes PlayVideoIntent
clean 4/4, as do を再生して and を見せて. The failure class is hi-IN-specific,
not an inherent single-sample weakness.

INTERPRETATION: pre-removal, the bare `{title} देखो` carried the [X][देखो]
pattern and routed the फिल्म-carried anchor (the JF-766 worker's 4/4 datum);
its removal left `फिल्म {title} देखो` as the देखो tail's only member, and the
weak loanword carrier फिल्म on a 3-token form cannot win Fallback competition
alone, while वीडियो and the long preambles can even single-member. The खोजो
and चलाओ failures have NO pre-removal datum (JF-766 never probed them), so
the फिल्म-carrier weakness may predate the removal for those tails; only
देखो has the pre-removal routing proof of regression.

CHOSEN SHAPE (the filing's option (a), evidence-refined): three more QUALIFIED
देखो variants added to PlayVideoIntent in templates/hi-IN.yaml (inline JF-770
comment carries the probe map):
- `वीडियो {title} देखो` (the carrier PROVEN to route short forms)
- `मूवी {title} देखो` (the filing's fresh-carrier suggestion)
- `फिल्म देखो {title}` (word-order twin, the filing's suggestion)
never bare (JF-459/JF-766 discipline). SKIPPED from the filing's list: the
"titled literal example" (a literal title inside a slot position is not
expressible in an interaction-model sample, and a slotless static sample on
the slot-REQUIRED PlayVideoIntent is anti-pattern #1; the JF-403
optional-slot boundary does not apply). The देखो family goes 1 → 4 members
(3 tail-final + 1 verb-medial twin).

PREDICTION (falsifiable, checked by the post-deploy matrix): the
[NOUN][X][देखो] pattern weight restored, `फिल्म इंसेप्शन देखो` routes
PlayVideoIntent 4/4.

Changed: templates/hi-IN.yaml (+3 samples + rationale comment),
model_hi-IN.json regenerated (361 → 364 samples, exactly 3 lines),
docs/VOICE_COMMANDS_BY_LOCALE.md via the generator (--check clean; 361 → 364
phrases; VOICE_COMMANDS.md unchanged, its summary covers it-IT/en-US).
docs/playback-lifecycle-hi-IN.md untouched (its two PlayVideo edges are
चलाओ-tailed, no देखो edge exists), parse_mermaid.py re-run as the no-op
check (no graphs.json/docs-site change). tests/integration/fixtures
deliberately NOT touched (a concurrent worker owns that surface; the
post-deploy matrix below replaces the pin for this task).

### LIVE-PROBE INSTRUCTION FOR THE ORCHESTRATOR (after the hi-IN rebuild rides the next wave DLL deploy)

Run each utterance via `ask smapi profile-nlu --locale hi-IN`, skill id
discovered fresh, 4 repeats, selectedIntent-first.

1. `फिल्म इंसेप्शन देखो` → EXPECT PlayVideoIntent, title='इंसेप्शन', 4/4. THE
   task bar (pre-fix: Fallback 4/4).
2. New variants: `वीडियो इंसेप्शन देखो`, `मूवी इंसेप्शन देखो`,
   `फिल्म देखो इंसेप्शन` → EXPECT PlayVideoIntent 4/4 each.
3. JF-766 steal fix must STAY CLOSED: `game of thrones सीज़न 1 एपिसोड 3 देखो`
   and `breaking bad सीज़न 1 एपिसोड 3 देखो` → EXPECT PlayEpisodeIntent 4/4
   each. If PlayVideoIntent absorbs the address again, the strengthened देखो
   family re-opened the steal: FILE it, do not tune blindly.
4. Bare-movie guard (JF-766 matrix leg 4): `इंसेप्शन देखो` → EXPECT
   PlayVideoIntent or NO_SELECTION/Fallback. FORBIDDEN: PlayEpisodeIntent.
5. Sibling stability: `इंसेप्शन शुरू करो`, `वीडियो इंसेप्शन चलाओ` → EXPECT
   PlayVideoIntent 4/4.
6. Escape hatch (nlu_trainer_nondeterminism): if leg 1 still reads Fallback,
   run the 6-10 probe battery and ONE identical-content rebuild before
   declaring failure (the JF-770 filing ruled the flip out for the PRE-fix
   model; a freshly deployed model's first build may still flip once).
7. JF-771 legs, informational for that task: `फिल्म इंसेप्शन खोजो` and
   `फिल्म इंसेप्शन चलाओ`: record what they read post-fix (if the फिल्म-carrier
   competition shifted generally, that is evidence FOR the same
   family-strengthening shape on those tails).
8. DURABLE PIN (close-out, after the fixtures surface is free of the
   concurrent worker): add the NLU pin for `फिल्म इंसेप्शन देखो` to
   tests/integration/fixtures/hi-IN.yaml per the JF-551/JF-761/JF-766
   convention (red until the rebuilt model deploys), pinning the RAW slot
   value ('इंसेप्शन') per the JF-769 raw-vs-canonical lesson. The deferral is
   the dispatch boundary itself (the orchestrator's instruction: a concurrent
   worker owns the fixture files in a SEPARATE worktree, invisible to a
   git-status check of this checkout, which is why the code review could not
   verify it), not a judgment that the pin is optional: the one-time matrix
   above cannot outlive this task, the pin is the durable guard, and DoD #5
   does not close without it.

- [x] #1 Hypothesis probed probe-first: the other qualified PlayVideo forms, the single-member tails, and the ja 映画 control, 4 repeats each (10 hi-IN utterances + 3 ja controls; verdict: the failure is फिल्म-carried-short-form-specific apart from the JF-766-accepted bare-देखो NO_SELECTION cost, not single-tail-generic; the ja analog routes clean)
- [x] #2 Remediation option (a) implemented: 3 QUALIFIED देखो variants, never bare (enumerated in CHOSEN SHAPE above); the "titled literal" skipped with the recorded reason; model regenerated (361 → 364, exactly 3 lines); mirrors regenerated via the generator (--check clean)
- [x] #3 Validators at baseline (validate_interaction_models PASS, 294 warnings = the main baseline, no new warning; validate_locales PASS, no new locale gaps)
- [x] #4 Full suite green once on the final state: 5207/5207 net9.0 AND net10.0, 0 failed (the true current baseline; the "~5205" figure predates JF-763's +2 endpoint pins, which merged 4 minutes before the JF-766 merge)
- [ ] #5 Post-deploy matrix (orchestrator): the 8-leg instruction above; this DoD closes when leg 1 reads PlayVideo 4/4 with the leg 3-5 guards green and the leg 8 pin landed

IMPLEMENTED 2026-10-05 by the worktree agent (template/model/mirrors only: no
.cs, no fixtures, no deploy). Hypothesis verdict and probe map: the
Implementation section above. Chosen shape: option (a), the three qualified
variants in CHOSEN SHAPE. JF-771 filed same-turn for the two adjacent
फिल्म-family failures the probe surfaced (खोजो Fallback, चलाओ PlaySong
steal). CLOSURE IS DEPENDENT on the post-deploy matrix and pin (DoD #5).

ORCHESTRATOR MATRIX RESULT (2026-10-05, post DLL deploy 2fddf2be + hi-IN rebuild, skill fresh): PARTIAL, with the फिल्म anomaly now proven TRAINER-SIDE. L1 FAIL: the anchor 'फिल्म इंसेप्शन देखो' reads Fallback 4/4 AFTER the three new samples deployed; with the exact sample 'फिल्म {title} देखो' AND the word-order twin 'फिल्म देखो {title}' AND four other फिल्म-family samples (चलाओ, खोजो, मैं फिल्म...) present in the live model. L1b PASS: the NEW 'वीडियो इंसेप्शन देखो' routes PlayVideoIntent clean on first reading; the strengthened family DOES route, via the new carriers. L3/L5 PASS (the JF-766 steal fix holds on both episode forms), L4 PASS (the bare-movie guard). ENCODING RULED OUT: the probe's फिल्म codepoints byte-equal the live model's (0x92b 0x93f 0x932 0x94d 0x92e...). The discriminator chain now reads: not encoding, not family strength (वीडियो routes with one sample), not the JF-766 removal (pre-removal the form routed with the same samples minus three), persistent across TWO rebuilds; a trainer-state anomaly specific to the फिल्म token sequence that template changes demonstrably cannot reach (the first remediation attempt failed against it; per the two-failed-fixes rule no second template patch). DISPOSITION: the task stays OPEN (In Progress) pending either a trainer-side recovery (a future rebuild after unrelated content changes may unstick it; re-probe then) or Paolo's device round; the WORKING user-facing carriers are वीडियो/मूवी (live-proven). The durable NLU pin (DoD #5) authors on the वीडियो row + the steal-fix rows (the surface from today's probes per the JF-769 rule), NOT on the फिल्म row while the anomaly stands. JF-771's cross-locale sweep note gains this anomaly as a fourth data point.

THIRD-REBUILD DATA POINT (2026-10-05, orchestrator): rebuild with the post-JF-755/JF-537.1 DLL (6e4fa345) - still Fallback 3/3. The anomaly now persists across THREE rebuilds and TWO DLL generations. Task stays parked on the device round / a future unrelated-content rebuild; the वीडियो/मूवी carriers remain the working path.

FOURTH-REBUILD DATA POINT (2026-10-10 02:05, orchestrator): the night batch deploy (0f0a3c7d build, md5 0e7e91e2) rode a full 17/17 locale model rebuild (all SUCCEEDED); the anchor 'film inceptio dekho' re-probed 4x on the fresh hi-IN model: AMAZON.FallbackIntent 4/4. The anomaly now persists across FOUR rebuilds and THREE DLL generations. Task stays parked exactly per its disposition (trainer-side, template changes cannot reach it; the working user-facing carriers remain video/moovie, live-proven); the remaining paths are Paolo's device round or a trainer-side recovery on some future rebuild.
FIFTH-REBUILD DATA POINT (2026-10-10 13:05, orchestrator): the JF-771 batch deploy rode a hi-IN rebuild (SUCCEEDED) whose model carries SIX MORE PlayVideo samples (the JF-771 family strengthening, real content change); the film anchors re-probed in its matrix: film+khojo Fallback 4/4, film+chalao PlaySong steal 4/4. The anomaly now persists across FIVE rebuilds (one with substantial unrelated PlayVideo content growth), THREE+1 DLL generations. All the NEW video/movie-carried samples route 4/4 on the same trainer - the anomaly is film-token-sequence-specific, reconfirmed.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-05 by the orchestrator from JF-766's post-deploy probe matrix (its
DoD #3 regression guard FAILURE, the file-not-weaken discipline).

EVIDENCE (all live profile-nlu, hi-IN, 4x repeats per step, selectedIntent-first,
skill discovered fresh):

1. PRE-REMOVAL (the JF-766 worker's probe on the old model):
   `फिल्म इंसेप्शन देखो` → PlayVideoIntent, title='इंसेप्शन', 4/4. CLEAN.
2. POST-REMOVAL model 1 (after the JF-766 DLL deploy + hi-IN rebuild):
   `फिल्म इंसेप्शन देखो` → AMAZON.FallbackIntent, 5/5 readings (1 in the matrix
   + 4 in the stability re-probe). REGRESSED.
3. The exact sample `फिल्म {title} देखो` IS in the live model's PlayVideoIntent
   (verified via get-interaction-model after the rebuild; the only देखो sample
   remaining, 11 total samples). The model content is CORRECT.
4. IDENTICAL-CONTENT REBUILD (the nlu_trainer_nondeterminism discrimination
   step): rebuilt hi-IN again, SUCCEEDED; re-probed 4x: Fallback 4/4. The
   classic trainer flip RECOVERS on identical-content rebuild; this does not.

The JF-766 steal fix itself is verified working (the lead/library/sibling probes
all route PlayEpisodeIntent clean), so this is NOT a revert case: it is a RECALL
LOSS on the qualified video-watch form, and per the matrix's own guard language
a new finding to file, not a fixture to weaken. Both video-watch forms are now
unreachable in hi-IN (bare इंसेप्शन देखो reads NO_SELECTION, the qualified form
reads Fallback), a larger JF-459-class recall cost than the filing anticipated.

HYPOTHESIS (for whoever picks it up): removing the bare sample removed the
trainer's only other देखो-tailed PlayVideo example, and the remaining single
qualified sample is not enough signal for the intent to win Fallback
competition. Candidate remediations to weigh: (a) strengthen the qualified
family (add 2-3 more qualified देखो variants: मूवी {title} देखो, फिल्म देखो
{title} word-order twin, a titled literal example), (b) re-add the bare sample
with a NARROWER slot type than SearchQuery if the model layer allows, (c) accept
the recall cost and document the workaround (the शुरू करो sibling still routes
video). Option (a) is the JF-459-discipline shape. Probe-first: verify the
family-strength hypothesis by probing the OTHER qualified PlayVideo forms
(फिल्म {title} चलाओ etc.) before choosing.

VERIFICATION BAR: the chosen shape deploys, the फिल्म anchor routes PlayVideo
4/4, the JF-766 steal fix stays closed (the episode forms keep routing
PlayEpisode), and the bare-movie guard still passes (no PlayEpisode steal).

## Implementation (2026-10-05, worktree agent)

HYPOTHESIS PROBE VERDICT (live profile-nlu 2026-10-05, skill
`amzn1.ask.skill.33dfacd5-3676-4cdc-8b02-81efb227df83` discovered fresh via
list-skills-for-vendor; the live hi-IN model re-fetched and verified
sample-for-sample equal to the committed post-JF-766 model BEFORE probing; 10
hi-IN utterances plus 3 ja cross-locale controls, 4 repeats each,
selectedIntent-first parsing):

The family-strength hypothesis is CONFIRMED IN ITS GENERAL FORM and REFINED:
single-member tails are NOT universally weak and the देखो subfamily is not
uniquely sick. The failure localizes to the फिल्म-carried SHORT form (the one
exception outside that class: the bare `इंसेप्शन देखो` shape itself reads
NO_SELECTION, the JF-766-accepted recall cost recorded in the filing; every
OTHER non-फिल्म short form probed routes clean). Map:

ROUTES PlayVideoIntent clean 4/4 (title='इंसेप्शन'):
- `वीडियो इंसेप्शन चलाओ` and `वीडियो इंसेप्शन शुरू करो` (multi-member tails)
- `वीडियो इंसेप्शन लगाओ` (SINGLE-member लगाओ tail: the strong carrier routes it)
- `चलो इंसेप्शन देखते हैं` and `क्या तुम इंसेप्शन चला सकते हो` (SINGLE-member
  tails: the long preambles route them)
- `इंसेप्शन शुरू करो` (the kept bare शुरू करो sibling)
- `मैं फिल्म इंसेप्शन देखना चाहता हूँ` (the long मैं preamble rescues the फिल्म
  carrier; nuance: it matched the BARE `मैं {title} देखना चाहता हूँ` sample with
  title='फिल्म इंसेप्शन' whole-absorbed, so the intent is right but the slot
  value is suboptimal)

FAILS (all three फिल्म-led short forms, 4/4 each):
- `फिल्म इंसेप्शन देखो` → AMAZON.FallbackIntent (the JF-770 anchor, re-confirmed)
- `फिल्म इंसेप्शन खोजो` → AMAZON.FallbackIntent (the OTHER single-member
  qualified tail; NEW finding, filed JF-771)
- `फिल्म इंसेप्शन चलाओ` → PlaySongIntent steal, song='फिल्म इंसेप्शन' (the bare
  slot-initial `{song} चलाओ` absorbs it; NEW finding, filed JF-771)

ja CROSS-LOCALE CONTROL (the 映画 family, live post-JF-761 model):
`映画インセプションを見たい` (the EXACT structural analog: single qualified member
of the watch-verb tail under the movie-carrier noun) routes PlayVideoIntent
clean 4/4, as do を再生して and を見せて. The failure class is hi-IN-specific,
not an inherent single-sample weakness.

INTERPRETATION: pre-removal, the bare `{title} देखो` carried the [X][देखो]
pattern and routed the फिल्म-carried anchor (the JF-766 worker's 4/4 datum);
its removal left `फिल्म {title} देखो` as the देखो tail's only member, and the
weak loanword carrier फिल्म on a 3-token form cannot win Fallback competition
alone, while वीडियो and the long preambles can even single-member. The खोजो
and चलाओ failures have NO pre-removal datum (JF-766 never probed them), so
the फिल्म-carrier weakness may predate the removal for those tails; only
देखो has the pre-removal routing proof of regression.

CHOSEN SHAPE (the filing's option (a), evidence-refined): three more QUALIFIED
देखो variants added to PlayVideoIntent in templates/hi-IN.yaml (inline JF-770
comment carries the probe map):
- `वीडियो {title} देखो` (the carrier PROVEN to route short forms)
- `मूवी {title} देखो` (the filing's fresh-carrier suggestion)
- `फिल्म देखो {title}` (word-order twin, the filing's suggestion)
never bare (JF-459/JF-766 discipline). SKIPPED from the filing's list: the
"titled literal example" (a literal title inside a slot position is not
expressible in an interaction-model sample, and a slotless static sample on
the slot-REQUIRED PlayVideoIntent is anti-pattern #1; the JF-403
optional-slot boundary does not apply). The देखो family goes 1 → 4 members
(3 tail-final + 1 verb-medial twin).

PREDICTION (falsifiable, checked by the post-deploy matrix): the
[NOUN][X][देखो] pattern weight restored, `फिल्म इंसेप्शन देखो` routes
PlayVideoIntent 4/4.

Changed: templates/hi-IN.yaml (+3 samples + rationale comment),
model_hi-IN.json regenerated (361 → 364 samples, exactly 3 lines),
docs/VOICE_COMMANDS_BY_LOCALE.md via the generator (--check clean; 361 → 364
phrases; VOICE_COMMANDS.md unchanged, its summary covers it-IT/en-US).
docs/playback-lifecycle-hi-IN.md untouched (its two PlayVideo edges are
चलाओ-tailed, no देखो edge exists), parse_mermaid.py re-run as the no-op
check (no graphs.json/docs-site change). tests/integration/fixtures
deliberately NOT touched (a concurrent worker owns that surface; the
post-deploy matrix below replaces the pin for this task).

### LIVE-PROBE INSTRUCTION FOR THE ORCHESTRATOR (after the hi-IN rebuild rides the next wave DLL deploy)

Run each utterance via `ask smapi profile-nlu --locale hi-IN`, skill id
discovered fresh, 4 repeats, selectedIntent-first.

1. `फिल्म इंसेप्शन देखो` → EXPECT PlayVideoIntent, title='इंसेप्शन', 4/4. THE
   task bar (pre-fix: Fallback 4/4).
2. New variants: `वीडियो इंसेप्शन देखो`, `मूवी इंसेप्शन देखो`,
   `फिल्म देखो इंसेप्शन` → EXPECT PlayVideoIntent 4/4 each.
3. JF-766 steal fix must STAY CLOSED: `game of thrones सीज़न 1 एपिसोड 3 देखो`
   and `breaking bad सीज़न 1 एपिसोड 3 देखो` → EXPECT PlayEpisodeIntent 4/4
   each. If PlayVideoIntent absorbs the address again, the strengthened देखो
   family re-opened the steal: FILE it, do not tune blindly.
4. Bare-movie guard (JF-766 matrix leg 4): `इंसेप्शन देखो` → EXPECT
   PlayVideoIntent or NO_SELECTION/Fallback. FORBIDDEN: PlayEpisodeIntent.
5. Sibling stability: `इंसेप्शन शुरू करो`, `वीडियो इंसेप्शन चलाओ` → EXPECT
   PlayVideoIntent 4/4.
6. Escape hatch (nlu_trainer_nondeterminism): if leg 1 still reads Fallback,
   run the 6-10 probe battery and ONE identical-content rebuild before
   declaring failure (the JF-770 filing ruled the flip out for the PRE-fix
   model; a freshly deployed model's first build may still flip once).
7. JF-771 legs, informational for that task: `फिल्म इंसेप्शन खोजो` and
   `फिल्म इंसेप्शन चलाओ`: record what they read post-fix (if the फिल्म-carrier
   competition shifted generally, that is evidence FOR the same
   family-strengthening shape on those tails).
8. DURABLE PIN (close-out, after the fixtures surface is free of the
   concurrent worker): add the NLU pin for `फिल्म इंसेप्शन देखो` to
   tests/integration/fixtures/hi-IN.yaml per the JF-551/JF-761/JF-766
   convention (red until the rebuilt model deploys), pinning the RAW slot
   value ('इंसेप्शन') per the JF-769 raw-vs-canonical lesson. The deferral is
   the dispatch boundary itself (the orchestrator's instruction: a concurrent
   worker owns the fixture files in a SEPARATE worktree, invisible to a
   git-status check of this checkout, which is why the code review could not
   verify it), not a judgment that the pin is optional: the one-time matrix
   above cannot outlive this task, the pin is the durable guard, and DoD #5
   does not close without it.

- [x] #1 Hypothesis probed probe-first: the other qualified PlayVideo forms, the single-member tails, and the ja 映画 control, 4 repeats each (10 hi-IN utterances + 3 ja controls; verdict: the failure is फिल्म-carried-short-form-specific apart from the JF-766-accepted bare-देखो NO_SELECTION cost, not single-tail-generic; the ja analog routes clean)
- [x] #2 Remediation option (a) implemented: 3 QUALIFIED देखो variants, never bare (enumerated in CHOSEN SHAPE above); the "titled literal" skipped with the recorded reason; model regenerated (361 → 364, exactly 3 lines); mirrors regenerated via the generator (--check clean)
- [x] #3 Validators at baseline (validate_interaction_models PASS, 294 warnings = the main baseline, no new warning; validate_locales PASS, no new locale gaps)
- [x] #4 Full suite green once on the final state: 5207/5207 net9.0 AND net10.0, 0 failed (the true current baseline; the "~5205" figure predates JF-763's +2 endpoint pins, which merged 4 minutes before the JF-766 merge)
- [ ] #5 Post-deploy matrix (orchestrator): the 8-leg instruction above; this DoD closes when leg 1 reads PlayVideo 4/4 with the leg 3-5 guards green and the leg 8 pin landed

IMPLEMENTED 2026-10-05 by the worktree agent (template/model/mirrors only: no
.cs, no fixtures, no deploy). Hypothesis verdict and probe map: the
Implementation section above. Chosen shape: option (a), the three qualified
variants in CHOSEN SHAPE. JF-771 filed same-turn for the two adjacent
फिल्म-family failures the probe surfaced (खोजो Fallback, चलाओ PlaySong
steal). CLOSURE IS DEPENDENT on the post-deploy matrix and pin (DoD #5).

ORCHESTRATOR MATRIX RESULT (2026-10-05, post DLL deploy 2fddf2be + hi-IN rebuild, skill fresh): PARTIAL, with the फिल्म anomaly now proven TRAINER-SIDE. L1 FAIL: the anchor 'फिल्म इंसेप्शन देखो' reads Fallback 4/4 AFTER the three new samples deployed; with the exact sample 'फिल्म {title} देखो' AND the word-order twin 'फिल्म देखो {title}' AND four other फिल्म-family samples (चलाओ, खोजो, मैं फिल्म...) present in the live model. L1b PASS: the NEW 'वीडियो इंसेप्शन देखो' routes PlayVideoIntent clean on first reading; the strengthened family DOES route, via the new carriers. L3/L5 PASS (the JF-766 steal fix holds on both episode forms), L4 PASS (the bare-movie guard). ENCODING RULED OUT: the probe's फिल्म codepoints byte-equal the live model's (0x92b 0x93f 0x932 0x94d 0x92e...). The discriminator chain now reads: not encoding, not family strength (वीडियो routes with one sample), not the JF-766 removal (pre-removal the form routed with the same samples minus three), persistent across TWO rebuilds; a trainer-state anomaly specific to the फिल्म token sequence that template changes demonstrably cannot reach (the first remediation attempt failed against it; per the two-failed-fixes rule no second template patch). DISPOSITION: the task stays OPEN (In Progress) pending either a trainer-side recovery (a future rebuild after unrelated content changes may unstick it; re-probe then) or Paolo's device round; the WORKING user-facing carriers are वीडियो/मूवी (live-proven). The durable NLU pin (DoD #5) authors on the वीडियो row + the steal-fix rows (the surface from today's probes per the JF-769 rule), NOT on the फिल्म row while the anomaly stands. JF-771's cross-locale sweep note gains this anomaly as a fourth data point.

THIRD-REBUILD DATA POINT (2026-10-05, orchestrator): rebuild with the post-JF-755/JF-537.1 DLL (6e4fa345) - still Fallback 3/3. The anomaly now persists across THREE rebuilds and TWO DLL generations. Task stays parked on the device round / a future unrelated-content rebuild; the वीडियो/मूवी carriers remain the working path.
<!-- SECTION:NOTES:END -->
<!-- SECTION:FINAL_SUMMARY:END -->
