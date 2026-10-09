---
id: JF-721
title: >-
  JF-721 - the ledger Error field is a three-writer wire protocol; replace the
  marker-parser family with a persisted caveat field
status: Done
assignee: []
created_date: '2026-10-02 21:05'
updated_date: '2026-10-04 03:39'
labels:
  - catalog
  - observability
  - design
dependencies:
  - JF-710
references:
  - >-
    backlog/tasks/jf-710 -
    JF-710-the-startup-skill-update-capture-overwrites-the-ledger-Error-null-erasing-the-JF-705-frozen-clause-under-the-skip-gated-restart.md
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the orchestrator gate-marker review of the JF-710 merge
(commit a09a0448, finding 3 of 5, the altitude finding). The per-locale ledger's Error
free-text field is now a wire protocol among THREE writers (the JF-705 PUT writer, the
JF-709 no-PUT writer, the JF-710 startup capture preserve), with each coordination
hazard fixed by layering another marker special case: the NoPutLedgerTail marker, then
the FrozenLedgerClauseMarker, then the PreviousLedgerDiagnosticPrefix framing literal,
then the two-Replace decomposer in PreserveLedgerErrorAcrossCapture. The JF-710 diff's
own six-shape trace shows the composition matrix every new writer must re-enumerate
against three Contains-matched literals; a miss compiles clean and surfaces only as a
corrupted or immortal diagnostic on a real box. The safety of the whole family rests
on the unstated invariant that only this subsystem's writers can ever put the three
literals into an Error (a richer canary quoting the clause, or a SMAPI build-error
message incidentally containing "; no PUT this run", would be silently preserved
forever or text-mutated).

THE WORK (a design task): add a structured, persisted caveat field to LocaleModelStatus
(an XmlSerializer-safe property beside Error, e.g. CatalogCaveat carrying the frozen
types and the no-PUT flag, or a small enum-plus-payload) so each writer sets its own
field instead of composing strings, the capture preserves by copying the field (no
parsing at all), and the config UI renders the caveat beside the free-text Error. This
deletes the parser family (IsOwnShapeLedgerError, PreserveLedgerErrorAcrossCapture's
Replaces, the three markers) and closes the invariant exposure by construction. Mind:
the persisted XML shape gains an additive field (the LastPlayedLaunchRoute compat
pattern); old persisted rows carry no caveat and must read as caveat-less with the
legacy Error text still rendered; config.html gains the rendering (the
embedded-resource clean-build dance applies); and the JF-705/JF-709/JF-710 pin families
migrate from string assertions to field assertions. Sequence AFTER JF-719 (the
IN_PROGRESS gate decision) lands or is decided, so the capture gate's final shape is
what the field design serves.

DESIGN-INPUT APPEND (2026-10-03, JF-722 rework round): the family this task deletes has
grown past the three writers and three markers this description was written against.
The startup capture's deferred refresh (JF-722) is the FOURTH ledger writer (it
rewrites only capture-authored rows; already named in the WriteLedgerEntry writer
inventory), and the rework added a FOURTH literal: SkillStartup's
ObservedBuildErrorsLedgerPrefix ("build errors: "), composed by the capture's and the
refresh's observed-errors arms and recognized-and-dropped by the refresh's clean-settle
arm (StartsWith). Delete it with the rest of the parser family. Required design input
for the caveat field: the field must encode WHICH arm composed an observation-era Error
(the observed-errors arm vs the preserve arm), because a clean settle must drop the
former and carry the latter; a caveat shape covering only frozen types + the no-PUT
flag has no slot for that distinction and would re-invent it mid-migration. Recorded
tradeoff worth weighing in the design (from the JF-722 altitude review): NOT composing
observed errors onto IN_PROGRESS rows at all (leaving the previous-build error text off
in-flight rows) would have made the fourth marker unnecessary entirely, at the cost of
a budget-exhausted still-in-flight row showing no error text on the panel.
<!-- SECTION:DESCRIPTION:END -->

