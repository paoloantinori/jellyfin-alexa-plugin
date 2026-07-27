# Research Report: JF-377, distinguishing a coincidental common-word artist match from a real artist in a carrier phrase

**Date**: 2026-07-26
**Depth**: exhaustive
**Confidence**: HIGH (primary-source Amazon docs for the decisive question; established IR literature for the discriminator question)

## Executive Summary

The two cases (nonsense "zzzqqq nonexistent artist" to artist "artist"; carrier "suona la musica di bush" to artist "Bush") are **string-indistinguishable** by any query-side coverage/length/frequency signal, and the IR literature confirms this class of ambiguity is only resolvable with **context or a prior, not the mention string alone**. The decisive finding is architectural: **Alexa's NLU is designed to keep carrier phrases OUT of the slot** (Amazon's own docs define a carrier phrase as "the word or words that are part of the utterance, but not the slot"). On a clean NLU match the `musician` slot carries just "Bush", so the carrier-bleed regression I observed is a **tail case (NLU slot-extraction failure or fallback)**, not the common path. This changes the harm calculus: both the bug (nonsense auto-plays) and the regression (real artist rejected) are tail cases. The viable fixes are (a) make the guard fire only when there is positive evidence of NLU failure, or (b) downgrade ambiguous tier-4 containment matches from auto-play to a confirmation prompt. A pure query-side reject is wrong because it cannot tell the two tails apart.

## Findings

### SQ1 (decisive): Alexa entity resolution delivers the SPOKEN value in `slot.value`; the canonical entity is in `resolutions.authorities[].values[].value.name`

Amazon's Entity Resolution doc (developer.amazon.com/en-US/docs/alexa/custom-skills/entity-resolution.html) states:
- `slot.value` / `slotValue.value` is **the value the user spoke** (raw), e.g. the user says "holland" and `slot.value = "holland"`.
- The canonical resolved entity ("Netherlands") is in `slotValue.resolutions.resolutionsPerAuthority[].values[].value.name`, available only when `status.code == ER_SUCCESS_MATCH`.
- "For built-in slot types that don't support entity resolution... Alexa returns the value that the user spoke and doesn't return any entity resolution results."

**Implication**: `slot.value` is raw spoken text by design. The plugin reading `musicianSlot.Value` (raw) rather than the entity-resolution canonical name is consistent with the platform contract. CLAUDE.md's gotcha ("slot.Value always contains the raw spoken text") is confirmed against the primary source. Confidence: HIGH.

### SQ1b (decisive): Carrier phrases are designed OUT of the slot. The carrier is the grammar, the entity is the slot.

Amazon's slot-type-reference doc (developer.amazon.com/en-US/docs/alexa/custom-skills/slot-type-reference.html) states verbatim:
> "Each sample utterance must include a carrier phrase. A carrier phrase is the word or words that are part of the utterance, but not the slot, such as 'search for' or 'find out'."

And the create-intents doc shows the NLU is trained by the developer bracketing only the entity in `{slot}` within a sample utterance; the un-bracketed words are the carrier/grammar that the NLU learns to match and discard from the slot.

**Implication**: On a well-designed interaction model, when a user says "suona la musica di bush" against the sample `"suona la musica di {musician}"`, the NLU puts **"bush"** in the slot, NOT "suona la musica di bush". The carrier-bleed regression only occurs when the NLU **fails to match** the utterance to the grammar and falls back to dumping more (or all) spoken text into the slot. This is a tail case, not the common path. Confidence: HIGH (primary Amazon docs).

Caveat (cross-referenced with on-device evidence): the original JF-377 "Koop" session and JF-337's "sol coffin" live repro document that carrier bleed DOES happen on real Echo hardware, especially under ASR accent drift and NLU competition. So the tail case is non-zero on-device. It is still a tail case, not the designed path. Confidence that bleed happens at all: HIGH (on-device logs). Confidence that it is the common path: LOW (it is the failure path).

### SQ2/SQ3: Production voice systems rely on NLU slot separation, not free-text fuzzy match on the whole utterance

