# Research Report: Italian consonant-doubling (gemination) in English — evidence for a catalog-synonym doubling rule

**Date**: 2026-07-23
**Depth**: exhaustive
**Confidence**: HIGH on the linguistic mechanism; MEDIUM-LOW on the specific ASR-transcription implication (the one thing docs can't settle).

## Executive Summary

Italian L1 speakers do **not** broadly double every English consonant. The single most relevant study (Bassetti 2017, Warwick, peer-reviewed) shows the opposite of a blanket rule: **Italian L2 speakers geminate an English consonant when it is *spelled with a double letter*, and produce a short single consonant when it is *spelled with a single letter***. The gemination is driven by the **orthography the speaker sees/has internalized**, not by an acoustic reflex. So a general "double all single intervocalic consonants" rule is **not** linguistically justified and would produce many wrong synonyms. The "coffin" observation (single-f "coughing" heard/transcribed as double-f "coffin") is **not predicted by this literature** — it is most likely an Alexa-ASR-model artifact (the Italian-locale ASR mapping the sound onto the familiar Italian word "coffin", which is double-f), not a production feature of the speaker. The safe, evidence-backed fix is therefore narrow: emit **both** single- and double-consonant spellings as synonyms for the small set of words where ASR has been *observed* to double, rather than a generative doubling rule.

## Findings

### Part 1 — Do Italians systematically geminate English consonants? (mostly NO, and it's spelling-driven)

- **Italian has phonemic gemination across all consonant classes** — stops, fricatives, affricates, nasals, liquids — confirmed by the multi-decade GEMMA project (Di Benedetto et al.) and Italian phonology references [1,2,3]. Italians are acutely sensitive to consonant *duration*.
- **But transfer to English is NOT a blanket doubling.** Bassetti (2017) [4] ran the decisive experiment: Italian L1 speakers of English L2 read English words spelled with a SINGLE vs DOUBLE consonant letter, and consonant duration was measured. Result: *"The English L2 speakers produced the same consonant as shorter when it was spelled with a single letter, and longer when spelled with a double letter. Spelling did not affect consonant duration in native English speakers."* The effect persisted even without visible orthography (delayed repetition), meaning speakers had internalized the spelling→length mapping.
- **Implication**: an Italian saying "coughing" (single f in spelling) would, per Bassetti, produce a **short single /f/** — NOT a doubled one. The literature therefore does **not** predict the observed "coffin" (double-f) transcription from production alone.

### Part 1b — The specific "coughing"→"coffin" case

- /f/ → [fː] (geminated f) is **not** a documented systematic Italian-accented-English feature. Italian CAN geminate /f/ (e.g. "goffo", "beffa"), but Bassetti shows Italians apply that length in English only where the spelling has "ff".
- The most plausible explanation for "soul coffin" (double-f) is **not** the speaker's production but **Alexa's ASR**: the Italian-locale acoustic model, hearing an English word ending in an /f/-like + /in/ sound after the /ŋ/→/n/ shift, maps it onto the closest familiar Italian word — **"coffin"** (English loanword in Italian, universally spelled double-f, pronounced /kof·fin/). The ASR *output* is the Italian word, not a phonetic transcription of the speaker's /f/ length.
- This is **inferred, not proven** — no public source documents Alexa's Italian ASR acoustic-to-orthography mapping. Confidence MEDIUM.

### Part 1c — Stress-conditioned gemination (raddoppiamento)

- Italian has stress-conditioned consonant lengthening: **raddoppiamento sintattico** (initial consonant of a word lengthens after a preceding stressed vowel) and reinforced ("intense") consonants after stressed vowels within words [5]. But this applies to *Italian-internal* phonology and Italian-loanword adaptation, not as a documented systematic rule applied to English single consonants. It does not justify a broad English-consonant-doubling synonym rule.

### Part 2 — Orthographic / ASR-transcription implication

- A doubled English letter normally = **one short consonant** in English ("dinner" [dɪnə], "happy" [hapi], "carry" [kari]) [6]. So if my synonym generator emits a double-letter spelling, it does not change how a *native English* ASR would hear it — but the **Italian-locale** ASR may map double-letter spellings to Italian geminate words.
- No public evidence documents Alexa's exact tolerance for single- vs double-consonant synonym matching. The observed "coffin" transcription suggests the Italian ASR favors the orthographic form of the Italian loanword.

### Part 3 — Scope and safety of a doubling rule

- A broad "double all single intervocalic consonants" rule is **NOT linguistically defensible** (Bassetti shows Italians do the opposite — they keep single consonants short when spelled single) and would generate vast numbers of wrong synonyms (e.g. "city"→"citty", "pretty"→"prettty", "love"→"lovve") that don't reflect how Italians speak OR how ASR transcribes them.
- **Collision risk is real**: doubled spellings can match different real Italian/English words (e.g. doubling in a stem could collide with an actual catalog entry). A broad rule would multiply this risk across every name.
- The evidence-backed, low-risk approach is **explicit, observed-only**: maintain a small map of *attested* ASR-capture doublings (e.g. coughing→coffin), add BOTH single- and double-consonant forms as synonyms for those specific words, and extend the map only when a new device capture proves a doubling. This is the same "explicit word-override map" pattern already in the codebase and is bounded + auditable.

## Synthesis for the JF-362 fix

| Question | Evidence-backed answer |
|---|---|
| Do Italians broadly double English consonants? | **No.** They double only where spelling shows a double letter (Bassetti 2017). |
| Why did ASR hear "coffin" (double f) from "coughing"? | **Most likely an Alexa Italian-ASR artifact** mapping the sound to the Italian loanword "coffin" — not the speaker's production. Inferred, not proven. |
| Should I write a generative doubling rule? | **No.** Not linguistically justified; high collision/wrong-synonym risk. |
| What's the safe fix? | **Explicit attested-doubling map** (coughing→coffin etc.), emitting BOTH single- and double-consonant forms. Extend only on observed device captures. |
| Does this need device confirmation? | **Yes** — the one genuinely uncertain link is the ASR's actual output, which no doc covers. Ship the explicit map, sample-verify on device, add entries as captures arrive. |

## Confidence Assessment

- **HIGH**: Italian gemination is phonemic and spans all consonant classes [1,2,3]; Italians do NOT blanket-double English single consonants — gemination in L2 English is spelling-driven (Bassetti 2017 [4], peer-reviewed, the load-bearing source).
- **MEDIUM**: the "coffin" transcription is best explained as an Alexa Italian-ASR artifact (loanword mapping), but no source documents Alexa's ASR internals — this is reasoned inference from the device capture + the linguistic evidence.
- **LOW / unverifiable**: the exact set of words Alexa's Italian ASR will double-transcribe; whether single- vs double-consonant synonyms match equivalently in entity resolution. Only device captures can establish these.

## Sources

1. Wikipedia — Italian phonology (https://en.wikipedia.org/wiki/Italian_phonology) — geminate consonants: shorten preceding vowel, first element unreleased; phonemic across classes.
2. Di Benedetto & De Nardis — "Consonant gemination in Italian: the affricate and fricative case" (https://www.researchgate.net/publication/341396337) — GEMMA project: fricatives (incl. /f/) geminate, represented by double letter.
3. GEMMA project — "Consonant gemination in Italian: the nasal and liquid case" (https://www.academia.edu/112345182) — all classes (stops/liquids/fricatives/nasals/affricates) analyzed over ~25 years.
4. **Bassetti, B. (2017)** — "Orthography Affects Second Language Speech: Double Letters and Geminate Production in English", J. Exp. Psych.: Learning, Memory, Cognition (https://wrap.warwick.ac.uk/id/eprint/87241/7/) — **the decisive source**: Italian L2 speakers produce consonants longer only when spelled with a double letter; single-letter spellings yield short single consonants. Orthography-driven, not blanket.
5. Latin/Italian gemination after stressed vowel — raddoppiamento sintuttico (https://latin.stackexchange.com/questions/1699/) — stress-conditioned initial-consonant lengthening is Italian-internal, not an English-transfer rule.
6. Improve Your Accent — "Double Consonants in English: Geminates?" (https://improveyouraccent.co.uk/double-consonants-in-english-geminates/) — doubled English letters = one short consonant; native geminate languages (Italian listed) may over-lengthen by instinct.

## Recommendation

Do NOT implement a generative consonant-doubling rule (the evidence actively argues against it). Instead, for the "Soul Coughing"→"sol coffin" gap, extend the existing explicit word-override map so that the *attested* doubled form is emitted alongside the single form: emit synonyms covering {sol, soul} × {cofin, coffin}. More generally, treat consonant-doubling as an **observed-ASR-capture phenomenon** to be added word-by-word to the override map, not a rule to infer. Confirm on device after each addition.

This reverses my earlier "generate single-f Cofin, don't double" conclusion — which was based on the (correct) linguistic point that Italians don't freely double, but missed that the ASR output is driven by Alexa's Italian loanword mapping, not the speaker's production. Your instinct to check the device logs was what surfaced the real behavior.
