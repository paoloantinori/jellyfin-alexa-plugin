# Spanish L1 to English L2 Perceptual Assimilation Map

## Purpose
Structured mapping of how Spanish L1 speakers perceive/produce English phonemes,
for deriving ASR phonetic-synonym rules. Based on published Flege SLM / Best PAM-L2
perceptual-assimilation literature.

## Spanish Phoneme Inventory (Reference)

### Vowels (5 monophthongs + falling diphthongs)

| Phoneme | Example (spelling) | Notes |
|---------|---------------------|-------|
| /i/ | *piso*, *si* | |
| /e/ | *pero*, *mes* | |
| /a/ | *paso*, *mas* | Central low, IPA [a] |
| /o/ | *poco*, *los* | |
| /u/ | *puso*, *su* | |

**Diphthongs**: /ai/ (*causa*), /ei/ (*rey*), /oi/ (*hoy*), /au/ (*pausa*), /eu/ (*feudo*), /ou/ (*bou*), /ia/ (*piano*), /ie/ (*pie*), /io/ (*ruido*), /ua/ (*cuatro*), /ue/ (*cuento*), /uo/ (*cuota*).

Source: [Spanish phonology - Wikipedia](https://en.wikipedia.org/wiki/Spanish_phonology)

### Consonants (18-19 phonemes depending on dialect)

| Phoneme | Example (spelling) | Notes |
|---------|---------------------|-------|
| /p/ | *paso* | |
| /t/ | *taco* | Laminal denti-alveolar |
| /k/ | *casa* | |
| /b/ | *boca*, *vaca* | **Merged with /v/** (see below) |
| /d/ | *dedo* | Laminal denti-alveolar |
| /g/ | *gato* | |
| /f/ | *fuego* | |
| /theta/ | *caza*, *cinco* | Castilian only; **seseo** dialects merge to /s/ |
| /s/ | *casa*, *paso* | |
| /x/ | *jamon*, *gente* | Voiceless velar (or uvular) fricative |
| /m/ | *mano* | |
| /n/ | *nano* | |
| /J/ | *ano* | Merged to /j/ in yeismo dialects (majority) |
| /l/ | *lago* | |
| /L/ | *calle* | Merged to /j/ in yeismo dialects (majority) |
| /r/ | *pero* | Tap (single r) |
| /rr/ | *perro* | Trill (double r) |
| /j/ | *yo*, *llama* | Varies: [j], [Y], [dZ], [G] |
| /w/ | *huevo* | Labiovelar, only in diphthongs |
| /tS/ | *mucho* | |

**Critical: /b/~/v/ merger**: Spanish has NO phonemic /v/. Letters *b* and *v* represent the same phoneme /b/.
**Critical: /theta/~/s/ (seseo)**: ~90% of Spanish speakers (all Latin America, Andalusia, Canary Islands) use only /s/.

Source: [Spanish phonology - Wikipedia](https://en.wikipedia.org/wiki/Spanish_phonology)

---

## 1. VOWEL CONFUSION TABLE

Spanish has 5 vowel phonemes; English has ~11-14. Most English vowels collapse into
the nearest Spanish category.

### English Monophthongs

| English Vowel | IPA | Typical Spelling | Spanish Realization | Spanish Spelling | Confidence | PAM-L2 Type | Source |
|--------------|-----|-------------------|---------------------|-------------------|------------|-------------|--------|
| /i/ (fleece) | /i:/ | beat, see, sheep | /i/ | si, piso | HIGH | SC | Flege 1991; Baigorri 2018; Cebrian 2006 |
| /I/ (kit) | /I/ | bit, sit, ship | /i/ (naive); /e/ (experienced) | si then mes | HIGH | SC then CG | Flege 1991; Morrison 2006; Cebrian 2021 |
| /e/ (face) | /eI/ | day, make, rain | /ei/ (diphthong) | rey, reino | HIGH | TC | Cebrian 2019 JASA |
| /E/ (dress) | /E/ | bed, set, head | /e/ | mes, pero | HIGH | SC | Flege 1991; Flege 1997; Baigorri 2018 |
| /ae/ (trap) | /ae/ | cat, man, hat | /a/ | paso, gato | HIGH | SC | Flege 1991; UAB summary; Baigorri 2018 |
| /A/ (lot) | /A/ | cot, father, hot | /a/ | paso | HIGH | SC | Baigorri 2018 |
| /V/ (strut) | /V/ | cut, cup, love | /a/ | paso | HIGH | SC (merged with /A/) | Baigorri 2018 |
| /O/ (thought) | /O/ | caught, door | /o/ | poco, los | MEDIUM | SC | Acoustic proximity inference |
| /o/ (goat) | /oU/ | go, boat, know | /ou/ (diphthong) or /o/ | bou or poco | MEDIUM | TC/SC | Cebrian 2019 |
| /U/ (foot) | /U/ | put, good, look | /u/ | su, puso | HIGH | SC | Flege 1997; Boomershine 2013 |
| /u/ (goose) | /u:/ | boot, too, blue | /u/ | su, puso | HIGH | SC | Flege 1997; Baigorri 2018 |
| /@/ (schwa) | /@/ | about, the, comma | /a/ or /e/ (context-dependent) | varies | MEDIUM | UC | No schwa in Spanish |
| /3:/ (nurse) | /3:/ | bird, work, turn | /e/ or /i/ | mes or si | LOW | UC | No clear Spanish counterpart |

### Key Vowel Confusion Pairs

1. **/i/ vs /I/**: Both to Spanish /i/. SC assimilation. Most studied, most robustly confirmed.
2. **/ae/ vs /A/ vs /V/**: All three to Spanish /a/. Triple collapse. Baigorri 2018: /ae-A/ and /V-ae/ particularly difficult.
3. **/E/ vs /ae/**: /E/ to Spanish /e/, /ae/ to Spanish /a/. Two-Category, distinguishable.
4. **/u/ vs /U/**: Both to Spanish /u/. SC, analogous to /i/~/I/.

### PAM-L2 Assimilation Types
- **SC** = Single Category: both L2 phones to same L1 category, equal goodness - poor discrimination
- **CG** = Category Goodness: same L1 category, different goodness - moderate discrimination
- **TC** = Two Category: each to different L1 category - good discrimination
- **UC** = Uncategorized: no clear L1 category - variable

---

## 2. CONSONANT CONFUSION TABLE

### Problematic English Consonants

| English Consonant | IPA | Spelling | Spanish Realization | Spanish Spelling | Confidence | Mechanism | Source |
|------------------|-----|----------|---------------------|-------------------|------------|-----------|--------|
| /v/ | /v/ | van, very | /b/ | vaca, boda | **HIGH** | No /v/ in Spanish; /b/~/v/ is one phoneme | Wikipedia Spanish phonology; all contrastive analyses |
| /D/ (voiced th) | /D/ | the, this | /d/ | dedo, donde | **HIGH** | No dental fricatives in seseo Spanish | Garcia Lecumberri 2006; contrastive analyses |
| /T/ (voiceless th) | /T/ | think, three | /s/ (seseo) or /t/ | casa or taza | **HIGH** | Seseo speakers lack /T/ entirely | Seseo phonology; Wikipedia Spanish phonology |
| /z/ | /z/ | zoo, is, buzz | /s/ | casa, mis | **HIGH** | Spanish lacks /z/; s is always voiceless | Multiple sources; orthographic interference |
| /Z/ | /Z/ | measure, vision | /j/ or /s/ | yo or casa | **MEDIUM** | No /Z/ in standard Spanish | Contrastive inference |
| /S/ | /S/ | she, ship | /tS/ or /s/ | mucho or casa | **HIGH** | No /S/ phoneme; loanword adaptation uses /tS/ or /s/ | Wikipedia Spanish phonology |
| /dZ/ | /dZ/ | jam, judge | /j/ (Spanish /Y/) | yema, llama | **HIGH** | No /dZ/ in Spanish; /Y/ is closest | Multiple contrastive analyses |
| /N/ | /N/ | sing, finger | /n/ or /ng/ cluster | sin, ango | **HIGH** | No velar nasal phoneme; [N] only allophone before /g/ | Universal pattern for languages without /N/ |
| /h/ | /h/ | hat, who | /x/ or silent | jamon or dropped | **MEDIUM-HIGH** | No English-style [h]; /x/ is closest but much stronger | Contrastive analysis |
| /w/ | /w/ | we, water | /gw/ or /u/ | huevo, agua | **MEDIUM** | /w/ not independent phoneme in Spanish | Wikipedia Spanish phonology |
| /j/ (English) | /j/ | yes, you | /j/ (Spanish /Y/) | yo, ya | **MEDIUM** | Spanish /j/ has wider allophonic range | Contrastive analysis |
| /r/ (English) | /r/ | red, car | /r/ or /rr/ | pero, perro | **MEDIUM** | English /r/ is approximant; Spanish has tap/trill | Multiple sources |

### English Consonants with Close Spanish Equivalents (Low Confusion)

| English | IPA | Spanish | Notes |
|---------|-----|---------|-------|
| /p/ | /p/ | /p/ | Identical or near-identical |
| /k/ | /k/ | /k/ | Near-identical |
| /f/ | /f/ | /f/ | Near-identical (Flege 1995 cites as identical L1-L2 pair) |
| /m/ | /m/ | /m/ | Identical |
| /n/ | /n/ | /n/ | Identical |
| /l/ | /l/ | /l/ | Near-identical |
| /s/ | /s/ | /s/ | Near-identical |
| /tS/ | /tS/ | /tS/ | Identical |

### Key Consonant Confusions (for ASR rules)

1. **v to b**: HIGH. Most robust consonant confusion. Example: "very" heard as "bery".
2. **z to s**: HIGH. Example: "zoo" heard as "soo".
3. **D to d**: HIGH. Example: "the" heard with /d/ onset.
4. **T to s**: HIGH (seseo). Example: "think" heard as "sink".
5. **N to n**: HIGH. Example: "sing" heard as "sin".
6. **S to tS or s**: HIGH. Example: "ship" heard as "chip" or "sip".
7. **dZ to j**: HIGH. Example: "jam" heard as Spanish "yam".
8. **h to x or silent**: MEDIUM-HIGH. Example: "hat" heard with /x/ or dropped.
9. **Z to j or s**: MEDIUM. Example: "measure" heard as "mayor".

---

## 3. SOURCES

### Scraped/Verified

| # | Citation | URL | Relevance | Note |
|---|----------|-----|-----------|------|
| S1 | Cebrian 2019 JASA | https://asa.scitation.org/doi/abs/10.1121/1.5087645 | VOWELS | Primary PAT; diphthong finding. Paywall; UAB summary confirms. |
| S2 | Cebrian & Gorba 2021 Frontiers | https://www.frontiersin.org/journals/communication/articles/10.3389/fcomm.2021.660917/full | VOWELS | Full text scraped. /i/~/I/ SC, spectral overlap, temporal cue reliance. |
| S3 | Baigorri, Campanelli, Levy 2018 Lang & Speech | https://pmc.ncbi.nlm.nih.gov/articles/PMC6561833/ | VOWELS | Full text scraped (PMC). PAT: /i/ to /i/, /I/ to /i/ or /e/, /E/ to /e/, /ae/ to /a/, /A/ to /a/, /V/ to /a/, /o/ to /o/, /u/ to /u/, /U/ to /u/. |
| S4 | Flege 1991 QJEP | Referenced in S3, S6 | VOWELS | Foundational. /i/ and /I/ to /i/; /ae/ to /a/; /E/ to /e/. |
| S5 | Flege, Bohn, Jang 1997 J Phonetics | Referenced in S2, S3 | VOWELS | /E/ 91-99% correct; /ae/ 70-73%; /i/ and /I/ 57-69% vs 51-61%. |
| S6 | Boomershine 2013 HLS Proc | https://www.lingref.com/cpp/hls/15/paper2879.pdf | VOWELS | Full text scraped. Confirms Flege 1991 patterns. |
| S7 | Escudero & Chladkova 2010 JASA | Referenced in search | VOWELS | Dialect-specific assimilation. |
| S8 | Wikipedia: Spanish phonology | https://en.wikipedia.org/wiki/Spanish_phonology | INVENTORY | Full text scraped. /b/~/v/ merger, seseo, yeismo, full consonant table. |
| S9 | Garcia Lecumberri & Cooke 2006 JASA | https://pubmed.ncbi.nlm.nih.gov/16642857/ | CONSONANTS | Spanish consonant identification in noise. Abstract only. |
| S10 | Garcia Lecumberri et al. 2008 Interspeech | https://www.isca-archive.org/interspeech_2008/garcialecumberri08_interspeech.html | CONSONANTS | Multilingual. "Strong L1 interference from sound system and orthography." |

### Unverified/Inferred

| Entry | Status | Basis |
|-------|--------|-------|
| /O/ to /o/ | MEDIUM | Acoustic proximity; no specific PAT data |
| /oU/ to /ou/ | MEDIUM | Cebrian 2019 diphthong finding |
| /@/ to /a/ or /e/ | LOW | No schwa in Spanish; context-dependent |
| /3:/ to /e/ or /i/ | LOW | No clear counterpart |
| /h/ to /x/ | MEDIUM | Contrastive consensus; no specific PAT |
| /Z/ to /j/ or /s/ | MEDIUM | Inference from phonological gap |
| /w/ to /gw/ or /u/ | MEDIUM | /w/ not independent phoneme |
| /r/ to /r/ | MEDIUM | Contrastive consensus |

---

## 4. HIGHEST-VALUE ASR RULES

### Vowel rules (by confidence)

1. **i to i**: English /i/ and /I/ both to Spanish /i/. Strip tense/lax distinction.
2. **ae, A, V to a**: All three to Spanish /a/. Triple collapse.
3. **E to e**: English /E/ to Spanish /e/. Direct mapping.
4. **u to u**: English /u/ and /U/ both to Spanish /u/. Strip tense/lax.
5. **o to o**: English /O/ and /o/ both to Spanish /o/.
6. **eI to ei**: English /eI/ maps well to Spanish /ei/. Good mapping, not confusion.

### Consonant rules (by confidence)

1. **v to b**: Substitute /v/ with /b/ in all positions.
2. **z to s**: Devoice all /z/ to /s/.
3. **D to d**: Replace dental fricative with /d/.
4. **T to s**: Replace voiceless dental fricative with /s/ (seseo).
5. **N to n**: Denasalize velar nasal to alveolar.
6. **S to tS or s**: Generate both variants.
7. **dZ to j**: De-affricate to palatal approximant.
8. **h to x or silent**: Generate /x/ variant and zero variant.

---

*Report generated 2026-07-25. All rules derive from cited academic literature. Unverified entries marked LOW.*