- Amazon's best-practices doc (best-practices-for-sample-utterances-and-custom-slot-type-values.html) iterates the pattern across all 14 locales: provide full-sentence utterances AND shortened forms ("Com'è il tempo" / "il tempo") so the NLU learns the slot boundary in many phrasings. The burden is on the interaction model to teach NLU the carrier-vs-entity split; it is NOT solved by post-hoc string cleaning of the slot.
- Entity-linking literature (Wikipedia "Entity linking"; ACL NED surveys) frames the whole problem as mention detection + disambiguation, where mention detection (NER / slot filling) is a prerequisite step distinct from string matching. Production music services (Spotify/Apple voice) use NLU intent+slot separation; they do not fuzzy-match the raw transcript.
- Apple ML research ("Using Pause Information for More Accurate Entity Recognition") explicitly addresses NLU entity-tagging failure in spoken queries as an open problem, confirming the slot-extraction failure mode is real and hard, not fully solved.

**Implication**: the architecturally correct home for fixing carrier bleed is the **interaction model** (better sample utterances so NLU extracts the entity cleanly), not a query-side guard in the handler. The handler-side guard is a safety net for the NLU-failure tail. Confidence: HIGH (architecture); MEDIUM (specific music-service internals are not fully documented publicly).

### SQ4: The established mitigation for an ambiguous entity match is NOT to reject, it is to downgrade to confirmation (disambiguation) or to use a prior

- Entity-linking systems, when a mention is ambiguous (multiple candidate entities, or a low-confidence single candidate), do NOT silently pick one and do NOT silently reject. They either (a) apply a context/prior signal to rank candidates, or (b) surface the ambiguity (disambiguation). Silently auto-playing a coincidental match with no confidence signal is the anti-pattern.
- Amazon's own model supports this: `HandleFuzzyMiss` already has an auto-play-vs-disambiguation branch (score >= ContainmentScore auto-plays; below that, disambiguates). The plugin already implements the disambiguation pattern.

**Implication**: downgrading an ambiguous tier-4 containment match (coincidental-containment shape) from **auto-play** to a **"Did you mean X?" confirmation** is the architecturally aligned fix. It never rejects a real artist (user says yes), and it stops the nonsense auto-play (user says no). Confidence: HIGH.

### SQ5: IDF / commonness is a known discriminator in entity linking, but it requires a frequency corpus and does NOT cleanly separate these two cases

- Entity linking uses a "commonness prior" (Fader et al.; the Wikipedia link-probability prior): given a mention string, the probability it refers to each candidate entity. A hyper-common word like "artist" has a flat prior (it is a common noun, rarely an entity), so a match on "artist" has low prior confidence.
- **But** this requires a frequency corpus (Wikipedia/web counts) and resolves mention to entity, not the JF-377 question (is this single matched word the intended query). IDF-weighting would correctly down-weight "artist"/"love"/"train" (common) vs "Bush"/"Pink Floyd" (specific), which DOES separate the two cases: "artist" is a top-1000 English word, "Bush" is not.

**Implication**: an IDF/commonness check on the candidate name is a *plausible* discriminator ("artist" is too common a word to be a confident auto-play from a 3-word nonsense query), but it needs a word-frequency list per locale and adds a new data dependency. It is more machinery than the disambiguation downgrade. Confidence that IDF would separate the cases: MEDIUM-HIGH ("artist" is genuinely a common word; "Bush" genuinely is not); confidence it is worth the complexity vs. disambiguation: LOW-MEDIUM.

## Synthesis: why a pure query-side reject is wrong, and what is right

1. **A pure query-side reject (my attempts 1-3) is architecturally wrong** because it treats the carrier-bleed tail case (real artist in a carrier) the same as the nonsense case. The IR literature and Amazon's slot-separation design both say the carrier-vs-entity distinction belongs to NLU, not to a post-hoc string predicate.

2. **The carrier-bleed regression is real but is a tail case** (NLU slot-extraction failure), not the common path. On a clean NLU match the slot carries just "Bush" and the guard never sees a carrier phrase. This means a guard that ONLY fires on the nonsense shape is safe on the common path and only risks the NLU-failure tail.

