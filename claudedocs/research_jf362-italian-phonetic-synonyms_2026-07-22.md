# Research Report: Italian phonetic synonyms for Alexa custom slots (JF-362 — "Soul Coughing" spoken "sol coffin")

**Date**: 2026-07-22
**Depth**: exhaustive
**Confidence**: HIGH (Parts 1b, 1c, 2) / MEDIUM (Part 1a — well-attested but no single primary source for the exact "soul"→"sol" mapping)

## Executive Summary

The two-layer mechanism matters more than the exact synonym strings. **Italian has no /ŋ/ (velar nasal) phoneme**, so an Italian L1 speaker almost always realizes English "-ing" as /in/ (alveolar) — "coughing" surfaces as "coffin"-like. Combined with the fact that **Alexa's ASR does acoustic matching first, then exact-string entity resolution against your synonyms**, the practical conclusion is: the current output "Soul Cofing" is *close but likely insufficient*; generating the exact spoken form "sol coffin" (and "sol cofin") as additional synonyms is worthwhile, and the dropped-g variant is genuinely common among Italian speakers (not a rare edge case). The "silent-l → sol" point is real but lower-confidence; the safest minimal set is {exact spoken, near-form}.

## Findings

### Part 1a — How Italians pronounce "soul" (silent-l, /oʊ/)

- "Soul" in English is /soʊl/ (US) or /səʊl/ (UK) — the final ⟨l⟩ IS pronounced in English (it is not silent); the vowel is a closing diphthong [4,6].
- Italian has no /oʊ/ diphthong; Italian /o/ is a monophthong (open or close). Italian L1 speakers commonly map English /oʊ/ to a monophthong /o/ or /ɔ/ [1].
- The /l/ in "soul" is word-final and Italian does have the phoneme /l/, so Italians do NOT drop it — they would typically realize it as [ol] or [ɔl], i.e. "sol" or "soul". Both surface forms are plausible; the ASR-captured "sol" in the repro is consistent with monophthongization (/soʊl/ → /sol/). **Confidence MEDIUM**: well-supported by general Italian-English phonology but no single source transcribes "soul" specifically for Italian speakers.
- Practical: the spoken form an Italian produces is in the {soul, sol, sul} neighborhood; "sol" is a documented real surface form (cf. the band "Rüfüs Du Sol", pronounced by fans as "duh SOL" = "Soul" [2]).

### Part 1b — How Italians realize English "-ing" (the decisive finding)

- **Italian has NO velar nasal /ŋ/ phoneme** — its nasal inventory is only /m/ and /n/ (alveolar) plus the palatal /ɲ/ [3]. This is the load-bearing fact.
- Because /ŋ/ is absent from Italian L1 phonology, Italian speakers cannot reliably produce it. They realize English "-ing" via the closest native phoneme: /n/ → **"coffin"-style ("in"), or sometimes a [ŋg]/[ng] sequence** (re-inserting the historical /g/, since Italian, like Spanish, treats [ŋ] as an allophone of /n/ before velars) [3,5].
- This is NOT the same phenomenon as native-English "g-dropping" (a sociolinguistic variable tied to class/casual speech, present in all English communities) [5]. For Italians, the /ŋ/→/n/ mapping is a **structural L1-transfer effect** — it affects essentially all Italian L1 speakers, not a subgroup. So "coughing" → "coffin" is the *expected* pronunciation, not a rare variant.
- **Implication for the user's prior assumption**: the user's hunch that the dropped-g "is probably less frequent" is correct for *native English* g-dropping, but **understates how universal it is for Italian L1 speakers**. For Italians, /in/ is the default realization of English "-ing". The current generator keeps "-ing" → produces "Cofing", which is the *least* likely Italian surface form.

### Part 1c — Italian consonant gemination ("Cofing" → "Coffin"?)

