# Research Report: JF-379 generative L1-transfer phonetic variant libraries

**Date**: 2026-07-27
**Depth**: exhaustive
**Confidence**: HIGH on the negative finding (no turnkey generator exists); MEDIUM-HIGH on the recommended composite path

## Executive Summary

**There is no turnkey library that GENERATES foreign-accent / L1-transfer spelling variants of an English word for a given target L1.** The state of the art is a COMPOSITE you assemble from three parts, one of which (the L1-interference mapping) has no code library at all, only theory (Flege SLM, Best PAM-L2) and research corpora (L2-ARCTIC). For the Jellyfin plugin, the realistic options are: (1) the current hand-rolled per-locale rules (what exists), (2) port a forward g2p engine (Epitran, MIT, the only mature multilingual one) and add a SMALL, DATA-DRIVEN interference layer over PHOIBLE feature vectors rather than hand rules, or (3) ship nothing more at the catalog layer and rely on the query-time Double Metaphone path (JF-381, already verified working for the Koop case). The user's requirement, "no hand-added substitutions one after another, must generalize across languages," is achievable ONLY via option 2's data-driven interference layer, and that is a real build (port Epitran's rule engine to C# + encode an interference matrix from PHOIBLE features), not a library import.

## Findings

### SQ1: Forward g2p (English text to IPA) is mature; inverse g2p does not exist

**Epitran** (github.com/dmort27/epitran, MIT license) is the only mature, massively-multilingual forward g2p. It transcribes orthography to IPA for 153+ languages. A Go port exists (github.com/xyz27900/go-epitran), proving the rule engine is portable (a C# port is a known quantity, not a research project).
- English G2P via Epitran requires CMU Flite + CMUdict (the CMU Pronouncing Dictionary), because English sound-symbol correspondence is too poor for rules alone. The English leg is DICT-backed, not rule-backed.
- Epitran is FORWARD ONLY (orthography to IPA). Repeated searches for reverse/inverse g2p (IPA to plausible orthographies) return nothing. This is expected: IPA-to-spelling is one-to-many and unbounded, so no tool does it. The English->IPA->target-locale-spelling round-trip is therefore NOT a turnkey pattern; the IPA->target-spelling leg must be hand-built per the target L1's orthography.

Other forward g2p (gruut, g2p-en, Sequitur, pronounceai-g2p) are either English-only or TTS-focused. None model L2/foreign accent.

Confidence: HIGH (primary: Epitran README, repo).

### SQ2/SQ3: L1-transfer / accent-interference GENERATION has NO code library (only theory + detection models)

This is the make-or-break finding. There is no open-source library that takes (English word, target L1) and outputs the L1-accented spellings. What exists:
- **Theory**: Flege SLM (Speech Learning Model), Best PAM-L2 (Perceptual Assimilation Model). These DESCRIBE how an L1 speaker perceives/produces L2 phonemes. They are cognitive-linguistic models, not code.
- **Research data**: L2-ARCTIC (non-native English speech corpus, 6 L1s in the original release, more later) with phonetic annotations; papers analyzing L1 Bengali/Yoruba/Javanese/Sasak interference on English. These are STUDIES and CORPORA, not generators. They supply the RAW DATA from which an interference mapping could be DERIVED, but they do not APPLY it.
- **Mispronunciation DETECTION models** (deep learning, e.g. Leung et al., LSTM-based MDD on HuggingFace): these DETECT errors in RECORDED non-native speech. They are detectors (requires audio input), not generators (produce text variants), and are research models trained on specific L1s, not a generalizable tool. Wrong direction for this problem.

**Net: the L1-interference layer, the whole point of JF-379, has no turnkey implementation.** Anyone who has built this (TTS foreign-accent front-ends, CAPT systems) assembled it themselves from g2p + a hand-derived interference matrix for their specific L1 pair. Confidence: HIGH (exhaustive search returned no library; consistent with the field's state).

### SQ4: Machine-readable phonology data DOES exist (PHOIBLE 2.0)

PHOIBLE 2.0 (phoible.org) is a downloadable database of phoneme inventories for 2000+ languages with **distinctive feature vectors for every phoneme** (Hayes 2009 feature system + extensions). IPA is the pivot, so any two phonemes in any two languages can be compared featurally. This is exactly the data needed to compute, programmatically, "which phoneme in L1=X is closest to English phoneme Y" without hand-writing the mapping.

This makes the "build from scratch" option mean "an ALGORITHM OVER DATA (PHOIBLE features)" rather than "hand-write rules per language," which directly satisfies the user's requirement. A small interference matrix (English phoneme -> nearest-L1 phonemes by feature distance) can be computed or curated for each supported L1 once, then applied to every name generatively. Confidence: HIGH that the data exists and is suitable; MEDIUM that feature-distance is a good proxy for ASR confusion (ASR confusion is acoustic, not just featural; L2-ARCTIC-style confusion data would be more accurate but harder to assemble).

License: PHOIBLE data is CC-BY (standard for linguistic resources; compatible with a Jellyfin plugin with attribution). Epitran is MIT. Both are usable.

### SQ5: Pragmatic verdict for a C# net9.0 plugin

**Option (d) is the honest answer: "this does not exist as a turnkey generator."** The realistic paths, ranked by fit to the user's requirement:

1. **Data-driven interference over a ported g2p (the only path that meets the requirement).** Port Epitran's forward rule engine to C# (the Go port proves portability), ship CMUdict for the English leg, and encode a small per-L1 interference mapping DERIVED FROM PHOIBLE features (English phoneme -> nearest target-L1 phonemes by feature vector distance), then map the resulting IPA back to target-L1 orthography. The per-L1 work becomes "curate/compute one interference vector once," not "hand-add c/k, then oo/u, then th/d, forever." Effort estimate: large, multi-session, real engineering (port + 2-3 data layers + inverse-orthography per L1 + tests + bounding). It generalizes across languages, which is the explicit goal.

2. **Ship nothing more; rely on query-time Double Metaphone (JF-381).** The reported Koop/cup defect is already fixed at the query layer (verified live this session). The catalog-time one-shot path remains weaker, but the cost of option 1 is high for a marginal UX gain (one-shot artist recognition without the invocation name). This is the YAGNI-respecting choice if one-shot recognition is not a priority.

3. **Keep the current hand-rolled rules.** Explicitly REJECTED by the user (will not keep adding substitutions by hand; cannot do it for all languages).

**Recommendation: do NOT start option 1 unless the team is committed to the multi-session build, because it is a sub-project, not a task.** If one-shot foreign-accent artist recognition is a real priority, option 1 is the correct architecture and is the only one that scales across languages without per-case hand rules. If it is a nice-to-have, option 2 (close JF-379, rely on JF-381) is the honest call. The research cannot make this prioritization decision; it only establishes that there is no cheap library-out option.

## Confidence Assessment

- HIGH: no turnkey L1-transfer generator exists (SQ2/SQ3); Epitran is the mature forward g2p and is forward-only (SQ1); PHOIBLE provides the feature data needed for a data-driven interference layer (SQ4); both Epitran (MIT) and PHOIBLE (CC-BY) are license-compatible.
- MEDIUM-HIGH: the composite (port Epitran + PHOIBLE-feature interference + inverse orthography) is a viable build that meets the requirement.
- MEDIUM: feature-distance as a proxy for ASR confusion accuracy (ASR confusion is acoustic; featural similarity is an approximation; L2-ARCTIC confusion data would be more accurate but is research data, not a clean dataset).
- LOW/unverified: exact effort for the C# Epitran port (the Go port's existence is evidence of portability but not a bound on effort); whether the inverse orthography step per L1 is small or large.

## Sources

1. Epitran (dmort27/epitran, MIT) - forward multilingual g2p, 153+ languages, forward-only. https://github.com/dmort27/epitran
2. go-epitran (xyz27900/go-epitran) - Go port, evidence of portability. https://pkg.go.dev/github.com/xyz27900/go-epitran
3. PHOIBLE 2.0 (phoible.org) - phoneme inventories + distinctive feature vectors for 2000+ languages, IPA pivot, CC-BY. https://phoible.org/
4. L2-ARCTIC: A Non-Native English Speech Corpus - annotated non-native English speech, source for confusion data (research data, not a library). https://www.researchgate.net/publication/327222258_L2-ARCTIC_A_Non-Native_English_Speech_Corpus
5. Epitran paper ("Precision G2P for Many Languages") - confirms multilingual scope + rule+postprocessor design. https://www.academia.edu/82586335/Epitran_Precision_G2P_for_Many_Languages
6. Mispronunciation Detection papers (Leung et al.; LSTM-MDD on HuggingFace) - DETECTION models (wrong direction: require audio, not text-in/text-out generators). https://huggingface.co/papers?q=mispronunciation%20detection
7. IPA-CHILDES & G2P+ - feature-rich cross-lingual phonemic inventories (related data approach). https://voxclamantisproject.github.io/tools.html
8. CMU Pronouncing Dictionary (CMUdict) - the English pronouncing dict Epitran uses for the English leg. https://en.wikipedia.org/wiki/CMU_Pronouncing_Dictionary
