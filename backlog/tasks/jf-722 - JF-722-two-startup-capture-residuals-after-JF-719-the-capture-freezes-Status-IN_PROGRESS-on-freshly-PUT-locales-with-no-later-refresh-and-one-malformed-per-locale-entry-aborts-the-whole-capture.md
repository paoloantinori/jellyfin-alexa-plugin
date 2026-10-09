---
id: JF-722
title: >-
  JF-722 - two startup-capture residuals after JF-719: the capture freezes
  Status IN_PROGRESS on freshly-PUT locales with no later refresh, and one
  malformed per-locale entry aborts the whole capture
status: Done
assignee: []
created_date: '2026-10-03 00:30'
updated_date: '2026-10-03 03:55'
labels:
  - catalog
  - observability
dependencies:
  - JF-719
references:
  - >-
    backlog/tasks/jf-719 -
    JF-719-the-startup-capture-preserve-fires-only-on-SUCCEEDEDan-IN_PROGRESS-capture-still-writes-Error-null-and-erases-the-preserved-clause.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-03 same-turn from the JF-719 code-review gate (effort high), findings 1 and
2 of 4: both verified mechanically, both out of JF-719's recorded scope (the coordinator's
gate-vs-settle-wait decision), both living in
SkillStartup.CaptureLocaleModelStatusesAsync.

