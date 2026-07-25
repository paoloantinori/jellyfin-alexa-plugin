# Dutch L1 Speakers Perception/Production of English Phonemes

## Purpose

Structured mapping of how Dutch L1 speakers perceive and produce English phonemes, derived from the Perceptual Assimilation Model (PAM / PAM-L2; Best 1995, Best and Tyler 2007), the Speech Learning Model (SLM; Flege 1995), the L2LP model (Escudero 2005), and data-driven error-frequency studies. Intended as input for ASR phonetic-synonym rules (Alexa skill context).

## Key Theoretical Note

Dutch and English are both West Germanic, sharing large portions of their consonant inventories. However, Dutch has no dental fricatives, no /w/ (realized as labiodental approximant /v/), a different /r/ (uvular fricative/trill vs. English alveolar approximant), and fewer vowel contrasts (no /ae/, no /3:r/, tense/lax pairs merged in some regions). The L2LP model predicts that sounds acoustically closest to a single L1 category (new scenario) are hardest to learn. PAM predicts poor discrimination for contrasts that map onto a single L1 category.

---

## 1. Vowel Confusion Table

| English Vowel | Typical Spelling | Dutch Realization | Dutch Phoneme | Dutch Spelling | Confidence | Source |
|---|---|---|---|---|---|---|
| /ae/ (cat, bat) | a (+ consonant) | /E/ or /a:/ | /E/, /a:/ | e.g. kat, bad | HIGH | Cucchiarini et al. 2011 (Table 1: /ae/ realized as /E/); Van den Doel 2006 (BAT error); Collins and Mees 2003; Broersma 2005 |
| /E/ (bed, head) | e, ea | /E/ | /E/ | bed, zet | HIGH (close match) | Cucchiarini et al. 2011; Simon et al. 2012 |
| /I/ (sit, bit) | i (+ consonant) | /I/ | /I/ | sit, pit | HIGH (close match) | Cucchiarini et al. 2011 (Table 4: /I/ confusions with /i:/) |
| /i:/ (seat, beat) | ee, ea, ie | /i/ | /i/ | ziek, lied | MEDIUM | Cucchiarini et al. 2011 (Table 4: /i:/ confused with /I/ at 6.0%); /i:/ vs /I/ distinction difficult when Dutch /I/-/i/ contrast is small in some regions |
| /A:/ (part, heart) | ar, a(+rC) | /a:/ | /a:/ | laat, kaas | MEDIUM | speakingvoices.com; Cucchiarini et al. 2011 (Table 4: /A:/ confused with /O:/ at 3.1%) |
| /O:/ (caught, port) | au, augh, or | /O:/ | /O:/ | koorts, door | MEDIUM | Cucchiarini et al. 2011 (Table 1: /O:/ before /r/ becomes /O/) |
| /Q/ (not, lot) | o (+ consonant) | /O/ | /O/ | kot, bos | HIGH | Cucchiarini et al. 2011 (Table 1: /Q/ + fortis -> /O/ as in good); Dutch lacks /Q/-/V/ distinction |
| /V/ (nut, love) | u, o | /O/ or /a:/ | /O/, /a:/ | pot, zat | HIGH | speakingvoices.com; Dutch has no /V/; Cucchiarini et al. 2011 (Table 4: /V/ confused with /O:/ at 2.2%) |
| /U/ (book, put) | oo, u | /u/ | /u/ | boek, put | HIGH | Cucchiarini et al. 2011 (Table 1: /U/ -> /u:/ as in soup); Van den Doel 2006 (PULL error) |
| /u:/ (pool, food) | oo, u | /u/ | /u/ | pool, boer | HIGH (close match) | Cucchiarini et al. 2011; Dutch /u/ is phonetically close |
| /@/ (sofa, about) | a, o, e (unstressed) | Full vowel (over-articulated) | /E/, /a/, /I/ | various | HIGH | speakingvoices.com; Cucchiarini et al. 2011 (Table 4: /@/ realized as full vowel) |
| /3:/ (bird, word) | ir, ur, er | /O:/ or /@r/ | /O:/ | woord, dorp | MEDIUM | speakingvoices.com; Dutch lacks /3:/ but has /@r/ sequences |
| /eI/ (day, make) | a_e, ay, ai | /e:/ | /e:/ | zee, de | MEDIUM | Dutch /e:/ is a monophthong or slight diphthong; English /eI/ is a clear diphthong |
| /aU/ (now, house) | ow, ou | /AU/ | /AU/ | nou, kauw | HIGH (close match) | Both languages have similar /aU/ diphthongs |
| /OI/ (boy, choice) | oy, oi | /OI/ | /OI/ | boy, ooit (close) | MEDIUM | Dutch /OEy/ is phonetically similar but slightly different starting point |
| /I@/ (near, beer) | ear, eer | /i:r/ | /i/ | bier, ier | MEDIUM | Cucchiarini et al. 2011 (Table 1: /I@/ before /r/ becomes /i/) |
| /e@/ (hair, square) | air, are | /E:r/ | /E:/ | haar, baar | MEDIUM | Dutch /E:/ is close; pre-/r/ allophone |
| /U@/ (tour, cure) | our, ure | /y:r/ | /y/ | uur, tuur | MEDIUM | Dutch uses /y/ (front rounded) where English uses back /U@/; Cucchiarini et al. 2011 (Table 4: /U@/ confused with /O:/) |