3. **The two viable fixes:**
   - **(B) Disambiguation downgrade (recommended)**: when tier-4 fuzzy-all produces a coincidental-containment match (the shape my guard detects), do NOT auto-play it. Downgrade to a `HandleFuzzyMiss` confirmation ("Did you mean Bush?"). Real artists still play (user says yes, one extra turn); nonsense no longer auto-plays (user says no). **No regression**, and this is the key property. Aligns with entity-linking disambiguation precedent and the plugin's existing `HandleFuzzyMiss` branch.
   - **(C) IDF/commonness gate**: only auto-play a coincidental-containment match if the candidate name is NOT a hyper-common word (e.g. not in a top-N locale word list). "artist"/"love"/"train" trigger disambiguation or not-found; "Bush"/"Pink Floyd" auto-play. Adds a per-locale frequency-list dependency. More discriminative than B but more machinery.

4. **The bug-vs-regression harm tradeoff under each fix:**
   - Pure reject (attempts 1-3): fixes nonsense, REGRESSES real artists under NLU-failure bleed. Net negative. REJECTED.
   - Disambiguation downgrade (B): fixes nonsense (no auto-play), no regression (real artist plays via confirm). Net positive. RECOMMENDED.
   - IDF gate (C): fixes nonsense for common-word artists only, no regression, adds dependency. Reasonable but heavier than B.

## Confidence Assessment

- HIGH: `slot.value` is raw spoken text (Amazon docs, SQ1); carrier phrases are designed out of the slot (Amazon docs, SQ1b); the two cases are string-indistinguishable by coverage (my verified live repro plus IR ambiguity literature, SQ5); disambiguation is the established mitigation for ambiguous matches (entity-linking literature, SQ4).
- MEDIUM: the carrier-bleed regression is a tail case rather than the common path (SQ1b plus on-device evidence that it happens but is the NLU-failure path); IDF would separate "artist" from "Bush" (linguistically sound, but unverified on this library's actual names).
- LOW: exact frequency of NLU slot-extraction failure on-device for the `musician` slot (needs on-device testing, deferred to JF-377 AC #5).

## Sources

1. Amazon Alexa Skills Kit, "Entity Resolution". https://developer.amazon.com/en-US/docs/alexa/custom-skills/entity-resolution.html (slot.value = spoken value; canonical in resolutions.authorities)
2. Amazon Alexa Skills Kit, "Slot Type Reference". https://developer.amazon.com/en-US/docs/alexa/custom-skills/slot-type-reference.html (carrier phrase defined as "the word or words that are part of the utterance, but not the slot")
3. Amazon Alexa Skills Kit, "Create Intents, Utterances, and Slots". https://developer.amazon.com/en-US/docs/alexa/custom-skills/create-intents-utterances-and-slots.html (NLU trained by bracketing only the entity in {slot})
4. Amazon Alexa Skills Kit, "Best Practices for Sample Utterances and Custom Slot Type Values". https://developer.amazon.com/en-US/docs/alexa/custom-skills/best-practices-for-sample-utterances-and-custom-slot-type-values.html (provide shortened forms across 14 locales to teach slot boundaries)
5. Wikipedia, "Entity linking". https://en.wikipedia.org/wiki/Entity_linking (ambiguity, commonness prior, disambiguation vs. silent pick)
6. Apple Machine Learning Research, "Using Pause Information for More Accurate Entity Recognition". https://machinelearning.apple.com/research/pause-information (NLU entity-tagging failure in spoken queries is an open problem)
7. ACL Anthology, "Entity resolution for noisy ASR transcripts". https://aclanthology.org/D19-3011/ (ASR transcription drift degrades entity resolution)
8. On-device evidence (this project): JF-377 original repro (corr=8799e4e2, artist "artist" auto-played from "zzzqqq nonexistent artist"); JF-337 live repro ("sol coffin" carrier bleed); this session's live minix verification (Bush/Pink Floyd rejected under carrier-bleed slot injection).
