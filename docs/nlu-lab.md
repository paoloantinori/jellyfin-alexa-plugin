# NLU Lab: test your locale's voice model before it breaks

The NLU Lab is a command-line tool for iterating on Alexa interaction models safely. It deploys candidate models to a dedicated lab skill (never your live skill), probes utterance routing against them, and shows exactly what changed between two iterations as a before/after matrix.

## Why you want this

When a voice command does not reach the right intent, the cause is usually a routing conflict in the interaction model: one intent's vocabulary overlaps another's, or a greedy slot type absorbs phrases meant for a different intent. Finding these conflicts by trial and error on a real device is slow, and every fix risks breaking a different route.

The lab turns this into a three-minute loop:

1. Edit the locale's YAML template
2. Deploy to the lab skill and probe your test utterances
3. Read the before/after matrix: green means a fix worked, red means a regression, amber means a route changed with no expectation to judge against

## What you need

- The `ask` CLI v2, authenticated with your Amazon developer account (`ask configure`)
- Python 3 with `pytest` and `pyyaml`
- This repository cloned

## Setup (one time)

Create the lab skill on your Amazon developer account. This is a separate skill used exclusively for testing; your live Jellyfin skill is never touched by the lab.

```bash
python3 scripts/nlu-lab/cli.py init-skill --endpoint "https://your-jellyfin-server/alexaskill"
```

If your ask CLI does not support skill creation, create a custom skill named `jellyfin nlu lab` in the Alexa Developer Console, then:

```bash
python3 scripts/nlu-lab/cli.py init-skill --pin-id amzn1.ask.skill.YOUR_ID
```

## Testing a locale's routing

Run the full test battery (committed fixtures plus your working set) against the current model:

```bash
python3 scripts/nlu-lab/cli.py run --template --locale de-DE --label my-test \
  --workingset my-test-phrases.yaml
```

The working set is a YAML file with utterances you care about:

```yaml
locale: de-DE
tests:
  - utterance: "spiele das lied mein lieblingslied"
    expected_intent: PlaySongIntent
  - utterance: "was ist das für ein lied"
    expected_intent: MediaInfoIntent
```

Leave `expected_intent` out for observation-only rows (they show as amber when they change, never red).

### Probing without redeploying

If you want to re-probe the SAME deployed model (to verify that routing results are stable), use `--skip-deploy`:

```bash
python3 scripts/nlu-lab/cli.py run --template --locale de-DE --label aa-2 \
  --workingset my-test-phrases.yaml --skip-deploy
```

This skips the deploy and build wait entirely and profiles the already-deployed model. Deploying the same model twice does not trigger a new Amazon-side build (no new eTag is minted), so a redeploy would time out; `--skip-deploy` is the correct instrument for A/A noise checking.

## Comparing two iterations

After editing the template and regenerating:

```bash
python3 scripts/nlu-lab/cli.py run --template --locale de-DE --label after-fix \
  --workingset my-test-phrases.yaml
python3 scripts/nlu-lab/cli.py compare my-test after-fix
```

The compare output shows one line per changed route:

```
red      spiele das lied mein lieblingslied    (workingset)
green    was ist das für ein lied             (workingset)
4 changed of 25 rows
```

Green: the route changed and now satisfies its expectation. Red: the route changed and now violates it. Amber: the route changed with no expectation. Error: the probe itself failed (transient Amazon error, not a routing change).

### The A/A gate

Before trusting any before/after comparison, verify that probing the SAME model twice produces identical results:

```bash
python3 scripts/nlu-lab/cli.py run --template --locale de-DE --label aa-1 \
  --workingset my-test-phrases.yaml -k my-substring
python3 scripts/nlu-lab/cli.py run --template --locale de-DE --label aa-2 \
  --workingset my-test-phrases.yaml -k my-substring --skip-deploy
python3 scripts/nlu-lab/cli.py compare aa-1 aa-2 --expect-zero-changes
```

If this gate fails, the probing itself is noisy and any diff is unreliable.

### What compare checks before showing the matrix

The compare command refuses to show a comparison (and exits with an error under `--expect-zero-changes`) when the two runs:

- Probed different models (the file sha does not match)
- Probed different Amazon builds (the eTag does not match; this catches the case where a `--skip-deploy` run was launched against a different model than the one actually deployed)
- Were run against different skills (the skill ID does not match)

