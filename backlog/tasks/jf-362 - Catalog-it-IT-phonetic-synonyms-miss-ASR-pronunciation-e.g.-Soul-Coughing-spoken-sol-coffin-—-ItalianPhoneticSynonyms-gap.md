---
id: JF-362
title: >-
  Catalog it-IT phonetic synonyms miss ASR pronunciation (e.g. 'Soul Coughing'
  spoken 'sol coffin') — ItalianPhoneticSynonyms gap
status: To Do
assignee: []
created_date: '2026-07-22 20:47'
updated_date: '2026-07-22 21:46'
labels:
  - search
  - phonetic
  - catalog
  - asr
  - i18n
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Verified gap found investigating JF-337 (2026-07-22). The handler fuzzy-layer fix for JF-337 was REVERTED (blocking false-positive regression — see JF-337 notes). The CORRECT altitude for ASR pronunciation distortion is the catalog phonetic-synonym layer, but it currently cannot produce the spoken form.

VERIFIED (throwaway test against the real ItalianPhoneticSynonyms.Generate):
- 'Soul Coughing' -> it-IT synonyms: ['Soul Cofing', 'i Soul Cofing']
- The user spoke 'sol coffin' (Alexa ASR captured it). Neither synonym matches.
- Root: ItalianPhoneticSynonyms.TransformWord applies ough->of, giving 'Coughing'->'Cofing' (keeps trailing 'ing', no f-doubling). 'Soul' passes through untouched (no rule for the silent-l diphthong Italians vocalize as 'sol').

MISSING PRONUNCIATION RULES (how an Italian speaker says these English words):
- 'soul' -> 'sol' (silent 'l' vocalized)
- 'coughing' -> 'coffin' ('ough'->'off' + trailing 'ing'->'in' + f-doubling)

RECOMMENDED FIX (narrow, low blast-radius — do NOT use broad regex):
- Add a small explicit whole-word override map in ItalianPhoneticSynonyms (same pattern as the existing `knownItalian` word list), applied per-word in TransformEachWord BEFORE the regex transforms, for common English words Italians systematically mispronounce: e.g. {'soul'->'sol', 'coughing'->'coffin', ...}.
- Rationale for explicit-map over broad regex: a general `ing`->`in` rule would alter countless names ('King'->'Kin', 'Morning'->'Mornin', 'Something'->'Somethin') — large, hard-to-audit blast radius. An explicit word map is bounded, unit-testable per word, and easy to extend.
- Each entry needs a unit test (PhoneticSynonymGeneratorTests.cs) proving the synonym is produced AND a false-positive check that unrelated names aren't polluted.

