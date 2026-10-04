---
id: JF-708
title: >-
  JF-708 - unify the refusal-translation family: one policy for the
  warming/token refusal catches and the four event-aware degrade copies
status: Done
assignee: []
created_date: ''
updated_date: '2026-10-04 16:12'
labels:
  - code-quality
dependencies: []
references:
  - >-
    backlog/tasks/jf-699 -
    JF-693-declined-altitude-findings-pipeline-level-refusal-translation-the-locale-long-tail-and-a-structural-scan-pin.md
priority: low
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

## Policy Decision (written 2026-10-04, BEFORE coding, after reading every site)

Sites read: RequestPipeline.ExecuteAsync (both catches), BaseHandler.BuildUserNotFoundResponse
(~647), BaseHandler.BuildSessionMissResponse (~667), AlexaSkillController.DegradeForEventRequest
(~452, four call sites at ~365/415/431/438), plus the non-degrade IsEventRequest consumer
CircuitBreakerInterceptor (untouched) and the per-site catches that must keep working unchanged
(MediaInfo/CrossMediaFallback/SearchMedia enrichment catches of SkillWarmingUpException,
SkillConnectionHandler's `is not StreamTokenNotConfiguredException` filter).

**WHAT IS SHARED (part 1, the refusal catches): a common base exception, not a
TranslateRefusal helper.** New abstract `SkillRefusalException` (Alexa/Exceptions/) carrying
the TWO per-refusal policy facts the single pipeline catch needs: `ResponseKey` (the
ResponseStrings key for the non-event Tell) and `Severity` (the pipeline log level:
warming Information, a routine cold-start state; token Error, admin action required).
SkillWarmingUpException and StreamTokenNotConfiguredException derive from it (both stay
sealed; messages/IndexName unchanged; every existing specific-typed catch keeps matching).
RequestPipeline's two catches collapse into ONE `catch (SkillRefusalException ex)`: one log
line at `ex.Severity` carrying the exception (token keeps its stack; warming gains it,
severity unchanged; the per-refusal narrative stays in the exception message), intent,
eventType, correlation id; `SkipColdLibraryWork = true` (both twins already set it); the
response from the shared event-aware degrade core with `ex.ResponseKey` + the request
locale. A new refusal type then adds a TYPE (key + severity), never a catch block, and can
no longer re-decide event-awareness. Sanctioned behavior change, the task's own "warming
inherits event-awareness" option: warming on an EVENT request now answers the keep-alive
instead of a Tell. Verified unreachable today (the throwers are IndexWarmingGate.EnsureReady
via handler entry gates, the ArtistSearch choke point, and the SongNgramIndexService choke
points; the event handlers query the DB/RadioTrackSource directly), so no live change; it
closes the latent INVALID_RESPONSE class the warming twin would produce if an event path
ever gains an index call. The gate-marker's DELIBERATE CLASSIFICATION note (PlaybackController
CommandIssued taps not classified; no incident; revisit only if Amazon documents the rule)
moves onto the shared core's doc, where the one decision now lives.