RESIDUAL 1 (the Status half of JF-719's filed world): the JF-719 widening fixed the Error
half (the preserve now fires on a clean IN_PROGRESS capture), but the capture still writes
Status="IN_PROGRESS" VERBATIM for freshly-PUT locales, and nothing refreshes that row
afterwards: both capture call sites run only on version mismatch, manifest FAILED, or
skill creation, so an ordinary restart runs NO capture (version now matches), and the
weekly CatalogSyncTask may be skip-gated (the JF-710 world). The ledger can therefore
show IN_PROGRESS as if it were settled truth for days. JF-709's PreservedOrSkippedStatus
doc rejects exactly this for the sibling writer ("IN_PROGRESS ... would freeze a
momentary state into a permanent row"), and the diagnostics panel's ModelsDeployed
checklist requires a SUCCEEDED row (DiagnosticsController), so a capture where no locale
read settled leaves the checklist showing models-not-deployed on a healthy setup.
Mitigating fact (weigh it): the PUT loop settles the PREVIOUS locale's build on every
iteration (PutLocaleModelPreservingWiringAsync settles-before-GET), so in practice only
the last-PUT locale, maybe the last two, freeze IN_PROGRESS; the all-IN_PROGRESS extreme
needs every build still in flight when the loop ends. FIX SHAPES to weigh: a deferred
one-shot re-poll (a delayed background GetSkillStatusAsync refresh ~30-60s after the
capture, no startup latency, adds a timer concern), reusing
CatalogManager.WaitForLocaleBuildToSettleAsync in the capture (the shape the coordinator
already rejected once for startup latency), or an explicit Status-clamp decision (name
what an IN_PROGRESS capture-row should read as once frozen).

RESIDUAL 2 (per-locale null isolation): the capture dereferences
status.InteractionModel entries and localeStatus.LastModified.Status unguarded inside the
whole-method try/catch, so ONE malformed per-locale entry (LastModified null, plausible
on a freshly created skill where a locale is registered but its build has not started,
exactly the line-267 create path) throws NullReferenceException, the outer catch swallows
it as a single non-critical warning, and every locale AFTER it in dictionary order gets
no row captured and no preserve applied: a partial capture the admin cannot distinguish
from a complete one. FIX SHAPE: a per-locale guard (skip + warn on LastModified null) or
a per-locale try isolating the bad entry.

Not defects of the JF-719 gate itself: the review verified the allowlist complete over
the actual three-member SkillStatusState enum, null-safe on the non-nullable enum
property, and failing safe on enum growth.

AUDIT ADDENDUM (2026-10-03, JF-719 gate-marker round, premise CORRECTED and priority
raised): this file's original mitigating fact ("the PUT loop settles the previous
locale's build on every iteration, so only the last-PUT locale, maybe the last two,
freeze IN_PROGRESS") is WRONG. The settle inside iteration i+1 is a
GetLiveModelJsonAsync -> WaitForLocaleBuildToSettleAsync(locale_{i+1}) that polls
locale i+1 BEFORE its own PUT this run - at that moment locale i+1's last build is the
previous deploy's settled result, so the settle is a no-op, and no iteration ever waits
on locale_i's fresh 15-30s build. The PUT loop paces ~1-2s per locale, so at capture
time every locale whose build started in the final ~15-30s reads IN_PROGRESS:
realistically ~10-17 of 17 locales, not 1-2. The frozen-IN_PROGRESS Status half (the
panel's ModelsDeployed checklist false, most rows gray until the weekly sync) is
therefore the COMMON case after every version-bump restart, and the settle-wait fix
shape listed below would cost ~17x the settle budget it implicitly assumes (this
strengthens the recorded WIDEN rejection; the real fix shapes are a post-capture
settle-and-rewrite, a Status-normalization at panel read, or JF-721's structured
field). ALSO FOLDED HERE (gate-marker finding 2, speculative, for triage): the
errors-first branch attributes any non-empty Errors array to the capture's own build,
so a stale previous-build error riding an IN_PROGRESS status on the freshly-PUT
locales would still wipe the clause the widening exists to protect (whether SMAPI
clears errors when a new build starts is undocumented and unverified from the
referenced DLL); if it ever bites, the defensive shape is gating the preserve on
"errors belong to a terminal observation" rather than array presence.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors. DONE: `dotnet build Jellyfin.Plugin.AlexaSkill.Tests` (builds both TFMs, plugin + tests) Build succeeded, 0 errors, on the final post-gates state.
- [x] #2 dotnet test passes. DONE (rework final state): `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` on the post-rework FINAL state: 5002/5002 passed net9.0 AND 5002/5002 net10.0, exit 0 (baseline 4981 + 21 new: 11 from the first round, 10 from the rework: the F1 marker end-to-end pair, the F3 recapture trio + capture-return pin, the F2 mid-poll cancellation pin, and the code-review-refresh trio: capture-checkpoint, recapture-leg cancellation, timeout-shape retry).
- [x] #3 No new compiler warnings introduced. DONE: the only warnings in any build of the final state are the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337 (already documented pre-existing at JF-719's closure); zero warnings attributable to this diff.
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization. N/A: no session attributes touched (startup/ledger surface only; the ledger row is the LocaleModelStatus record).
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress. N/A: no HttpClient construction or BaseAddress change; the refresh reuses the SmapiManagement Refit surface through AlexaUtil.CallAsync exactly like the capture.
- [x] #6 NLU test fixtures updated if interaction model changed. N/A: no interaction model, template, or sample change.
- [x] #7 E2E test added for new intent or handler logic. N/A with justification: the changed surface is startup internals (SMAPI status reads + ledger writes), not an intent/handler or Alexa-speech path; the InternalsVisibleTo fake-status seam (the JF-710 harness JF-719 also drove) is the pinning harness for this path, and a live E2E would need a real version-bump restart plus SMAPI build windows on the production box.
- [x] #8 Locale response strings added to all 17 locales. N/A: no user-facing Alexa speech added or changed.
- [x] #9 /simplify passed (no blocking cleanups remaining). DONE across BOTH rounds: the first 4-agent round (reuse / simplification / efficiency / altitude): 8 APPLIED, 3 SKIPPED with recorded reasons (details in the round-1 commit message and Final Summary). The rework round refreshed all 4 agents on the new diff: 4 simplification findings APPLIED (roster hoisted bool; pre-check folded into a mode-initialized local; recapture rationale consolidated onto the wrapper; RefreshAsync token parameter), the altitude round's 3 APPLIED (the derived recapture mode replacing call-site literals; the token threaded into the capture with its save-boundary checkpoint; the JF-721 ownership connection incl. the same-turn task-file append), 2 efficiency SKIPPED (the second same-poll GET and the capture's unconditional save: negligible magnitude, pins document the call sequence) and 1 reuse borderline SKIPPED (the end-to-end carry pin's halves duplicate individual pins; the composition link is the point). No blocking cleanups remain.
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked). DONE across BOTH rounds: the first round's 7 findings ALL APPLIED (per-locale + per-poll ledger-enumeration guards; fresh Configuration reads; the CaptureRefreshPairingTests roster, counterfactual-proven twice; the transient-retry pin; the sparse-status guard; observation-accurate exhaustion logging) with the 2 pre-existing observations FILED as JF-724. The rework round refreshed code-review at effort high on the new diff: 6 findings, 5 APPLIED (RC1 the token-state OCE filters fixing the timeout-shaped-TaskCanceledException silent kill; RC2 the per-user loop's OCE rethrow clause; RC3 the third budget-exhaustion shape's honest message; RC4 the prefix-rule quarantine invariant documented on the constant and in the IsOwnShapeLedgerError invariant note; RC6 three new pins: the capture's own cancellation checkpoint, the recapture-leg cancellation, the timeout-shape retry), 1 SKIPPED (RC5, the two-GETs-per-recapture-poll efficiency duplicate of the simplify round's skipped finding, same reasons).
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including a rework round: worker commits 01f35db7 + 19e4a0cc, merged as b175f615. The startup capture residuals closed: the post-capture settle-and-REWRITE (RefreshInProgressLocaleStatusesAsync with a 45s/4-poll budget, the cancellation token threaded through the family with checkpoints after every await and before every save, timeout-shaped TaskCanceledExceptions falling to warn-and-retry so one timed-out GET cannot kill the family, pinned); the arm-distinction marker (ObservedBuildErrorsLedgerPrefix composed on every observed-errors text and dropped on clean settle, the JF-710/JF-719 preserve product carried verbatim, both halves pinned end-to-end through capture-then-refresh); the creation-site pairing made real by derivation (the capture returns whether it wrote rows, driving the recapture across both sparse shapes and deleting the cross-user global-ledger inference); and the per-locale null-isolation guards in both passes. 21 new pins across the two rounds including the pairing roster counterfactual-proven twice. Worker gates green on both rounds (simplify 8+7 applied; code-review high 7/7 then 5/6 applied). The orchestrator gate-marker verified all six axes mechanically and its 7 findings all landed (the rework's three must-fixes F1/F2/F3, the race-sizing and dual-shape sparse-warning corrections, and JF-724 items 3-5: the multiplied refresh race exposure with the canary-loss victim, the family-membership pin gap, the cross-user pre-check interplay). JF-724 filed; JF-721 appended with the fourth writer's design input. Suites: worker 4992 then 5002 after rework, orchestrator independent 5002/5002 both TFMs on the rework head, merged-tree 5007/5007 both TFMs exit 0 on both split-TFM legs. Production surface changed (SkillStartup, LibrarySyncService, InteractionModelRedeployer): deployed in the post-closure deploy.
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Design decisions (2026-10-03, implementation round, written before coding)

RESIDUAL 1 (frozen IN_PROGRESS Status): shape (a), the post-capture
settle-and-REWRITE, adopted; (b) panel-read normalization and (c) deferring to JF-721
both rejected after weighing against the consumer read.

- (b) REJECTED on a correctness axis, not just the lying-ledger one: normalizing an
  aged IN_PROGRESS row to "settled" at read time would also normalize the row whose
  build FAILED after the capture read it mid-flight, masking a real failure behind a
  time heuristic (the panel would read ModelsDeployed=true on a broken deploy until
  the weekly sync); today's false-gray never claims success. It would also need the
  same threshold logic duplicated across two read sites (DiagnosticsController and
  config.html) while the ledger keeps lying.
- (c) REJECTED because JF-721 owns the Error/caveat field design, not the Status
  half; the corrected analysis makes the frozen Status the COMMON case after every
  version-bump restart (~10-17 of 17 locales), so deferring leaves a medium-priority
  symptom live indefinitely for a task whose headline it is.
- (a) SHAPE: SkillStartup gains RefreshInProgressLocaleStatusesAsync (internal, the
  InternalsVisibleTo test seam, driven through the same faked GetSkillStatusAsync),
  scheduled fire-and-forget by BOTH capture call sites (the version-mismatch /
  FAILED-manifest site and the skill-creation site) via a scheduler wrapper running
  under the startup's linked cancellation token. Flow: pre-check (no IN_PROGRESS
  capture-family rows -> return before any delay or GET), initial delay 45s (model
  builds are 15-30s and the capture fires at PUT-loop end), then up to 4 polls 45s
  apart with early exit when the family empties; a failed poll GET warns and retries
  on the next poll; rows still IN_PROGRESS after the budget keep their
  healthy-neutral status (the panel does not count IN_PROGRESS failed) with the
  weekly sync as backstop. The budget deliberately does NOT chase a fully
  SMAPI-serialized 17-deep build queue (17 x 15s would need unbounded polling; the
  common concurrent-build case settles by the first poll).
- PRESERVE SEMANTICS HONORED: the refresh rewrites ONLY rows that are Status
  IN_PROGRESS AND Source "Embedded" (the capture family: the sync writers never
  write IN_PROGRESS, PreservedOrSkippedStatus clamps it, and no other writer does),
  and it does NOT re-run PreserveLedgerErrorAcrossCapture. On a clean settle the
  row's Error (already the capture's post-preserve product, JF-710/JF-719) carries
  forward verbatim with only Status and LastUpdated refreshed; an observation with
  its own errors, or a failure-weight state without them, replaces wholesale exactly
  like the capture's own branches (a build-failure observation never carries a
  catalog clause that had nothing to do with it), enum-growth-safe via the same
  allowlist shape (SUCCEEDED/IN_PROGRESS carry, anything else wholesale).