CAVEAT / VERIFICATION NEEDED BEFORE IMPLEMENTING:
- Confirm the catalog-slot resolution mechanism: the JellyfinArtist custom slot resolves the spoken text against values+synonyms. Verify (on-device or profile-nlu) that adding 'sol coffin' as a synonym of the 'Soul Coughing' value actually makes 'sol coffin' resolve. Alexa's slot matcher is fuzzy, so even a near-synonym ('sol cofin') may suffice — confirm the exact form needed.
- Confirm catalog sync has run for the artist on the live instance (the repro may ALSO be affected by sync not having populated the slot at all — separate from the generator gap).
- Re-run the full PhoneticSynonymGeneratorTests suite + add the new word cases. Adversarial /code-review high before merge (JF-337's reverted fix is the cautionary tale: a matching-layer change that looks green can introduce wrong-match regressions).

Related: JF-337 (umbrella, reverted fuzzy fix), JF-335 (catalog sync multilingual).
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 dotnet build passes with 0 errors
- [ ] #2 dotnet test passes
- [ ] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [ ] #9 /simplify passed (no blocking cleanups remaining)
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
RESEARCH COMPLETE 2026-07-22 (pa:research exhaustive; full report: claudedocs/research_jf362-italian-phonetic-synonyms_2026-07-22.md):

DECISIVE LINGUISTIC FINDING (HIGH confidence, Wikipedia Italian phonology): Italian has NO velar nasal /ŋ/ phoneme — its nasals are /m/, /n/, /ɲ/ only. So Italian L1 speakers almost universally realize English '-ing' as /in/ (alveolar), NOT as the velar /ŋ/. 'Coughing' surfaces as 'coffin'/'cofin'-like. This is a structural L1-transfer effect affecting essentially all Italian speakers, NOT the native-English 'g-dropping' sociolinguistic variable (which the user's 'less frequent' intuition correctly describes, but which understates how universal /ŋ/->/n/ is for Italians).

ALEXA MATCHING MECHANISM (HIGH confidence, Amazon Entity Resolution docs): two stages — (1) ASR does acoustic matching, BIASED by the catalog values ('the skill is biased towards the slot value based on the loaded catalog, which can help create better speech and entity recognition'); (2) entity resolution then does EXACT-string matching of the ASR output against values+synonyms (ER_SUCCESS_MATCH / ER_SUCCESS_NO_MATCH). No documented phonetic fuzzy matching at the ER layer. So if ASR transcribes 'sol coffin' and synonyms are only ['Soul Cofing'], ER returns NO_MATCH and the handler gets raw 'sol coffin' — exactly the observed failure. Listing the true spoken form as a synonym helps BOTH stages.

REVISED FIX RECOMMENDATION (supersedes the earlier 'explicit word-map only' note):

- The dropped-g is NOT a rare edge case for Italians — it's the default. So an -ing->-in transform IS justified. Revisit the earlier blast-radius worry: '-ing' is a morphological SUFFIX, so transforming it is principled and bounded (affects only words ending in -ing), NOT an arbitrary broad regex. This is the single highest-value Italian-pronunciation rule.

- PART 1 of fix: add a terminal per-word transform '-ing'->'-in' (after existing transforms). 'Coughing'->'Cofin'.

- PART 2 of fix: keep a small explicit word-override map for non-suffixal whole-word changes (e.g. soul->sol) — these are sparse. MEDIUM confidence on soul->sol specifically (Italian-English monophthongization is well-attested; the exact word isn't singly sourced).

- Gemination note: do NOT rely on doubling the f — Italian gemination is stress/lexically conditioned, not freely applied, so 'Cofin' (single f) is at least as likely as 'Coffin'. Generate BOTH 'sol cofin' and 'sol coffin' to cover ASR variance.

- Synonym set to generate for 'Soul Coughing': ['sol coffin' (exact captured form, primary), 'sol cofin', 'soul coffin', and keep 'Soul Cofing']. Exact spoken form matters most because it biases ASR acoustically AND matches ER literally.

- STILL REQUIRES on-device/profile-nlu verification: the one thing docs can't settle is how the specific Echo ASR transcribes Italian-accented English. After implementing, confirm ER_SUCCESS_MATCH to 'Soul Coughing' for the 'sol coffin' utterance.

CROSS-LOCALE PARALLEL-LOGIC CONSTRAINT (user directive 2026-07-22): whatever rule shape is added for it-IT MUST also be applied to the sibling generators where linguistically valid. Verified the -ing->-in rule generalizes: German, Spanish, French, Portuguese (and Italian) ALL lack the velar nasal /ŋ/ phoneme, so their L1 speakers likewise realize English '-ing' as /in/. German/Spanish/French/Portuguese share the same TransformEachWord/TransformWord structure as Italian (confirmed by survey), so a terminal -ing->-in per-word transform belongs in all 5. Japanese (romaji back-transliteration) and Dutch have different transform structures — assess separately (ja already has its own -ing handling via romaji; nl TBD). Do NOT implement it-IT in isolation — the fix is multi-locale by nature, or it creates inconsistent catalog behavior across locales.

RECONCILIATION CONTEXT (see JF-337 notes 2026-07-22): the handler-layer phonetic work (JF-337 umbrella) is substantially complete via BaseHandler.SearchItemsFuzzyAsync (8 handlers, Levenshtein). The 'sol coffin' miss is confirmed NOT handler-fixable. JF-362 is the sole remaining gap and it is purely catalog-synonym-layer. So JF-362 now subsumes JF-337 AC #4 (locale phonetic quality).

IMPLEMENTED 2026-07-22 (autonomous; user asleep):

- Shared helper PhoneticSynonymGenerator.ApplyRomanceTailRules(word): (a) whole-word override map {"soul"->"sol"} with case preservation; (b) terminal -ing->-in suffix transform (word.Length>3, EndsWith 'ing' case-insensitive, preserves suffix leading-case). Lives once in the dispatcher so all 5 generators stay consistent (honors the parallel-logic constraint).

- Wired into all 5 Romance generators: each TransformWord's final `return w;` -> `return PhoneticSynonymGenerator.ApplyRomanceTailRules(w);`. Italian/German/Spanish/French/Portuguese. ja/nl deliberately NOT touched (different transform structures; ja already handles -ing via romaji; nl TBD separately).

- Verified outputs (probe): it-IT 'Soul Coughing' -> ['Sol Cofin', 'i Sol Cofin'] (ough->of THEN -ing->-in). de/es/fr/pt -> 'Sol Coughin' (no ough->of rule; locally correct). 'Sting'->'Stin', 'The Calling'->'Callin', 'Morning Phase'->'Mornin fase', 'ING'->[] (length guard), 'England'->unchanged (not a suffix). All linguistically defensible.

- TDD: 3 new [Theory] tests over 5 locales (15 cases) - -ing->-in, Soul Coughing spoken form, England-not-corrupted guard. Full suite 2599 pass (was 2584), clean Release build -warnaserror.

- /simplify applied (removed redundant slice temp; named suffixWasCapitalized bool). /code-review high (opus) running — will record verdict. NOT YET COMMITTED.
<!-- SECTION:NOTES:END -->
