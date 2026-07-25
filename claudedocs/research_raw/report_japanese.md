# Japanese L1 to English L2 Perceptual-Assimilation Map for ASR Phonetic-Synonym Rules

## Purpose

Structured rule list for generating ASR phonetic synonyms: how Japanese L1 speakers perceive and produce English phonemes. Japanese has a constrained syllabary (5 vowels, ~14 consonant phonemes, moraic timing, no codas except /N/), so the confusion set is distinct from European languages.

## Theoretical Framework

- **PAM-L2** (Best & Tyler, 2007): Non-native sounds are perceptually assimilated to the most similar L1 category. Single-Category assimilation (both L2 sounds map to one L1 phoneme) predicts poor discrimination. Two-Category assimilation predicts good discrimination.
- **SLM** (Flege, 1995, 2003): Perceived phonetic dissimilarity between L1 and L2 sounds predicts category formation. Similar L2 sounds undergo equivalence classification and remain non-nativelike; dissimilar sounds can form new categories.
- **Key finding**: Japanese speakers assimilate English /r/ and /l/ to a single L1 category (Japanese alveolar flap /R/), a textbook Single-Category assimilation pattern (Best & Strange, 1992; Takagi, 1993).

## Japanese Phoneme Inventory (Target Side)

**Vowels (5)**: /a/, /i/, /u/ (phonetically unrounded high central-back, traditionally transcribed [M] or [u-umlaut]), /e/, /o/. Long vowels are phonemic: /aa/, /ii/, /uu/, /ee/, /oo/.

**Consonants (core ~14 phonemes)**:
- Stops: /p/, /b/, /t/, /d/, /k/, /g/
- Fricatives: /s/, /z/, /h/
- Affricates: /ts/, palatalized affricate before /i/, voiced palatal affricate
- Nasals: /m/, /n/
- Liquid: alveolar tap /R/ (ra-line, the famous merged r/l sound)
- Glides: /j/, /w/
- Special moras: /N/ (moraic nasal, place-assimilates), /Q/ (geminate obstruent)

**Allophonic notes**:
- /s/ before /i/ palatalizes (shi-line)
- /h/ before /u/ becomes bilabial fricative (fu), NOT English /f/
- /t/ before /u/ becomes [ts] (tsu); /t/ before /i/ becomes palatal affricate (chi)
- /d/ before /u/ becomes [dz]; /d/ before /i/ becomes voiced palatal affricate (ji)
- Word-final tap often deleted, preceding vowel lengthened (corner -> koonaa)

**Syllable structure**: (C)V(N|Q). No consonant clusters except geminates and nasal+homorganic. No coda consonants except /N/.

## Vowel Epenthesis Rules

When English has consonant clusters or coda consonants, Japanese inserts vowels to break them:

| Context | Epenthetic vowel | Example |
|---------|-----------------|----------|
| After most consonants (cluster break, coda) | /u/ | strike -> sutoraiku; bus -> basu |
| After /t/ or /d/ | /o/ (NOT /u/, because tu->tsu, du->dzu) | present -> puresento; strike -> sutoraiku (t+o) |
| After /ts/, /dz/ | /u/ | goods -> guzzu; guts -> gattsu |
| Word-final /k/ (older loans, 19th c.) | /i/ | cake -> keeki; steak -> suteeki |
| Word-final /r/ | (deleted, vowel lengthened) | corner -> koonaa |
| Word-final other consonants | /u/ | beer -> biiru; club -> kurabu |

## 1. VOWEL CONFUSION TABLE

Based on Strange et al. (1998) perceptual categorization task (24 Japanese listeners, 4 AmE speakers, /hVb/ syllables). Reported in Yazawa et al. (2023, Laboratory Phonology 14(1), doi:10.16995/labphon.6427). Goodness ratings 1-7 (7 = best exemplar of L1 category).

