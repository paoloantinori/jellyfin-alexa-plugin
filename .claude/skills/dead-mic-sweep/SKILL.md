---
name: dead-mic-sweep
description: Find and fix dead-mic question sites (a response that asks the user a question with shouldEndSession=true, so the mic never opens for the answer). Triggers: "dead mic sweep", "find dead-mic questions", "questions that don't listen", "sweep the Tell questions", or after adding any new DidNotCatch*/Elicit*/prompt string. Runs the static detector, then drives the JF-549 fix pattern per site.
---

# Dead-Mic Question Sweep

A question spoken via `ResponseBuilder.Tell` ships with `shouldEndSession=true`: Alexa
asks, the mic stays closed, the user's answer goes nowhere. Live incident 2026-09-12
17:45 (JF-549): PlayEpisode asked "Quale serie vorresti guardare?" three times, every
time to a closed mic.

## Step 1: Detect

```bash
python3 scripts/validate_question_responses.py            # advisory inventory
python3 scripts/validate_question_responses.py --strict   # exit 1 on any live site
```

The detector crosses the locale JSONs (any sentence of the string ending in "?" marks
the key question-shaped) against every `ResponseBuilder.Tell` site whose speech is
that key (inline `ResponseStrings.Get` or a local variable assigned from it). Retry
instructions ("Per favore riprova.") are not questions and never match.

A site is legitimately Tell-shaped when the string does not expect an answer (e.g. a
statement followed by an instruction). Suppress with an inline comment on the Tell
line: `// dead-mic-ok: <reason>`; the reason is echoed in the report.

## Step 2: Fix each site (the JF-549 pattern)

1. Replace the Tell with the shared elicitation helper:
   `BuildDialogElicitResponse(promptKey, locale, slotToElicit, intentName, allSlotNames)`
   (BaseHandler; open session + reprompt + `Dialog.ElicitSlot` with a COMPLETE
   updatedIntent - Amazon rejects partial slot lists, live INVALID_RESPONSE
   2026-08-28).
2. Register the intent in `dialog.intents` (with `elicitationRequired: false` on the
   slots) in ALL 17 locale templates, or Alexa SILENTLY drops the directive (repo
   anti-pattern #9). Regenerate the models.
3. Add the cancel-word hatch for the open elicit: while an elicit is open, Alexa
   captures the next utterance INTO the slot, so a bare "stop"/"ferma" arrives as a
   slot value with dialogState IN_PROGRESS (`CancelWords.IsDialogInProgress` +
   `CancelWords.AnySlotIsCancelWord`); that must cancel, not search. Hoist the shared
   leg into a BaseHandler helper when converting a batch (JF-550).
4. Unit-test the shape: open session (`TestHelpers.AssertSessionOpen`), the
   `Dialog.ElicitSlot` directive targeting the right slot, reprompt present, and the
   cancel hatch ending the session with no library work (mirror
   `PlayEpisodeIntentHandlerTests`, JF-549).
5. E2E pin (optional but preferred for the first site of each intent): an
   `expected_response_type: directive` / `expected_directive_type: "Dialog.ElicitSlot"`
   / `expected_end_session: false` / `expected_reprompt: true` entry in
   `tests/integration/fixtures/e2e_<locale>.yaml`.

Exemplar commit: the JF-549 fix in `PlayEpisodeIntentHandler` (78d2eb47). Sweep
driver task: JF-550 (the site list there was the detector's first output).

## Step 3: Verify the sweep moved the needle

Re-run the detector; the count must drop to zero live sites (suppressions must each
carry a reason). Do not mark the sweep done while unsuppressed sites remain.
