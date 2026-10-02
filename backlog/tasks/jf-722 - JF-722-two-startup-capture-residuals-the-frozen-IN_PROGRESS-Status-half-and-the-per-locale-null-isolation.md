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
