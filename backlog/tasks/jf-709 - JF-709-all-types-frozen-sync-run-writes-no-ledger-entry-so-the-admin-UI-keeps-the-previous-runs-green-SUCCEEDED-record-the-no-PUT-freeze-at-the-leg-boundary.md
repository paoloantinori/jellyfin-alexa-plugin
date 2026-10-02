---
id: JF-709
title: >-
  JF-709 - all-types-frozen sync run writes no ledger entry, so the admin UI
  keeps the previous run's green SUCCEEDED; record the no-PUT freeze at the leg
  boundary
status: Done
assignee: []
created_date: '2026-10-02 12:00'
updated_date: '2026-10-02 15:40'
labels:
  - catalog
  - observability
dependencies:
  - JF-705
references:
  - >-
    backlog/tasks/jf-705 -
    JF-705-per-locale-model-status-ledger-records-SUCCEEDED-over-a-partially-frozen-leg-surface-the-frozen-types-in-the-admin-UI.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 same-turn from the JF-705 /simplify gate (altitude agent, finding 1 of
2; number reserve: max existing was JF-707 plus this session's JF-705). JF-705 threads a
leg's frozen types into the ledger entry on the model-PUT path
(`RecordModelUpdateInLedger(locale, modelUpdate, frozenTypes)`), which fixes the
PARTIAL freeze: PUT succeeded, one type froze, the entry's Error field now carries
"Artist catalog FROZEN (last-good pinned)" next to the green SUCCEEDED check. The
residual is the TOTAL freeze shape: the ledger write sits inside the PUT gate
(LibrarySyncService.cs, the `if (artistVersion != null || albumVersion != null ||
seriesVersion != null)` block in RunLegAsync), and when ALL versions are null the gate
skips both the PUT and the ledger write. The JF-705 task text framed that shape as
"already stays OUT of the ledger", which is true only in the no-previous-entry sense:
on a server that ever had a healthy run, the locale KEEPS the previous run's clean
SUCCEEDED entry while every catalog type is now pinned to last-good. The failure an
admin sees is the same "all locales green, artist recognition stale" triage trap
JF-705 was filed for, in the worse shape. Reachable forms: all three types frozen, or
one type frozen plus zero-item siblings (the zero-items shape also yields a null
version per the JF-495 rule documented at the call site), and the JF-513.3
all-types-hash-skipped leg (every payload byte-identical to an earlier locale's this
run, so all versions are null with nothing frozen at all; today masked because ar-SA
is excluded and hi-IN is the only remaining no-generator locale, but reachable the
moment another no-generator locale is added or the exclusion is lifted). Note the
hash-skip form is NOT a freeze: decide whether it deserves a ledger clause at all or
is fine leaving the previous entry untouched, since its model genuinely still
references current catalog versions.

THE WORK: record the freeze at the LEG boundary, not the PUT boundary. The locale
attempt loop already consumes `legFrozenTypes` after `RunLegAsync` returns (the
`legFrozenTypes is { Count: > 0 }` branch that merges into `result.FrozenTypes`), and
that is where a no-PUT freeze should append the frozen clause to the locale's EXISTING
ledger entry. Open decision to make when picking this up: what Status an entry carries
when this run performed no model build at all (candidates: keep the previous entry's
Status and append the clause to its Error, matching the JF-705 shape of
"caveat-without-failure"; or an observation status in the UNVERIFIED family). Keep the
run-level honesty unchanged (FrozenTypes gates Success=false either way). Watch the
leg-retry interaction: types frozen on a retried-away attempt refreeze on the winning
attempt, so only the returned attempt's list is truthful (same caveat the merge site
documents). This change adds a FOURTH frozen-types string format next to the three
already in LibrarySyncService (the run-level completion clause, the run-level
LogWarning, and the JF-705 ledger clause): when landing it, decide whether to
consolidate them behind one formatter (the JF-705 code-review finding 3, skipped
there as maintainability-only with intentionally different per-surface wordings) or
keep the per-surface wordings and say so in the code.

