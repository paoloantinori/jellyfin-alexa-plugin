---
id: JF-722
title: >-
  JF-722 - two startup-capture residuals after JF-719: the capture freezes
  Status IN_PROGRESS on freshly-PUT locales with no later refresh, and one
  malformed per-locale entry aborts the whole capture
status: To Do
assignee: []
created_date: '2026-10-03 00:30'
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

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors. DONE: `dotnet build Jellyfin.Plugin.AlexaSkill.Tests` (builds both TFMs, plugin + tests) Build succeeded, 0 errors, on the final post-gates state.
- [x] #2 dotnet test passes. DONE: `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1` on the FINAL state: 4992/4992 passed net9.0 AND 4992/4992 net10.0, exit 0 (baseline 4981 + 11 new: 9 initial pins in SkillStartupTests + the code-review F5 transient-retry pin + the F4 CaptureRefreshPairingTests roster pin).
- [x] #3 No new compiler warnings introduced. DONE: the only warnings in any build of the final state are the pre-existing xUnit1030 pair at VideoAudioControllerTests.cs:1337 (already documented pre-existing at JF-719's closure); zero warnings attributable to this diff.
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization. N/A: no session attributes touched (startup/ledger surface only; the ledger row is the LocaleModelStatus record).
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress. N/A: no HttpClient construction or BaseAddress change; the refresh reuses the SmapiManagement Refit surface through AlexaUtil.CallAsync exactly like the capture.
- [x] #6 NLU test fixtures updated if interaction model changed. N/A: no interaction model, template, or sample change.
- [x] #7 E2E test added for new intent or handler logic. N/A with justification: the changed surface is startup internals (SMAPI status reads + ledger writes), not an intent/handler or Alexa-speech path; the InternalsVisibleTo fake-status seam (the JF-710 harness JF-719 also drove) is the pinning harness for this path, and a live E2E would need a real version-bump restart plus SMAPI build windows on the production box.
- [x] #8 Locale response strings added to all 17 locales. N/A: no user-facing Alexa speech added or changed.
- [x] #9 /simplify passed (no blocking cleanups remaining). DONE: the 4-agent round (reuse / simplification / efficiency / altitude) returned 11 deduped findings: 8 APPLIED (FormatInvocationErrors extraction consolidating the three SMAPI build-error join sites; the refresh allowlist's dead IN_PROGRESS arm dropped with its misleading comment; skip-path filter reorder + per-pass UtcNow hoist; the CaptureAndScheduleStatusRefreshAsync structural pairing replacing hand-scheduling at two call sites; the CaptureLedgerSource constant shared by writer and family predicate; the fourth-ledger-writer sentence in WriteLedgerEntry's doc; the UserServing delegation; the fake's StatusToServe private set) and 3 SKIPPED with recorded reasons (the injected-Func compose-observation helper, optional by the reviewer's own rating and deliberately different clean arms; the HasInProgressCaptureRows LINQ Any one-liner, hand-rolled scans are this file's house style; the pre-check hoist before Task.Run, the efficiency agent's own clear). No blocking cleanups remain.
- [x] #10 /code-review high passed (no blocking findings remaining or findings applied/tracked). DONE: 7 findings, ALL 7 APPLIED (F1/F3 per-locale + per-poll guards around every ledger enumeration incl. the pre-check's degrade-to-proceed; F2 fresh Configuration reads at every use, never captured across the ~3-minute budget; F4 the CaptureRefreshPairingTests IL roster pinning that only the wrapper calls the capture/scheduler, counterfactual-proven: an injected direct capture call fails the roster on both TFMs, reverted; F5 the transient-poll-failure retry pin via the fake's FailNextCalls; F6 the capture's sparse-status InteractionModel null guard; F7 the lastPollFailed-accurate exhaustion logging). The reviewer's two PRE-EXISTING observations (the admin panel's unguarded ledger enumerations able to 500 on a concurrent writer; the cross-user global-by-locale row ownership with two linked SMAPI users) are FILED as JF-724, not this diff's regressions.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Implemented 2026-10-03 (worker round, both gates run). SHAPE DECISIONS, weighed per the
audit addendum and recorded in the Design decisions section above: residual 1 took
shape (a), the post-capture settle-and-REWRITE: a fire-and-forget deferred refresh
under the startup's linked cancellation token (45s initial delay, up to 4 polls 45s
apart with early exit), scheduled at BOTH capture call sites through the structural
CaptureAndScheduleStatusRefreshAsync wrapper (the simplify altitude round's fix for the
"missed one" wiring class, enforced by the CaptureRefreshPairingTests IL roster). Shape
(b), panel-read normalization, was rejected on a correctness axis: normalizing aged
IN_PROGRESS rows would also normalize rows whose builds FAILED after the capture read
them mid-flight, masking real failures behind a time heuristic (and needs the threshold
duplicated across DiagnosticsController and config.html while the ledger keeps lying).
Shape (c) was rejected because JF-721 owns the Error/caveat field, not the Status half,
and the corrected analysis makes the frozen Status the COMMON case after every
version-bump restart (~10-17 of 17 locales). config.html deliberately UNCHANGED: the
writer-side refresh makes IN_PROGRESS a ~45-180s transient and the gray bullet is honest
during it (verified against both consumers: ModelsDeployed needs a Succeeded row,
failedModels counts FAILED/TIMEOUT only). The refresh rewrites ONLY Status
IN_PROGRESS + Source "Embedded" rows (the capture family; the sync writers never author
IN_PROGRESS: PreservedOrSkippedStatus clamps it, the PUT path records settled outcomes
only, verified against CatalogModelUpdateResult's vocabulary) and does NOT re-run the
JF-710/JF-719 preserve: a clean settle carries the capture-composed Error forward
verbatim, while an observation with build errors or a failure-weight state replaces
wholesale exactly like the capture's own branches. Residual 2 landed as the explicit
LastModified-null skip (previous row untouched) PLUS the per-locale try/catch, mirrored
in the refresh's poll pass, plus the capture's sparse-status InteractionModel null
guard. Gate hardening folded in: fresh Configuration reads at every ledger use
(UpdateConfiguration replaces the object), guarded ledger enumerations at every refresh
read point, and observation-accurate budget-exhaustion logging. New shared helper
LibrarySyncService.FormatInvocationErrors consolidates the three SMAPI build-error
join sites (capture, refresh, InteractionModelRedeployer). 11 new tests: the isolation
pin (malformed FIRST entry skipped, later locales still captured, previous row
untouched), 8 refresh pins (settled carry-verbatim, FAILED wholesale with/without
errors, re-poll until settled, budget-exhausted leaves row intact, no-network pre-check,
sync-authored family boundary, shutdown cancellation, transient-poll retry), and the
counterfactual-proven pairing roster. Gates: /simplify 8 applied / 3 skipped with
reasons; /code-review high 7/7 applied, 2 pre-existing findings filed as JF-724.
Suites on the final state: 4992/4992 net9.0 AND net10.0 (baseline 4981 + 11).
<!-- SECTION:FINAL_SUMMARY:END -->