### Vowel PAM-L2 Scenarios

- /ae/ vs /E/: Single-category assimilation (both map to Dutch /E/). Predicted VERY DIFFICULT. Confirmed by Broersma 2005, Van den Doel 2006.
- /I/ vs /i:/: Single-category or category-goodness assimilation. Difficult when Dutch regional /I/-/i/ contrast is small.
- /Q/ vs /V/: Single-category assimilation (both map to Dutch /O/). Predicted VERY DIFFICULT.
- /U/ vs /u:/: Single-category assimilation (both map to Dutch /u/). Predicted DIFFICULT. Confirmed by Van den Doel 2006 (PULL error).
- /A:/ vs /O:/: Two-category assimilation in some regions, single in others. Moderately difficult.

---

## 2. Consonant Confusion Table

| English Consonant | Position | Dutch Realization | Dutch Phoneme | Dutch Spelling | Confidence | Source |
|---|---|---|---|---|---|---|
| /T/ (think, breath) | word-initial, medial | /t/ or /s/ | /t/, /s/ | denk, snel | HIGH | Cucchiarini et al. 2011 (Table 5: /T/ confused with /t/ 75x, /s/ 45x); Van den Doel 2006 (THIN error); Collins and Mees note /t/ has overtaken /s/ as most common substitution |
| /D/ (this, breathe) | word-initial, medial, function words | /d/ or /z/ | /d/, /z/ | deze, dan | HIGH | Cucchiarini et al. 2011 (Table 5: /D/ is THE most frequent consonant error at 8.0%, confused with /d/ 1443x, /t/ 36x); Van den Doel 2006; Collins and Mees |
| /w/ (wine, water) | word-initial | /v/ | /v/ | wijn, water (Dutch w = [v]) | HIGH | Van den Doel 2006 (WINE error); Collins and Mees 2003; Cucchiarini et al. 2011 (Table 2: /w/ -> /v/); Wikipedia Dutch Phonology |
| /v/ (vine, very) | word-initial | /f/ (devoiced) or /v/ | /f/, /v/ | fiets, vlaag | HIGH | Cucchiarini et al. 2011 (Table 2: /v/ -> /f/); Wikipedia Dutch Phonology (/v/ can devoice and merge with /f/) |
| /z/ (zoo, easy) | word-final | /s/ (final devoicing) | /s/ | huis, wijs | HIGH | Cucchiarini et al. 2011 (Table 5: /z/ confused with /s/ 887x, 5.7%); Van den Doel 2006 |
| /d/ (dog, bad) | word-final | /t/ (final devoicing) | /t/ | bed -> [bEt] | HIGH | Cucchiarini et al. 2011 (Table 5: /d/ confused with /t/ 313x); Van den Doel 2006 (BED error) |
| /g/ (go, get) | word-initial | /x/ or /G/ | /x/, /G/ | goed, gang | HIGH | Dutch /g/ is not a native phoneme; /x/ and /G/ are the fricative realizations. Wikipedia Dutch Phonology |
| /r/ (red, run) | all positions | uvular fricative [R] or uvular trill [r] | /r/ | rood, rijden | HIGH | Van den Doel 2006 (RED error: uvular r is completely unacceptable); Collins and Mees 2003; Wikipedia Dutch Phonology |
| /l/ (light, full) | coda (word-final) | pharyngealized dark [l] | /l/ | vol, bal | MEDIUM | Van den Doel 2006 (FULL error); Collins and Mees 2003 (Dutch dark /l/ involves pharyngealisation rather than velarisation) |
| /N/ (sing, ring) | before /g/ | /Nk/ sequence | /Nk/ | zingen -> [zINk@] | MEDIUM | Dutch does not always simplify /Ng/ to /N/; -ng spelling often pronounced as [Nk] in careful speech |
| /h/ (house, hit) | word-initial | /H/ or dropped | /h/ or zero | huis, heet | MEDIUM | speakingvoices.com; Dutch /h/ is typically a voiced glottal fricative /H/ which differs from English voiceless /h/ |
| /S/ (ship, action) | all positions | /C/ (alveolo-palatal) or /s/ | /sj/ -> [C] | sjaal, show | MEDIUM | Wikipedia Dutch Phonology ([S, Z] are not native phonemes); /sj/ and /zj/ clusters assimilated to [C] or [S] |
| /tS/ (chip, match) | all positions | /tC/ (from /tj/) | /tj/ -> [tC] | tiener, check | MEDIUM | Wikipedia Dutch Phonology; Cucchiarini et al. 2011 (Table 2: /tS/ -> /sj/) |
| /dZ/ (jam, edge) | all positions | /dZ/ (from /dj/) | /dj/ -> [dZ/] | jagen | MEDIUM | Wikipedia Dutch Phonology; Cucchiarini et al. 2011 |
| /t/ (tip, cat) | word-initial | /t/ (unaspirated) | /t/ | tip, kat | HIGH | Wikipedia Dutch Phonology (Dutch stops are unaspirated unlike English); sounds flat but not misidentified |
| /p/, /k/ | word-initial | /p/, /k/ (unaspirated) | /p/, /k/ | pen, kan | HIGH | Same aspiration issue as /t/; sounds non-native but not misidentified |