| English Vowel | Typical Spelling | Japanese Realization | Katakana | Romaji | Goodness | Confidence | Source |
|--------------|------------------|---------------------|----------|--------|----------|------------|--------|
| /i:/ tense high front | see, meat | long /ii/ | イー | ii | 6 | HIGH | Strange et al. 1998 / Yazawa et al. 2023 |
| /I/ lax high front | sit, gin | short /i/ | イ | i | 4 | MEDIUM | Strange et al. 1998 / Yazawa et al. 2023 |
| /E/ lax mid front | bed, sense | short /e/ | エ | e | 4 | MEDIUM | Strange et al. 1998 / Yazawa et al. 2023 |
| /ae/ low front | cat, map, cab | long /aa/ | アー | aa | 2 | LOW | Strange et al. 1998 / Yazawa et al. 2023; Tofugu |
| /A:/ low back | father, hot | long /aa/ | アー | aa | 5 | MEDIUM-HIGH | Strange et al. 1998 / Yazawa et al. 2023 |
| /V/ lax low back | cup, guts | short /a/ | ア | a | 4 | MEDIUM | Strange et al. 1998 / Yazawa et al. 2023 |
| /O:/ mid back | caught, off | short /o/ | オ | o | -- | MEDIUM | Tofugu |
| /oU/ mid back tense | go, snow | long /oo/ | オー | oo | -- | MEDIUM | Tofugu |
| /U/ lax high back | book, look | short /u/ | ウ | u | 3 | LOW-MEDIUM | Strange et al. 1998 / Yazawa et al. 2023 |
| /u:/ tense high back | food, blue | long /uu/ | ウー | uu | 5 | MEDIUM-HIGH | Strange et al. 1998 / Yazawa et al. 2023 |
| /@/ schwa | sofa, about | short /a/ | ア | a | -- | LOW | Tofugu |
| rhotic | bird, word | /a/ + epenthesis | アー | aa | -- | LOW | Tofugu |

**Key vowel collapse patterns** (for ASR synonym generation):
- /i:/ and /I/ both -> /i/ (different mora length in Japanese: /ii/ vs /i/)
- /E/ -> /e/ (clean map)
- /ae/ and /A:/ and /V/ and /@/ all -> /a/ (biggest collapse: 4-5 English vowels -> 1 Japanese vowel)
- /U/ and /u:/ both -> /u/ (different mora length: /uu/ vs /u/)
- /O:/ and /oU/ -> /o/ (different mora length: /oo/ vs /o/)
- Japanese /u/ is phonetically unrounded, NOT the rounded English /u/ or /U/ -- but for ASR purposes, romaji "u" covers both
- After /k/ or /g/ before /ae/, palatal glide /j/ is inserted preserving frontness (cabin -> kyabin)

## 2. CONSONANT CONFUSION TABLE

| English Consonant | IPA | Japanese Realization | Katakana Line | Romaji | Confidence | Notes | Source |
|------------------|-----|---------------------|---------------|--------|------------|-------|--------|
| /l/ | [l] | alveolar tap /R/ | ra-line | ra/ri/ru/re/ro | HIGH | MERGED with /r/. Single-Category PAM assimilation (Best & Strange 1992). /l/ rated as BETTER fit to Japanese /R/ than /r/ (Takagi 1993, Iverson et al. 2003). | Best & Strange 1992; Takagi 1993; Iverson 2003; Feng 2020; Kubozono 2015 |
| /r/ | [r] | alveolar tap /R/ | ra-line | ra/ri/ru/re/ro | HIGH | MERGED with /l/. Japanese /R/ is acoustically similar to AmE flap in "butter", not to English /r/ or /l/. | Best & Strange 1992; Price 1981; Kubozono 2015 |
| /T/ voiceless th | [T] | /s/ (palatalized before /i/) | sa/shi-line | sa/shi | HIGH | No interdentals in Japanese. "thin"->"sin" (marathon->marason). Before /i/: /Ti/ -> /Ci/ (shi). | Tofugu; denwasensei; Lombardi |
| /D/ voiced th | [D] | /z/ (palatalized before /i/) | za/ji-line | za/ji | HIGH | "the"->"za" (leather->rezaa). Before /i/: /Di/ -> voiced palatal affricate (ji). | Tofugu; Lombardi |
| /v/ | [v] | /b/ (or /w/ in some speakers) | ba-line | ba | HIGH | No /v/ in Japanese. Standard: /v/->/b/ (vanilla->banira, vase=base both->beesu). Some speakers use /w/. | Tofugu; denwasensei; Lombardi |
| /f/ | [f] | bilabial fricative [phi] | fu/fa-line | fu/fa | HIGH | Japanese /f/ is bilabial (both lips), NOT labiodental (lip+teeth). Close enough for substitution. | Tofugu; Wikipedia; denwasensei |
| /w/ | [w] | /w/ | wa-line | wa | HIGH | Direct map. But /w/+/U/ is difficult ("would"->"ood", /w/ omitted). | denwasensei |
| /j/ | [j] | /j/ | ya-line | ya | HIGH | Direct map. | -- |
| /S/ sh | [S] | palatalized [C] | shi/sha-line | shi/sha | HIGH | Before /i/ -> shi; before other vowels -> sha/shu/sho. | Wikipedia |
| /Z/ zh | [Z] | voiced palatal affricate | ji/ja-line | ji/ja | MEDIUM | No native /Z/. Borrowed via palatal affricate (measure->mejaaju). | Tofugu; Wikipedia |
| /tS/ ch | [tS] | palatal affricate | chi/cha-line | chi/cha | HIGH | Direct map to Japanese alveolo-palatal affricate. | -- |
| /dZ/ j | [dZ] | voiced palatal affricate | ji/ja-line | ji/ja | HIGH | Direct map. | -- |
| /N/ ng | [N] | /N/ + /g/ or /N/ alone | n+ga or n | ng/nga | MEDIUM | Word-final /N/ -> moraic nasal: singing->shingu. | Tofugu |
| /h/ | [h] | /h/ (bilabial fric before /u/) | ha/fu-line | ha/fu | HIGH | Before /u/ -> [phi] (fu). | Wikipedia |
| /s/ | [s] | /s/ (palatalized before /i/) | sa/shi-line | sa/shi | HIGH | Before /i/ palatalizes to shi. May hypercorrect /s/->/S/ before /I/ in rapid speech ("sin"->"shin"). | denwasensei; Wikipedia |
| /z/ | [z] | /z/ (palatalized before /i/) | za/ji-line | za/ji | HIGH | Before /i/ -> voiced palatal affricate (ji). | Wikipedia |
| /p/,/b/,/t/,/d/,/k/,/g/ | -- | same | pa/ba/ta/da/ka/ga | same | HIGH | Direct 1:1 maps. /p/ rare word-initially in native Japanese but common in loans. | Wikipedia |
| /m/, /n/ | -- | same | ma/na | same | HIGH | Direct 1:1 maps. | Wikipedia |
| word-final /r/ | [r] | deleted, vowel lengthened | -- | vowel-macron | HIGH | "corner"->koonaa. | Tofugu |

