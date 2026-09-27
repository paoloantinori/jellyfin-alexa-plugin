---
id: JF-646
title: >-
  JF-646 - catalog-side katakana synonyms: Latin artist/album names get kana
  variants in the ja catalog upload so NLU selection resolves naturalized ja
  voice (the routing-layer complement to JF-643)
status: To Do
assignee: []
created_date: '2026-09-27 08:01'
updated_date: '2026-09-27 10:49'
labels:
  - catalog
  - i18n
  - ja-JP
  - nlu
dependencies:
  - JF-642
  - JF-643
references:
  - >-
    backlog/tasks/jf-642 -
    JF-642-ja-JP-noun-qualified-artist-carriers-stolen-by-PlayByGenre-the-free-text-genre-capture-even-canonical-pre-existing-forms-route-to-genre-artist-intent-unreachable-by-noun-voice.md
  - >-
    backlog/tasks/jf-643 -
    JF-643-katakana-query-values-never-match-Latin-library-names-script-gap-in-fuzzy-phonetic-search-naturalized-ja-JP-artist-and-genre-requests-all-end-not-found.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-27 same-turn from the JF-643 altitude review: the routing-layer complement to the query-side romanizer.

THE GAP: query-side romanization (JF-643) fixes everything AFTER the request reaches a handler, but NLU SELECTION is untouchable from there. JF-642's own evidence: en-US/hi-IN select PlayArtistSongs because catalog-backed musician ER wins selection (JellyfinArtist catalog with phonetic synonyms), while ja's katakana loses selection to the free-text genre slot (and will keep losing to OTHER competition even after JF-642 fixes the genre steal). For a ja user, artist selection needs the catalog to carry KATAKANA forms of the library's Latin names so AMAZON.Musician (or a catalog-backed type) resolves them NLU-side.