- The refresh is another unguarded read-modify-write of ledger rows (the KNOWN RACE
  class, now firing on the widened IN_PROGRESS set); the one-directional accepted
  trade is unchanged and the race note at the capture's preserve gate names the
  refresh as the same class.
- config.html UNCHANGED: with the writer-side refresh, IN_PROGRESS rows become a
  ~45-180s transient and the gray bullet is honest during it; no UI threshold logic
  shipped, so the embedded-resource rebuild dance is not triggered.

RESIDUAL 2 (per-locale null isolation): the explicit guard shape PLUS a per-locale
try. A locale whose entry has null LastModified (freshly created skill, first build
not started) is skipped with a warning: there is no observation to record, and any
previous row stays the last settled truth instead of being overwritten with a guess.
The remaining loop body runs under a per-locale try/catch so an unexpected malformed
shape (or the ledger collection-mutation race inside Get/SetLocaleModelStatus during
a concurrent sync write) costs ONE locale's row, not every later locale in dictionary
order; the old whole-method catch turned either into a silent partial capture the
admin could not distinguish from a complete one. The LibrarySyncService
WriteLedgerEntry doc sentence describing the capture's whole-capture coarser failure
granularity is updated with it.

## Rework round (2026-10-03, gate-marker findings F1-F7, all dispositions)

