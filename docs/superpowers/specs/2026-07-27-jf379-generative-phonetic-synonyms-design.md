# JF-379 Design Spec: data-driven generative phonetic synonyms for catalog slot filling

**Date**: 2026-07-27
**Status**: Design (awaiting review)
**Related**: JF-379 (task), JF-381 (query-time Double Metaphone, shipped), JF-362 (existing hand-rolled per-locale generators)
**Research**: `claudedocs/research_jf379_generative_phonetic_libs_2026-07-27.md` (exhaustive, primary-source), `claudedocs/research_phonetic_rules_2026-07-25.md` (per-L1 phonological data)

## Problem

The catalog sync populates per-locale SMAPI catalog slots (`JellyfinArtist`, `AlbumName`) with the user's library names so Amazon's on-device ASR recognizes them when spoken in the user's locale. Foreign-accent pronunciation drift (an Italian speaker saying "Koop" heard as "cup"/"coop") defeats recognition. Today the plugin uses a HAND-ROLLED per-locale rule engine (`PhoneticSynonymGenerator` + 7 locale generators) that the maintainers will not keep extending by hand, case by case, and cannot hand-author for all languages.

**User requirement (binding):** a SYSTEMATIC, GENERATIVE approach that generalizes across the plugin's supported L1s without per-case hand rules. Library, port, or algorithm-over-data are all acceptable; "add another substitution rule" is not.

**Scope (binding):** only the 7 L1s the plugin already has generators for: Italian (it), German (de), Spanish (es), French (fr), Portuguese (pt), Japanese (ja), Dutch (nl). NOT all languages.

## Research verdict (why this design)

Exhaustive research (primary sources) confirmed: **there is no turnkey library that GENERATES L1-transfer spelling variants.** The L1-interference layer is theory (Flege SLM, Best PAM-L2) + research corpora (L2-ARCTIC), not code. What DOES exist as tools/data:
- **Forward g2p (text -> IPA)** is mature: Epitran (MIT, 153+ languages, forward-only). A Go port exists, proving portability.
- **PHOIBLE 2.0** (CC-BY) provides distinctive feature vectors for every phoneme in 2000+ languages, IPA-pivoted, so phoneme similarity between any two languages is computable from data.
- **Inverse g2p (IPA -> spelling) does not exist** (one-to-many, unbounded) and must be built per target L1's orthography.

So the build is a composite: forward g2p + data-driven interference + inverse orthography. This is the only path that meets the requirement and scales across L1s without per-case hand rules.

## Architecture

Three stages, run at catalog-sync time per (library name, target L1):

```
library name (English text)
   |  [Stage 1: forward English g2p]
   v
English IPA phonemes
   |  [Stage 2: per-L1 phonological interference, data-driven from PHOIBLE]
   v
L1-adapted IPA phonemes (how the L1 speaker would realize the English phonemes)
   |  [Stage 3: inverse orthography, per-L1 IPA->spelling]
   v
variant spelling(s) -> catalog slot synonym list (bounded, device-forms first)
```

### Stage 1: forward English g2p

**Verified fact:** CMUdict (public domain, ~135k entries) contains MORE artist-name coverage than expected: spot-check found `koop`, `bush`, `soul`, `coughing`, `pink`, `floyd`, `adele`, `metallica`, `nirvana` all present; only genuinely novel coinages (`radiohead`) miss. So CMUdict is valuable but needs a rule-based fallback for misses.

**Open sub-decision (to resolve in spec review):** how to provide CMUdict at runtime in the Jellyfin container (offline):
- (a) Bundle the full CMUdict (~3-5MB) as a plugin embedded resource.
- (b) Bundle a TRIMMED CMUdict containing only entries for words that appear in the user's catalog (built at catalog-sync time, small, but needs a one-time full-dict lookup at build).
- (c) A lightweight rule-based English g2p (no dict) for all names, accepting lower accuracy on English-word names.

Recommendation: (b) trimmed dict, because the user's vocabulary is bounded (their catalog), and it keeps the plugin small while preserving CMUdict accuracy for the common case. The trim runs once per catalog sync against a bundled full-dict, emits a small per-user lookup. **(This is the bounded-vocabulary insight from the user.)**

For names not in CMUdict (proper-noun coinages), fall back to a simple rule-based English g2p (letter->phoneme with basic digraph handling). Lower accuracy, accepted.

### Stage 2: per-L1 phonological interference (data-driven, the core win)

For each of the 7 L1s, a **once-curated interference mapping** from English phonemes to the set of L1 phonemes an L1-speaker would plausibly substitute, DERIVED FROM PHOIBLE feature vectors (closest L1 phonemes by feature distance to each English phoneme). This is the "algorithm over data" that replaces per-case hand rules: the per-L1 work is ONE feature-distance table, computed or curated once, then applied to every name generically.

Example (Italian L1): English /u:/ (as in "Koop") has no exact Italian match; nearest Italian phonemes by feature distance are /u/ and /u.o/ diphthong realizations -> ASR may surface as "u" or "o" -> "Koop" -> "Kup"/"Kop". English /k/ maps to Italian /k/ (both exist) but Italian orthography renders /k/ as "c" before back vowels and "ch" before front vowels -> "cup"/"chop" drift.