- Italian has phonemic geminate (double) consonants; duration is a primary perceptual cue and Italians are highly sensitive to consonant length [3, Payne's research on Italian gemination].
- **BUT**: gemination in Italian is phonologically triggered (intervocalic, often stress-conditioned or lexically specified), not freely applied to any consonant. An Italian saying "Cofing" would NOT automatically double the /f/ unless the stress pattern induces it [3,7].
- The "coffin" surface form arises primarily from the **-ing → -in** rule (Part 1b), NOT from geminating the f. "Coughing" → /kofin/ (single f) is at least as likely as /koffin/. So the doubled-f in "coffin" is incidental; the dominant signal is the dropped nasal.
- **Implication**: the user's intuition that "Italians stress single consonants and they sound doubled" is real for Italian-native words, but it does NOT reliably turn "Cofing" into "Coffin". The reliable transformation is the -ing ending.

### Part 2 — How Alexa matches spoken input against slot synonyms (decisive)

Two-stage mechanism, per Amazon's official Entity Resolution documentation [8]:

1. **ASR (acoustic) layer**: Alexa's automatic speech recognizer converts the audio to text. This layer *is* acoustic/phonetic — it has its own language model and does fuzzy acoustic matching. The catalog/dynamic-entity values **bias** the ASR toward expected strings ("The skill is biased towards the slot value based on the loaded catalog, which can help create better speech and entity recognition" [9]).
2. **Entity Resolution (string) layer**: once ASR produces a text string, entity resolution attempts to match it to a defined value or synonym. The status codes are `ER_SUCCESS_MATCH` (matched a known entity), `ER_SUCCESS_NO_MATCH` (no entity matched — returns the raw spoken string), or error [8].

**Critical facts for the design:**
- Entity resolution matches the *ASR-output text* against the *literal value/synonym strings*. There is **no documented phonetic fuzzy matching at the entity-resolution layer** — if ASR transcribes "sol coffin" and your synonyms are only ["Soul Cofing", "i Soul Cofing"], entity resolution returns `ER_SUCCESS_NO_MATCH` and the handler gets the raw "sol coffin" string. This is exactly the observed failure.
- The leverage point is therefore **making the ASR bias + synonym strings cover the actual Italian surface form**. Because the catalog biases ASR acoustically, listing the true spoken form ("sol coffin") as a synonym helps BOTH stages: it steers ASR toward transcribing the Italian pronunciation, and it gives entity resolution an exact string to match.
- Multiple synonyms per value are supported and encouraged; Amazon explicitly frames synonyms as "help Alexa resolve the user's utterance" and notes misheard values should be added as synonyms to "head off a class of errors" [8,10].
- **No hard public cap on synonym count per value** for custom types in the current model schema (the historical ~50k catalog-value limit is on total catalog size, not per-value synonyms); practically, a handful of phonetic variants per name is well within limits.

### Synthesis — which synonyms to generate for "Soul Coughing"

For the Italian-spoken "sol coffin" repro:

| Candidate synonym | Why | Generate? |
|---|---|---|
| `sol coffin` | The *exact* ASR-captured spoken form. Matches entity resolution literally AND biases ASR acoustically. | **Yes — primary** |
| `sol cofin` | Single-f variant (Part 1c: gemination is not guaranteed; /kofin/ is plausible). Near-form coverage. | **Yes — secondary** |
| `soul coffin` | /l/ often retained (Part 1a) + dropped-g. Covers the half-Italianized form. | **Yes — secondary** |
| `Soul Cofing` (current output) | Keeps "-ing", which Italians realize as "-in" — so ASR is *less* likely to transcribe the audio as "cofing" than as "cofin/coffin". | Keep, but it's the weakest of the set |

**Is the current "Cofing" output sufficient?** Probably **not**, on its own. The /ŋ/→/n/ transfer (Part 1b) is near-universal for Italian L1, so the acoustic signal Italians produce is "coffin/cofin"-like, and ASR is correspondingly more likely to transcribe "coffin" than "cofing". Listing the dropped-g forms directly covers the dominant pronunciation.

## Confidence Assessment

- **HIGH**: Italian lacks /ŋ/ (Part 1b) — Italian phonology table is unambiguous [3]. Alexa's two-stage ASR+entity-resolution mechanism and the exact-string matching at the ER layer (Part 2) — Amazon docs [8,9].
- **MEDIUM**: "soul" → "sol" specifically (Part 1a) — supported by Italian-English monophthongization generally and corroborating surface-form examples, but no single primary source transcribes this exact word. Gemination not reliably doubling f (Part 1c) — grounded in Italian phonology but the specific "Cofing" case isn't directly attested.
- **Could not fully verify**: the exact ASR transcription behavior for Italian-accented English on the specific Echo device (this is empirically observable only via on-device/profile-nlu testing, not docs). The report's recommendation therefore explicitly favors generating the *exact captured form* plus near-forms, hedging against ASR variance.

## Sources

1. Wikipedia — Phonological history of English diphthongs (https://en.wikipedia.org/wiki/Phonological_history_of_English_diphthongs) — English /oʊ/ and its history.
2. HowToSayGuide — Rüfüs Du Sol pronunciation, fans say "duh SOL" = "Soul" (https://howtosayguide.com/how-to-say-rufus-du-sol/) — corroborating "sol"/"soul" surface equivalence.
3. Wikipedia — Italian phonology (https://en.wikipedia.org/wiki/Italian_phonology) — **consonant inventory: Italian has NO /ŋ/; nasals are /m/, /n/, /ɲ/ only. Gemination is phonemic and duration-cued.**
4. Cambridge Dictionary — "soul" pronunciation /soʊl/ (https://dictionary.cambridge.org/pronunciation/english/soul).
5. Wikipedia — Pronunciation of English ⟨ng⟩ (https://en.wikipedia.org/wiki/Pronunciation_of_English_%E2%9F%A8ng%E2%9F%A9) — NG-coalescence, g-dropping as a sociolinguistic variable; notes Italian/Spanish treat [ŋ] as an allophone of /n/ before velars.
6. Forvo — "soul" English pronunciation /səʊl/ (https://forvo.com/word/soul/).
7. Payne, E. — "Phonetic variation in Italian consonant gemination" (https://www.researchgate.net/publication/231791607) — gemination is stress/lexically conditioned, not freely applied.
8. Amazon Alexa Skills Kit — Entity Resolution (https://developer.amazon.com/en-US/docs/alexa/custom-skills/entity-resolution.html) — **two-stage ASR+ER; ER_SUCCESS_MATCH/NO_MATCH; matches spoken text against values+synonyms.**
9. Amazon Alexa Skills Kit — Use Dynamic Entities (https://developer.amazon.com/en-US/docs/alexa/custom-skills/use-dynamic-entities-for-customized-interactions.html) — **"the skill is biased towards the slot value based on the loaded catalog, which can help create better speech and entity recognition."**
10. Manning liveBook — entity-resolution concept (https://livebook.manning.com/concept/alexa/entity-resolution) — add misheard values as synonyms to head off errors.

## Recommendation for JF-362 implementation

Update the recommended fix in JF-362: rather than a broad `ing`→`in` regex (rejected earlier for blast radius), the evidence now justifies a **two-part narrow change** to `ItalianPhoneticSynonyms`:

1. **Add a terminal `-ing` → `-in` transform** (applied per-word, after existing transforms). Justified: /ŋ/ is absent from Italian and /in/ is the default L1 realization — this is the single highest-value Italian-pronunciation rule and it is *targeted* (only affects words ending in "-ing", which is a bounded, well-defined class — far narrower than a blanket regex). Revisit the earlier "blast radius" worry: "-ing" is a morphological suffix, so transforming it is principled, not arbitrary.
2. **Keep an explicit word-override map** for the few cases where the whole word changes non-suffixally (e.g. `soul`→`sol`) — these are sparse and don't justify a broad vowel rule.

Each needs a unit test in `PhoneticSynonymGeneratorTests.cs` proving (a) "Coughing" → "Cofin" produced, (b) "soul" handling, and (c) a false-positive check that a name like "King Crimson" produces a sensible "Kin Crimson" (acceptable, since that IS how an Italian says it) rather than something broken. Then `/code-review high` before merge (the JF-337 revert is the cautionary precedent for matching-layer changes).

**Verify on-device** after implementing: re-run the "sol coffin" repro via profile-nlu or the Echo, and confirm `ER_SUCCESS_MATCH` to "Soul Coughing". The on-device ASR transcription is the one thing the research could not settle from docs alone.