THE WORK: extend the catalog synonym pipeline with a Japanese generator: Latin name -> katakana rendering(s), coverage-oriented (one-to-many is fine; extra near-miss synonyms are harmless to ER, the JF-362 principle), capped like the Romance generators (per-name cap, SlotValueHelper.Truncate, the 140-char limit). The reverse direction of KatakanaRomanizer: syllabary-driven Latin->kana with vowel insertion; long-vowel mark handling (drop, matching the romanizer's contraction so クイーン and Queen round-trip); germinate/nasal edge cases. Wire into PhoneticSynonymGenerator's dispatch (the JapanesePhoneticSynonyms.cs file is catalog-side Latin-to-Latin today: si->shi etc.; the kana generator complements it for the ja-JP locale's catalog upload). Watch the ar-SA catalog-wiring exclusion (JF-543) and the CatalogSyncLocales default.

INTERPLAY: JF-642 (custom genre type) + this task together close naturalized ja voice end-to-end: JF-643 bridges search, JF-642 stops the genre steal, this makes NLU selection resolve katakana artist names via ER so the request reaches the right intent with a resolved entity. Verify with profile-nlu ja probes: クイーン carriers select PlayArtistSongs with ER_SUCCESS_MATCH on the JellyfinArtist authority (the en-US/hi-IN shape from the JF-642 investigation).

VERIFICATION BAR: profile-nlu selection probes as above; catalog upload logs show the kana variants appended; no regression in the other 16 locales' catalog builds (the generator is ja-locale-scoped).
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
- [ ] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-27 PRIORITY RAISED to high + scope note: the JF-642 live battery refuted the custom-type steal fix at NLU selection (single-word katakana still selects PlayByGenre deterministically; the evidence is in JF-642's notes), so THIS task is now the demonstrated routing-layer fix for the steal, not a complement: with katakana variants in the ja JellyfinArtist catalog, AMAZON.Musician ER resolves naturalized ja artist names and wins selection the way en-US/hi-IN do today. The description's interplay sentence (JF-642 stops the genre steal) is superseded by JF-642's battery note.

2026-09-27 IMPLEMENTED (worktree branch, awaiting orchestrator merge):
- Generator: new `KatakanaSynonymGenerator` (Alexa/Catalog/KatakanaSynonymGenerator.cs), spelling-driven Latin->mora walk. Decision table: vowel teams map to ONE long mora (ee/ea->イー, oo->ウー, oa->オー, au/aw->オー) or two plain morae (ai/ay->エイ, oi/oy->オイ, ou->オウ); silent-e and vowel-r lengthen (er/ir/ur->アー, or->オー); moraic nasals for n/m before consonants; epenthetic vowel o for t/d, u for the rest (Bob->ボブ, card-class->ド); geminates ONLY for ck, doubled letters (except final zz), and word-final t/d/k/g after a SINGLE-SHORT vowel (bit->ビット vs beat->ビート, floyd keeps フロイド because a diphthong's second mora blocks it); final s voiced ズ after vowel/y (ビートルズ, ジョーンズ), plain ス after consonant, merged away after th (スミス); dark-l only word-final/-les (ビートルズ, シンプル); final y is /aɪ/ in monosyllables (スカイ) and /i/ elsewhere (シティ, パーティ); x unfolds k+s (マックス); soft c/g before e/i/y; qu emits ク with the vowel as its own mora (クイーン).
- SYLLABARY REUSE: the kana come from KatakanaRomanizer's own tables in reverse via a new `KatakanaRomanizer.TryKatakana(romaji)` (reverse map built from Syllables+Digraphs with a canonical-priority rule: full-size modern unigram > small/obsolete kana > loanword digraph). One source of truth for both directions; the generator's Latin-letter digraph rules (th/gh/ea/silent-e) key on spelling and have no kana-side counterpart, which is why they live beside, not inside, the syllabary.
- ROUND-TRIP POLICY: each long vowel is exactly ONE ー (matching the romanizer's contraction), so Queen->クイーン->kuin and クィーン->kuin (the small-kana form folds to the same romaji; it is composed directly since no クィ key exists in the syllabary, deliberately NOT adding one to avoid changing query-side romanization). Pinned: exact round-trips + the Double Metaphone equality for Queen.
- COVERAGE variants per name (the JF-362 principle): standard form, article-dropped form for 'The ' names (ザ・ビートルズ + ビートルズ), ONE whole-name ambiguous-shape alternate (ea as /iː/ vs /ɛ/, v as b-line vs ヴ-line, letter-adjacent ti as チ vs ティ, クイ vs クィ), and a space-joined form (ザ ビートルズ) since ja ASR writes pauses/prefixes either way.
- WIRING POINT: PhoneticSynonymGenerator's `"ja"` dispatch arm now calls a private combiner (kana FIRST as the device-captured forms, then JapanesePhoneticSynonyms' romaji, distinct, Take(5) = the Romance cap). This single point covers CatalogPayload.FromItems, CatalogSeedEnrichment.MergeInto (the Queen static seed gets クイーン too), and DynamicEntityBuilder's turn-2+ entities. The other 16 switch arms are untouched (pinned by a 16-locale no-kana test); ar-SA's catalog exclusion (JF-543) is upstream of the generator and untouched; no config flags.
- Known accepted near-misses (coverage, not bugs): Soul Coughing -> ソウル・コウフィング (extra ウ), Nirvana -> ナーバナ (unstressed ir r-colors; the ヴ-alt ナーヴァナ is closer), AC/DC/P!nk stylizations produce junk-but-harmless synonyms, Simon -> シモン (サイモン). Extra variants are harmless to ER (one hit suffices).
- VERIFICATION TAIL (unit level, both TFMs green): 4629 tests per TFM (baseline ~4525 + the new KatakanaSynonymGeneratorTests: exact table, round-trips incl. DM equality, well-formedness battery over 20 names, cap/order, 16-locale no-kana pin, CatalogPayload integration incl. the 140-char truncate); JapanesePhoneticSynonymsTests' dispatch pin updated from equality to superset+contains-kana. LIVE TAIL (orchestrator, post-merge): catalog sync on ja, then profile-nlu probe クイーン の曲を再生して must select PlayArtistSongs with ER_SUCCESS_MATCH on the JellyfinArtist authority.

2026-09-27 REVIEW ROUND applied (coordinator gate, five findings, one commit "review(jf646)"):
1. No-yoon double vowel: the ConsumeConsonantPlusY fallback now skips the folded vowel unit (return pos+1+consumed); Myers -> マース and Ryerson -> ラーソン pinned (was マーアース-class double emission for every Xye/Xyi shape with no yoon key).
2. DYNAMIC BUDGET POLICY (the chosen fix): the ja DYNAMIC entity arm is capped at the kana-first pair (DynamicEntityBuilder.JaDynamicSynonymCap = 2, applied BEFORE the 1+synonyms cost arithmetic; the kana forms are ordered first so Take keeps them). The FULL kana coverage stays in the catalog upload, which has no shared budget. Arithmetic named in the code comment: shared budget 90, 85 after the last-played reserve, spent across artists then albums then series then audiobooks at 1+synonyms per value; uncapped, ~14 kana-enriched artists at cost 6 consume 84 of 85 and the turn-2+ surface silently loses albums/series. Pinned by DynamicEntityBuilderJaBudgetTests: a ja build over 20 kana-enriched artists + 5 albums still carries the album values and every artist value carries at most 2 dynamic synonyms.
3. Word-final 'ey' team added to ParseVowelUnit: /iː/ final or checked (Whitney -> ウィトニー, Buckley -> ブックリー), /eɪ/ before a vowel (beyonce-class). Was ウィトネユ-class junk before.
4. Reverse-syllabary tie-break made deterministic by construction: modern full-size unigram priority 0; non-canonical unigram (small kana, obsolete ヰヱヲ, historical ヂヅ) priority 1; digraph 2 + first-kana priority. No romaji value has two same-priority candidates, so Dictionary enumeration order can never decide. Pinned: TryKatakana("ji")==ジ, ("zu")==ズ, ("ja")==ジャ.
5. Kana-containing names skip the romaji arm's Latin tail-rules (guard inside JapanesePhoneticSynonyms, matching the generator's ContainsKana entry-guard shape): 'クイーン' no longer becomes 'クイーンu' via AppendFinalVowel. The ja dispatch returns empty for kana and mixed names (pinned).
<!-- SECTION:NOTES:END -->