## 3. STRUCTURAL RULES FOR ASR SYNONYMS

### 3a. Consonant Cluster Epenthesis

Japanese breaks all non-permitted consonant clusters by inserting vowels:

1. **Coda consonant -> CV**: any word-final consonant (except /N/) gets a vowel after it.
   - "bus" -> /basu/, "club" -> /kurabu/, "strike" -> /sutoraiku/
2. **CC cluster -> CVCV**: two consonants in a row get a vowel between them.
   - "ski" -> /sukii/, "fry" -> /furai/, "glass" -> /gurasu/
3. **CCC cluster -> CVCVCV**: three consonants get two vowels.
   - "strike" -> /sutoraiku/ (s-t-r -> su-to-ra)
4. **Exception: nasal + homorganic OK**: /np/, /nt/, /mp/, /nd/, /nb/ are permitted.
   - "panda" -> /panda/, "present" -> /puresento/ (pr->pur, nt->nt, not nuto)
5. **Exception: geminates OK**: doubled identical consonants are permitted.
   - "bed" -> /beddo/, "taxi" -> /takushii/
6. **Exception: affricates are single consonants**: /ts/ = tsu (one mora), so "guts" -> /gattsu/ (one vowel added, not two).

### 3b. Epenthetic Vowel Selection

- Default: /u/ (after p, b, k, g, m, n, r/l, s, sh, h, f, ch, j, w, y)
- After /t/: /o/ (because /tu/ -> /tsu/ in Japanese; /to/ preserves /t/)
- After /d/: /o/ (same reason: /du/ -> /dzu/)
- After /ts/, /dz/: /u/
- Word-final /k/ in older loans: /i/ (cake -> keeki, steak -> suteeki)
- Word-final /r/: deleted, vowel lengthened

### 3c. /ae/ -> /a/ + /j/ Insertion After Velars

After /k/ and /g/, the frontness of /ae/ is partially preserved by inserting the palatal glide /j/:
- "cabin" -> /kyabin/
- "gamble" -> /gyamburu/
- "cat" -> /kyatto/
- This does NOT happen after other consonants: "map" -> /mappu/ (NOT myappu)

### 3d. /s/ -> /S/ Hypercorrection Before /I/

In rapid speech, Japanese speakers may insert /S/ before /i/ even where English has /s/:
- "sin" -> /shin/, "sill" -> /shill/, "medicine" -> /medishin/
This is because /si/ is not a valid Japanese sequence (it becomes shi).

