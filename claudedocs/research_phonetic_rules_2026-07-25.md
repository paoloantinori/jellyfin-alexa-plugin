# Phonetic Synonym Rules for Alexa ASR: Cross-Language Research Synthesis

**Date**: 2026-07-25
**Depth**: deep (7 parallel per-language research agents)
**Purpose**: Evidence-backed specification for JF-379 (PhoneticSynonymGenerator consonant + vowel substitution rules). Replaces the initial Koop-case guesses (c/k/ck, u/oo) with studied, per-language L2-English perceptual-assimilation data.
**Confidence**: HIGH for the core rules (backed by multiple peer-reviewed studies per language); MEDIUM/LOW flagged inline.

Per-language raw reports: `claudedocs/research_raw/report_{italian,spanish,french,portuguese,german,dutch,japanese}.md`.

---

## Executive Summary

The cross-language data confirms three things that shape the implementation:

1. **Per-language generators are mandatory; no shared rule set works.** The same English phoneme maps to different L1 realizations across languages. The clearest example is the voiced dental fricative, which splits three ways: Italian and Dutch map it to /d/; French and German map it to /z/; BP varies (/d/, /z/, /v/). The existing per-language-generator design in the codebase is correct.

2. **The Koop-case rules (c/k, u/oo) are Italian-specific, not universal.** German and Dutch have /k/ natively (no confusion), and the u/oo collapse is a Romance-L1 vowel-inventory effect. So the c/k/ck/q rule belongs in the Romance generators only, and the existing CLAUDE.md rule that German/Dutch skip the Romance tail rules is validated by the data (they have the velar nasal natively too).

3. **The orthography layer matters most for instructed L2 learners.** Bassetti and Atkinson 2015 (for Italian) shows that spelling drives mispronunciation more than phonology for classroom-taught speakers. Since the plugin's input is the written name, the orthography-to-spelling rules (Section "Orthography" per language) are the highest-value layer, more so than the pure phoneme maps.

---

## Cross-Language Confusion Comparison (the divergences that force per-language generators)

### /ð/ (voiced "th", as in "this")
| L1 | Realization | Conf |
|---|---|---|
| Italian | /d/ | HIGH |
| Spanish | /d/ | HIGH |
| French (European) | /z/ | HIGH |
| French (Quebec) | /d/ | HIGH (differs from European) |
| German | /z/ or /d/ | MEDIUM-HIGH |
| Dutch | /d/ (8.0% error rate, most frequent Dutch error) | HIGH |
| Portuguese (BP) | /d/, /z/, or /v/ | HIGH |
| Japanese | /z/ | HIGH |

### /θ/ (voiceless "th", as in "think")
| L1 | Realization | Conf |
|---|---|---|
| Italian | /t/ | HIGH |
| Spanish | /s/ (seseo) | HIGH |
| French (European) | /s/ | HIGH |
| French (Quebec) | /t/ | HIGH |
| German | /s/ (29% rate, NOT /t/) | HIGH |
| Dutch | /t/ or /s/ | HIGH |
| Portuguese (BP) | /t/, /s/, or /f/ | HIGH |
| Japanese | /s/ | HIGH |

### /k/ (as in "cat")
| L1 | Realization | Conf |
|---|---|---|
| Romance (IT/ES/FR/PT) | /k/, but orthography varies (c/k/ck/ch/qu), so the c/k/ck rule applies | HIGH |
| German | /k/ native, no confusion | HIGH |
| Dutch | /k/ native, no confusion | HIGH |
| Japanese | /k/ native, but epenthesis applies (k+i often becomes "ki") | HIGH |

### /ŋ/ (velar nasal, "-ng")
| L1 | Realization | Conf |
|---|---|---|
| Romance (IT/ES/FR/PT) | NOT native, decomposed to /n/+/g/ or /n/ (the -ing to -in rule applies) | HIGH |
| German | native, no substitution | HIGH |
| Dutch | native, no substitution | HIGH |
| Japanese | moraic /N/, maps but with epenthesis | MEDIUM |

---

## Per-Language Actionable Rule Sets

Each language's full vowel + consonant table is in its raw report. Below are the highest-value rules for the generator, with confidence.

