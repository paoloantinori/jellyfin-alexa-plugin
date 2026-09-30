---
id: JF-684
title: >-
  JF-684 - catalog musician slot blocks intent selection for non-catalog values:
  bare artist names produce NO intent (fuzzy tiers voice-unreachable)
status: Done
assignee: []
created_date: '2026-09-30 16:53'
labels:
  - catalog
  - interaction-model
  - routing
  - bug
dependencies: []
references:
  - >-
    backlog/tasks/jf-666 -
    JF-666-artist-plays-stop-at-the-initial-5-track-page-the-precompute-fast-path-starves-the-continuation-and-the-fetchers-JF-358-query-shape-silently-returns-zero.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-09-30 same-turn, live-verified twice (morning device round: 3 attempts, zero requests; evening: profile-nlu A/B proof at Paolo's suggestion).

THE EVIDENCE (profile-nlu, it-IT, skill amzn1.ask.skill.33dfacd5):
- "suona la musica di pink" -> selectedIntent: NONE (no intent at all)
- "suona la cantante pink" -> NONE (the carrier form does not help)
- "suona la musica di norah jones" -> PlayArtistSongsIntent, musician resolves (exact catalog value)
- "suona la musica di pink floyd" -> PlayArtistSongsIntent (exact catalog value)

THE MECHANISM: PlayArtistSongsIntent's musician slot is the CATALOG-BACKED type JellyfinArtist (valueSupplier.valueCatalog, fed by CatalogSyncTask from the user's library + phonetic synonyms). When the spoken value matches NO catalog value (bare "pink" is not a synonym of any entry), the NLU does not select ANY intent for the utterance: on-device this surfaces as "nothing happens" (zero requests to the endpoint; verified in logs across 4 attempts). This REFINES the JF-642 finding: static custom types restrict only ER resolution (slot filling still captures raw text), but CATALOG types (valueSupplier) constrain NLU intent SELECTION itself when no value matches.

THE BIG CONSEQUENCE: the entire artist fuzzy-recall machinery (the 4-tier chain, ASR-truncation shapes like "crash" -> Crash Test Dummies, the JF-377/JF-420 containment gates) is UNREACHABLE BY VOICE for any spoken name that does not already match a catalog value: the request never arrives. The fuzzy tiers only fire for catalog-ADJACENT inputs (synonym drift like "pink floid") but not for partial/truncated names.

FIX DIRECTIONS TO EVALUATE (decision task, needs the catalog-generator knowledge):
(a) Catalog-side partial synonyms: CatalogSyncTask adds first-word/dropped-article synonyms for multi-word artist names ("pink" -> Pink Floyd's entry, "crash" -> Crash Test Dummies) gated on distinctiveness (length >= N, not a stop word in any locale, unique among the library's first words) - "the" and "led"-class words must never become synonyms. Risk: a bare word that prefixes TWO artists ("miles" -> Miles Davis + Miles Kane) becomes a two-value synonym collision - acceptable: ER returns no match and the raw text still arrives, the handler fuzzy chain takes over (which is the whole point).
(b) Verify (a) actually unblocks selection: re-profile "suona la musica di pink" after adding the synonym; if catalog types still refuse selection on non-exact matches, (a) is dead and the only fix is moving the musician slot off the catalog type for PlayArtistSongs (a JF-96.2-architecture decision needing the CLAUDE.md anti-pattern 10 guardrails).
(c) Minimum viable: document the platform behavior (catalog types gate intent selection) in CLAUDE.md near the JF-642 note and in the JF-96.2 architecture notes.

VERIFICATION: profile-nlu A/B (before/after) on the failing corpus: "pink", "crash", "beatles" (beatles presumably already works via the dropped-article synonym - verify), plus a two-artist prefix collision case; the existing NLU fixture suite; the e2e artist matrix unchanged.

## Findings (2026-09-30, worker session, worktree agent-aac7fdd7c1702e0ab)

