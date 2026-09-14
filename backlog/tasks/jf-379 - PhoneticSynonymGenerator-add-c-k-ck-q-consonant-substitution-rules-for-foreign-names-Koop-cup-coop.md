---
id: JF-379
title: >-
  PhoneticSynonymGenerator: add c/k/ck/q consonant-substitution rules for
  foreign names (Koop->cup/coop)
status: Done
assignee:
  - zai
created_date: '2026-07-25 18:07'
updated_date: '2026-09-14 13:16'
labels:
  - enhancement
  - phonetic
  - asr
  - artist-search
  - catalog
  - designed
  - multi-session
dependencies: []
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
PhoneticSynonymGenerator (Alexa/Catalog/PhoneticSynonymGenerator.cs) currently handles: whole-word overrides (soul->sol), the -ing->-in tail rule, and an intervocalic consonant doubler. It has NO c/k/ck/q consonant-substitution rules.

User insight (2026-07-25, Koop debugging): an it-IT Echo transcribed the artist 'Koop' as 'cup' (natural pronunciation) and 'coop' (when spoken with Italian vowel sounds). The c/k/ck family is a well-documented Romance-L1 ASR confusion: Italian speakers render foreign /k/ unpredictably, and Italian's native 'qu' pattern means /k/ can land as 'q' too (quop). Adding c<->k<->ck<->q substitution rules would cover a large family of foreign artist/album names, not just Koop.

This is the same coverage goal the existing machinery serves (emit enough plausible variants that one matches ASR output). The rules belong alongside the existing Romance tail rules / GetRomanceConsonantVariants.

NOTE: distinct from the catalog-injection question (JF-380). This task is about generating variants; whether they reach the device depends on the catalog being correctly populated per locale.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Add c/k/ck/q consonant-substitution rules to PhoneticSynonymGenerator.ApplyRomanceTailRules (or a new consonant-variant step) so an English name like 'Koop' emits variants covering ASR's Romance-L1 transcription drift: Coop, Cop, Cup, Quop, Ckop, etc.
- [x] #2 Bound the variant count per name (consistent with the existing per-name cap, currently 5) so coverage doesn't explode the catalog/slot size; device-captured forms ordered first
- [x] #3 Unit tests: given 'Koop', the generator emits at least one of cup/coop/cop; given a name with no k/c, no spurious variants
- [x] #4 Live verify: re-sync catalog, confirm the JellyfinArtist catalog version for 'Koop' includes the new phonetic variants; on-device 'suona koop' resolves (manual)
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
REOPENED-BY-MISTAKE 2026-08-29: briefly set In Progress based on the stale AC#1 ('add c/k/ck/q rules'); the 2026-07-27 REDESIGN supersedes that AC - data-driven generative composite (Epitran port + PHOBLE interference maps + inverse orthography), explicitly multi-session with open questions awaiting maintainer confirmation ('Confirm the team wants to commit', spec question 4). Reverted to To Do untouched. Before ANY implementation: the open questions in docs/superpowers/specs/2026-07-27-jf379-generative-phonetic-synonyms-design.md need decisions (CMUdict delivery form, replace-vs-alongside transition, rollout flag).

New device-ASR evidence for the phonetic-variant need (2026-09-06, Echo Dot): 'soul coughing' heard as 'i soul coffin' (song slot, corr=61db93dd). The title n-gram search found 100 'soul' candidates, the fuzzy best was an unrelated 'Glory-Of Soul Ignoring' (53), and the disambiguation 'no' ended in a clean not-found; the correct recovery was the ARTIST path, never reached because the title search had candidates. A generative phonetic synonym for 'coughing' emitting 'coffin' (the Romance consonant-doubler already emits exactly this direction: Cofin->Coffin) would have covered the artist-side resolution had the song-miss cascaded; the deeper fix is also letting a low-confidence title disambiguation 'no' cascade to the cross-media artist fallback. Operational note until then: 'suona la band soul coughing' (artist carrier) works.

2026-09-14 STAGE-0 IMPLEMENTED under maintainer directive '379, in tdd, per evitare regressioni' (this session). This supersedes the 2026-08-29 REOPENED-BY-MISTAKE warning: the maintainer explicitly directed implementing JF-379 now, as an incremental rule-family slice (stage 0), NOT the full 2026-07-27 generative composite. The generative redesign (Epitran + PHOIBLE + inverse orthography, spec 814e93b) remains the long-term architecture and its open questions still gate the multi-session build; this slice is a migration/fallback surface the replace-vs-alongside decision must carry.

Stage-0 shape: velar-stop family in PhoneticSynonymGenerator (GetVelarStopVariants: c/k/q swaps, oo->u/o drifts, QuGlide /kw/ form, ck collapse; soft-c and ch-digraph excluded via IsHardCAt/IsVelarStopAt) wired into ItalianPhoneticSynonyms.Generate ONLY (it-IT evidence; QuGlide is Italian orthography - es/pt need localized glides cu/qu before wiring, do NOT mirror the call site). Per-name cap 5 via PerNameVariantCap const with a provably-full skip guard. 13+2 unit tests in VelarStopVariantTests.cs (TDD red-first); suite 3734/3734 green both TFMs.

