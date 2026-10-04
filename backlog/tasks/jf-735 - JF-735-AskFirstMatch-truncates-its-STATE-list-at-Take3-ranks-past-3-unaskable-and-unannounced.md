---
id: JF-735
title: >-
  JF-735 - AskFirstMatch truncates its STATE list at Take(3): ranks past 3
  unaskable and unannounced (the stricter sibling of the JF-729 fix)
status: To Do
assignee: []
created_date: '2026-10-03 19:35'
labels:
  - ux
  - disambiguation
dependencies:
  - JF-729
references:
  - >-
    backlog/tasks/jf-729 -
    JF-729-the-capped-multi-artist-ask-presents-its-3-spoken-names-as-an-exhaustive-found-set-no-and-others-hint-in-the-17-locale-strings.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the orchestrator gate-marker review of the JF-729 merge
(commit 8b77909e, finding 2 of 2; the JF-729 worker surfaced the observation in its
task file's Final Summary, and the global review-recommendation rule requires a tracker
entry the moment an out-of-scope finding is reported - the summary mention is not
tracking). AskFirstMatch (the JF-377 coincidental-containment downgrade prompt) truncates
its STATE list at Take(3) exactly as its speech does: ranks past 3 are unaskable AND
unannounced, the stricter sibling of the exhaustive-found-set defect JF-729 just fixed
for AskMultipleArtists (which now speaks 3 but cycles the full list and hints "Plus N
more").

The documented-deliberate defense (the MultipleArtistsSpeakCap doc): AskFirstMatch's
callers pass progressively weaker fuzzy candidates, where a rank-4+ tail is noise, while
the ER gate's candidates are exact library-name resolutions. Weighed against: the JF-377
design says bug-vs-carrier-bleed cases are string-indistinguishable, so a genuine rank-4
EXACT match can exist in the fuzzy candidate list too (a containment match on a
multi-word artist whose rank-4 form is the real artist), and the user gets no signal it
exists. THE WORK: decide whether AskFirstMatch's state should keep the full list (with
the cycling reaching rank 4+ the way AskMultipleArtists now does, plus the hint), or
whether the weaker-candidates defense holds and the truncation stays deliberate with
the cap doc's rationale strengthened. If changed: pin the rank-4 reachability and the
hint's absence-or-presence per the decision.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (final state: dotnet build Jellyfin.Plugin.AlexaSkill.sln --configuration Release --no-restore -warnaserror: Build succeeded, 0 Warning(s), 0 Error(s); the executable diff is a const substitution (Take(3) -> Take(FirstMatchStateCap)) plus comments)
- [x] #2 dotnet test passes (FINAL state 5095/5095 BOTH TFMs, dotnet test -m:1, full suite run ONCE on the final state per the workflow discipline; baseline 5093 + 2 net new (the ArtUrl-overload state pin + the census test), prediction matched. Filtered: DisambiguationHelperTests + AskFirstMatchCallerCensusTests 64/64 both TFMs after every gate round. RED PROOF: census expectation sabotaged (PlayNextIntentHandler 1 -> 2) failed on both TFMs with the actionable message naming the type and the FirstMatchStateCap doc, reverted, green again)
- [x] #3 No new compiler warnings introduced (final CI-exact Release -warnaserror build: 0 warnings 0 errors)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: the decline ships no behavior or attribute-shape change; the disambig_matches wire shape is untouched and stays pinned by the pre-existing attribute-key tests)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model or locale string change; validate scripts untouched)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new behavior to exercise end to end; the decline's locks are the two FirstMatchStateCap state pins (both overloads, exact stored count + first-three names) plus the IL census test. simulate-skill cannot assert state truncation more strongly than the builder pin does)
- [x] #8 Locale response strings added to all 17 locales (N/A by decision: the DECLINE ships no speech change and no new key; the overflow-hint string remains AskMultipleArtists-only, where its callers' lists are exact ER resolutions)
- [x] #9 /simplify passed (4 agents. Reuse: pin read-backs switched to the canonical DisambiguationHelper.ReadState (the pattern the file's newer tests already use) and the 14-line defense comment on the tuple pin trimmed to the 3-line pointer; Simplification: the redundant unlinked/deliberate sentence pair merged into one; Efficiency: CLEAN, with a mechanical comments-only proof on the production file at that round; Altitude: the strongest finding APPLIED: the defense moved to its own single home as the named FirstMatchStateCap const (AskFirstMatch's cap was documented on MultipleArtistsSpeakCap, a constant the prose itself calls deliberately unlinked), the caller census became an ENFORCED roster (new AskFirstMatchCallerCensusTests, the WarmingGateCoverageTests pattern), and HandleFuzzyMiss's doc gained the pointer sentence at the edit site an editor of the threshold/outcome set would trip)
- [x] #10 /code-review high passed (4 findings, ALL APPLIED, no correctness bug in executable code. F1+F4: the structural lock was not strictly true as first written: FindBestMatchWithScore's length band (maxLenDiff) can leave a 90-class containment candidate UNSCORED in a NotFound list (null treated as NotFound), and containment is bidirectional (either string containing the other scores 90), so the doc now states the invariant among SCORED candidates, names the band corner, and records why the verdict keeps anyway (pre-existing band behavior shared by every acceptance surface; the binding truncation at nine of ten sites is the caller's own Take(3)); the HandleFuzzyMiss doc sentence and the census test doc carry the same precision. F2: the JF-743 guardrail overstated what trips (list-shaping changes trip NOTHING; now stated exactly, with the by-hand re-weigh instruction). F3: the ArtUrl pin's census claim scoped to what the census enforces (the caller roster, not overload attribution) and the 2-tuple overload's zero-production-callers fact recorded on its pin (removal of the pre-existing overload declined as a drive-by, its StillWorks pin is deliberate). The band corner from F1 is also filed INTO JF-743 as a third consideration)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
THE DECISION: DECLINE (option b). AskFirstMatch keeps truncating its state at
3; no cycling-to-rank-4, no overflow hint, no new locale key. The weaker-
candidates defense held, and the weighing upgraded it from an assertion to a
structural invariant with one honestly-named corner.