<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (JF-721 worker 2026-10-04, both rounds: Debug build of plugin + tests clean on both TFMs; Release `dotnet build --configuration Release -warnaserror` at the worktree root on the final state of EACH round (round 1 and the rework): Build succeeded, 0 errors, 0 warnings, after deleting the output DLLs first for the config.html re-embed)
- [x] #2 dotnet test passes (JF-721 worker 2026-10-04, REWORK FINAL STATE: full suite ONCE, `dotnet test Jellyfin.Plugin.AlexaSkill.Tests -m:1`: 5087/5087 net9.0 (1m28s) AND 5087/5087 net10.0 (1m28s), exit 0; baseline 5078 + 9 new pins: the 5 round-1 LocaleModelStatusCaveatTests pins + the round-1 file's migration-grammar test + the rework's capture-chain, writer-half, and refresh-mirror pins. The pre-rework state had passed 5083/5083 both TFMs)
- [x] #3 No new compiler warnings introduced (Release -warnaserror over the whole tree, both rounds: 0 warnings; the pre-existing xUnit1030 pair did not fire under the flag on this stack)
- [x] #4 Session attributes use proper DTOs not raw ValueTuples for serialization (N/A: no session attributes touched; the new ValueTuple returns on the private helpers are method-local and never serialized; the persisted shape is the enum + string fields on LocaleModelStatusEntry, XML-round-trip-pinned)
- [x] #5 HttpClient instances are not shared across calls that modify BaseAddress (N/A: no HttpClient changes)
- [x] #6 NLU test fixtures updated if interaction model changed (N/A: no interaction model change)
- [x] #7 E2E test added for new intent or handler logic (N/A with justification: the surface is startup-ledger observability, not an intent/handler or Alexa-speech path; pinned through the InternalsVisibleTo fake-status seam (SkillStartupTests, the JF-710/722 harness), the real sync fakes (LegIsolation), the REAL controller action (GetCustomModelStatus driven directly in LocaleModelStatusCaveatTests), and the XML round-trip/legacy-read pins; a live E2E would need a version-bump restart on the production box)
- [x] #8 Locale response strings added to all 17 locales (N/A: no user-facing Alexa speech)
- [x] #9 /simplify passed (4 parallel agents: 5 findings APPLIED after dedup, namely the ComposeObservedErrorsCaveat one-owner helper for the observation arm's bit-and-text pairing, the FrozenCatalogCaveat factory pairing the bit with the payload for both sync writers, the shared DropObservedBuildErrors owner of the observation-era drop rule used by preserve + clean settle, the CaveatText third-null-path removal, the config.html tooltip drop for the never-truncated caveat; 3 SKIPPED with reasons: the controller-construction hoist to TestHelpers (the repo's hoist-on-third convention, this is the second copy), the CaveatText allocation micro-optimization (admin-only rare path, the agent itself judged it below the bar), the Replace-vs-Split nit (the split feeds the plural count))
- [x] #10 /code-review high passed (round 1: 4 findings, ALL landed: F1 the third persisted upgrade-transition shape documented in the preserve's UPGRADE TRANSITION note (mainline unreachable because the pairing's capture rewrites every observed locale before the refresh reads it; the race-composed residual bounded by the next writer, same as the other transition shapes), F2 the preserve now enforces bit-iff-payload (payload nulled when FrozenCatalogs does not survive, pinned), F3 the dead else in the capture's preserve arm deleted, F4 the refresh's branch-decision debug log widened with the surviving Caveat like its capture twin; REWORK REFRESH on the incremental diff: 5 findings, 4 applied (R1 clause-arm hardening + pinned, R2 the shared CarryLedgerCaveatAcrossCleanObservation owner, R3/R4 doc corrections), 1 skipped (R5 contradicts the coordinator's MUST FIX, reasons recorded in the rework section); the reserved JF-742 number went UNUSED, nothing out-of-scope remained)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle including a rework round: worker commits c291449b + 27a28656, merged as f7748c0d. The structured ledger caveat field: LocaleModelStatus and its XML twin gain the additive CatalogLedgerCaveats flags enum + FrozenCatalogTypes CSV (old rows caveat-less with legacy Error text still rendered, pinned; rollback-safe), the four writers set fields, the entire marker-parser family DELETED (the JF-710 load-bearing invariant closes by construction), config.html rendering the amber CaveatText span (inline CSS per JF-365). The rework landed the legacy-row MIGRATION (MigrateLegacyLedgerError's one-shot grammar decomposition wired into every text-carrying path, the R1 clause-arm hardening, the demanded skip-gated-capture-chain pin; the round-1 next-writer-rewrite bound disproven and replaced), the symmetric payload guard, and the previous-run attribution prefix. 9 new pins across both rounds; 24 existing migrated to field assertions. Worker gates green on both rounds (the R5 skip argued against the reviewer's own contradiction of the MUST FIX mandate, documented); the orchestrator gate-marker verified all five axes at source with 4 findings all landed via the rework. JF-742 unused. Suites: worker 5087/5087 both TFMs on the exact final state (Release -warnaserror clean, the config markup strings-verified), orchestrator independent 5083/5083 pre-rework, merged-tree 5093/5093 both TFMs exit 0 on both split legs. Production surface changed (PluginConfiguration, LibrarySyncService, SkillStartup, ConfigurationController, config.html): deployed in the batched post-closure deploy with JF-732, including the served-page curl-grep.
<!-- SECTION:FINAL_SUMMARY:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