### Italian (it-IT) - source: report_italian.md
**Orthography layer (Bassetti 2015, highest value):**
- "w" word-initial becomes "v" (water becomes vater) - HIGH
- "h" word-initial deleted - HIGH
- "th" voiceless becomes "t" - HIGH
- "th" voiced becomes "d" - HIGH
- "oo" short becomes "o"; "oo" long becomes "u" - MEDIUM
- double consonants become geminates - HIGH
- "-ing" becomes "-in" - HIGH
- c/k/ck/q consonant variants (the Koop rule) - HIGH (orthography-driven)

**Vowel collapses:** /iː/+/ɪ/ to /i/; /ɛ/+/æ/ to /ɛ/; /ʌ/+/ɑː/+/æ/ to /a/; /əʊ/ to /o/ - all HIGH

### Spanish (es-ES/MX/US) - source: report_spanish.md
- /v/ becomes /b/ (the b/v merger) - HIGH
- /z/ becomes /s/ - HIGH
- /ð/ becomes /d/ - HIGH
- /θ/ becomes /s/ (seseo) - HIGH
- /ŋ/ becomes /n/ - HIGH
- /dʒ/ becomes /j/ (yeismo) - HIGH
- /h/ becomes /x/ or dropped - MEDIUM
- c/k/ck rule applies (Romance) - HIGH

**Vowel collapses:** /iː/+/ɪ/ to /i/; /æ/+/ɑː/+/ʌ/ to /a/ (triple); /uː/+/ʊ/ to /u/ - all HIGH

### French (fr-FR European) - source: report_french.md
- /h/ dropped - HIGH
- /θ/ becomes /s" (NOT /t/) - HIGH (European; Quebec uses /t/)
- /ð/ becomes /z" - HIGH (European; Quebec uses /d/)
- /r/ becomes uvular - HIGH
- /ŋ/ becomes /n/ or /ɲ/ - MEDIUM
- /tʃ/ becomes /ʃ/; /dʒ/ becomes /ʒ/ - MEDIUM
- c/k/ck rule applies (Romance) - HIGH

**Vowel collapses:** /iː/+/ɪ/ to /i/; /uː/+/ʊ/ to /u/; /ɛ/+/æ/ to /ɛ/; /ʌ/ to /a/ or /ɔ/; all diphthongs monophthongize or become hiatus - HIGH

**NOTE:** fr-CA (Quebec) differs from fr-FR on the dental fricatives. If the generators are locale-specific, fr-CA should use /t/ and /d/ (matching Italian), not /s/ and /z/.

### Portuguese (pt-BR) - source: report_portuguese.md
- /θ/ becomes /t/, /s/, or /f/ - HIGH
- /ð/ becomes /d/, /z/, or /v/ - HIGH
- /h/ and /r/ inverted (BP word-initial /R/ sounds like /h/, BP "h" is silent) - MEDIUM
- English /r/ maps to BP /R/ (uvular) - HIGH
- /t/ and /d/ palatalize before /i/ and /e/ to /tʃ/ and /dʒ/ - HIGH
- final voiceless stops get epenthetic /i/; final voiced obstruents devoice - HIGH
- c/k/ck rule applies (Romance) - HIGH

**Vowel collapses:** /ɛ/+/æ/ to /ɛ/; /ɔː/+/ɑː/ to /ɔ/ or /a/; /uː/+/ʊ/ to /u/; /ʌ/+/ɑː/ to /a/; /ɜː/+/ɑː/ worst pair - HIGH

### German (de-DE) - source: report_german.md
- /w/ becomes /v/ (40.9% error rate, the primary German merge) - HIGH
- /θ/ becomes /s/ (NOT /t/, 29% rate) - HIGH
- /ð/ becomes /z/ or /d/ - MEDIUM-HIGH
- /r/ becomes uvular /ʁ/ - HIGH
- final devoicing: /z,d,g,v,ð,ʒ,dʒ/ to /s,t,k,f,θ,ʃ,tʃ/ (fricatives AND affricates, not just stops) - HIGH
- /æ/ becomes /ɛ/ - HIGH
- /eɪ/ and /oʊ/ monophthongize - MEDIUM