Without `--expect-zero-changes`, these mismatches produce loud warnings but the comparison still renders.

## The web dashboard

```bash
python3 scripts/nlu-lab/cli.py serve --port 8766
```

Open `http://your-workstation:8766` on your phone or desktop. The dashboard shows the run list with failure counts and model/build identifiers, the before/after matrix with green/red/amber/error/absent cells, per-row detail with filled slots and considered intents, and the full intent legend. The interface language switches between English and Italian based on your browser, with a manual override.

When the selected pair of runs probed different models or builds, a warning banner explains that the pair cannot support a before/after reading.

## Exporting a model for the custom import

The plugin supports importing a custom interaction model via URL. To produce a deploy-ready model file with a specific invocation name (the name is required; the plugin deploys it verbatim):

```bash
python3 scripts/nlu-lab/cli.py export --template --locale de-DE --label my-model \
  --invocation-name "my jellyfin"
```

This writes `de-DE-my-model.json` to the current directory, validated and ready to host at a URL for the plugin's custom model import.

## What the lab does NOT do

- It never touches your live Jellyfin skill (deploys go only to the lab skill, enforced by a hard-coded guard that checks the skill ID and the live skill name on every deploy)
- It does not test the actual playback pipeline (use the E2E tests for that)
- Catalog-backed slots (artist names, album names) behave differently on the lab skill because it carries no catalog wiring

## The catalog limitation: what the lab CANNOT reproduce

This is the most important limitation to understand, learned from live verification during the 1.1.1.0 release cycle.

**The lab skill has no catalog wiring.** Your live skill does (artist names, album names, series names, movie titles are synced from your library to Amazon's catalog system). The catalog changes how the NLU resolves competition between intents. Concretely:

- A routing result that is CORRECT on the lab skill may be WRONG on the live skill, and vice versa
- This was observed live: asking to watch a TV series routed correctly on the lab skill but incorrectly on the live skill, because the live skill's artist catalog changed the NLU's confidence in a way the lab could not predict
- For series whose title is also a famous song title (Stranger Things, Game of Thrones), the live skill may route the request to PlaySongIntent instead of PlayEpisodeIntent because the artist catalog makes the music anchor stronger; the lab, without the catalog, has no such bias

**Practical consequence**: always verify a fix against the live skill (via the NLU test suite or on-device testing) before shipping it. The lab is the fast iteration loop; the live skill is the ground truth.

## Known routing limitations (platform-level, not fixable at the model layer)

These behaviors were verified live during the 1.1.1.0 release cycle and are documented here so you do not chase them in the lab:

1. **Non-famous song titles**: for titles Amazon does not know (personal recordings, children's audio plays), the NLU cannot fill the free-text song slot and routes the utterance to another intent (or no intent at all). The long-term fix is catalog-backed song recognition (in development).

2. **Series with song-title names**: asking to watch a series whose title is also a famous recording may route to PlaySongIntent. The series-last phrasing ("schau staffel 2 folge 1 von [series]") works reliably.

3. **German compound carriers**: the sleep timer's compound carrier ("schlaf-timer") only fills the duration slot for the simplest phrases ("eine stunde"); compound durations ("sechzig minuten") fail because the compound word is too weak an NLU boundary. Standard prepositional carriers ("stoppe in", "schalte aus in") work for all durations.

## Technical details

- `scripts/nlu-lab/` is the tool root; `runs/` holds JSON run files (gitignored)
- The deploy guard reads `scripts/nlu-lab/lab.json` and refuses to deploy to any skill that is not the pinned lab skill
- Build-state waiting uses the SMAPI eTag as the per-build identity token, so a stale build result is never trusted (a redeploy that mints no new eTag is correctly detected and does not falsely satisfy the wait)
- Each run file records the model sha, the build eTag, and the skill ID, so the compare command can verify pair integrity without re-probing
- The NLU fixtures live in `tests/integration/fixtures/<locale>.yaml`; the lab loads them plus your working set
- The `-k` flag filters the battery by substring, which is the fast iteration loop (the full de-DE battery is 83 fixture rows plus your working set; a filtered run with 5 rows completes in seconds)
- The lab skill's manifest is created from the plugin's committed manifest template, pruned of the events block and empty icon URIs (Amazon's skill creation API rejects both since late 2026)
