# German L1 to English L2 Perceptual Assimilation Map for ASR Phonetic Synonym Rules

**Date**: 2026-07-25

## Key structural note

German is Germanic, so unlike the Romance languages it DOES have /ŋ/ and /k/ (no confusion on those). Its distinct confusions are /w/ to /v/, /θ/ to /s/ (NOT /t/), final obstruent devoicing, and the /r/ to uvular /ʁ/. The c/k rule (from the Koop case) does NOT apply to German.

---

## 1. Vowel Confusion Table

Sources: Flege, Bohn and Jang 1997; Bohn and Flege 1992; Dauenhauer 2023; Wikipedia German phonology.

| English Phoneme | German Realization | Conf | Notes |
|---|---|---|---|
| /æ/ | /ɛ/ | HIGH | No /æ/ in German; learners merge /æ/ and /ɛ/ |
| /ʌ/ | /a/ | MEDIUM-HIGH | No equivalent; substituted with open central |
| /ɒ/ (Br) | /ɔ/ or /a/ | MEDIUM | German lacks open rounded back vowel |
| /aɪ/, /aʊ/, /ɔʏ/ | native diphthongs | HIGH | German HAS these natively, no confusion |
| /eɪ/ | /eː/ monophthong | MEDIUM | Glide lost |
| /oʊ/ | /oː/ monophthong | MEDIUM | Glide lost |
| /ɪə/, /eə/, /ʊə/ | absent | MEDIUM | Centring diphthongs absent in German |
| /iː/, /ɪ/, /ɛ/, /ʊ/, /uː/, /ə/ | identical or near-identical | HIGH | Direct matches |

## 2. Consonant Confusion Table

Sources: Hanulikova and Weber 2010; Bien et al. 2016; Ankerstein and Morschett 2013; Hamann and Sennema 2005.

| English | German Realization | Conf | Notes |
|---|---|---|---|
| /θ/ | /s/ | HIGH | 29% production rate. NOT /t/ (only 7%). This differs from Romance (Italian maps /θ/ to /t/). |
| /ð/ | /z/ or /d/ | MEDIUM-HIGH | 26.1% error rate for /ð/-/z/ discrimination |
| /w/ | /v/ | HIGH | 40.9% error rate on /v/-/w/. German "w" = /v/. Key merge. |
| /r/ | uvular /ʁ/ | HIGH | Completely different articulation |
| final /z,d,g,v,ð,ʒ,dʒ/ | devoice to /s,t,k,f,θ,ʃ,tʃ/ | HIGH | Final obstruent devoicing applies to fricatives and affricates too, not just stops |
| /dʒ/ | /tʃ/ | MEDIUM-HIGH | Marginal phoneme (loanwords only); devoicing common |
| /ŋ/ | native, no confusion | HIGH | German has it |
| /k/ | native, no confusion | HIGH | German has it |

## 3. Sources (scraped, full text)

- Ankerstein and Morschett 2013 (PDF) - consonant discrimination error rates
- Bien et al. 2016 PMC - full text
- Dauenhauer 2023 thesis (PDF) - reproduces Flege/Bohn data
- Wikipedia Standard German phonology, English phonology
- LearnEnglishSounds.com German guide
- Flege 1997 and Bohn and Flege 1992 found but paywalled; cited second-hand via Dauenhauer 2023 and Bien et al. 2016

## 4. Actionable rules for PhoneticSynonymGenerator (German)

1. /w/ becomes "v" (the primary merge, 40.9% error rate).
2. /θ/ becomes "s" (NOT "t" - distinct from Romance).
3. /ð/ becomes "z" or "d".
4. Final devoicing: final z/d/g/v devoice to s/t/k/f (also affects fricatives and affricates).
5. /æ/ becomes "e" (merges with /ɛ/).
6. /eɪ/ and /oʊ/ monophthongize to /eː/ and /oː/.
7. DO NOT apply the Romance c/k/ck rule or the -ing rule (German has /ŋ/ and /k/ natively).

## 5. Cross-language note

The German generators must NOT call the shared Romance tail rules (the -ing to -in rule, which CLAUDE.md already documents: German and Dutch deliberately do not call ApplyRomanceTailRules because they have /ŋ/). This research confirms that design: German handles /ŋ/ natively, so no -ing variant is needed.