AUDIT UPDATE (2026-10-02, from the JF-706 code-review round): this task's premise for
the hash-skip form ("its model genuinely still references current catalog versions")
is wrong for any locale whose EVERY run is all-skipped, and that shape is live today
at scale, not a future-reachable edge. The uploaded payload depends on the locale
only through the synonym generator, which is keyed by language prefix
(Util.LocalePrefix.Of), and the seed enrichment is a locale-INDEPENDENT union across
the committed models (CatalogSeedEnrichment), so the byte-identical equivalence
classes are the prefix families: {es-ES, es-MX, es-US}, {fr-FR, fr-CA}, and the
no-generator cluster {en-AU, en-CA, en-GB, en-IN, en-US, hi-IN} (generators cover
it/de/es/fr/pt/ja/nl prefixes only; ar-SA is excluded by JF-543). Under the default
"*" config only the FIRST member of each class to run uploads and gets its model
PUT; every later member returns null versions for all three types on EVERY run, the
injection gate (identical before and after JF-706) skips the PUT, and since the
embedded models carry zero valueCatalog blocks those locales' interaction models
NEVER receive catalog references: catalog ER never activates there. That is 8 of
the 16 synced locales today (2 es + 1 fr + 5 en/hi). The observability gap this task
files (no ledger entry) and this product gap (no wiring, ever) share the same
trigger and the same fix boundary (the leg boundary, where a no-PUT outcome should
be handled explicitly); whoever picks this up should treat the starved-locale shape
as a first-class case to decide, not only the freeze shapes; options include
keying the hash-skip per locale rather than per catalog (a skipped locale still
needs its OWN model wired to the shared catalog version), or recording and
surfacing "identical to <earlier locale>, not re-wired" so the admin sees why the
locale is unwired. Note a narrow config (e.g. "es-MX" alone with it-IT) wires the
locale fine; only the shared-default multi-locale run starves it. The stale outer
JF-513.3 comment that claimed the skip "returns the last uploaded version ... treats
as current" was corrected in the JF-706 change; this file is now the only record of
that historical wrongness.

AUDIT UPDATE 2 (2026-10-02, JF-706 gate-marker round, code-CONFIRMED): the starvation
analysis is verified end to end - CatalogPayload.cs:45 and CatalogSeedEnrichment.cs:211
are the only locale consumers, PhoneticSynonymGenerator.cs:42-54 dispatches purely on
LocalePrefix (empty for everything else), the seed union is locale-independent, and
UpdateInteractionModelAsync is the sole wiring path. The byte-identical classes are
exactly {es x3}, {fr x2}, {en-AU/CA/GB/IN/US + hi-IN x6}; under the default "*" config
8 of 16 synced locales NEVER get their model PUT (the first member of each class to run
uploads, the rest hash-skip forever; CatalogWiringGraft cannot help, it only preserves
existing wiring). PRIORITY RAISED to high. A JF-706 gate-marker finding adds the
hash-skip ordering hazard to this family - see JF-703's audit addendum.

IMPLEMENTATION DECISIONS (2026-10-02, made at pick-up as this file requires):

1. STATUS VALUE, decided from the consumer semantics (config.html keys
   `status === "SUCCEEDED"` for the green check and shows the raw status text gray
   otherwise, rendering Error unconditionally; DiagnosticsController counts
   Any(Status=="Succeeded") for ModelsDeployed and only FAILED/TIMEOUT as failures):
   the no-PUT frozen leg PRESERVES the previous entry's Status verbatim, composes
   a leading freeze clause + "; no PUT this run" (+ "; previous: <old Error>" when
   the prior row carried one, so a preserved FAILED status keeps its diagnostic)
   into Error, sets Source to the catalog-sync label (catalog sync authors THIS
   row; no consumer filters on Source, code-review F4), and stamps a fresh
   LastUpdated. Rationale: the live model WAS deployed and its SMAPI
   build status is unchanged; a distinct status would flip ModelsDeployed false on a
   single-locale setup even though the model is live, which is a false negative, and
   hardcoding SUCCEEDED would lie on a locale whose previous entry was FAILED. This
   is NOT the JF-705 "keep SUCCEEDED" shape: SUCCEEDED is preserved only because the
   previous entry already said it and it is still true about the live model. When NO
   previous entry exists (fresh install, everything frozen on the first run) the new
   entry carries Status "Skipped" (documented meaning extended: locale not carried
   by the skill, or a no-PUT leg with nothing to preserve) and Source
   CatalogSyncLedgerSource. The stale-green trap is closed because the entry is now
   REACHED and overwritten: the previous row is replaced, not shadowed (the ledger
   is a per-locale Collection keyed by SetLocaleModelStatus, which replaces in
   place; the UI reads the single current entry per locale).
2. HASH-SKIP / STARVED-LOCALE SHAPE, decided: a no-PUT leg with ZERO frozen types
   (all types zero-items or byte-identical hash-skips) writes NO ledger entry; the
   previous entry stays untouched because its model genuinely still references the
   current catalog version of its equivalence class. The PRODUCT gap (starved
   locales never get their model wired to the shared catalog) is a separate,
   catalog-keying fix, filed as JF-717 so it is not lost; this observability task
   does not grow a "not re-wired" clause for it.
3. JF-703 FOLD DECISION: the JF-703 audit ADDENDUM's ordering hazard (hash recorded
   before the upload; a failed upload plus the 401 retry hash-skips an unminted
   version) FOLDED here - it shares the uploadedPayloadHashes seam, the fix is the
   one-line record-after-successful-upload move, and the false ~578 comment is
   corrected in the same touch. JF-703's CORE (PUT-completed-vs-skipped tracking
   across the attempt loop after a 401 lands on the PUT itself) stays with JF-703,
   noted in both files.