## Design decisions (2026-10-04, implementation round, written before coding)

Sequencing satisfied: JF-719 and JF-722 are both closed, so the capture gate's
final shape (clean = SUCCEEDED or IN_PROGRESS, no errors of its own) and the
refresh's final shape (fourth writer, observed-errors arm) are what this field
design serves.

THE FIELD: a flags enum `CatalogLedgerCaveats` (None / FrozenCatalogs /
NoCatalogPut / ObservedBuildErrors) plus a payload string `FrozenCatalogTypes`
(CSV of CatalogType names, present iff FrozenCatalogs), both added to the
`LocaleModelStatus` record AND the `LocaleModelStatusEntry` XML twin, both
additive (the LastPlayedLaunchRoute compat pattern: a pre-JF-721 persisted row
deserializes caveat-less and keeps rendering its legacy composed Error text;
an old DLL reading post-JF-721 XML ignores the unknown elements, so a rollback
is safe too). Plural enum name because CA1714 is live under
AllEnabledByDefault + TreatWarningsAsErrors. CSV payload rather than per-type
flag bits: a future synced type joins the caveat by simply appearing in the
list, whereas a bit-per-type design loses a fourth type SILENTLY until someone
widens the enum (the JF-711 loud-widening discipline inverts here: the payload
degrades gracefully, the bits would not). CSV-of-names has an in-file precedent
(MoodGenreOverride.Genres).

ARM DISTINCTION (the JF-722 design input): ObservedBuildErrors is a CAVEAT BIT,
not a prefix. The capture's and refresh's own-errors arms set
`Caveat = ObservedBuildErrors` with the formatted Errors array as plain
free-text Error; the refresh's clean-settle arm drops Error and clears the bit
when the bit is set (field read, no StartsWith), and carries caveat + Error
verbatim otherwise. The recorded JF-722 tradeoff (not composing observed errors
onto IN_PROGRESS rows at all, saving the fourth marker) is REJECTED now the
discriminator is a field: the marker fragility was the entire cost side of that
tradeoff, and composing keeps the budget-exhausted in-flight row's error text
on the panel.

WRITERS (each sets its own fields; Error becomes pure free text, never
protocol): PUT writer: Caveat=FrozenCatalogs when types froze (payload CSV),
Error=CanaryError alone. No-PUT writer: Caveat=FrozenCatalogs|NoCatalogPut,
Error=the previous row's foreign diagnostic carried VERBATIM, unframed (the
"; previous: " framing literal dies); the once-only carry is keyed on the
previous row's caveat bits (FrozenCatalogs or NoCatalogPut = this subsystem
composed that row = replace, do not re-carry), the field successor of
IsOwnShapeLedgerError. Capture: clean arm copies the previous row's caveat
fields wholesale (PreserveLedgerCaveatAcrossCapture, a pure field copy: the
catalog-sync Source label OR any caveat bit keys recognition; the
run-scoped NoCatalogPut bit is masked off because a version WAS pushed; the
ObservedBuildErrors bit never preserves). Refresh: mirrors the capture's arms
with field reads.

DELETED with the migration: FrozenLedgerClauseMarker, FrozenLedgerClause (its
join/plural moves to the row renderer), NoPutLedgerTail,
PreviousLedgerDiagnosticPrefix, ObservedBuildErrorsLedgerPrefix (all four
literals), IsOwnShapeLedgerError, PreserveLedgerErrorAcrossCapture, and the
RC4 quarantine invariant (nothing matches on Error text anymore, so no foreign
text can collide with a recognizer). The display wordings survive exactly once
each, as DISPLAY-ONLY strings inside `LocaleModelStatus.CaveatText` (a
computed, non-persisted property rendering "Artist + Album catalogs FROZEN
(last-good pinned)" / "; no PUT this run" / "build errors"); the JF-710
LOAD-BEARING INVARIANT (only this subsystem can ever put the literals into an
Error) closes by construction: no Error text is parsed by anything ever again.

UPGRADE-TRANSITION TRADEOFF (accepted, documented in-code): a pre-JF-721
persisted SYNC row (Source=catalog-sync, no caveat bits, legacy composed Error
"clause tail; previous: foreign") has fields indistinguishable from a bare
foreign diagnostic, so the no-PUT writer carries its legacy text verbatim for
ONE run (the NoCatalogPut bit this write sets makes the next run drop it), and
a clean capture preserves it verbatim rather than decomposing it. A
pre-JF-721 CAPTURE-preserved row (Source=Embedded, clause-led Error, no bits)
clears on the first post-upgrade clean capture (recognition is field-keyed
now); both shapes self-heal at the next catalog sync. Rejecting any legacy
text-sniffing migration is the point of this task.

UI: ConfigurationController's status JSON gains `caveat` (the rendered
CaveatText, null when none); config.html renders it as an amber span BEFORE
the Error span (the clause-leads-within-80-chars concern becomes structural:
separate spans), Error keeps its truncation/tooltip. The embedded-resource
clean-build dance applies (delete the output DLL before building, verify the
served markup string in the built DLL).

