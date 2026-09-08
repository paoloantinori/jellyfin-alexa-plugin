---
id: JF-407
title: >-
  Consolidation follow-ups from /simplify: ResolveActiveFlow arbitration, shared
  picker pick-words, AskLocalized helper
status: Done
assignee:
  - zai
created_date: '2026-08-23 11:59'
updated_date: '2026-09-08 12:34'
labels:
  - refactor
  - multi-turn
  - tech-debt
milestone: m-15
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Follow-ups from the /simplify pass over JF-394..398 (commit 114b490 skipped these as beyond-cleanup scope):

1. ResolveActiveFlow: the resume > pagination > disambiguation arbitration is written three times (YesIntentHandler, NoIntentHandler, FallbackIntentHandler), each with its own comment. Move to ConversationalFlows.ResolveActiveFlow(sessionAttributes) -> Flow enum owning the order once; also folds the FindSong CanHandle-level FallbackIntent capture into the same mechanism. Add a Flow-typed MarkOthersInactive overload so call sites cannot pass a typo'd key array.
2. Shared pick-words: CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/ResolvePick/IsNegativeAnswer live private in FindSongIntentHandler, but numbered-candidate picking is a general DisambiguationHelper capability (used by 10 handlers). Moving them down gives every picker the cardinal/ordinal answer and the JF-395 negative-exit; today only FindSong has them.
3. AskLocalized helper: the SSML-or-plain Ask pattern (GetSsml ?? AskSsml : ResponseBuilder.Ask) is hand-written at ~6 sites (LaunchRequestHandler resume offer, FallbackIntentHandler re-ask, DisambiguationHelper x3, BaseHandler x2); one BaseHandler.AskLocalized(ssmlKey, textKey, repromptKey, locale, args) removes the drift the FallbackIntentHandler reprompt inconsistency came from.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Item-by-item (bounded, no behavior change):
1. AskLocalized helper in BaseHandler: one method for the SSML-or-plain Ask pattern (GetSsml ?? AskSsml : ResponseBuilder.Ask), replacing the ~6 hand-written sites (LaunchRequestHandler resume, FallbackIntentHandler re-ask, DisambiguationHelper x3, BaseHandler x2).
2. ResolveActiveFlow in ConversationalFlows: move the resume > pagination > disambiguation arbitration (currently duplicated in YesIntentHandler, NoIntentHandler, FallbackIntentHandler) to a single Flow-enum method.
3. Shared pick-words to DisambiguationHelper: CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/ResolvePick/IsNegativeAnswer currently private in FindSongIntentHandler; move to DisambiguationHelper so every picker gets cardinal/ordinal + JF-395 negative-exit.
Each item: tests stay green (no behavior change), commit individually.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
ITEM 3 (AskLocalized) DONE (commit ~14989076): BaseHandler.AskLocalized(ssmlKey, textKey, repromptKey, locale, args) consolidates the SSML-or-plain Ask pattern from 7 hand-written sites (DisambiguationHelper x3, FallbackIntentHandler, LaunchRequestHandler, BaseHandler HandleFuzzyMiss + BuildCrossMediaArtistOfferAsk). The helper does XML escaping internally (matching BuildOutputSpeech), takes RAW args, looks up the reprompt by key. Net -21 lines. No behavior change; one site (LaunchRequestHandler) that passed the reprompt string without a Reprompt wrapper is now consistent. Suite 2749 green.

ITEM 1 (ResolveActiveFlow) DEFERRED, finding recorded: the three handlers' arbitration is less duplicated than the task description suggested. Yes/No/Fallback share the same ORDER (resume > pagination > disambiguation) but each branch's ACTIONS are completely different (confirm/decline/re-ask). A ResolveActiveFlow -> Flow enum would move the if/else-if chain to ConversationalFlows without eliminating any real duplication; the value would only be locking the order against future drift, which the existing JF-398 ConversationalFlows class already does via MarkOthersInactive (at most one flow is active). The real remaining item is the FindSong CanHandle-level FallbackIntent capture fold-in, which is a behavioral change (not a refactoring) and needs its own design.

ITEM 2 (shared pick-words) NOT STARTED: CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/ResolvePick/IsNegativeAnswer still private in FindSongIntentHandler. The move to DisambiguationHelper is straightforward but touches 10+ handler call sites; best done in a fresh session with the review gate.

REVIEW GATE COMPLETE (2 agents over /tmp/jf407.diff): Finding 1 (bug-scan + comments, converged): the LaunchRequestHandler resume site's reprompt changed wire format from SSML (old AskSsml(string,string) overload wrapped in speak tags) to PlainText (helper wraps in Reprompt(string) which produces PlainTextOutputSpeech). Zero practical impact (verified: no locale's ResumeReprompt contains XML-reserved chars; verified against the Alexa.NET 1.22.0 pinned source via reflection on the actual DLL). Doc comment corrected in commit 5114791 to disclose the delta instead of claiming zero behavior change. Finding 2: transient XML-doc compilation error (angle brackets in summary) during the fix, resolved in the same commit; convention noted. Finding 3 (minor): the item-3 note cited 'commit ~14989076' which is not a valid hash; the actual AskLocalized commit is 19c6862 (corrected in this note). All other claims verified: escaping identical (EscapeStringArgs delegates to the same EscapeXml), reprompt lookup never throws (ResponseStrings.Get has a 4-level fallback), 13 keys exist in all 17 locales, 139/139 targeted tests green, 0 warnings/0 errors under TreatWarningsAsErrors.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
CLOSED (items 3 + 2 shipped; item 1 deferred-by-design). ITEM 3 AskLocalized: shipped earlier (commit 19c6862), 7 sites consolidated, -21 lines. ITEM 2 pick-words move: shipped 2026-09-08 (merged on main) - all five members (CardinalPickWords/OrdinalStemsByRank/NegativeAnswerWords/IsNegativeAnswer/ResolvePick + 3 private helpers) relocated byte-identically (reviewer-verified programmatically, whitespace-normalized diff empty incl. accented entries) to internal statics in DisambiguationHelper; FindSong keeps two thin delegators (test stability) and the JF-395 negative-exit ordering is untouched. Scope correction vs the original description: only FindSong ever consumed these members (the '10+ handlers' figure referred to DisambiguationHelper's other picking capability), so the move makes the machinery available without changing any consumer. ITEM 1 ResolveActiveFlow: DEFERRED BY DESIGN, not tracked as a task - the three handlers share the arbitration ORDER but each branch's actions are entirely different (confirm/decline/re-ask), so a Flow-enum extraction would move an if-chain without removing real duplication; the JF-398 MarkOthersInactive invariant already locks at-most-one-active. The genuine behavioral leftover - folding the FindSong CanHandle-level FallbackIntent capture into the flow mechanism - is a feature-design decision, not cleanup; revisit only if the multi-turn flows grow another capture. Gates: /simplify combined agent (zero findings on the move; cosmetic comment consolidation applied; ResolvePick generalization + delegator removal -> JF-524), code-review high SAFE TO MERGE (programmatic byte-identity across all 8 moved members, delegator fidelity, no orphaned references). Tests 3481/3481 on branch and main post-merge. DoD 4-8 N/A (pure relocation + comment move). Deploy: not required (behavior-identical refactor; the DLL rides the next functional deploy).
<!-- SECTION:FINAL_SUMMARY:END -->

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