The interference mapping is **bounded and static per L1** (does not change per name), which is what makes this scale. PHOIBLE provides the feature data; the mapping is curated from PHOIBLE + validated against the L2-ARCTIC-style confusion data where available.

### Stage 3: inverse orthography (IPA -> target-L1 spelling), per L1

For each L1, a mapping from IPA phonemes to the L1's orthographic realizations (one-to-many, bounded to the common spellings). This is necessarily per-L1 (Italian spells /k/ differently than German), but it is a SMALL, STATIC table per L1, applied generically. This is where the variant generation happens: each L1-adapted phoneme sequence maps to a SET of plausible spellings, bounded by the per-name cap.

### Bounding and ordering

- Per-name synonym cap preserved (currently 5, per `ItalianPhoneticSynonyms.Generate`). The generative pipeline's combinatorial output is bounded at the end, device-captured forms ordered first (preserve the existing convention).
- The pipeline REPLACES the current hand-rolled generators for the 7 L1s. The `PhoneticSynonymGenerator` dispatch switch stays; each L1's generator calls the pipeline instead of hand rules.

## Components (units, each independently testable)

1. **`EnglishG2p`** (new, Alexa/Catalog/Phonetics/): CMUdict lookup + rule fallback. Input: English text. Output: IPA phoneme sequence. Test: "Koop" -> [k, u, p]; "radiohead" (not in dict) -> rule fallback; known-word coverage.
2. **`PhonologicalInterferenceMap`** (new): per-L1 phoneme->phonemes mapping, loaded from curated data (PHOIBLE-derived, embedded resource). Input: L1 code + English IPA phoneme. Output: set of L1-adapted IPA phonemes. Test: Italian /u:/ -> {/u/, /o/}; German /k/ -> {/k/} (native, no drift).
3. **`InverseOrthography`** (new, per L1): L1 IPA -> L1 spellings, bounded. Input: L1-adapted IPA sequence. Output: set of plausible spellings. Test: Italian [k, u, p] -> {"cup", "coop", "cop", ...}.
4. **`GenerativePhoneticSynonyms`** (new): composes 1->2->3 for a (name, L1), applies bounding + device-forms-first ordering. Replaces the body of each L1 generator's `Generate`.
5. **Curated data files** (embedded): 7 L1 interference maps (PHOIBLE-derived), CMUdict (trimmed or full, per Stage 1 decision).

## Data dependencies and licenses

- **Epitran rule engine**: MIT. Port to C# (the forward g2p for non-English, and the structure for the English fallback). Go port exists as a portability reference.
- **CMUdict**: public domain. Bundled (full or trimmed per Stage 1).
- **PHOIBLE 2.0**: CC-BY (attribution required; compatible with the plugin). Provides the feature vectors to curate the 7 interference maps.
- All licenses verified compatible with a Jellyfin plugin.

## Test plan

- **Unit, per component** (above).
- **Regression (binding):** the existing JF-362/JF-379 cases must still resolve. "Koop" in it-IT must emit at least one of cup/coop/cop (AC #3 from JF-379). "Soul Coughing" must still emit sol coffin / cofin variants (existing behavior preserved).
- **Live verify (AC #4):** re-sync catalog, confirm the JellyfinArtist catalog version for "Koop" (it-IT) includes the generated variants; on-device "suona koop" resolves.
- **No-regression for L1s that don't need drift:** German/Dutch (native /k/) must not generate spurious drift variants (the interference map returns identity for native phonemes).

## Open questions for spec review

1. **CMUdict delivery (Stage 1):** bundle full (~5MB) vs trimmed-per-user-catalog vs rule-only. Recommendation: trimmed. Confirm.
2. **Interference map curation method:** compute purely from PHOIBLE feature distance, or curate manually from PHOIBLE + L2-ARCTIC confusion data (more accurate, more effort)? Recommendation: curate for the 7 L1s (bounded, one-time), using PHOIBLE features as the starting point and the existing research reports (`research_phonetic_rules_2026-07-25.md`) as ground truth.
3. **Should the pipeline replace the existing hand-rolled generators entirely, or run alongside for a transition period?** Recommendation: replace per-L1, gated behind a feature flag for safe rollout, with the hand-rolled generators kept as fallback until the generative path is verified live per L1.
4. **Effort sizing:** this is a multi-session build. Rough breakdown: (a) C# Epitran port of the rule engine + CMUdict loader, (b) 7 L1 interference maps curated from PHOIBLE, (c) 7 L1 inverse-orthography mappers, (d) compose + bounding + tests, (e) live verify + rollout flag. Confirm the team wants to commit.

## Out of scope

- ar-SA and hi-IN (no existing generator; add only if/when prioritized).
- Query-time changes (JF-381's Double Metaphone path is separate and shipped).
- A general "all languages" solution (scoped to the 7 plugin L1s per the user).