---

## 3. Dutch Phoneme Inventory

### Consonants (from Wikipedia: Dutch Phonology)

| Phoneme | IPA | Example | Notes |
|---|---|---|---|
| /p/ | [p] | pen | Unaspirated (unlike English [p_h]) |
| /b/ | [b] | been | Fully voiced |
| /t/ | [t] | ten | Unaspirated (unlike English [t_h]) |
| /d/ | [d] | den | Fully voiced |
| /k/ | [k] | kat | Unaspirated |
| /g/ | [g] | goal | NOT a native phoneme; loanword only |
| /f/ | [f] | fiets | |
| /v/ | [v] or [f] | vlaag | Often devoiced to [f] in Netherlands |
| /s/ | [s] | sok | |
| /z/ | [z] or [s] | zeep | Often devoiced to [s] in Netherlands |
| /x/ | [x] or [X] | goed | Hard g; velar/uvular fricative. In north, /x/ and /G/ merge |
| /G/ | [G] or [x] | gaan | Voiced velar fricative; merges with /x/ in north |
| /H/ | [H] or [h] | huis | Voiced glottal fricative; some speakers use voiceless [h] |
| /m/ | [m] | man | |
| /n/ | [n] | noot | |
| /N/ | [N] | zing | |
| /v/ (labiodental approximant) | [v_] | wat | Labiodental approximant (English /w/ maps here) |
| /l/ | [l] | land | Dark/pharyngealized in coda |
| /j/ | [j] | jaar | Palatal approximant |
| /r/ | [r], [R], [R_] | rood | Uvular fricative/trill (north) or alveolar trill; NEVER English alveolar approximant |

### Vowels (from Wikipedia: Dutch Phonology)

| Phoneme | IPA | Example | Quality |
|---|---|---|---|
| /I/ | [I] | pit | Near-close near-front unrounded (lax) |
| /i/ | [i] | pijl | Close front unrounded (tense) |
| /y/ | [y] | fuut | Close front rounded (tense) |
| /Y/ | [Y] or [@] | put | Close-mid central rounded (lax); often merged with /@/ |
| /u/ | [u] | hoed | Close back rounded (tense) |
| /E/ | [E] | pet | Open-mid front unrounded (lax) |
| /e:/ | [e:] ~ [EI] | peer | Close-mid front unrounded (tense; often diphthongized) |
| /2:/ | [2:] ~ [2Y] | deur | Close-mid front rounded (tense; often diphthongized) |
| /@/ | [@] | de | Mid central (schwa) |
| /O/ | [O] | pot | Open-mid back rounded (lax) |
| /o:/ | [o:] ~ [@U] | boot | Close-mid back rounded (tense; often diphthongized) |
| /a:/ | [a:] | laat | Open central unrounded (tense) |
| /A/ | [A] | man | Open back unrounded (lax) |

### Diphthongs

| Phoneme | Example | Notes |
|---|---|---|
| /EI/ | ijl | Fronting |
| /AU/ | hou | Backing |
| /OEy/ | ui | Fronting+rounding |

---

## 4. Sources