AC status: #1 done (rules + bounds), #2 done (cap 5, attestation-ordered: c-form and drifts before q-forms), #3 done (Koop emits cup/coop/cop; Bianchi/Beatles/Adele/Beyonce empty). #4 (live catalog re-sync + on-device 'suona koop') PENDING Paolo: needs DLL deploy + catalog re-sync, then device check.

2026-09-14 (Paolo, to fold into the generative build when it starts): the variant set must explicitly cover the CORRECT-pronunciation case, not just L1-mangled speech. A user who says an English name perfectly is still heard by a locale-built ASR that cannot write English sounds, so the correct rendering lands as a LOCAL spelling anyway (Koop -> cup AND coop from a correct-ish pronunciation; one Italian ear, two renderings - not two accents). The PHOIBLE-derived substitution data serves both directions (speaker can't say it / listener can't hear it - same sound-inventory gap). Make 'correct pronunciation, foreign ear' an explicit acceptance test on a sample of names when the composite is built, so alias sets are checked against both kinds of rendering rather than by accident. Paolo deferred this with the rest of the redesign: 'keep that in the backlog for now, we will get to that'.

2026-09-14 15:00 AC#4 FIRST HALF VERIFIED ON AMAZON GROUND TRUTH (forced early at Paolo's request: backed up the plugin XML, removed the LastCatalogSync element so the 12h gate read never-synced, restarted; sync succeeded 1133 artists / 886 albums / 138 series across 16 locales; the it-IT model PUT pinned artist catalog 6590add1 version 904 from its own leg - per-locale version pinning is why later locale legs minting 905-909 do NOT overwrite the it-IT value set). Evidence: ask smapi profile-nlu it-IT, utterance 'suona la band cup' -> PlayArtistSongsIntent, musician slot ER_SUCCESS_MATCH -> 'Koop' (id jellyfin_artist_9c6c9122ab59d67f60435c54c15252b8); 'suona la band coop' -> same match; control 'suona la band xyzzyfoo' -> no intent, no catch-all. REMAINING: on-device spot check (real ASR + speaker) at Paolo's convenience: 'suona koop' / 'suona cup' / 'suona coop'.

2026-09-14 15:15 AC#4 DEVICE VERIFICATION COMPLETE. On-device it-IT (Echo Show, log-verified): LaunchRequest 15:13:54 (invocation 'mia collezione' working again after enablement propagation settled), then PlayArtistSongsIntent 15:14:28 req=amzn1.echo-api.request.d37237e4 with slot musician spoken value 'cup' resolved by the STATIC catalog authority to 'Koop' (id jellyfin_artist_9c6c9122ab59d67f60435c54c15252b8; the dynamic authority NO_MATCHed, the catalog upload is what matched), then AudioPlayer.PlaybackStarted 15:14:34 (5.9s after the request). This is the filed incident case end to end: ASR heard 'cup', the catalog alias resolved it to Koop, playback started. Task complete: all 4 ACs verified (unit + Amazon ground truth + device).
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Stage-0 velar-stop (c/k/ck/q) phonetic variant family for the Italian catalog generator, shipped in TDD per maintainer directive. Koop now emits Coop/Cup/Cop/Qoop/Quop catalog aliases; soft-c and the ch digraph excluded from drift sites (Bianchi must not drift; Kitchen keeps ch intact); multi-word names compose per-word variants with siblings; per-name cap 5 with provably-full skip guard. 15 unit pins (VelarStopVariantTests), suite 3734 green both TFMs, gates run (simplify 4-agent + code-review high: 2 P3s fixed and red-green pinned: QuGlide uu-doubling, cap test Equal(5)). Deployed to minix (net10.0, md5-verified), catalog re-synced (1133/886/138, it-IT model pins artist catalog v904), verified on Amazon ground truth (profile-nlu 'suona la band cup'/'coop' -> ER_SUCCESS_MATCH -> Koop) and ON DEVICE: spoken 'cup' resolved to Koop via the catalog and playback started (log req amzn1.echo-api.request.d37237e4, 2026-09-14 15:14). Italian-only wiring is deliberate (QuGlide is Italian orthography; es/pt need localized cu/qu glides). The 2026-07-27 generative redesign remains the long-term architecture; deferred notes for it live in the task notes ('correct pronunciation, foreign ear' acceptance case per Paolo).
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes
- [x] #3 No new compiler warnings introduced
- [ ] #4 Session attributes use proper DTOs not raw ValueTuples for serialization
- [ ] #5 HttpClient instances are not shared across calls that modify BaseAddress
- [ ] #6 NLU test fixtures updated if interaction model changed
- [ ] #7 E2E test added for new intent or handler logic
- [ ] #8 Locale response strings added to all 17 locales
- [x] #9 /simplify passed (no blocking cleanups remaining)
- [x] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
