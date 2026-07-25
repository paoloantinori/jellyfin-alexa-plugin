# Italian L1 to English L2 Perceptual Assimilation Map for ASR Phonetic Synonym Rules

**Purpose**: Structured mapping of how Italian L1 speakers perceive/produce English phonemes, for building `PhoneticSynonymGenerator` rules. Only source-backed mappings included.

**Date**: 2026-07-25

---

## Italian Phoneme Inventory (Reference Anchor)

Source: Wikipedia "Italian phonology" (Rogers and d'Arcangeli 2004)

### Vowels (7 phonemes)

| Phoneme | Example | Italian Spelling |
|---------|---------|------------------|
| Close front unrounded /i/ | ingrassare | i |
| Close-mid front /e/ | pero | e (stressed, close) |
| Open-mid front /ɛ/ | pesca | e (stressed, open) |
| Open central /a/ | amare | a |
| Close-mid back /o/ | porta | o (stressed, close) |
| Open-mid back /ɔ/ | porco | o (stressed, open) |
| Close back /u/ | uva | u |

Note: /e/ vs /ɛ/ and /o/ vs /ɔ/ neutralize in unstressed syllables (merge to close-mid). Italian has NO diphthong phonemes (only vowel+glide sequences /j/, /w/). Italian has NO /ə/, NO /ʌ/, NO /æ/, NO /ɪ/ as distinct phonemes.

### Consonants (key subset)

- /p, b, t, d, k, ɡ/: /t, d/ are dental; /p, t, k/ are NEVER aspirated
- /tʃ/ ("ci", "ce"), /dʒ/ ("gi", "ge"): Italian has these natively
- /r/: rolled alveolar trill, NOT the English approximant
- /n/: velar [ŋ] allophone before /k, ɡ/ only (not phonemic)
- NOT in Italian: /h/, /θ/, /ð/, /ŋ/ (phonemic), /w/ (as in "water"), /ʒ/

---

## 1. Vowel Confusion Table

| English Phoneme | Example | Spelling | Italian Realization | Conf | Source |
|---|---|---|---|---|---|
| /ɪ/ | pin, sit | single "i" (lax) | /i/, spelled "i" | HIGH | Flege and MacKay 2004; Farmer 2009 |
| /iː/ | see, beat | "ee","ea","ie", single "e" | /i/, spelled "i" (collapses with /ɪ/) | HIGH | Flege and MacKay 2004 |
| /ɛ/ | pen, bed | single "e" | /ɛ/, spelled "e" (direct match) | HIGH | Flege and MacKay 2004; Farmer 2009 |
| /æ/ | cat, man | single "a" | /ɛ/, spelled "e" (SC with /ɛ/, poor discrimination) | HIGH | Flege and MacKay 2004 (core) |
| /ʌ/ | cut, bus | single "u" | /a/, spelled "a" | HIGH | PronunciationStudio; BoldVoice |
| /ɑː/ | car, father | "ar","a" | /a/, spelled "a" (collapses with /ʌ/,/æ/) | HIGH | PronunciationStudio; BoldVoice |
| /uː/ | food, two | "oo","ue","ew" | /u/, spelled "u" (direct match) | HIGH | Flege et al. |
| /ʊ/ | good, book | "oo","u" | /o/, spelled "o" | MEDIUM | Inferred from chart |
| /ɒ/ | dog, lot | single "o" | /ɔ/, spelled "o" | HIGH | BritishAccentAcademy |
| /əʊ/ (GO) | go, no | "o","oa","ow" | /o/ single vowel, spelled "o" (glide lost) | HIGH | BoldVoice; PronunciationStudio |
| /ə/ (schwa) | about, the | unstressed | /a/ or /e/ (no schwa in Italian) | MEDIUM | Italian phonology |
| /eɪ/ (FACE) | say, make | "ay","a..e","ai" | /e/, spelled "e" (diphthong collapses) | MEDIUM | Inferred |
| /aɪ/ (PRICE) | my, time | "y","i..e","igh" | /ai/, spelled "ai" | LOW | Inferred |
| /ɔɪ/ (CHOICE) | boy | "oy","oi" | /oi/, spelled "oi" | LOW | Inferred |
| /ɜː/ (NURSE) | word, bird | "or","ir","ur" | /ɔ/, spelled "o" (plus rolled /r/) | MEDIUM | BritishAccentAcademy |
| /ɪə/ (NEAR) | near | "ear","ere" | /i/ or /ie/ | LOW | Inferred |

### PAM-L2 Single-Category collapses (poorest discrimination)
- /ɛ/ and /æ/ both map to Italian /ɛ/
- /iː/ and /ɪ/ both map to Italian /i/
- /ʌ/, /ɑː/, /æ/ all map to Italian /a/

---

## 2. Consonant Confusion Table

| English | Example | Spelling | Italian Realization | Conf | Source |
|---|---|---|---|---|---|
| /h/ | house, hard | initial "h" | DROPPED (Italian "h" silent) | HIGH | PronunciationStudio; MacKay et al. 2001 |
| /θ/ | think, three | "th" (voiceless) | /t/, spelled "t" (think becomes tink) | HIGH | MacKay et al. 2001 |
| /ð/ | this, the | "th" (voiced) | /d/, spelled "d" (this becomes dis) | HIGH | MacKay et al. 2001 |
| /ŋ/ | sing, long | "ng" | /nɡ/, spelled "ng" (decomposed; Italian only has [ŋ] allophonically) | HIGH | Italian phonology |
| /r/ (approximant) | red, rain | "r" | /r/ alveolar trill, spelled "r" | HIGH | MacKay et al. 2001 |
| /r/ (silent RP) | car, word | "r" post-vocalic | /r/ PRONOUNCED (Italian is rhotic) | HIGH | BritishAccentAcademy |
| /w/ | water, wood | "w" | /v/, spelled "v" OR /u/ glide (water becomes vater) | HIGH | Bassetti and Atkinson 2015 |
| /k/ | cat, skin, back | "c","k","ck","ch" | /k/, spelled "c"/"ch" before front vowels (direct match) | HIGH | Italian phonology |
| /dʒ/ | jump, gem | "j","g" before e/i | /dʒ/, spelled "gi"/"ge" (native) | HIGH | Italian phonology |
| /tʃ/ | chair, city | "ch","t"(-tion) | /tʃ/, spelled "ci"/"ce" (native) | HIGH | Italian phonology |
| /ʒ/ | measure, vision | "si","ge" | /ʃ/ or /dʒ/ (no /ʒ/ phoneme) | MEDIUM | Italian phonology |
| /p,t,k/ aspirated | park, tall | initial | UNASPIRATED | HIGH | PronunciationStudio |

### Consonant-final epenthesis: words ending in consonants get a vowel appended (stop becomes stopa). HIGH confidence.

---

## 3. Orthography to Pronunciation Rules (the layer PhoneticSynonymGenerator operates on)

Source: Bassetti and Atkinson 2015 (Appl. Psycholinguistics 36:67-91); Sokolovic-Perovic et al. 2020. Orthography wins over phonology for instructed learners.

| English Spelling | English Phoneme | Italian L1 Misreading | Output Spelling | Conf | Source |
|---|---|---|---|---|---|
| "w" (word-initial) | /w/ | [v] (loanword GPC) | v | HIGH | Bassetti 2015 |
| "h" (word-initial) | /h/ | silent (deleted) | (deleted) | HIGH | Bassetti 2015 |
| "th" (voiceless) | /θ/ | [t] | t | HIGH | Bassetti 2015 |
| "th" (voiced) | /ð/ | [d] | d | HIGH | Bassetti 2015 |
| double consonants (bb,tt,kk,...) | same as singleton | geminate (long) | doubled | HIGH | Bassetti 2015; Bassetti 2018 |
| "oo" (short, good) | /ʊ/ | [o] | o | MEDIUM | Italian GPC |
| "oo" (long, food) | /uː/ | [u] | u | MEDIUM | Italian GPC |
| "ee","ea" (tense) | /iː/ | [i] | i | HIGH | Flege and MacKay 2004 |
| single "e" (he, me) | /iː/ | [e] | e | HIGH | Bassetti 2015 |
| "a" (lax, cat) | /æ/ | [a] | a | HIGH | PronunciationStudio |
| "u" (lax, cut) | /ʌ/ | [u] or [a] | u/a | MEDIUM | PronunciationStudio |
| "-ed" after voiced | /d/ | [ed] (all letters read) | ed | MEDIUM | BoldVoice |
| "-ed" after voiceless | /t/ | [et] | et | MEDIUM | BoldVoice |
| silent letters in clusters | various | pronounced | full spelling | HIGH | Bassetti 2015 |
| vowel digraph (no, go) | /əʊ/ | [o] single | o | HIGH | BoldVoice |
| "ng" (word-final) | /ŋ/ | [ŋɡ] hard g | ng | HIGH | Italian phonology |
| "c" before e/i | /s/ | /tʃ/ (Italian GPC) | ci/ce | MEDIUM | Italian GPC |
| "g" before e/i | /dʒ/ | /dʒ/ | gi/ge | MEDIUM | Italian GPC |

### Double-letter length effect: homophones (finish/Finnish, seen/scene) become NON-homophonic under Italian GPC (double letter means geminate). HIGH confidence.

---

## 4. Sources

### HIGH confidence
- Flege and MacKay 2004, "Perceiving vowels in a second language", SSLA 26:1-34. https://doi.org/10.1017/S0272263104026117. Core vowel map.
- Farmer, Liu, Mehta and Zevin 2009, CogSci. https://escholarship.org/content/qt6ts7m2dg/qt6ts7m2dg.pdf. Mouse-tracking confirmation of the vowel collapse.
- MacKay, Meador and Flege 2001, Phonetica 58:103-125. https://pubmed.ncbi.nlm.nih.gov/11096371/. Consonant identification errors.
- Bassetti and Atkinson 2015, Appl. Psycholinguistics 36:67-91. https://doi.org/10.1017/S0142716414000435. Italian-specific orthographic effects.
- Bassetti et al. 2018, Language and Speech. https://doi.org/10.1177/0023830918772148. Orthography-induced length contrasts.
- Wikipedia "Italian phonology". https://en.wikipedia.org/wiki/Italian_phonology. Canonical inventory.
- PronunciationStudio / BoldVoice / BritishAccentAcademy (pedagogical confirmation).

---

## 5. Most Actionable Rules for PhoneticSynonymGenerator (Italian)

1. /ɪ/ and /iː/ collapse: generate "i" variant for both.
2. /ɛ/ and /æ/ collapse: /æ/ reads as Italian /a/ (orthography wins).
3. /ʌ/, /ɑː/, /æ/ collapse to /a/.
4. /θ/ becomes "t"; /ð/ becomes "d".
5. /h/ dropped.
6. "w" becomes "v" (Bassetti direct finding).
7. /əʊ/ diphthong becomes single "o".
8. "-ing" becomes "-in".
9. Double consonants become geminates (may matter for ASR).
10. Final consonant gets a vowel appended (stop becomes stopa).