**WHAT IS SHARED (part 2, the four degrade copies): one static, two overloads, on
BaseHandler beside IsEventRequest/BuildKeepAliveResponse.** Primary:
`DegradeForEventRequest(Request? request, string tellMessage)`: null request (the
controller's catch paths) answers the Tell; an event request answers BuildKeepAliveResponse;
everything else answers Tell(message). The string overload composes the message eagerly
(pure dictionary lookup; fine for every message-form site). Second overload
`DegradeForEventRequest(Request? request, Func<SkillResponse> nonEventResponse)`: the
decision core the string form delegates to; exists for the ONE site whose non-event leg is
not a single localized Tell.

**WHAT IS LEGITIMATELY PER-SITE:** only the non-event RESPONSE CONTENT, expressed as the
argument to the core. (a) BuildUserNotFoundResponse folds to the string overload
(byte-identical: UserNotFound key + request locale). (b) AlexaSkillController's private
DegradeForEventRequest is DELETED; its four call sites call the shared core with the same
pre-composed strings (two hardcoded English catch paths, the locale-keyed
CouldNotUnderstand, the ErrorRef interpolation). (c) The pipeline refusal catch calls the
core via the SkillRefusalException translation. (d) BuildSessionMissResponse keeps its
multi-branch non-event tail (dead-token relink Tell+Card vs plain UserNotFound) as an
extracted private `BuildSessionMissTell`, passed to the FUNC overload so it never evaluates
on event requests: its branch reads DeviceQueueManager and writes the AccountRelink
diagnostic log, so eager string-overload folding would fire side effects on event requests
(a behavior change). The laziness is pinned.

**WHAT THE JF-299 CONTRACT CONSTRAINS:** nothing here may put outputSpeech or
`shouldEndSession=false` on an AudioPlayer EVENT response. The shared keep-alive leg
(BuildKeepAliveResponse, ShouldEndSession=null, no speech) is exactly the event shape the
contract mandates; the event handlers' own keep-alive returns are untouched.

**PINS.** Existing pins held unchanged (no edits): PipelineTests' four JF-699 token pins
(intent Tell it-IT/en-US, event keep-alive, interceptor/SkipColdLibraryWork), the
SkillWarmingUpTests intent-request warming pins, EventHandlerTests' JF-507/JF-527
user-not-found/session-miss event+intent shapes, PlaybackNearlyFinishedRefusalTests. New:
(1) the red proof: warming driven through the pipeline on an AudioPlayer event request
answers keep-alive (fails pre-change: today it Tells); (2) the shared core's shape matrix
(null/intent -> Tell with the exact message; AudioPlayer/SessionEnded/SystemException ->
keep-alive; the Func factory NOT invoked on event requests, invoked on intent); (3) a
structural roster pin (IlCallScanner idiom): the only plugin method referencing BOTH
IsEventRequest and BuildKeepAliveResponse is the core's Func overload, so a fifth inline
degrade copy cannot regrow silently (the roster's SECOND BOUNDARY note qualifies this for the one shape that escapes the scan, a copy spelling the type test itself; JF-752 pairs it).

**DECLINED (with reason):** the TranslateRefusal-helper-only shape (keeps two catches, so
the next refusal still re-decides policy per catch); unifying the two log lines' severity
to one level (warming errors at every cold start would cry wolf; severity stays a per-type
fact carried by the base); touching SkillConnectionHandler/MediaInfo/CrossMediaFallback/
SearchMedia's specific catches (they are deliberate per-site degrades outside the pipeline
translation, already correct); the double token-marker Contains noted as context (out of
scope per the filing).

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (Release --no-restore -warnaserror: Build succeeded, 0 Warnings 0 Errors, both TFMs, on the final state)
- [x] #2 dotnet test passes (full suite, both TFMs: 5137/5137 net9.0 AND net10.0; baseline 5126 + 11 new pins)
- [x] #3 No new compiler warnings introduced (the Release -warnaserror run above is the proof; two transient authoring errors, an ambiguous cref and a target-typed new on an abstract value type, were fixed before the final state)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples (N/A: no session-attribute shape touched)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient code touched)
- [x] #6 NLU fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E added for new intent/handler logic (N/A: no new intent; the unified policy is pinned at unit level across the pipeline seam, the core matrix, and the roster)
- [x] #8 Locale strings in all 17 locales (N/A: reuses SkillWarmingUp + StreamTokenNotConfigured, both verified present in all 17 locale files; the new ResponseStringsTests ledger pin now walks SkillWarmingUp for all 17 locales too, closing the pre-existing ledger gap the review found)
- [x] #9 /simplify passed (4 agents: efficiency 2 applied as the Func-overload laziness restore at the pipeline catch and BuildUserNotFoundResponse; simplification 3 applied, IndexName deletion declined in favor of the deeper review's disposition, see F2 below; reuse 2 applied on the roster test, IlCallScanner.ContainsCallToAnyToken + LogicalMethodName; altitude 1 applied, the SkillConnectionHandler escape guard widened to the base; dispositions in the Final Summary)
- [x] #10 /code-review high passed (0 correctness bugs; 4 findings ALL applied: the SkillWarmingUp ledger gap + the subtype ResponseKey reflection pin, the dead IndexName property deleted (the simplify doc-reword superseded), two banned dash forms + the roster message separator; dispositions in the Final Summary)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The refusal-translation family is ONE policy. SkillRefusalException (Alexa/Exceptions/, new
abstract base) carries the two per-refusal facts the single pipeline catch consumes:
ResponseKey (the ResponseStrings key) and Severity (warming Information, token Error); both
concrete exceptions derive from it (SkillWarmingUpException lost its now-dead IndexName
property; the index name rides in the message). RequestPipeline's two sibling catches are ONE
catch: one log line at the type's severity carrying the exception, intent, eventType, corr;
SkipColdLibraryWork; and the response from the shared core, so a new refusal adds a TYPE,
never a catch block. The four event-aware degrade copies fold onto
BaseHandler.DegradeForEventRequest (beside IsEventRequest/BuildKeepAliveResponse): the string
overload for the message-form sites (controller catch paths; user-not-found), the Func
overload as the decision core for the laziness-bound sites (the pipeline refusal translation
and the session-miss, whose extracted BuildSessionMissTell must not fire its AccountRelink
diagnostic log or DeviceQueueManager read on event requests); the controller's private
DegradeForEventRequest is deleted and its four call sites call the core. Warming INHERITED
event-awareness (the task's sanctioned option): unreachable on event requests today (gates
and choke points sit on intent paths; verified), so no live change, but the latent
INVALID_RESPONSE Tell is closed for the next event-path index call; pinned red. The
SkillConnectionHandler task-error filter widened from the concrete token type to
SkillRefusalException (the /simplify altitude round: it is refusal-ESCAPE policy, not a
per-site degrade, so a future subtype escapes by type instead of being swallowed as a task
error speaking raw exception text). Pins: 11 new (warming event-request red proof through the
real pipeline seam; the core's shape matrix incl. all three event request classes, the
null-request leg, and the Func laziness; the roster pin, under which only the core's Func overload may
reference BOTH IsEventRequest and BuildKeepAliveResponse, self-red against the revert shape;
the subtype ResponseKey ledger pin + SkillWarmingUp added to AllExpectedKeys, closing the
pre-existing gap where Get fails soft and would speak a raw key). RED PROOFS: three
sabotages (unconditional Tell in the catch, the ternary inlined back into
BuildUserNotFoundResponse, the Func made eager) flipped exactly the three target pins on
both TFMs, green on restore; all pre-existing pins (JF-699 pipeline set, JF-507/JF-527
EventHandlerTests set, warming set) green with no pin method, assertion, or pin body edited; the shared seam
helper's parameter in SkillWarmingUpTests was widened from IntentRequest to
Request, and that precision was the JF-708 gate-marker finding. Gates: /simplify 4 agents (8 findings: 7
applied, 1 declined-with-reason, the IndexName deletion superseded by the review's harder
call); /code-review high 0 correctness bugs, 4 findings all applied. Suites: 5137/5137 both
TFMs on the final state (baseline 5126 + 11); Release --no-restore -warnaserror 0 warnings
0 errors. No locale or model surface changed; no deploy (the orchestrator's batched deploy
owns the DLL).

CLOSED 2026-10-04 by the orchestrator after the full cycle: merged into main (worker commit 9b9475cc + orchestrator tail 0c0e42c6 + the simplify-tail follow-ups merged as fbb1315b; the gate-marker ran its own full suite 5137/5137 both TFMs), its four low findings dispositioned (three doc tails applied, the site-level laziness pin filed as JF-752 pairing the roster's inline-evasion boundary; the JF-752 filing text itself corrected by the simplify altitude round before anyone picks it up, naming the existing shape test and the real harness seam). Awaiting the wave's batched deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