4. FROZEN-CLAUSE FORMATTERS: kept per-surface per the JF-705 code-review decision
   (run-level log wording, completion line, and ledger wording are intentionally
   different); the two LEDGER sites (JF-705 PUT path and this JF-709 no-PUT path)
   share one FrozenLedgerClause helper so the entry text cannot drift.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 dotnet build passes with 0 errors (full solution, both TFMs; the only warnings are the pre-existing xUnit1030 pair on the untouched VideoAudioControllerTests ConfigureAwait line)
- [x] #2 dotnet test passes (4938/4938 net9.0 and 4938/4938 net10.0 on the final state; baseline 4935 + the 3 new pins)
- [x] #3 No new compiler warnings introduced (0 new; only the pre-existing xUnit1030 pair remains)
- [x] #4 Pin: all-types-frozen run with a pre-existing green entry surfaces the freeze in that locale's ledger entry (SyncUserLibraryAsync_AllTypesFrozen_PreExistingGreenEntry_PreservedWithFrozenClause: Status/LastUpdated preserved-and-refreshed, freeze clause leads Error, prior Error trails as "previous:", clean re-run leaves no clause) plus the no-prior-entry shape (SyncUserLibraryAsync_AllTypesFrozen_NoPriorEntry_LedgerRecordsSkipped) and the JF-703 addendum ordering pin (SyncUserLibraryAsync_ArtistUpload401_RetriedAttempt_ReuploadsUnmintedVersion: the 401-retried attempt RE-uploads the unminted version and the PUT wires all three catalogs)
- [x] #5 /simplify passed (4 angles; applied: the WriteLedgerEntry shell dedup of the two ledger writers, the "; see skip check" pointer for the duplicate JF-703 comment, the trimmed else-if call-site comment, the dropped redundant _artist401Served flag, and the shared LwaHappyHandler promoted from SmapiTokenRefresherTests' private HappyHandler; skipped with reasons: SaveConfiguration-per-locale cost mirrors the pre-existing PUT-path writer and amortizes over the 30-45 min sync cadence, and the altitude angle's merged-writer alternative was judged scaffolding-dedup-only against the same three internal branches)
- [x] #6 /code-review high passed (7 findings: 4 applied - F3 the prior Error now trails as "previous: ..." so a preserved FAILED status keeps its diagnostic, F4 Source is always the catalog-sync label, F5 the suffix shortened to "; no PUT this run" so the 3-type clause fits the 80-char window, F6 the 80-char rationale consolidated onto FrozenLedgerClause's doc; 1 skipped with reason - F7 the [^2] URL parse is already pinned end-to-end by Assert.Equal(1, VersionUpload401sServed), which fails if the parse breaks and the 401 never fires; 2 filed same-turn into JF-703's AUDIT ADDENDUM 2 - F1 the 401-on-one-type retry wires only the retried type's catalog on fresh installs, F2 the PUT-stage 401 retry shape reaches this same no-ledger-write hole; both are the JF-703 core's attempted-PUT tracking, not this observability fix)
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
The all-frozen no-PUT leg now writes a ledger entry at the leg boundary (RunLegAsync's
injection-gate else side), so a locale whose every catalog type froze no longer keeps
the previous run's green SUCCEEDED row. DESIGN DECISION (consumer-read): the entry
PRESERVES the previous Status (the live model is unchanged, so its recorded build
status is still true and the diagnostics panel's ModelsDeployed Any("Succeeded") stays
truthful for single-locale setups; NOT the JF-705 "keep SUCCEEDED" shape, which would
lie on a previously-FAILED locale), carries Status "Skipped" when no previous entry
exists (documented meaning extended), leads Error with the shared FrozenLedgerClause +
"; no PUT this run" and trails the prior entry's Error as "previous: ...", sets Source
to the catalog-sync label, and refreshes LastUpdated (the row is replaced, not
shadowed). The starved-locale (zero-frozen, all hash-skipped) shape deliberately
writes nothing and its PRODUCT fix is filed as JF-717. The JF-703 addendum's ordering
hazard FOLDED here: uploadedPayloadHashes records only AFTER a successful upload (a
failed upload now re-uploads on the 401 retry instead of hash-skipping an unminted
version; the accepted trade, a post-mint-lost duplicate version, is named in the
comment); JF-703's CORE (attempted-PUT tracking) stays with JF-703, now carrying AUDIT
ADDENDUM 2 with this review's two retry-shape residuals. Three new pins (preserve,
no-prior, 401-retry ordering); JF-705's partial-freeze pins green unchanged. Gates:
simplify 5 applied + adjudicated skips, code-review high 4 applied, 1 skipped with
reason, 2 filed into JF-703. Suites 4938/4938 both TFMs, 0 new warnings. No deploy.
<!-- SECTION:FINAL_SUMMARY:END -->