## Rework round (2026-10-04, coordinator gate-marker findings F1-F4, all dispositions)

- F1 (MUST FIX, persistence semantics) APPLIED: the legacy-row bound ("carried
  once; the next all-frozen run drops it") did not hold under a skip-gated
  capture chain, which copied the pre-JF-721 composed Error VERBATIM
  indefinitely ("no PUT this run" for a run that DID push; the baked clause
  duplicating beside the caveat span once the no-PUT carry minted a bitted row).
  Fix: MigrateLegacyLedgerError, a ONE-SHOT decomposition of the old
  composition grammar (the four literals live once more, as LEGACY-MIGRATION
  vocabulary, clearly labeled scaffolding not protocol), wired into EVERY
  text-carrying path (the preserve, the no-PUT writer's carry, and the refresh's
  clean settle, the latter two via the shared CarryLedgerCaveatAcrossCleanObservation
  owner): the clause becomes the caveat bit + the names parsed from the clause
  head, the tail and framing drop, the bare foreign diagnostic survives, the
  own-errors shape clears. The renderer gained a nameless arm for a
  bit-without-payload row (hand-authored persisted XML only; the migration
  always recovers names). Pinned end to end: the demanded legacy-row +
  skip-gated-capture-chain pin (no stale wording, second capture idempotent),
  the writer half (all-frozen run over a legacy row), the refresh mirror, and
  the full grammar at unit level.
- F2 (APPLY) APPLIED: the refresh's clean-settle payload copy now guards
  bit-iff-payload like the preserve (both via the shared carry owner; pinned
  with the NoCatalogPut-only stray-payload shape).
- F3 (APPLY) APPLIED: the no-PUT writer's carried diagnostic is attributed
  "previous run: " (CarriedDiagnosticLedgerPrefix, DISPLAY-ONLY, deliberately
  shaped to match no migration literal, survives captures verbatim because it
  stays true); the carry pins assert the attribution.
- F4 (APPLY) APPLIED: the two "word - word" hyphen forms in the DoD prose
  fixed.

Gate refresh on the rework diff (the F1 migration exceeds trivial, so
/code-review high was refreshed; the /simplify 4-agent round was NOT, stated
skip: the incremental diff is one leaf helper plus three call-site wire-ups and
pins, no reuse/simplification surface beyond round 1's coverage): 5 findings, 4
APPLIED (R1 the clause arm HARDENED: a marker only counts as a clause when its
head is clause-shaped (non-empty, no ';' or ':', ending in the catalog noun),
so a foreign diagnostic quoting the marker mid-sentence neither mints a bogus
FrozenCatalogs bit nor gets chopped, pinned; the remaining window, a foreign
text carrying the bare tail/framing literals, is pinned as documented behavior
with its heal bound; R2 the migrate+guard copy shape extracted into ONE shared
owner, CarryLedgerCaveatAcrossCleanObservation, called by both the preserve and
the refresh, with the preserve's NoCatalogPut mask named at its call site as
the ONE deliberate divergence; R3 the FrozenCatalogTypes doc corrected: the
migration never mints bit-without-names, the nameless render is the
hand-authored-row shape; R4 the stale CaveatText trailing comment replaced),
1 SKIPPED (R5 "the migration re-introduces text recognition": rejected because
it contradicts the coordinator's MUST FIX, which explicitly mandates
distinguishing legacy composed text and migrating it; the baseline's
next-writer-rewrite bound was disproven for skip-gated capture chains, which
copy text verbatim indefinitely; the helper's doc carries the old family's
vigilance burden one last time, including why the attribution prefix matches
no literal).
<!-- SECTION:NOTES:END -->