1. **Cucchiarini, C., Neri, A., Strik, H. (2011).** Error selection for ASR-based English pronunciation training in My Pronunciation Coach. *Interspeech 2011*, Florence. https://www.isca-archive.org/interspeech_2011/cucchiarini11_interspeech.pdf -- PRIMARY QUANTITATIVE SOURCE. 226 Dutch university students, 520,000 phonemes. Tables 1, 2, 4, 5 provide ranked frequency data.

2. **Van den Doel, R. (2006).** How problematic is it to be Dutch? PhD thesis, Utrecht University. https://dspace.library.uu.nl/server/api/core/bitstreams/be921440-3b9b-4932-9e8a-41ac5cf48f50/content -- Large-scale native-speaker evaluation. Table 2.1 describes all phonemic errors. BAT, BED, COLOUR, PULL, THAT, THIN, WINE, VAN, OFF errors discussed.

3. **Collins, B. and Mees, I. (2003).** The Phonetics of English and Dutch (5th ed.). Leiden: Brill. -- Canonical textbook. Referenced by all other sources. /D/->/d/ described as most common and persistent Dutch error. /T/->/t/ now overtaking /T/->/s/. Uvular /r/ completely unacceptable.

4. **Gussenhoven, C. and Broeders, A. (1997).** English Pronunciation for Student Teachers (2nd ed.). Groningen: Wolters-Noordhoff. -- Substitution tables (p. 113, 171).

5. **Simon, E., Debaene, M. and Van Herreweghe, M. (2012).** The perception of English front vowels by North Holland and Flemish listeners. *Journal of Phonetics* 40, 280-288. -- L2LP predictions tested for Dutch learners of English.

6. **van Leussen, J.-W. and Escudero, P. (2015).** Learning to perceive and recognize a second language: the L2LP model revised. *Frontiers in Psychology* 6:1000. https://pmc.ncbi.nlm.nih.gov/articles/PMC4523759/ -- L2LP model scenarios described. Broersma 2005 (bet/bat for Dutch) cited as single-category assimilation exemplar.

7. **Best, C.T. (1995).** A direct realist view of cross-language speech perception. In W. Strange (ed.), Speech Perception and Linguistic Experience. York Press. -- Original PAM model.

8. **Best, C.T. and Tyler, M.D. (2007).** Nonnative and second-language speech perception. In O.-S. Bohn and M.J. Munro (eds.), Language Experience in Second-Language Speech Perception. Springer. -- PAM-L2 extension.

9. **Flege, J.E. (1995).** Second language speech learning: Theory, findings, and problems. In W. Strange (ed.), Speech Perception and Linguistic Experience. York Press. -- SLM framework.

10. **Escudero, P. (2005).** Linguistic Perception and Second Language Acquisition. PhD thesis, Utrecht University. LOT. -- L2LP model.

11. **Wikipedia: Dutch Phonology.** https://en.wikipedia.org/wiki/Dutch_phonology -- Dutch consonant and vowel inventory, /r/ variants, /v/-/f/ merger, unaspirated stops, /g/ non-native status.

12. **SpeakingVoices.com (2025).** Dutch Speaker: Common English Pronunciation Mistakes. https://www.speakingvoices.com/blog/dutch-english-pronunciation-mistakes/ -- NON-ACADEMIC. Used only for corroboration. Confidence from this source alone is LOW.

---

## 5. Priority Confusions for ASR Phonetic-Synonym Rules

1. /D/ -> /d/ (8.0% error rate; most common and persistent; function words: the, that, they, this)
2. /T/ -> /t/ (dominant substitution per Collins and Mees)
3. /w/ -> /v/ (Dutch w = English /v/; wine/vine minimal pair confusion)
4. /v/ -> /f/ (devoicing; especially Netherlands Dutch)
5. /z/ -> /s/ (final devoicing; 5.7% error rate)
6. /d/ -> /t/ (final devoicing; 1.3% error rate)
7. /ae/ -> /E/ (no Dutch /ae/; notoriously persistent per Collins and Mees)
8. /Q/ -> /O/ (no Dutch /Q/; merged with /O/)
9. /V/ -> /O/ (no Dutch /V/; confused with /O:/)
10. /U/ -> /u/ (no Dutch /U/; pull/pool error; all Dutch-speaking students per Collins and Mees)
11. /g/ -> /x/ (no native Dutch /g/; /x/ or /G/ substitution)
12. /r/ -> uvular (completely unacceptable per Collins and Mees; no English alveolar approximant equivalent)
13. /@/ -> full vowel (schwa over-articulation; weak-form failure)
14. /A:/ -> /O:/ (backing of /A:/; part/port confusion)
15. Final devoicing of all obstruents (systematic Dutch phonological process)
