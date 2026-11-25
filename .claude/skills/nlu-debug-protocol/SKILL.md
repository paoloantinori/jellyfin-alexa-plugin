---
name: nlu-debug-protocol
description: The order of operations for debugging or changing voice-model routing. Use before filing or approving any task that adds, removes or moves interaction-model samples, before writing a research claim about what the models contain, before any ad hoc profile-nlu probe, when a red NLU row is about to be diagnosed, and before a lab ablation. Complements nlu-verify (the pre-deploy wrapper battery) and docs/nlu-static-analysis.md (the tools); this skill is the discipline around them.
---

# NLU debug protocol

The tools already exist: the static toolkit (`scripts/nlu_static/cli.py`), the lab harness (`scripts/nlu-lab/cli.py`), the results ledger and the triage report. The failures of 2026-11 were not tool failures. They were process failures of four kinds, each seen more than once: a task or report asserted what the models contain without checking them; a red row was diagnosed while the live state (catalogs, deployed DLL, propagation) was not what everyone assumed; a probe was run as a one-off shell command and left no comparable record; a fix was deployed live to test a hypothesis that nobody had written down first. The five gates below close those four holes. Run them in order.

## Gate 1: the model-content premise is tool output, never recall

Any sentence of the form "locale X has / lacks carrier Y", "intent Z owns this order", "slot S is type T" must be backed by a command run in the same sitting, and the task, report or consult must quote the output. Four premises of this shape were wrong on 2026-11-25 alone (SongTitle "static everywhere", "no `music by {musician}` row", "we hold neither artist order", "en carries by-order on FindSong and de/fr/es lack it"), and each one would have produced work on rows that already existed.

For a real utterance: `python3 scripts/nlu_static/cli.py match --locale <L> "<utterance>"`. For a candidate SAMPLE this is not enough: `match` only shows a catalog-slotted sample when the spoken value is in the vocabulary, so `match --locale en-US "play some jazz"` does not list `play some {musician}` on PlayArtistSongsIntent, although a bare `play some {genre}` would be the identical carrier. Until the sample-level lookup of JF-970 exists, check a candidate sample by normalizing every sample of the locale (lowercase, every `{slot}` replaced by one placeholder) and comparing carriers, and report the owner intent and its slot type for every hit.

A candidate list in a task therefore carries one line per (candidate, locale): ABSENT, PRESENT on the same intent, or SAME CARRIER on another intent (with that intent's slot type). Only ABSENT rows are work. SAME CARRIER rows are a refusal unless the maintainer decides otherwise.

## Gate 2: establish the live state before reading any live result

A live result means nothing until four facts are known and written next to it: the deployed DLL is the one you think (the loaded-assembly log line, not the file you pushed); template HEAD is not newer than the deployed models (the staleness gate); no rebuild is in flight; and the catalogs are alive AND propagated for the locale under test. The last one has no automatic signal yet (JF-971). Until it does, probe one known library value per wired catalog type before the session and record whether its slot came back with `ER_SUCCESS_MATCH`. A session where a canary fails is a catalog-state session: its reds are not model evidence and its flips are not trainer noise. The canary is asymmetric. A failed canary proves the catalog is not usable; a passed canary proves only that the catalog is alive and propagated for THAT value. Propagation is per value: on 2026-11-25 "coldplay" resolved at ~17:12 while "pink floyd" and "radiohead" still failed at 17:16 (monitor output file mtime), and all values resolved by ~17:18; times verified from the artifacts, not recalled. So a red row on a catalog-backed slot is model evidence only after its own value has been seen resolving in the same state, and during a known propagation window the values under test are probed directly instead of trusting the canary.

Do not compare two runs taken in different catalog states. A baseline taken during propagation and a post-change run taken after it measure the catalog, not the change.

## Gate 3: every probe is a recorded run

No bare `ask smapi profile-nlu` one-liners for anything you will reason about. Put the utterances in a workingset YAML (fixture format; `expected_intent` optional for observation rows) and run them through the lab harness, which keeps the raw response, the selected intent, the slots and the considered intents in a run file that `compare` can diff:

```bash
python3 scripts/nlu-lab/cli.py run --live --locale <L> --template --workingset <ws.yaml> --label <what-and-why>
python3 scripts/nlu-lab/cli.py compare <RUN_A> <RUN_B>
```

`--live` is read-only against the real skill (catalog wiring active); without it the run deploys to the dedicated lab skill. A one-off shell probe is acceptable only to answer "what shape is this field", never to support a routing claim.

## Gate 4: measure the noise floor before attributing a difference

Before any before/after comparison, run the SAME battery twice on the SAME deployed model and require `compare --expect-zero-changes` to pass (`--skip-deploy` on the lab skill; two `--live` runs on the real one). Rows that differ in the A/A pair are excluded from the verdict and listed as unstable. An effect counts only if it appears on rows that were stable in the A/A pair. For a row with ledger history, `ledger stats` gives the same answer for free: a row with proven same-model flips is not evidence in either direction.

## Gate 5: an experiment is written down before it runs

A lab round or ablation starts with a card, pasted into the task notes before the first deploy:

- The question, as two or more rival hypotheses that predict DIFFERENT outcomes on at least one row. If every hypothesis predicts the same table, the experiment cannot discriminate and must be redesigned.
- The arms, each differing from the control in ONE variable, and the predicted outcome of every arm under every hypothesis.
- The battery, with sentinel rows for the intents that share a carrier with the change (take them from `graph diff-git` and `select`).
- The control arm run FIRST and LAST. If the two control runs differ, the session drifted and the middle arms are void.
- The decision each outcome leads to, including the outcome "no hypothesis survives".

Results are filled into the same card: predicted versus observed per arm, and a surprise (including an unexpected pass) is investigated before the next arm. The JF-961 ablation is the model to copy: it refuted the by-elimination attribution only because arm 6 and the pair arms were added.

Where the experiment runs depends on what the hypothesis is about. A hypothesis about SAMPLE CONTENT (carriers, word order, slot types, vocabulary) is tested on the lab skill, never by deploying a candidate fix to the live skill: the live skill receives a sample change only after the lab round predicted its effect, and the live run is then a confirmation with a known expected table. A hypothesis that depends on CATALOG STATE (wiring conjunctions, propagation, which types are wired) cannot be reproduced on the lab skill, because the wired catalogs belong to the real skill; it runs as recorded `--live` runs on the real skill, under the same card, the same A/A check and the same control-first-and-last rule, and every wiring state changed for an arm is restored and verified at the end. The JF-961 ablation was of this second kind. Two failed fixes of the same kind end the patching: revert to the last known-good model, write down what is ruled out, and go back to the card.

## Cheapest discriminating test first

In this order, and stop at the first one that answers the question: static toolkit (seconds, no network); ledger history; one recorded `--live` run; lab A/B; full suite; device. A device round is for ASR and skill arbitration questions only, because nothing else can answer them and nothing it shows can be attributed to the model without a profile-nlu control.

## What a finding must carry

A routing finding handed to the maintainer, another agent or a task states: the run ids it rests on, the live state of Gate 2 at the time, whether the rows passed the A/A check, and which parts are observed and which are inferred. "The graph shows an edge" is an inference about cause; "run B differs from run A on rows stable in A/A" is an observation.