- F2 (lifecycle, MUST FIX) APPLIED: the cancellation token now runs through the whole
  refresh family. The poll body checkpoints after every await and immediately before
  every SaveConfiguration the family performs, INCLUDING inside
  CaptureLocaleModelStatusesAsync itself (it takes an optional token; its non-fatal
  whole-method catch rethrows OCE under a TOKEN-STATE filter). The refresh-round
  code-review then sharpened the filter twice: (RC1) an HttpClient timeout surfaces as
  TaskCanceledException, an OCE subclass with NO cancellation requested, so an
  exception-type filter would silently kill the whole refresh on one timed-out GET;
  both the capture's rethrow and the per-poll catch filter on
  cancellationToken.IsCancellationRequested instead (the timeout shape falls to the
  warn-and-retry arm; pinned by the TimeoutShapedCancellation test). (RC2) the
  capture's rethrown shutdown OCE now bypasses the startup user loop's generic
  per-user Error catch via an OCE-with-token-state rethrow clause, reaching the same
  outer handler the loop-top check always did. Residual accepted and documented
  in-code: the synchronous in-memory loop pass between checkpoints has no checkpoint.
- F1 (behavioral, MUST FIX) APPLIED: ObservedBuildErrorsLedgerPrefix ("build errors: ")
  is composed onto every observed-errors Error text (the capture's wholesale arm and
  the refresh's own twin) and the refresh's clean-settle arm DROPS a marker-prefixed
  existing.Error (the errors describe the error-carrying observation, stale on a clean
  settle: the unverified SMAPI stale-errors shape) while carrying every non-marker
  Error (the JF-710/JF-719 preserve's product) verbatim. Both halves pinned END TO
  END through capture-then-refresh (CleanSettle_OverStaleObservedErrorsRow_DropsThem,
  CleanSettle_OverPreservedClauseRow_EndToEnd_CarriesIt). The quarantine invariant the
  PREFIX rule adds (foreign diagnostics must never START with the literal) is
  documented on the constant and folded into the IsOwnShapeLedgerError invariant note
  (code-review refresh RC4); JF-721's migration inventory was appended same-turn with
  the fourth literal, the fourth writer, and the arm-distinction design input.
- F3 (pairing inert at creation, MUST FIX) APPLIED, then DEEPENED by the rework
  simplify altitude round: the recapture mode is now DERIVED, not a call-site literal:
  CaptureLocaleModelStatusesAsync returns whether it wrote any row, and the pairing
  wrapper computes captureWroteNoRows from that return. This covers BOTH sparse shapes
  at BOTH sites (freshly created skill AND a transient sparse response on an
  established skill after a version bump, which the call-site-literal design missed),
  with no self-classification a future capture site could get wrong. The worker
  retries the recapture until the CAPTURE ITSELF reports writing rows (the capture
  swallows its own GET failures, so its return is the only per-skill, per-invocation
  signal; the earlier LedgerHasAnyRows global-ledger inference and its cross-user
  blindness were deleted with it). Pinned: the capture-return pin (both sparse shapes
  return false, healthy returns true), the recapture-writes-first-builds pin, the
  transient-failure retry pin, the empty-ledger-no-recapture boundary pin, and the
  roster's refresh-worker allowance (counterfactual re-proven after both rounds).
- F4 (APPLY) APPLIED: the KNOWN RACE notes at the capture's preserve gate and the
  rewrite pass now carry the multiplied sizing (up to 4 polls x 17 locales across the
  ~3-minute budget, overlapping the post-restart CatalogSyncTask window) and the
  diagnostic-loss victim (a sync-authored settled row with a real canary clobbered
  back to the stale read); appended to JF-724 item 3.
- F7 (APPLY) APPLIED: the sparse-status warning names both shapes (freshly created
  skill; transient sparse response after a deploy), and the code-review refresh added
  the THIRD exhaustion shape's accurate message (the status never became observable
  within the budget; no rows were written) instead of claiming in-flight rows over an
  empty ledger (RC3).
- F5 + F6 (FILE) FILED into JF-724 items 4 and 5 (the family-membership pin gap: the
  Source default "Embedded" plus a roster that pins call wiring, not row content; the
  cross-user pre-check interplay). JF-724 item 5's LedgerHasAnyRows instance was
  ELIMINATED by the F3 derivation fix (the helper no longer exists); the pre-check's
  cross-user read remains filed as before.

Gates refreshed on the rework diff: /simplify (4 agents) returned 4 simplification
findings (all APPLIED: the roster's hoisted callsCapture bool; the pre-check folded
into a mode-initialized hasFrozenRows; the recapture rationale consolidated onto the
wrapper with pointers elsewhere; RefreshAsync's token parameter) plus the altitude
round's derivation, token-into-capture, and JF-721-connection findings (all APPLIED),
2 efficiency findings SKIPPED with reasons (the second same-poll GET: one extra call
per skill creation, the pins deliberately document the call sequence, and reusing the
  recapture's own observation is semantically dead since family rows read still
  IN_PROGRESS in it; the capture's unconditional save: pre-existing shape, negligible
  magnitude) and 1 reuse borderline SKIPPED (the end-to-end carry pin duplicates its
  halves' individual pins; the composition link IS the point, pairing with the
  marker-drop twin). /code-review high refreshed: 6 findings, 5 APPLIED (RC1 the
  timeout-shaped TCE token-state filter, RC2 the per-user OCE mislabel, RC3 the third
  exhaustion shape, RC4 the prefix-rule invariant documentation, RC6 three new pins:
  the capture's own cancellation checkpoint, the recapture-leg cancellation, and the
  timeout-shape retry), 1 SKIPPED (RC5 the two-GETs-per-recapture-poll efficiency
  duplicate of the simplify round's skipped finding, same reasons).
<!-- SECTION:NOTES:END -->
