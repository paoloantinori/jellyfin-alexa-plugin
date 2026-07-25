# L2-English Perceptual Assimilation Map: Brazilian Portuguese (pt-BR) Speakers

Researched 2026-07-25. For use in phonetic-synonym generation for Alexa ASR.

## Theoretical Framework

- **SLM (Flege 1995)**: L2 sounds similar to existing L1 categories are assimilated (equivalence classification) and fail to form new categories. Only acoustically "new" L2 sounds get new categories.
- **PAM-L2 (Best & Tyler 2007)**: Two L2 sounds assimilated to a single L1 category (single-category assimilation) yield poor discrimination. Two-category assimilation yields good discrimination.
- Both models predict difficulty from L1-L2 phonetic similarity, confirmed for BP-English vowels and consonants across multiple studies.

## Brazilian Portuguese Phoneme Inventory (Target Side of Map)

### Oral Vowels (7 phonemes)

| Phoneme | Height | Backness | Typical BP Spelling | Example |
|---------|--------|----------|---------------------|--------|
| /i/ | Close | Front | i, e (stressed) | **i**dade, s**e**nte |
| /e/ | Close-mid | Front | e, ei | s**e**de, p**ei**xe |
| /E/ (open-mid) | Open-mid | Front | e (stressed open) | c**e**ca, b**e**la |
| /a/ | Open | Central | a | c**a**sa, f**a**la |
| /O/ (open-mid) | Open-mid | Back | o (stressed open) | c**o**rpo, p**o**rta |
| /o/ | Close-mid | Back | o, ou | **o**uro, **ou**ro |
| /u/ | Close | Back | u | c**u**ra, **u**va |

### Nasal Vowels (5 phonemes)

| Phoneme | Oral counterpart | Typical BP Spelling | Example |
|---------|------------------|---------------------|---------|
| /I~/ | /i/ + nasal | in, im (nasalized) | l**im**po, t**im** |
| /E~/ | /E/ + nasal | en, em (nasalized) | t**em**po, p**en**sa |
| /a~/ | /a/ + nasal | an, am (nasalized) | c**am**po, **an**da |
| /O~/ | /O/ + nasal | on, om (nasalized) | c**om**pra, b**om** |
| /u~/ | /u/ + nasal | un, um (nasalized) | m**um**do, **um** |

### Consonants (17 phonemes)

| Phoneme | BP allophones (Brazilian) | Typical Spelling | Example |
|---------|--------------------------|-------------------|--------|
| /p/ | [p] (unaspirated) | p | **p**ato |
| /b/ | [b], prevoiced [-b] | b | **b**ola |
| /t/ | [t] before a/o/u; [tS] before i/e | t | **t**ato, **t**ia (=[tSa]) |
| /d/ | [d] before a/o/u; [dZ] before i/e | d | **d**ado, **d**ia (=[dZa]) |
| /k/ | [k] | c, qu | **c**asa, **qu**ase |
| /g/ | [g] | g, gu | **g**ato, **gu**erra |
| /f/ | [f] | f | **f**ato |
| /v/ | [v] | v | **v**ela |
| /s/ | [s] | s, ss, c, c (before e/i) | **s**apo, **c**asa, **c**ede |
| /z/ | [z] | z, s (between voiced) | **z**ero, ca**s**a |
| /S/ | [S] | x, ch | **x**adrez, **ch**ave |
| /Z/ | [Z] | j, g (before e/i) | **j**ogo, **g**elo |
| /m/ | [m] | m | **m**ao |
| /n/ | [n] | n | **n**ada |
| /N/ | [j~] (nasal palatal approximant) in BP | nh | ni**nh**o |
| /l/ | [l] or [w] (word-final/syllable-final in BP) | l | **l**ata, Bra**s**il (=[w]) |
| /R/ | [x], [h], [X] (word-initial/singleton in BP); [R] (tap between vowels) | r, rr | **r**ato (=[x/h]), ca**r**ro (=[x]), ca**r**o (=[R]) |
| /w/ | [w] | u (semivowel) | sa**u**de |
| /j/ | [j] | i (semivowel) | pa**i** |

