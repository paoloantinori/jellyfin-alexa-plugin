---
name: nlu-verify
description: Verifies interaction-model routing changes against the wrapper-phrase battery before any deploy. Use when interaction model templates (templates/*.yaml), generated models (model_*.json), NLU fixtures, or any intent samples/slots change, and before deploying or committing those changes. Fires the live wrapper battery via profile-nlu so one-shot routing gaps (the FallbackIntent class) surface in seconds instead of on the user's device.
---

# NLU Verify

The wrapper-form gap class (a phrase a user naturally says routing to
`AMAZON.FallbackIntent` or the wrong intent) has bitten this project more than
a dozen times: the mood-slot misroute (JF-354/356), bare album carriers
(PR #15, JF-459), static samples without slots (JF-403), and four wrapper gaps
in two days (2026-09-21/22: «aggiungere...alla coda», «attivare ripetizione»
routing loop-OFF, «impostare timer un minuto», «di aggiungere alla playlist»).
Model-side prose rules do not catch these; only probing the SAVED model does.

## When this runs

- Any edit to `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/templates/*.yaml`
  or `model_*.json` (the PostToolUse hook `model-change-guard` reminds you).
- Before committing or deploying interaction-model changes.
- Before handing device-test instructions to Paolo (verify every phrase first).

## Steps

1. **Regenerate the model from the template** (never hand-edit the JSON):
   ```bash
   python3 scripts/generate_interaction_model.py <locale>
   ```

2. **Structural validation** (must PASS):
   ```bash
   python3 scripts/validate_interaction_models.py
   python3 scripts/validate_locales.py
   ```

3. **Deploy the model to SMAPI** (profile-nlu tests the SAVED model; a local
   regen alone tests nothing). The rebuild endpoint redeploys and polls:
   ```bash
   # via the plugin endpoint (locale-scoped; "*" rebuilds all):
   ssh $SSH_OPTS pantinor@minix "curl -s -X POST 'http://localhost:8096/alexaskill/api/custom-model/rebuild' \
     -H 'Authorization: MediaBrowser Token=\"$JELLYFIN_API_KEY\"' -H 'Content-Type: application/json' \
     -d '{\"userId\":\"<resolve>\",\"locale\":\"it-IT\"}'"
   ```
   On Jellyfin 12.x use `?ApiKey=` or the MediaBrowser Token header, never
   `X-Emby-Token` (rejected). NEVER cache the skill id: discover it with
   `ask smapi list-skills-for-vendor` every time.

4. **Run the wrapper battery** (the point of this skill):
   ```bash
   python3 scripts/nlu_wrapper_battery.py -l it-IT
   ```
   Every phrase must PASS. A FAIL means the phrase routes to Fallback or a
   wrong intent: fix the locale TEMPLATE (add the infinitive/wrapper twin),
   regenerate, redeploy, re-run. Do NOT hand-edit model JSON and do NOT weaken
   the battery expectation to make it pass.

   **Simulate-outage fallback** (measured 2026-11-26): the battery has a
   simulate half that goes down environment-wide; when it does, verify the
   one-shot forms with recorded profile-nlu probes instead, and read them by
   the domain rules in nlu-debug-protocol Gate 3 (Fable's refutation round,
   runs `fable-headed-fixture-rows-*` and `fable-lab-headed-control-*`):
   profile-nlu has NO invocation layer, so the BARE TAIL (the
   infinitive/subjunctive row itself, no invocation head) is the
   profile-nlu-verifiable form: it routes or the trainer never picked the
   row up. A HEADED phrase is outside the instrument's domain: whether the
   trainer generalizes over the head tokens is decided row by row (the fr
   twins split on carrier alone; headed skip rows routed even on the lab
   skill with a foreign invocation name), so a headed Fallback is not
   evidence of a model gap and a headed pass is not evidence the one-shot
   works. Never re-add rows or reword a twin because a headed probe read
   Fallback; confirm headed forms with simulate-skill plus a profile-nlu
   control, and finally on a real device. Fixtures pin tails, never headed
   forms.

5. **New phrases**: when a new wrapper gap is found, ADD it to
   `BATTERY` in `scripts/nlu_wrapper_battery.py` AND as a fixture row in
   `tests/integration/fixtures/<locale>.yaml` in the same change, so the live
   battery and the NLU suite both pin it.

6. **Mirrors**: sample changes regenerate `VOICE_COMMANDS.md` (emitted by
   `scripts/generate_voice_reference.py`, never hand-edit) and may touch
   `docs/playback-lifecycle-<locale>.md` edges (run
   `python3 docs-site/parse_mermaid.py`; a no-op regen is the health check).

7. **Twin rule for new samples** (lesson of 2026-11-26): when a template edit
   adds samples to an intent in a locale with a wrapper family (fr
   `De + infinitive`, es `que + subjunctive`, en/de `to`/`zu + infinitive`),
   run the carriers check on the wrapper-twin form in the SAME sitting and
   land the twin in the same change as the base row. Shipping the imperative
   row alone leaves the one-shot composition dead or stolen (one fr form was
   stolen by AddToQueueIntent). A twin that loses arbitration to another
   intent (the en record twin lost to PlayByGenre `listen to {genre}`, stable
   2/2) is DROPPED, not shipped: a misrouting one-shot is worse than dead
   air; document the residual in the task notes.

## Rules

- The battery phrases are REAL expected utterances, not aspirations: a FAIL is
  a bug, not a fixture to relax.
- The it-IT battery is the live household; extend other locales by adding to
  `BATTERY` (es/ja/pt/nl/hi/ar wrapper families are the JF-551 backlog).
- Trainer nondeterminism: before calling a PASS a fluke or a FAIL a regression,
  re-run the single phrase once; if it flips, consult the NLU trainer
  nondeterminism memory (identical-content rebuilds can flip slot filling).
