---
id: JF-729
title: >-
  JF-729 - the capped multi-artist ask presents its 3 spoken names as an
  exhaustive found-set (no "and others" hint in the 17 locale strings)
status: To Do
assignee: []
created_date: '2026-10-03 12:10'
labels:
  - ux
  - disambiguation
  - localization
dependencies:
  - JF-707
references:
  - >-
    backlog/tasks/jf-707 -
    JF-707-cap-the-multi-value-ER-artist-ask-spoken-list-long-first-word-families-in-one-breath.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-707 code-review high gate (finding 1 of 4, the
only behavior-facing one; findings 2 to 4 were applied inside JF-707 itself). JF-707
caps the multi-artist ask's SPOKEN list at `DisambiguationHelper.MultipleArtistsSpeakCap`
(3) while the cycling state keeps the full resolved list, so a 4-plus-artist
same-first-word family is reachable only by saying "no" past names the user already
heard. The 17 `DisambiguateMultipleArtists` locale strings were NOT adapted: the
prompt still reads "I found multiple artists: A, B, C. Shall I play the first one?
Say no for the next.", which presents the three spoken names as an exhaustive
found-set. A user who wants rank 4 or later and reads the list as complete gives up
or rephrases instead of cycling; the reprompt's "say no for the next" is the only
signal that anything follows, and it does not say how many.