THE EVIDENCE (all three axes the task demanded):
1. The JF-377 research hypothesis (bug-vs-carrier-bleed string-
   indistinguishability implies a genuine rank-4 exact containment match can
   exist in the fuzzy list) is REFUTED at the machinery level: the JF-377
   root-cause lock itself (FindBestMatchWithScore_NonsenseQueryContaining-
   CandidateName, pinned in FuzzyMatcherTests) scores ANY either-direction
   containment candidate 90 and an exact name 100, while every multi-candidate
   AskFirstMatch caller except PlayAlbum's direct-search leg reaches the ask
   only through HandleFuzzyMiss's NotFound outcome (best SCORED candidate below
   the EFFECTIVE suggestion threshold, FuzzyMatcher.GetSuggestionThreshold(user):
   per-user FuzzySuggestionThreshold override, default 40, config range 0-100):
   at the default threshold a scored exact/containment candidate diverts to
   auto-play or the Confirm ask and never lands in a NotFound list. The task's
   hypothetical candidate therefore cannot exist among scored candidates at the
   default; the one config exception is a user-set threshold above 90, which
   CAN admit a scored 90-class containment candidate into a NotFound list (the
   construction is default-conditional, not absolute; the gate-marker round
   corrected this phrasing here and on the const's doc).
2. Candidate ordering: the multi-candidate lists are NOT score-sorted (tier-1
   in library order, PlayAlbum alphabetical by JF-427 design), but that cuts
   FOR the decline: with the whole list sub-threshold among scored candidates, no
   rank contains a strong candidate at nine of ten sites, so ordering cannot
   rescue a rank-4 exact match that the scorer already diverted.
3. Exhaustion behavior verified: NoIntent at the end of the state list returns
   the NoMoreMatches Tell (clean session-ending exit); nothing in the cycling
   machine misbehaves on a 3-entry list.
4. THE CENSUS (the decisive fact the task framing assumed away): nine of the
   TEN multi-candidate callers pre-truncate with their own Take(3) AT THE CALL
   SITE (PlayArtistSongs, PlayAlbum, SearchMedia, PlaySong, AddToQueue,
   PlayVideo, PlayBook, PlayNext, AlbumPlayService); the helper's Take(3)
   actually bites at exactly ONE caller (PlayPodcast's multi-candidate leg).
   A helper-side state change would therefore change behavior at one site
   (sub-threshold tail) and reach none of the caller-side truncations; the JF-729
   sibling fix has no such reachability gap (AskMultipleArtists's lists come
   from exact ER resolutions).

WHAT SHIPPED: the verdict lives in ONE place, the new named const
DisambiguationHelper.FirstMatchStateCap (both overloads' Take(3) now use it;
the two-cap asymmetry is cross-linked from MultipleArtistsSpeakCap), with the
full defense, the caller census, and the JF-743 pointer; HandleFuzzyMiss's
doc carries the pointer at the edit site an editor of the threshold/outcome
set would trip; the deliberate Take(3) is locked by two state pins (renamed
AskFirstMatch_TruncatesStateAtThree_DeliberateWeakTailCap + the new ArtUrl-
overload twin, read back through the canonical ReadState) and the census
itself is ENFORCED by the new AskFirstMatchCallerCensusTests (IL scan, 13
sites across 10 types, both directions, red-proven by sabotage). The
narrow honest corner the code-review round surfaced (FindBestMatchWithScore's
length band can leave an UNSCORED 90-class containment candidate in a
NotFound list) is documented on the constant with the keep rationale and
filed into JF-743.

FILED: JF-743, the caller-side Take(3) legs where a dropped rank-4+ candidate
IS user-relevant: PlayAlbum's alphabetical direct-search hits (the exact album
name can sit past rank 3; the strongest leg, with the "rush" after "Gold
Rush"/"Love Rush"/"Midnight Rush" shape and no exact pick-off in that flow)
and PlayPodcast's untruncated search-relevant sub-threshold tail, plus the band
corner; guardrail scope stated exactly (new/removed call sites trip the
census; helper cap changes trip the state pins; caller-side list-shaping
changes trip NOTHING and need the by-hand re-weigh).

GATES: /simplify 4 agents (3 findings applied incl. the altitude round that
moved the defense to FirstMatchStateCap and made the census a test; efficiency
CLEAN with a mechanical comments-only proof). /code-review high: 4 findings,
all applied (the band-corner truth fix being the substantive one; zero
correctness bugs in executable changes). Suites: full 5095/5095 both TFMs on
the final state (baseline 5093 + 2 pins), filtered 64/64 after every round,
census red-proofed both TFMs; CI-exact Release -warnaserror 0 warnings.
No deploy (doc + test surface only; the orchestrator's batched post-closure
deploy owns the DLL anyway).
<!-- SECTION:FINAL_SUMMARY:END -->