**Key BP allophonic notes for ASR mapping:**
- BP /t/ palatalizes to [tS] before /i/ and /e/: "tinha" = [tSiN a]
- BP /d/ palatalizes to [dZ] before /i/ and /e/: "diga" = [dZiga]
- BP /R/ in word-initial position is [x], [h], or [X] (NOT an alveolar sound). Between vowels it is a tap [R].
- BP word-final /l/ velarizes to [w]: "Brasil" = [bRa'ziw]
- BP has NO /T/ (voiceless interdental) or /D/ (voiced interdental)
- BP /R/ word-initial realization [h] is acoustically close to English /h/
- BP drops word-initial orthographic "h" entirely (silent letter)

---

## 1. VOWEL CONFUSION TABLE

English has ~11 stressed vowel phonemes. BP has 7 oral vowels. Under SLM/PAM-L2, multiple English vowels assimilate to the same BP category (single-category assimilation), causing poor discrimination.

### Front Vowels

| English Vowel | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|---------------|---------------|----------------|------------|-------------|------------|--------|
| /i:/ (tense high front) | "bead", "see" | /i/ (close front) | /i/ | i, e (stressed) | HIGH | Rauber et al. 2005 (Interspeech) - F1/F2 overlap; Rato & Carlet 2020 (Ilha Desterro) - confirmed assimilation |
| /I/ (lax high front) | "bit", "sit" | /i/ (close front) | /i/ | i, e (stressed) | HIGH | Rauber et al. 2005: 93.83% discrimination for /i:/-/I/ (good, because some F1/F2 distance), but 50% of participants produced /I/ with F1/F2 identical to BP /i/. Rato & Rauber 2015: /I/ assimilated to BP /i/, no distinction made. |
| /eI/ (diphthong) | "bait", "say" | /e/ or /i/ (inverted) | /e/ or /i/ | e, ei | MEDIUM | Rauber et al. 2005: 56.25% distinguished /I/-/eI/ in production; tendency to invert positions. Slight diphthongization facilitates perception (85%+ discrimination involving diphthongs). |
| /E/ (open-mid front) | "bed", "head" | /E/ (open-mid front) | /E/ | e (open stressed) | HIGH | Rauber et al. 2005: IL F1/F2 for /E/ (848/2074) close to BP /E/ (713/1669). Direct phonetic match. |
| /ae/ (near-open front) | "bad", "cat" | /E/ (open-mid front) | /E/ | e (open stressed) | HIGH | Rauber et al. 2005: IL F1/F2 for /ae/ (832/2153) close to BP /E/. Discrimination /E/-/ae/ = 44% (very poor, single-category assimilation). Rato & Rauber 2015: /ae/ assimilated to BP /E/. |

### Central Vowels

| English Vowel | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|---------------|---------------|----------------|------------|-------------|------------|--------|
| /V/ (open-mid back unrounded) | "bud", "cup" | /a/ (open central) or /O/ | /a/ or /O/ | a, o (open) | HIGH | Rato & Carlet 2020: Flege (1995) confirmed /V/-/a/ discrimination failure. F1/F2 proximity: English /V/ sits near BP /a/ and /O/ in acoustic space. |
| /a:/ (open back unrounded) | "father", "box" | /a/ (open central) | /a/ | a | HIGH | Rauber et al. 2005: /O/-/a:/ discrimination = 29.5% (extremely poor). Both map to BP /O/ for most participants. Rato & Rauber 2015: /a:/ and /V/ both assimilated to BP categories with poor discrimination. |
| /3:/ (central) | "bird", "word" | /e/ or /a/ (varies) | /e/ or /a/ | e, a | MEDIUM | Rauber et al. 2005: /U/-/3:/ discrimination = 71% (moderate difficulty). /3:/-/a:/ = 20.83% (worst discriminated pair). The rhotic coloring makes this complex. |

### Back Vowels

| English Vowel | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|---------------|---------------|----------------|------------|-------------|------------|--------|
| /oU/ (diphthong) | "boat", "go" | /o/ (close-mid back) | /o/ | o, ou | MEDIUM | Rauber et al. 2005: produced too high, close to BP /o/. Discrimination /U/-/oU/ = 85.67% (diphthongization helps). |
| /O:/ (open-mid back) | "aw", "thought" | /O/ (open-mid back) | /O/ | o (open stressed) | HIGH | Rauber et al. 2005: IL F1 for /O:/ (501) close to BP /O/ (328) but somewhat different. /O/-/a:/ = 29.5% (very poor discrimination with /a:/). |
| /U/ (lax high back) | "book", "put" | /u/ (close back) | /u/ | u | HIGH | Rauber et al. 2005: 56.25% produced /U/ and /u/ with nearly identical F1; 25% made no distinction. Discrimination /u/-/U/ = 54.33% (poor). Rato & Rauber 2015: /U/ assimilated to BP /u/. |
| /u:/ (tense high back) | "boot", "blue" | /u/ (close back) | /u/ | u | HIGH | Rauber et al. 2005: IL F1/F2 for /u:/ (355/1327) close to BP /u/ (328/994). Direct phonetic match. |

### Vowel Collapse Summary (Single-Category Assimilation Pairs)

These English vowel pairs collapse into a single BP category and are poorly discriminated:

| English Pair | Collapsed to BP | Discrimination Rate | Confidence |
|--------------|----------------|---------------------|------------|
| /E/ - /ae/ ("bed" - "bad") | /E/ | 44% | HIGH |
| /O:/ - /a:/ ("thought" - "father") | /O/ (or /a/) | 29.5% | HIGH |
| /u:/ - /U/ ("boot" - "book") | /u/ | 54.33% | HIGH |
| /V/ - /a:/ ("cup" - "father") | /a/ | poor (confirmed Flege 1995, Rato & Rauber 2015) | HIGH |
| /3:/ - /a:/ ("bird" - "father") | /a/ | 20.83% | HIGH |

**Well-discriminated pairs** (two-category or new-category assimilation):
- /i:/ - /I/ ("bead" - "bit"): 93.83% (large F1/F2 distance helps)
- Pairs involving diphthongs (/eI/, /oU/): 85%+ (diphthongization is a cue BP uses)

---

## 2. CONSONANT CONFUSION TABLE

### Interdental Fricatives (Absent from BP)

| English Consonant | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|-------------------|---------------|----------------|------------|-------------|------------|--------|
| /T/ (voiceless interdental "th") | "think", "three", "breath" | [t] (alveolar stop) or [s] (alveolar fricative) or [f] (labiodental) | /t/ or /s/ or /f/ | t, s, f | HIGH | Reis 2006 (UFSC thesis): systematic substitution pattern exists. /T/ is LESS difficult than /D/. Primary substitution: [t]. Secondary: [s], [f]. Osborne 2008: interdental [tS] produced as overcompensation. |
| /D/ (voiced interdental "th") | "this", "the", "bathe" | [d] (alveolar stop) or [z] (alveolar fricative) or [v] (labiodental) | /d/ or /z/ or /v/ | d, z, v | HIGH | Reis 2006: /D/ is MORE difficult than /T/. Primary substitution: [d]. Secondary: [z], [v]. Feature overlap: /D/ shares [+voiced, +fricative] with /z/, and [+voiced, +dental] with /d/. |

### /h/ and /r/ Confusion

| English Consonant | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|-------------------|---------------|----------------|------------|-------------|------------|--------|
| /h/ (voiceless glottal fricative) | "head", "house", "hot" | Variable. BP has no /h/ phoneme (orthographic "h" is always silent). May be realized as [h] (borrowing from BP /R/ allophone) or confused with /r/ via spelling. | None (phoneme gap) | h is silent in BP | MEDIUM | Pedagogical sources (spelo.app 2024): H/R inversion is systematic. BP word-initial /R/ = [h] acoustically, and English /h/ is orthographic but absent from BP phonology, causing "head"/"red" confusion. Osborn (Benjamins, JSLP 1.2): L2 perception of initial /h/ and /r/ by BP speakers (could not access - Cloudflare block). Wikipedia: BP /R/ = [x], [h], or [X] word-initially. |
| /r/ (alveolar approximant) | "red", "run", "right" | [x] or [h] (velar/glottal fricative, via BP /R/) | /R/ | r, rr | HIGH | Wikipedia Portuguese Phonology: BP /R/ word-initial = [x], [h], or [X]. Osborne 2008: BP /R/ is a guttural, not alveolar. The English alveolar/retroflex approximant /r/ has no BP equivalent. |

### Stop Consonants (VOT Differences)

| English Consonant | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|-------------------|---------------|----------------|------------|-------------|------------|--------|
| /p/ (voiceless aspirated bilabial) | "pat", "pen" | [p] (unaspirated) | /p/ | p | HIGH | Osborne 2026 (DELTA): BP /p/ is unaspirated; English /p/ is aspirated. VOT boundaries differ. Flege SLM: single-category assimilation. |
| /t/ (voiceless aspirated alveolar) | "ten", "take" | [t] unaspirated before a/o/u; [tS] (palatalized) before i/e | /t/ | t | HIGH | Wikipedia Portuguese Phonology: BP /t/ = [tS] before /i/, /e/. Osborne 2008: confirmed palatalization transfer. English "team" may surface with [tS]. |
| /d/ (voiced alveolar, short-lag VOT) | "den", "dog" | [d] before a/o/u; [dZ] (palatalized) before i/e | /d/ | d | HIGH | Wikipedia: BP /d/ = [dZ] before /i/, /e/. English "did" may surface as [dZId]. |
| /b/ (voiced bilabial, short-lag VOT) | "bat", "bed" | [b] (prevoiced, negative VOT in BP) | /b/ | b | HIGH | Osborne 2026 (DELTA): BP /b/ has prevoicing (negative VOT ~ -80ms); English /b/ has short-lag VOT (~13ms). |
| /k/ (voiceless aspirated velar) | "cat", "keep" | [k] (unaspirated) | /k/ | c, qu | HIGH | Same VOT issue as /p/, /t/. |
| /g/ (voiced velar) | "got", "give" | [g] | /g/ | g, gu | HIGH | Same VOT issue as /b/, /d/. |

### Fricatives and Affricates

| English Consonant | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|-------------------|---------------|----------------|------------|-------------|------------|--------|
| /S/ (voiceless postalveolar fricative) | "ship" | [S] | /S/ | x, ch | HIGH | Direct match. |
| /Z/ (voiced postalveolar fricative) | "measure" | [Z] | /Z/ | j, g (before e/i) | HIGH | Direct match. |
| /tS/ (voiceless postalveolar affricate) | "chip" | [tS] | /tS/ (allophone of /t/) | t (before i/e) | HIGH | BP /t/ before /i/, /e/ produces [tS] allophonically. |
| /dZ/ (voiced postalveolar affricate) | "judge" | [dZ] | /dZ/ (allophone of /d/) | d (before i/e) | HIGH | BP /d/ before /i/, /e/ produces [dZ] allophonically. |
| /s/ (voiceless alveolar fricative) | "sip" | [s] | /s/ | s, c | HIGH | Direct match. |
| /z/ (voiced alveolar fricative) | "zip" | [z] | /z/ | z, s (voiced) | HIGH | Direct match. |
| /f/ (voiceless labiodental fricative) | "fan" | [f] | /f/ | f | HIGH | Direct match. |
| /v/ (voiced labiodental fricative) | "van" | [v] (word-final: 62% devoiced) | /v/ | v | MEDIUM | Direct match in inventory. Osborne 2008: 62% devoicing of final /v/. Some BP dialects merge /v/ with /b/. |

### Liquids and Nasals

| English Consonant | Example Words | BP Realization | BP Phoneme | BP Spelling | Confidence | Source |
|-------------------|---------------|----------------|------------|-------------|------------|--------|
| /l/ (velarized alveolar lateral, "dark l") | "ball", "feel" | [l] (clear, non-velar) or [w] (word-final) | /l/ | l | HIGH | Wikipedia: BP /l/ is clear in all positions. Word-final /l/ vocalizes to [w]: "Brasil" = [bRa'ziw]. English "milk" may surface as [mIwk]. |
| /r/ (retroflex approximant) | "red", "car" | [x]/[h] (word-initial) or [R] (intervocalic tap) | /R/ | r, rr | HIGH | No BP equivalent for English retroflex /r/. BP /R/ is guttural [x/h/X] word-initially, tap [R] between vowels. |
| /R/ (alveolar tap, AmE "butter") | "butter", "water" | [R] (alveolar tap) | /R/ (intervocalic) | r (between vowels) | HIGH | BP has the tap [R] between vowels. English flapped /R/ maps directly. |
| /m/ (bilabial nasal) | "man" | [m] | /m/ | m | HIGH | Direct match. |
| /n/ (alveolar nasal) | "net" | [n] | /n/ | n | HIGH | Direct match. |
| /N/ (velar nasal) | "sing" | [N] or [n] + epenthesis | /N/ (restricted) | n (before velars) | MEDIUM | BP has /N/ but only before velar consonants. Word-final /N/ may be [n] or [n] + [i]: "sing" = [sINgi]. Osborne 2008: "things" = [tINks]. |
| /N/ (palatal nasal) | "onion" | [j~] (nasal palatal approximant) | /N/ | nh | HIGH | Direct match. |

### Word-Final Consonant Modifications

| English Pattern | BP Modification | Confidence | Source |
|-----------------|-----------------|------------|--------|
| Final voiceless stops (/p/, /t/, /k/) | Epenthetic [i] vowel: "hot" = [hotSi] | HIGH | Osborne 2008: BP prohibits stop consonants in final position. Spelo.app: most iconic BP accent feature. |
| Final voiced obstruents (/d/, /v/, /z/, /dZ/) | Devoicing: [d] to [t], [v] to [f], [z] to [s] | HIGH | Osborne 2008: 43-100% devoicing rates. /v/ = 62%, /z/ = 77%, /dZ/ = 100%. |
| Final -ed suffix | Pronounced as extra syllable [id]: "walked" = [wOkid] | HIGH | Osborne 2008, Spelo.app: BP speakers pronounce every written letter. |
| Final /m/, /n/ | Nasalized vowel, consonant dropped: "time" = [taI~], "sun" = [sA~] | MEDIUM | Spelo.app: nasal vowel substitution. Consistent with BP nasal phonology. |

---

## 3. SOURCES

### Academic (Peer-Reviewed)

1. Rauber, A.S., Escudero, P., Bion, R.A.H., & Baptista, B.O. (2005). The interrelation between the perception and production of English vowels by native speakers of Brazilian Portuguese. *Interspeech 2005*. https://www.isca-archive.org/interspeech_2005/rauber05_interspeech.pdf

2. Rato, A. & Carlet, A. (2020). Second language perception of English vowels by Portuguese learners: The effect of stimulus type. *Ilha do Desterro*, 73(3), 205-226. https://www.scielo.br/j/ides/a/V4FGY6dRnHkYPtLz6jHMH5p/?lang=en

3. Reis, M. (2006). The perception and production of English interdental fricatives by Brazilian EFL learners [Master's thesis]. Universidade Federal de Santa Catarina. https://repositorio.ufsc.br/bitstream/handle/123456789/89154/228272.pdf

4. Osborne, D.M. (2008). Systematic differences in consonant sounds between the interlanguage phonology of a Brazilian Portuguese learner of English and Standard American English. *Ilha do Desterro*, 55, 111-132. https://www.redalyc.org/articulo.oa?id=478348693006

5. Osborne, D.M. (2026). From language-specific perceptual strategies to phonetic drift: Perception of stops in L2 English and L1 Brazilian Portuguese. *DELTA*, 42(1). https://www.scielo.br/j/delta/a/dTFfgfSpzkbNvKTwtVwpdSN/?lang=en

6. Osborn, D.M. (year unknown). The L2 perception of initial English /h/ and /r/ by Brazilian Portuguese speakers. *Journal of Second Language Phonology*, 1(2). https://www.benjamins.com/catalog/jslp.1.2.02osb -- Could not access (Cloudflare block). Referenced by search snippet.

7. Flege, J.E. (1995). Second language speech learning: Theory, findings, and problems. In W. Strange (Ed.), *Speech perception and linguistic experience* (pp. 233-277). York Press.

8. Best, C.T. & Tyler, M.D. (2007). Nonnative and second-language speech perception. In O.-S. Bohn & M.J. Munro (Eds.), *Language experience in second language speech learning* (pp. 13-34). Benjamins.

### Reference (Non-Academic)

9. Wikipedia contributors. Portuguese phonology. *Wikipedia*. https://en.wikipedia.org/wiki/Portuguese_phonology -- BP phoneme inventory, allophonic rules.

10. Spelo.app (2024). English pronunciation mistakes Brazilian Portuguese speakers make. https://spelo.app/blog/english-pronunciation-portuguese-speakers -- Pedagogical, not peer-reviewed. Used only where consistent with academic sources.

### Confidence Legend
- **HIGH**: Multiple peer-reviewed studies, consistent findings, or direct phoneme inventory mismatch.
- **MEDIUM**: Single study or pedagogical source consistent with theory but not experimentally confirmed for BP specifically.
- **LOW**: Unverified inference. (None in this report.)
