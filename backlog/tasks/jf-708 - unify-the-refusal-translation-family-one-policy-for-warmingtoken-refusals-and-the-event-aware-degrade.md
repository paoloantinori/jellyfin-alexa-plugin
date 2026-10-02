---
id: JF-708
title: >-
  JF-708 - unify the refusal-translation family: one policy for the warming/token
  refusal catches and the four event-aware degrade copies
status: To Do
priority: low
labels:
  - code-quality
references:
  - backlog/tasks/jf-699 - JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-699 /code-review high + /simplify rounds (two
findings that share one mechanism family; both deliberately not taken in JF-699):

1. THE TWO PIPELINE CATCHES DRIFT: RequestPipeline now carries two sibling
   translation catches (SkillWarmingUpException and
   StreamTokenNotConfiguredException) with the same policy shape (log, set
   SkipColdLibraryWork, request-locale Tell), and the NEW twin is the better one:
   it is event-aware (the keep-alive shape on Alexa event requests, where
   outputSpeech is rejected with INVALID_RESPONSE), while the warming twin always
   answers a Tell. Warming is currently unreachable on event requests (the warming
   gates sit on intent paths and the two index choke points), so no live bug, but
   the next refusal type copy-pastes a third catch and re-decides event-awareness.
   The deeper shape: one shared refusal translation (a common base exception
   carrying the ResponseStrings key, or one TranslateRefusal helper) so a new
   refusal adds a type, not a catch block; warming inherits event-awareness or the
   difference is stated once.
2. THE EVENT-AWARE DEGRADE IS NOW FOUR INLINE COPIES: the
   `IsEventRequest ? keep-alive : localized Tell` shape exists at
   BaseHandler.BuildUserNotFoundResponse, BaseHandler.BuildSessionMissResponse,
   AlexaSkillController.DegradeForEventRequest, and (JF-699) the RequestPipeline
   refusal catch. Extract one static beside IsEventRequest/BuildKeepAliveResponse
   and fold the four.

JF-699 dispositions for the record (why not taken there): (1) unifying the catches
would change the warming contract's event-request shape, a behavior change outside
the JF-699 diff's mandate; (2) the degrade extraction touches the controller and
two BaseHandler sites JF-699 did not otherwise modify. The third related skip, the
pre-JF-699 double token-marker Contains on the async VideoApp launch path
(BuildVideoAppLaunchResponseAsync guards, then the sync twin re-checks), is noted
here only as context: it predates JF-699, costs one short string scan per launch,
and is NOT part of this task's scope.
<!-- SECTION:DESCRIPTION:END -->