## 4. SOURCES

1. **Yazawa, K., Konishi, T., Whang, J., Escudero, P., & Kondo, M. (2023).** "Spectral and temporal implementation of Japanese speakers' English vowel categories: A corpus-based study." *Laboratory Phonology*, 14(1). doi:10.16995/labphon.6427 -- Contains the Strange et al. (1998) perceptual categorization table (Table 1) with goodness ratings. Tests SLM(-r), PAM-L2, and L2LP predictions against 102 Japanese speakers. https://www.journal-labphon.org/article/id/6427/

2. **Strange, D., Akahane-Yamada, R., Kubo, R., Trent-Brown, A. J., Nishi, K., & Jenkins, J. J. (1998).** "Perceptual assimilation of American English vowels by Japanese listeners." *Journal of the Acoustical Society of America*, 104(3), 311-344. doi:10.1006/jpho.1998.0078 -- The primary perceptual categorization data (Table 1 reproduced in Yazawa et al. 2023). 24 Japanese listeners categorized 8 AmE monophthongs into Japanese categories with goodness ratings.

3. **Best, C. T. & Strange, W. (1992).** "Effects of phonological and phonetic factors on cross-language perception of approximants." *Journal of Phonetics*, 20, 305-330. -- Established Single-Category assimilation of English /r/ and /l/ to Japanese /R/.

4. **Feng, Z. (2020).** "Effects of Identification and Pronunciation Training Methods on L2 Speech Perception and Production: Training Adult Japanese Speakers to Perceive and Produce English /r/-/l/." *Working Papers in TESOL & Applied Linguistics*, Teachers College, Columbia University. -- Reviews PAM and SLM predictions for /r/-/l/; confirms English /l/ is perceived as more similar to Japanese /R/ than English /r/ (citing Takagi 1993, Iverson et al. 2003). https://files.eric.ed.gov/fulltext/EJ1288481.pdf

5. **Kubozono, H. (2015).** Japanese has no phoneme categorized as /l/; Japanese /r/ is phonetically an alveolar flap or stop. Cited in Feng (2020).

6. **Lombardi, L. (2015).** "Japanese Loanword Phonology." Tofugu. -- Practical consonant and vowel substitution patterns with katakana examples. Sources: Irwin, M. *Loanwords in Japanese*; Tsukimura, N. *An Introduction to Japanese Linguistics*. https://www.tofugu.com/japanese/japanese-loanword-phonology/

7. **Wikipedia.** "Japanese phonology." -- Canonical reference for the Japanese phoneme inventory (consonant table, vowel system, moraic structure, allophonic rules, phonotactic constraints). https://en.wikipedia.org/wiki/Japanese_phonology

8. **denwasensei.com.** "What English sounds are difficult for the Japanese?" -- Pedagogical summary of /r/-/l/, /s/-/S/, /f/-/v/, /T/-/s/ confusion patterns. LOW academic confidence (blog, no citations), used only for supplementary pattern confirmation. https://denwasensei.com/what-english-sounds-are-difficult-for-the-japanese/

9. **Takagi, N. (1993).** Cited in Feng (2020): found English /l/ rated higher than English /r/ in goodness-of-fit to Japanese /R/.

10. **Iverson, P. et al. (2003).** Cited in Feng (2020): confirmed higher goodness-of-fit ratings for English /l/ than English /r/ to Japanese /R/ using synthesized stimuli.

11. **Aoyama, K. et al. (2004).** Cited in Feng (2020): Japanese speakers identify English /r/ more successfully than English /l/.

## 5. CONFIDENCE SUMMARY

- **HIGH**: Rules backed by multiple peer-reviewed sources (Strange et al. 1998, Best & Strange 1992, Kubozono 2015, Wikipedia). These include: /r/-/l/ merge, /T/->/s/, /D/->/z/, /v/->/b/, /f/->bilabial, all vowel mappings with goodness ratings, epenthesis rules.
- **MEDIUM**: Rules backed by one peer-reviewed source or strong descriptive source (Tofugu, which cites Irwin and Tsukimura). These include: /Z/->palatal affricate, /N/ adaptation, /j/ insertion after velars before /ae/, /s/->/S/ hypercorrection.
- **LOW**: Rules from pedagogical/non-academic sources only (denwasensei). These include: /w/ omission before /U/ in "would". Used only for supplementary confirmation, not as primary evidence.
- **UNVERIFIED**: Not included. Every rule above has at least one identifiable source.