THE WORK: a locale-string change across all 17 locales signaling that more candidates
exist when the resolved list exceeds the speak cap. Shape options to weigh: (a) an
"and N more" tail fed as a second format arg by `DisambiguationHelper.AskMultipleArtists`
(empty or absent at or below the cap, so the two-name shape stays byte-identical); (b)
a standalone conditional string key chosen when `matches.Count > MultipleArtistsSpeakCap`.
Either way: ResponseStrings plumbing, all 17 locale JSONs, `validate_locales.py`
(baseline-aware) green, and a pin asserting the hint appears above the cap and NOT at
or below it. The cycling contract itself is already pinned by JF-707's
`PlayArtistSongs_MultiValueEr_FourInLibrary_SpeaksThree_CyclesToTheFourth`. Deliberately
NOT done inside JF-707: that task's decided scope was the cap plus its pin; a 17-locale
string pass with its own validation is a separate work item.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (dotnet build Jellyfin.Plugin.AlexaSkill.sln on the final state: Build succeeded, 0 Warning(s), 0 Error(s); an isolated first build had shown the transient xUnit1030 pair inside VideoAudioControllerTests.cs, the concurrent worker's file outside this diff, absent from every later build)
- [x] #2 dotnet test passes (FINAL state: 5067/5067 net9.0 AND net10.0, dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1, full suite run ONCE on the final state per the workflow discipline; predicted 5046 baseline + 21 net new (2 overflow-count theory cases + 2 no-hint theory cases + the 17-locale key-coverage theory), prediction matched. Filtered runs: the two touched classes 192/192 both TFMs, then 286/286 with the caller suites (MusicianMultiValueEr + PlayArtistSongs) after each gate round. RED PROOFS both directions on both TFMs: hint suppressed (`if (false && ...)`) -> exactly the 2 AboveCap cases fail; hint forced (`if (true)`) -> exactly the 2 AtOrBelowCap cases fail; restored, 192/192 green again)
- [x] #3 No new compiler warnings introduced (final sln build 0 warnings 0 errors; the diff touches no analyzers and adds no diagnostics)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (unchanged: the ask still stores List<MatchInfo> via BuildAttributes; the hint is speech-only)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: locale RESPONSE strings only, no interaction model change; validate_versions.py untouched and PASS)
- [x] #7 E2E test added for new intent or handler logic (builder-level pins per the task's own pin spec: AskMultipleArtists_AboveCap_SpeaksOverflowHintWithUnspokenCount (4 and 5 candidates: "Plus 1/2 more" present, "Plus 4/5 more" absent, pinning the count as the UNSPOKEN remainder) and AskMultipleArtists_AtOrBelowCap_NoOverflowHint (2 and 3 candidates: hint absent, the byte-identical below-cap shape); cycling itself stays pinned by JF-707's handler-level suite, and the 17-locale key-coverage theory DisambiguateMultipleArtistsMore_ExistsWithCountArg mirrors the FindSongFoundMultipleSingular precedent. No SMAPI e2e: the change is one speech-suffix conditional inside an already-e2e-covered builder, and simulate-skill cannot assert count phrasing more strongly than the builder pin does)
- [x] #8 Locale response strings added to all 17 locales (new key DisambiguateMultipleArtistsMore in all 17 JSONs; python3 scripts/validate_locales.py: PASS, 305 keys, all locales OK, no new gaps, no baseline edit needed)
- [x] #9 /simplify passed (4 agents: efficiency CLEAN (one extra dictionary probe + one small concat on a once-per-turn human-paced path, BuildAttributes dwarfs it); reuse 1 finding APPLIED with simplification's dedup (the third hand-rolled match-list construction hoisted to CreateArtistMatches, used by all three AskMultipleArtists tests) plus the style alignment APPLIED (string.Concat -> the house `speech += " " + ...` suffix idiom, QueryArtistLibraryIntentHandler.cs:285 et al.); simplification 1 APPLIED (one of the two below-cap asserts dropped as redundant); altitude CLEAN, verdict RIGHT at the builder for all three axes with its AskFirstMatch note ruled pre-existing-and-documented, not a finding)
- [x] #10 /code-review high passed (0 correctness bugs; 4 findings, all landed: F1 APPLIED (ar-SA plural-after-numeral broke at the most common overflow count=1, the JF-487 "1 canzoni" class, hi-IN the milder sibling -> both rephrased count-invariant: Arabic count-predicate "وعدد الآخرين {0}." and Hindi verbless "इनके अलावा और भी {0}।", so the JF-487 singular-key split is not needed); F2 CONFIRMED INTENDED with the placement decision documented in the method doc (the task's specified "appended" shape; the reprompt is the mic-open CTA) and its ja-JP ASCII-space sub-point REFUTED on evidence (the existing ja DisambiguateMultipleArtists string already joins sentences with an ASCII space, "…しますか？ 次の方は…"); F3 APPLIED (the below-cap assert re-keyed from the bare word "more" to the hint's own "Plus", immune to a main-string rewording and consistent with the above-cap pin); F4 APPLIED (the LocaleStringsTests doc comment no longer claims an en-US-fallback guarantee the assert cannot make; names validate_locales.py as the presence guard). JF-735 NOT consumed: no real-but-out-of-scope finding survived (the single candidate, AskFirstMatch's state-list truncation at 3, is pre-existing, documented as deliberate in the MultipleArtistsSpeakCap doc, and disclaimed by the reviewer), so the reserved number stays free)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Worker branch off 4f0f4ee5. STRING-SHAPE DECISION: a separate hint key (DisambiguateMultipleArtistsMore) appended by DisambiguationHelper.AskMultipleArtists only when matches.Count > MultipleArtistsSpeakCap, {0} = the UNSPOKEN count (matches.Count - cap); NOT a second format arg on the main key, so the at/below-cap speech stays byte-identical to the pre-JF-729 shape and the conditional lives in one builder rather than 17 string tables (also no empty-arg format hazard). PER-LOCALE PHRASING: translated the pattern, not the English words, and chose COUNT-INVARIANT constructions because the most common overflow is exactly 1 unspoken (4 resolved minus cap 3): en "Plus {0} more." (5 locales), it "E {0} in più.", de "Und noch {0} mehr.", fr "Et {0} de plus." (2), es "Y hay {0} más." (3), pt "E tem mais {0}.", nl "En nog {0} meer.", ja "このほかにも{0}件あります。" (counter 件 is count-invariant), ar "وعدد الآخرين {0}." (count-predicate: grammatical at every numeral), hi "इनके अलावा और भी {0}।" (verbless: no verb-number agreement). The ar/hi first drafts used plural agreement and were rephrased in the code-review round (finding 1, the JF-487 "1 canzoni" defect class), which is why no singular/plural key pair was needed anywhere. GATES: simplify (2 applied + 1 style alignment + 2 clean angles), code-review high (3 applied, 1 confirmed-intended with one sub-point refuted on the in-file ja space precedent); red proofs both directions; JF-735 unused (nothing out of scope survived). EVIDENCE: full suite 5067/5067 both TFMs on the final state (baseline 5046 + 21 pins, prediction matched), validate_locales.py PASS (305 keys, all 17 OK), sln build 0 warnings 0 errors. Out-of-scope observation surfaced, not filed: AskFirstMatch truncates its STATE list at Take(3) (ranks past 3 unaskable and unannounced), pre-existing and documented as deliberate in the MultipleArtistsSpeakCap doc (weaker fuzzy candidates vs exact resolutions); surfaced here for the maintainer, no tracker entry since it re-litigates a documented decision.
<!-- SECTION:FINAL_SUMMARY:END -->
