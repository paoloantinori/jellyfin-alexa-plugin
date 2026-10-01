---
id: JF-687
title: >-
  JF-687 - launch-side empty secret mints dead URLs: the five PlaybackLaunchBuilder
  mint sites use the unchecked StreamTokenSecret
status: Done
priority: low
labels:
  - streaming
  - robustness
references:
  - backlog/tasks/jf-682 - Single-chapter-audiobook-redirect-mints-an-empty-chapter-token-when-the-secret-empties-mid-request-serve-the-gates-own-503-instead.md
---

## Definition of Done

<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors
- [x] #2 dotnet test passes (full recipe, both TFMs)
- [x] #3 No new compiler warnings introduced
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session-attribute code touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: response-string key only; no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A: no new intent; builder-level guard pinned by unit pins, the empty-secret state is not safely reachable on the live test server)
- [x] #8 Locale response strings added to all 17 locales (StreamTokenNotConfigured in all 17; validate_locales PASS; en-family verbatim per the parity test)
- [x] #9 /simplify passed (4 agents: reuse, simplification, efficiency, altitude; findings applied/skipped with justification)
- [x] #10 /code-review high passed (8 consolidated findings: 4 applied, the rest justified or tracked in JF-693)
<!-- DOD:END -->

## Description

Filed 2026-09-30 same-turn from the JF-682 orchestrator gate-marker round (finding 4).

THE GAP: the controller's empty-secret handling is now ONE shape (StreamTokenSecretNotConfigured, JF-682), but that consolidation is controller-scoped only. The five PlaybackLaunchBuilder mint sites (PlaybackLaunchBuilder.cs ~113/139/162/1410/1439) mint stream-URL tokens with the UNCHECKED _config.StreamTokenSecret: with the secret empty, they mint syntactically valid tokens HMAC'd over an empty key, and the skill hands the Echo a launch URL that 503s at the route gate - surfacing on-device as an opaque playback failure instead of a configuration error where the user can act on it (the Alexa response could speak "stream token not configured, check the plugin settings").

THE WORK: a launch-side guard at the builder entry (or one shared check the five sites route through) that, on an empty secret, answers the configuration error honestly instead of minting dead URLs. Mind the JF-682 lesson: read the secret ONCE per build and check it before the first mint; do not thread a snapshot the five sites could disagree on.

VERIFICATION: a builder-level pin per site family (the empty-secret config answers the config-error response, no URL minted); the existing launch-builder tests green; full suite both TFMs.

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Landed 2026-10-01 (worker on fc842406). THE FIX: a launch-side delivery guard, ONE shared private helper in PlaybackLaunchBuilder (StreamTokenSecretRefusal(sourceUrl, locale)): when StreamTokenSecret is empty AND the URL about to be delivered targets one of the plugin token-gated endpoints (marker "/alexaskill/api/", DERIVED from the route map constant AlexaSkillController.ApiBaseUri, not a fresh literal), the launch answers the localized Tell (new locale key StreamTokenNotConfigured, all 17 locales) instead of emitting the directive. Called at the FIVE builder delivery points: BuildVideoAppLaunchResponse (sync) + BuildVideoAppLaunchResponseAsync (before the progressive announce), BuildAudioPlayerResponse (top, before any ledger/launch-scope write; covers the JF-636 speed route and the JF-507 episode audio-transcode route minted by ResolveAudioLaunchSource from ANY caller), BuildVideoAppAudioResponse (after its three URL branches, before assembly), BuildAudiobookResumeResponse (after the resume URL build). The five MINT sites are unchanged: the guard sits at the delivery decision because that is the only read that can see the secret state the fetch will meet; one read per launch path, in the same method tail as the response assembly, so no entry-gate snapshot can disagree with a later mint (the JF-682 shape). Static Jellyfin URLs (/Audio/, /Videos/, api_key) and live-TV resolver URLs carry no plugin token and are NEVER refused (pinned). Locale: optional string? locale params threaded on the three builders that lacked one; in-file callers thread the request locale (episode launch family) or the announce locale (BuildAudioPlayerResponse folds announceLocale in at the chokepoint); callers passing neither answer en-US (the handler-side threading residual is JF-693 item 3). Two post-build announce overwrites inside the builder are gated by a shared HasLaunchDirective predicate so neither clobbers the refusal Tell (and the screenless AudioPlayer degrade keeps its announce; that regression the /simplify round caught and fixed). Ledger policy unified at the code-review round: all three ledger-adjacent sites now refuse BEFORE RecordLastPlayed, so a refused launch never flips the device last-played entry.

PINS (PlaybackLaunchBuilderStreamTokenSecretTests, 11): seven ConfigTell pins, one per site family (speed URL + episode-transcode URL through the AudioPlayer chokepoint driven via the REAL ResolveAudioLaunchSource; music video-audio; album concat; audiobook resume; remux URL through BuildVideoAppLaunchResponse; async variant additionally pinning NO progressive send; plus the secret-configured control), and four no-overblock/control pins (static URL still plays, screenless audiobook resume still degrades to the static AudioPlayer path, live-TV-shaped URL still launches, token-gated URL with a configured secret still plays). RED PROOF RUN (both TFMs): with the shared guard disabled at its single point, exactly the 7 ConfigTell pins flip ("a refused launch must not deliver any playback directive"; the dead-URL mint delivery returns) while the 4 no-overblock/control pins stay green; guard restored, 11/11 green again.

SUITES: full recipe on the final state, 4841/4841 BOTH TFMs (net9.0 + net10.0; baseline 4830 + the 11 new pins). validate_locales PASS (no new gaps). GATES: Skill simplify (4 agents) + Skill code-review high run on the final diff in this transcript. simplify dispositions: APPLIED the route-constant reuse (TokenGatedUrlMarker derives from ApiBaseUri), the RemuxEpisode fixture hoist to TestHelpers (both suites now share it), the shared HasLaunchDirective predicate (also fixing the screenless-degrade announce regression three agents flagged), locale ?? "en-US"; SKIPPED with justification: widening ResponseStrings.DefaultLocale to internal (outside the task file scope; the inline literal matches the house convention), the write-only locale seam on BuildAudiobookResumeResponse (it is the JF-693 item-3 fix seam), the Any() enumerator micro-allocation, and the mint-site typed-exception redesign (RequestPipeline is outside the file scope; the exception escapes uncaught from the 14 out-of-scope handler resolver callers). code-review dispositions: APPLIED the announceLocale fallback at the chokepoint, the refusal-before-ledger unification, two banned-dash prose fixes, the JF-693 site-list extension; SKIPPED with justification: config-Tell-before-capability-Tell ordering (deliberate: the broken config is site-wide and fixable, the capability limit is not), the mint-site/StreamTokenHelper.IsTokenGatedUrl altitude (same file-scope boundary), SleepTimer + handler-overwrite residuals (tracked).

RESIDUALS: filed same-turn as JF-693 (handler-level token-gated delivery bypassing the builder guard: the JF-628 sleep-timer re-issue; refusal-Tell overwrites at the handler sites incl. the SetPlaybackSpeed success-speech case and the skip/jump family; audiobook locale threading; the persist-before-refuse order). DoD 4-7 N/A as annotated.
<!-- SECTION:FINAL_SUMMARY:END -->
