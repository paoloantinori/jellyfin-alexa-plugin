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

Green: the route changed and now satisfies its expectation. Red: the route changed and now violates it. Amber: the route changed with no expectation. An A/A gate (`--expect-zero-changes`) verifies the probing itself is noise-free.

## The web dashboard

```bash
python3 scripts/nlu-lab/cli.py serve --port 8766
```

Open `http://your-workstation:8766` on your phone or desktop. The dashboard shows the run list with failure counts, the before/after matrix with red/amber/error cells, per-row detail with filled slots and considered intents, and the full intent legend. The interface language switches between English and Italian.

## What the lab does NOT do

- It never touches your live Jellyfin skill (deploys go only to the lab skill, enforced by a hard-coded guard)
- It does not test the actual playback pipeline (use the E2E tests for that)
- Catalog-backed slots (artist names, album names) behave differently on the lab skill because it carries no catalog wiring; intent selection is faithful, entity resolution is not

## Known limitations

For song titles unknown to Amazon (not in any catalog), the free-text song slot cannot anchor, and the NLU may route the utterance to a different intent. This is a platform-level behavior; the lab reproduces it faithfully and shows it in the matrix. The long-term fix is catalog-backed song slots, which is on the roadmap.

## Technical details

- `scripts/nlu-lab/` is the tool root; `runs/` holds JSON run files (gitignored)
- The deploy guard reads `scripts/nlu-lab/lab.json` and refuses to deploy to any skill that is not the pinned lab skill
- Build-state waiting uses the SMAPI eTag as the per-build identity token, so a stale build result is never trusted
- The NLU fixtures live in `tests/integration/fixtures/<locale>.yaml`; the lab loads them plus your working set