### STEP 1 - live catalog state (artist catalog 6590add1, version 1188, synced 15:18 UTC; values fetched via GET /versions/1188/values, 1135 entries)

- "Pink Floyd" value carries synonyms ["i Pink Floyd", "Pinc Floyd", "Pinq Floyd"]: bare "pink" ABSENT. Claim CONFIRMED.
- "Norah Jones" value present (synonyms ["i Norah Jones"]). Claim CONFIRMED.
- "The Beatles" carries ONLY ["i Beatles"]: bare "beatles" ABSENT. The task's "presumably already works via the dropped-article synonym" assumption is REFUTED: ItalianPhoneticSynonyms drops "The" internally but emits only the "i <name>" Italian-article form, never the bare form. Consistent with the JF-510 skips (e2e "metti una canzone dei beatles" absorbed by PlaySongIntent; "suona i beatles" selected NO intent).
- "Crash Test Dummies" is NOT in the library (0 hits): "crash" stays a unit-level corpus case, untestable live.
- The velar-stop family already proves the bare-word shape end to end: "Cup" is a synonym of "Koop" and the e2e "suona brani dei cup" passes.

### STEP 2 - the spike (direction (a) PROVEN, live A/B, then fully reverted)

Method: fetched the live it-IT model (GET-modify-PUT, the plugin's own flow), built a throwaway catalog version identical to v1188 except "pink" appended to Pink Floyd's synonyms and "beatles" to The Beatles', hosted the payload at a secret gist (deleted after), created catalog version 1197, pinned the model to it, waited for the SUCCEEDED build.

- BEFORE (v1188): "suona la musica di pink" NO selectedIntent; "suona la musica di beatles" NO selectedIntent; "suona la cantante pink" NO selectedIntent. Controls "suona la musica di pink floyd" and "norah jones" route (both select PlaySongIntent on this carrier, musician ER_SUCCESS_MATCH).
- SPIKE (v1197): "suona la musica di pink" selects PlaySongIntent, musician ER_SUCCESS_MATCH ["P!nk", "Pink Floyd"] (the multi-artist shared-word case is real and behaves as designed: multiple ER values, the JF-420 flow arbitrates). "suona la musica di beatles" selects PlaySongIntent, ER_SUCCESS_MATCH ["The Beatles"]. "suona la cantante pink" selects PlayArtistSongsIntent directly. Controls unchanged.
- NEGATIVE control: "suona la musica di norah" (first word NOT added) still NO selectedIntent. The synonym is the only variable.
- AFTER revert (model PUT back to the pristine bytes pinning 1188, build SUCCEEDED, gist deleted): "pink" back to NO selectedIntent, "pink floyd" control still routes, live pin verified 1188. The throwaway version 1197 remains on the catalog as an unreferenced historical version (same residue as any sync run).

### STEP 3 - implementation (Alexa/Catalog/)

- `PartialNameSynonyms` (new, Alexa/Catalog/PartialNameSynonyms.cs): the ONE gate. First substantive word of a multi-word name (one leading stop-word skip, so "The Beatles" gives "Beatles"); rejected when shorter than 4 chars ("Led Zeppelin" yields nothing), when the candidate itself is a stop word, or when it carries non-letter characters ("P!nk floyd", "50 Cent"). Stop words via the NEW `KeywordMatcher.IsStopWordInAnyLocale` (union of all locale sets, single source with Tokenize). Appended AFTER the phonetic family so it never consumes the PerNameVariantCap; dedup case-insensitive; SlotValueHelper.Truncate applied.
- Wired at the two payload-assembly sites: `CatalogPayload.FromItems` + `CatalogSeedEnrichment.MergeSeeds`, gated to CatalogType.Artist ONLY (album first words deliberately untouched: the AlbumName anchors already steal artist queries, the JF-508 family, and that surface was not probed).
- Multi-artist shared first word: added to BOTH entries by design (the spike's P!nk + Pink Floyd match is the exact shape; ER returns multiple values, the JF-420 disambiguation flow arbitrates).
- Tests: Jellyfin.Plugin.AlexaSkill.Tests/Unit/PartialNameSynonymsTests.cs (accept/reject table incl. cross-locale stop words, the article skip, the length bar, non-letter rejection, dedup, the type gate, the payload assertions for Pink Floyd / The Beatles seed / shared-word pair / album no-op).

### Live rollout note

The code change reaches the deployed skill only on the NEXT catalog sync (weekly task or manual): each upload mints a new catalog version and the sync re-pins the model to it. Post-fix live verification (profile-nlu "pink" selecting) requires that sync to run first; nothing changes on the live skill until then. Model JSONs, slot types, NLU fixtures, and locale strings are all untouched (catalog payload only).

### Gate round (2026-09-30, worker transcript)

- /simplify (4 agents): applied 4 findings (the KeywordMatcher union check precomputed as a HashSet field like EnglishStopWords instead of a per-call scan; the AppendTo dedup now rides PhoneticSynonymGenerator.AppendDistinct, promoted private -> internal, which also unifies check-vs-add on the truncated string; the class tightened to internal with a private MinWordLength; the dedup unit test now seeds only the case variant so OrdinalIgnoreCase is genuinely pinned). Skipped with reasons: the span-based word extraction (weekly cold path, the Split idiom matches the four existing sites) and dropping the Truncate belt (the CLAUDE.md 140-char rule mandates the cap on catalog-value builders).
- Known recall gap (by design, the correct failure direction): "Al Green"-class names yield NO partial synonym because "al" is deliberately absent from the ar stop-word set (it is a name prefix, JF-389) and then fails the 4-char bar; bare "green" stays unselectable. A miss is strictly safer than a function word owning selection.
- Residual filed: the two parallel CatalogValue construction sites (FromItems + MergeSeeds) carry the enrichment with no structural tie; a third Artist-catalog site would regress silently. Filed as JF-689 (factory consolidation), NOT folded in here (smallest change; both sites are pinned by direct tests).
- Follow-up filed: post-sync live verification + JF-510 skip-family re-triage = JF-688.

### /code-review high round (2026-09-30, worker transcript; 5 findings)

- APPLIED (doc corrections, the strongest finding, chain verified in code before writing): the original class doc (and CLAUDE.md, and the shared-word test comment) claimed the JF-420 disambiguation flow arbitrates shared first words. FALSE on the canonical path: GetCanonicalValue returns authority.Values[0] only, the JF-659 contract feeds it to the artist search verbatim, and the exact-name hit auto-plays via the JF-420.1 equality bypass (the live spike's own slotValue="P!nk" for values [P!nk, Pink Floyd] matches this). All three docs now state the real mechanism; the behavioral gap (no prompt on multi-value ER matches) is filed as JF-690.
- APPLIED (doc): the ja/hi romaji stop-word entries ("made", "kara", "yori", "nado", "mein") make the union over-reject colliding Latin first words ("Made in Heights" -> nothing); documented in the class doc as a known miss in the safe direction, sets deliberately not forked.
- APPLIED (doc): DynamicEntityBuilder (turn-2+ in-session entities) deliberately gets NO partial word (it serves open-session ER, not intent selection, and its entry budget would evict at the margin); the scope boundary is now stated in the class doc.
- SKIPPED: NBSP/tab-split names yield no synonym (Split(' ') limitation). Pre-existing property of the whole synonym family (all four generators split on ASCII space the same way); a one-off fix here would diverge, a family fix is a drive-by.
- Finding 5 (the two construction sites) = JF-689, already filed; no new entry needed.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code touched)
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [ ] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change; catalog payload only)
- [ ] #7 E2E test added for new intent or handler logic (N/A: no new intent or handler logic; live A/B spike is the routing verification; post-sync round filed as JF-688)
- [ ] #8 Locale response strings added to all 17 locales (N/A: no user-facing strings changed)
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked)
<!-- DOD:END -->

Status: Done (2026-09-30, worker session).
