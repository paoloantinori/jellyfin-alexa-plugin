---
id: JF-717
title: >-
  JF-717 - catalog sync starves the byte-identical equivalence-class locales of
  their model PUT forever; key the JF-513.3 hash-skip per locale (or wire the
  shared catalog into their models) so all synced locales get catalog ER
status: To Do
assignee: []
created_date: '2026-10-02 12:00'
labels:
  - catalog
  - interaction-model
dependencies:
  - JF-709
references:
  - >-
    backlog/tasks/jf-709 -
    JF-709-all-types-frozen-sync-run-writes-no-ledger-entry-so-the-admin-UI-keeps-the-previous-runs-green-SUCCEEDED-record-the-no-PUT-freeze-at-the-leg-boundary.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed 2026-10-02 from the JF-709 pick-up: JF-709's audit updates code-confirmed the
PRODUCT half of the starvation analysis and this file is where the fix lives. JF-709
itself closed only the observability half (the all-FROZEN no-PUT leg now writes a
ledger entry) and explicitly decided a no-PUT leg with ZERO frozen types (all types
zero-items or hash-skipped) writes no ledger clause, because its live model genuinely
still references the current catalog version of its equivalence class.

THE PRODUCT GAP (code-confirmed in the JF-706 gate-marker round, restated): the
uploaded catalog payload depends on the locale only through the synonym generator,
keyed purely on language prefix (CatalogPayload.cs:45, CatalogSeedEnrichment.cs:211,
PhoneticSynonymGenerator.cs:42-54 dispatches on Util.LocalePrefix.Of), and the seed
enrichment is a locale-independent union. The byte-identical classes are therefore
exactly {es-ES, es-MX, es-US}, {fr-FR, fr-CA}, and the no-generator cluster
{en-AU, en-CA, en-GB, en-IN, en-US, hi-IN} (generators cover it/de/es/fr/pt/ja/nl
only; ar-SA is excluded by JF-543). Under the default "*" config only the FIRST
member of each class to run uploads and gets the model PUT; every later member
returns null versions for all three types on EVERY run, the injection gate skips the
PUT, and since the embedded models carry zero valueCatalog blocks those locales'
interaction models NEVER receive catalog references: catalog ER never activates
there. That is 8 of the 16 synced locales today. CatalogWiringGraft cannot help (it
only preserves existing wiring). A narrow config (e.g. "es-MX" alone with it-IT)
wires the locale fine; only the shared-default multi-locale run starves it. Also
note: the equivalence-class FIRST member can itself be hash-skipped after a
failed-upload retry, except that specific ordering hazard was fixed in JF-709
(hash recorded only after a successful upload).

FIX SHAPES to weigh at pick-up (from the JF-709 audit, not yet chosen):
1. Key uploadedPayloadHashes per LOCALE (or per locale-prefix class) instead of per
   catalog id, so each locale of a class uploads its own version and gets its own
   PUT. Cost: re-mints byte-identical content per locale, the exact SMAPI quota
   burn JF-513.3 exists to prevent - but only for classes whose members are not yet
   wired, if combined with shape 2.
2. Keep the catalog-side skip but wire the SHARED catalog id/version into every
   later member's model PUT anyway: the injection does not require a freshly minted
   version, only a (catalogId, version) pair that exists on SMAPI. The first
   member's minted pair is valid for the whole class. This gets catalog ER into all
   16 locales with zero extra uploads. Requires threading the class's minted pair
   into RunLegAsync's injection gate (currently all-null skips the PUT entirely).
3. Surface-only fallback: record "identical to <earlier locale>, not re-wired" in
   the ledger so the admin sees why the locale is unwired (cheapest, fixes nothing).
Shape 2 looks dominant; verify against UpdateInteractionModelAsync's contract that
re-PUTting an unchanged model with catalog references is idempotent, and that SMAPI
model builds are not the scarcer quota.

Pins when landing: a two-locale run in one equivalence class results in BOTH locales
carrying valueCatalog references after the run; the it-IT-first ordering does not
matter; the no-generator cluster stays within JF-513.3's quota goal.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] Under the default "*" config, every synced locale in each byte-identical equivalence class carries catalog valueCatalog references in its live interaction model after a full sync run
- [ ] The JF-513.3 quota goal is not regressed beyond the chosen shape's stated cost (documented in the task on landing)
- [ ] Pins: two-locale same-class run wires BOTH locales; result independent of which class member runs first
- [ ] dotnet build 0 errors, dotnet test green both TFMs, no new warnings
<!-- DOD:END -->
