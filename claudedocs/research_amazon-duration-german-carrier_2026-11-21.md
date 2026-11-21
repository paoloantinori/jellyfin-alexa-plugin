# Research Report: AMAZON.DURATION with the "schlaf-timer" German carrier fails for compound durations

**Date**: 2026-11-21
**Depth**: exhaustive (live probe-driven; web search yielded no directly applicable results for this niche)
**Confidence**: HIGH (16 live profile-nlu probes on the production skill, reproducible pattern)

## Executive Summary

The "schlaf-timer {sleep_duration}" sample fails because the compound "schlaf-timer" is too weak a carrier for the NLU to confidently segment the boundary to the duration slot. This is NOT a hyphenation issue, NOT an AMAZON.DURATION issue, and NOT a German worded-numbers issue: all three work correctly with other carriers. The fix is to remove or replace the broken carrier.

## Findings

### 1. The pattern (live probe evidence, de-DE, production skill)

Carrier "schlaf-timer {sleep_duration}" (or any spelling variant):
| Duration | Result |
|---|---|
| eine stunde (simplest: one article + one unit) | SleepTimerIntent PT1H ✓ |
| eine halbe stunde (compound duration phrase) | NO_SELECTION ✗ |
| fünf minuten (small worded number + unit) | NO_SELECTION ✗ |
| sechzig minuten (large worded number + unit) | NO_SELECTION ✗ |
| 30 minuten (digit + unit) | NO_SELECTION ✗ |

Carrier "stoppe in / stoppe nach / ausschalten in {sleep_duration}":
| Duration | Result |
|---|---|
| sechzig minuten | SleepTimerIntent PT60M ✓ |
| any duration phrase | SleepTimerIntent ✓ |

Spelling variants of the broken carrier all fail identically:
- "schlaf-timer sechzig minuten" (hyphen) ✗
- "schlaf timer sechzig minuten" (space) ✗
- "schlaftimer sechzig minuten" (compound) ✗

### 2. Root cause analysis

The NLU's German language model cannot confidently segment "schlaf-timer" (or any spelling variant) as a carrier distinct from the duration slot value. Three factors compound:

a) "schlaf-timer" is not a standard German compound: it is an anglicism (English "sleep timer" calqued into German). The NLU's language model has no strong prior for it as a semantic unit.

b) The word "Schlaf" (sleep) has its own semantic weight in German, potentially competing with sleep-mode, do-not-disturb, or bedtime intents in the NLU's model.

c) Unlike the working carriers ("stoppe in", "stoppe nach", "ausschalten in"), which are standard German prepositional phrases with clear syntactic boundaries, "schlaf-timer" has no preposition or syntactic marker separating it from the slot value.

### 3. AMAZON.DURATION itself works correctly in de-DE

Verified by the fact that "stoppe in sechzig minuten" returns PT60M and "stell einen timer für sechzig minuten" (a DIFFERENT intent, SetReminderIntent) also fills a duration slot with PT60M. The slot type parses German worded numbers (sechzig), digit numbers (30), and natural phrases (eine stunde) without issue when the carrier boundary is clear.

### 4. Web search findings

The official Amazon slot type reference documents AMAZON.DURATION as available in all supported locales with examples only in English [1]. No documented locale-specific issues for German. No Stack Overflow threads specifically about German compound carriers with AMAZON.DURATION. This appears to be an undocumented NLU behavior specific to non-standard German compounds.

### 5. The existing sample model has 4 SleepTimerIntent samples

Three work ("stoppe in", "stoppe nach", "ausschalten in"), one does not ("schlaf-timer"). The failure is isolated to the one non-standard carrier.

## Recommended fix

Remove "schlaf-timer {sleep_duration}" from the de-DE model template and replace it with a working alternative:

- Option A (safest): Remove it entirely. The three working carriers cover the use case. Users who say "schlaf-timer 60 minuten" will get no response (same as today), but users who say "stoppe in 60 minuten" (the natural German phrasing) will work.
- Option B (most user-friendly): Replace with a different sleep-related carrier that uses standard German syntax, e.g., "schalte aus in {sleep_duration}" (turn off in X) or "schlafe in {sleep_duration}" (sleep in X).
- Option C (if the schlaf-timer phrasing is important): Add the sample without the hyphen AND add several training variants to strengthen the NLU's model. This is the highest-risk option with no guarantee of improvement.

**Recommendation: Option B.** "schalte aus in {sleep_duration}" is natural German, semantically equivalent to a sleep timer, and follows the prepositional-phrase pattern that the NLU handles correctly.

## Confidence Assessment

**HIGH**: The live probe results (16 probes, consistent pattern, reproducible). AMAZON.DURATION works in de-DE. The "schlaf-timer" carrier is the cause, not the slot type or hyphenation.

**MEDIUM**: The root cause analysis (NLU language model weakness for non-standard compounds) is inference from the probe pattern, not confirmed by Amazon documentation. The specific mechanism inside the NLU is opaque.

**NOT FOUND**: No Amazon documentation or community reports specifically about German compound carriers with AMAZON.DURATION.

## Sources

1. Amazon: "Slot Type Reference" (AMAZON.DURATION section, all locales, English examples only): https://developer.amazon.com/en-US/docs/alexa/custom-skills/slot-type-reference.html#duration
2. Live profile-nlu probes against the production skill (amzn1.ask.skill.33dfacd5), de-DE locale, 2026-11-21, 16 utterances covering carrier variants and duration types
