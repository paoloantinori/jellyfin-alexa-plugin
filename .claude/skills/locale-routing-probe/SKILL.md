---
name: locale-routing-probe
description: Probe how an utterance routes per locale on the live skill (profile-nlu for model matching, simulate-skill for full pipeline incl. one-shot wrapper forms). Use when adding/changed utterances or slot types in any locale, when investigating "X works in locale A but not B", before deploying interaction-model changes to new locales, or when asked "does the one-shot form work in <locale>?".
---

# Locale Routing Probe

Answers "how does this utterance route in locale L?" on the live skill, with the
two SMAPI tools and their KNOWN DIVERGENCES:

- **profile-nlu**: model matching only (no invocation, no wrapper). Deterministic
  and rate-cheap. The truth for "would the NLU select X given this payload".
- **simulate-skill**: full pipeline (invocation resolution + NLU + skill call).
  Needed for one-shot wrapper questions, but UNRELIABLE: results flake with
  rapid repeated calls (SMAPI forbids concurrent simulations per user; a few
  probes/minute max, cooldown on contradictions), and an English invocation
  name ("jellyfin player") is marginally recognized by non-English ASR, so
  one-shot failures in non-English locales may be invocation-layer, not
  model-layer. ALWAYS pair a simulate failure with a profile-nlu control before
  concluding the model is broken.

## Probe protocol

1. **profile-nlu the bare natural form** (imperative or infinitive as the
   wrapper would present it). `selectedIntent` is the top-level field of the
   response (NOT result.intent - that field yields false all-NO_SELECTION
   readings, see the JF-549-era incident note in CancelWords.cs).
2. **simulate the full one-shot** with the locale's wrapper (see the
   composition table below; verify the composition is natural for the locale,
   not just grammatical).
3. **Discriminate**: simulate-fail + profile-nlu-match = invocation-layer or
   simulator flake. simulate-fail + profile-nlu-no-match/misroute = model-side
   (competition or missing samples). simulate-ok = fine.
4. Re-probe contradictions once after a 2-3 minute cooldown before recording.

## Wrapper compositions (verified live 2026-09-13, JF-551)

Verified FUNCTIONING one-shot: it-IT "chiedi a {inv} di {payload}", en-* "ask
{inv} to {payload}", de-DE "sag {inv} {payload}" (1 of 4 attempts; otherwise
unreliable - English invocation name under German ASR), nl-NL/ja-JP
invocations reach the skill but payload routing under the wrapper is
unverified-stable. NO working composition found 2026-09-13 for es-*, pt-BR,
hi-IN, ar-SA (favorites-payload controls also failed: the invocation layer
itself, not the payload). Two-step alternative for in-session testing (opens
reliably): the e2e smoke convention "abre/öffne/abri {inv}" + bare in-session
command. The SmapiClient.INVOCATION_PREFIX table records the per-locale
prefixes and their status.

## Interpretation

- Verbatim model sample failing profile-nlu = live model is not what you think
  (check `ask smapi get-interaction-model` for the live state; after JF-552 a
  rebuild grafts catalog wiring which changes NLU competition).
- Catalog-backed slot values (SeriesName, JellyfinArtist, AlbumName) fill only
  when the spoken text matches a catalog value or the model's static seed; a
  slot resolving to the wrong multi-word value (e.g. swallowing a carrier verb)
  is the catalog greed shape - note it per locale, do not "fix" by removing
  catalog wiring.
- Number words ("uno"/"ein") vs digits depend on the slot type per locale
  (ItalianNumber custom type in it-IT; AMAZON.NUMBER elsewhere).

## Artifacts

SmapiClient (tests/integration/smapi_client.py) wraps both calls with rate
limiting and parses responses; prefer it over raw CLI. `--dry-run` variants of
run_nlu_tests.sh / run_e2e_tests.sh validate fixtures without SMAPI.