**DO NOT apply:** the Romance c/k/ck rule (German has /k/ natively), the -ing to -in rule (German has /ŋ/ natively). This validates the existing codebase design.

### Dutch (nl-NL) - source: report_dutch.md
- /ð/ becomes /d/ (8.0% error, most frequent Dutch consonant error, Cucchiarini 2011) - HIGH
- /z/ becomes /s/ (5.7%) - HIGH
- /w/ becomes /v/ (Dutch /w/ IS /v/) - HIGH
- /θ/ becomes /t/ or /s/ - HIGH
- /æ/ becomes /ɛ/ - HIGH
- /r/ uvular - HIGH
- /ʌ/, /ɒ/, /ʊ/ each map to a single Dutch vowel - HIGH

**DO NOT apply:** Romance c/k/ck rule or -ing rule (Dutch has /k/ and /ŋ/ natively).

### Japanese (ja-JP) - source: report_japanese.md
- /r/ and /l/ MERGE to the alveolar tap (the famous confusion, Best and Strange 1992 single-category) - HIGH
- /θ/ becomes /s/; /ð/ becomes /z/ - HIGH
- /v/ becomes /b/ (vase becomes base) - HIGH
- /f/ becomes bilabial fricative - HIGH
- vowel epenthesis: default /u/, /o/ after /t/ and /d/, /i/ after word-final /k/ in older loans - HIGH
- consonant clusters broken by epenthesis - HIGH
- /j/ insertion after velars before /æ/ - HIGH
- /s/ becomes /ʃ/ hypercorrection before /i/ - MEDIUM

**Vowel collapses:** /æ/+/ɑː/+/ʌ/+/ə/ all to /a/ (/æ/ worst, 2/7 goodness); /iː/+/ɪ/ to /i/; /ʊ/+/uː/ to /u/ - HIGH

---

## Confidence Assessment

- **HIGH**: All the /ð/, /θ/, /h/, /r/, /w/, /ŋ/ mappings above (multiple peer-reviewed studies per language: Flege, MacKay, Brannen, Cucchiarini, Van den Doel, Best and Strange, Yazawa, Bassetti, Rauber, Cebrian, Baigorri, Hanulikova).
- **MEDIUM**: Some vowel-position inferences (e.g., /ʊ/ to /o/ in Italian), schwa mappings, the fr-CA vs fr-FR divergence (needs a fr-CA-specific source to confirm).
- **LOW**: A few inferred diphthong mappings (Italian /aɪ/ to /ai/, /ɔɪ/ to /oi/) with no direct source.

The c/k/ck/q rule (JF-379's original proposal) is confirmed for Romance languages but is orthography-driven (Bassetti 2015) rather than purely phonological, and must NOT be applied to German/Dutch/Japanese.

## Sources (key, across all languages)
- Flege and MacKay 2004 (Italian vowels); Flege, Bohn and Jang 1997 (FR/ES vowels); Flege 1995 (SLM framework)
- MacKay, Meador and Flege 2001 (Italian consonants)
- Bassetti and Atkinson 2015 (Italian orthography effects); Bassetti et al. 2018 (length contrasts)
- Brannen 1999 (French dental fricatives); LINGUIST List 10.662
- Hanulikova and Weber 2010; Bien et al. 2016; Ankerstein and Morschett 2013 (German)
- Cucchiarini et al. 2011 (Dutch error frequencies); Van den Doel 2006; Collins and Mees 2003
- Rauber 2005; Rato and Carlet 2020; Reis 2006 (BP)
- Baigorri et al. 2018; Cebrian and Gorba 2021; Boomershine 2013 (Spanish)
- Best and Strange 1992; Yazawa et al. 2023 (Japanese l/r, vowel goodness)
- Wikipedia phonology articles (canonical inventories, all 7 languages)

Full URLs are in each per-language raw report.

## Recommended next step
Use this synthesis as the input to a /superpowers:brainstorming session to design how PhoneticSynonymGenerator consumes these per-language rule tables (data structure, variant bounding, ordering device-captured forms first). Do NOT implement directly from this report without that design step.
