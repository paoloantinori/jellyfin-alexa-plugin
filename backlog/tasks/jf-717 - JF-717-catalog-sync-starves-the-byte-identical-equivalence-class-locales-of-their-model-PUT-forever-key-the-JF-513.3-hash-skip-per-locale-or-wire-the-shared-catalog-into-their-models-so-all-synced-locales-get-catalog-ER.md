---
id: JF-717
title: >-
  JF-717 - catalog sync starves the byte-identical equivalence-class locales of
  their model PUT forever; key the JF-513.3 hash-skip per locale (or wire the
  shared catalog into their models) so all synced locales get catalog ER
status: Done
assignee: []
created_date: '2026-10-02 12:00'
updated_date: '2026-10-03 04:37'
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

DESIGN DECISION (written before coding, 2026-10-03): SHAPE 2 (wire the shared
catalog) with a keying refinement, NOT shape 1 (per-locale hash keys). The JF-513.3
skip keeps suppressing the redundant catalog VERSION upload, but the skip now hands
back the version minted by the byte-identical earlier leg, so RunLegAsync's minted
table fills naturally and the unchanged injection gate (minted.Count > 0) fires the
per-locale model PUT wiring the SHARED (catalogId, version) pair. The JF-706 seam is
untouched: the Minted(type) extraction and the six positional arguments stay as
they are (the JF-716 hand-ternary concern cannot recur: nothing at the seam
changes), and the JF-495 rule survives in its strict form: a forwarded id is always
paired with a version minted in THIS run (by the class's first member), never a
stored id with a null version; zero-item and frozen types still contribute nothing.

Keying refinement: the run-scoped dictionary becomes keyed PER PAYLOAD
("{type}:{catalogId}:{payloadHash}" -> minted version) instead of per catalog with a
value comparison. The old single-slot-per-catalog keying is order-sensitive: with an
interleaved locale order (the production "*" list is manifest-order; alphabetically
the hi-IN leg runs AFTER the fr legs, and hi-IN's no-generator payload is
byte-identical to the en cluster's), each class switch evicts the slot and re-mints
byte-identical content, so the old keying both starved models AND burned the exact
quota it existed to save (today's "*" run re-mints hi-IN's payload per type).
Per-payload keying makes the dedup exactly "one upload per byte-identical payload
per catalog per run", order-independent.

Weighing vs shape 1: (1) mints one version per LOCALE (16 uploads per type per run
under "*"), the exact SMAPI quota burn JF-513.3 exists to prevent, and buys nothing
shape 2 lacks; an independent version per locale adds no capability because the
catalog CONTENT is identical by definition of the class. Shape 2 keeps uploads at
one per class per type and pays only model PUTs, which are not the quota JF-513.3
protects, are idempotent on unchanged content (GET-modify-PUT re-submits the live
model with the injection applied; intent/sample counts are unchanged by injection,
so the JF-495 canary keeps matching), and are frequency-bounded by the 12h
CatalogSyncTask gate. Shape 3 (surface-only) rejected: fixes nothing. STATED COST
OF SHAPE 2 (DoD item 2): model PUTs per full "*" run rise from ~9 (one per minting
locale: it, de, en-AU, es-ES, fr-CA, hi-IN re-mint, ja, nl, pt) to 16 (one per
synced locale); catalog version uploads per type DROP from ~9 to ~8 (the hi-IN
re-mint disappears); each added PUT costs a settle+build+canary cycle (order 90s
each per the JF-497 serialized-queue budget) inside a sync whose JF-544 per-leg
token refresh already covers longer runs.

Test note: the JF-513.3 family pin
(LibrarySyncServiceSeriesTests.SyncUserLibraryAsync_IdenticalPayload_SecondLocaleLeg_
SkipsVersionUpload) lost its discriminating power when JF-543 started filtering
ar-SA out of the sync entirely (its config "ar-SA" now runs it-IT only, so no second
leg exists); landing restores it with a real same-class pair (es-MX + es-US).

GATE-MARKER TAIL (2026-10-03, orchestrator review of commit 9b612d48, 2 low findings; all
six scrutiny axes verified mechanically - the reviewer re-ran the red proofs itself by
reverting the skip's return and watching exactly the three fix-pins flip while the
boundary pin stayed green, then restored the worktree byte-clean; the merge into main
confirmed conflict-free via merge-tree): F1 APPLIED (syncedAnyUser now set BEFORE the
sync call so a mid-burst THROW still counts as a burst the inter-user spacing must
separate - the old after-await placement skipped the spacing exactly when the failed
sync had fired SMAPI calls); F2 APPLIED (the fake's per-skill status map is now driven
by a mutable ServedStatusLocales set defaulting to the four synced locales, so a future
pin names its locale in the arrange instead of silently burning the fallback tracker's
~150s per-locale budget; the status GET is per-skill with no locale parameter, so the
map must enumerate its keys). Affected classes 16/16 both TFMs after the tail;
independent suite 4990/4990 both TFMs on the worker commit; merged-tree follows.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [x] #1 Under the default "*" config, every synced locale in each byte-identical equivalence class carries catalog valueCatalog references in its live interaction model after a full sync run. DONE: the JF-513.3 skip now returns the version minted by the byte-identical earlier leg (the run-scoped memo is keyed PER PAYLOAD, "{type}:{catalogId}:{payloadHash}"), so RunLegAsync's minted table fills and the unchanged minted.Count > 0 gate fires the per-locale model PUT wiring the class's shared (catalogId, version); the JF-706 seam (Minted extraction, six positional args) is untouched. Pinned end-to-end through SyncUserLibraryAsync with two members of the REAL es class: LibrarySyncServiceEquivalenceClassTests pins both locales' PUTs wired (the skipped locale pins the class's fresh mint, not it-IT's different-payload version, via per-catalog incrementing fake versions), RED on the pre-fix code (3 fix-pins failed, the de-DE boundary pin passed as expected). The "*" list resolution itself rides Alexa.NET.Management's internal Refit client (no hermetic seam) and is upstream of the fix; pinned separately by LibrarySyncServiceLocaleTests.
- [x] #2 The JF-513.3 quota goal is not regressed beyond the chosen shape's stated cost (documented in the task on landing). DONE: the stated cost is in the DESIGN DECISION above (model PUTs per "*" run ~9 -> 16, catalog uploads per type ~9 -> ~8); the pins assert the upload counts stay one-per-distinct-payload (2 uploads for it + one es class under "es-MX,es-US", both orderings), and the restored JF-513.3 family pin (SeriesTests) re-asserts the skip on a real class pair after its ar-SA config had been vacuous since JF-543's filter.
- [x] #3 Pins: two-locale same-class run wires BOTH locales; result independent of which class member runs first. DONE: SyncUserLibraryAsync_SameClassSecondLocale_GetsModelPutWiredWithSharedVersion and SyncUserLibraryAsync_SameClassReversedOrder_BothLocalesStillWired (both orderings wire both locales with the same shared version); plus the boundary pin SyncUserLibraryAsync_UniquePayloadLocale_UploadsAndPinsItsOwnVersion (de-DE unaffected) and the review pin SyncUserLibraryAsync_MidLeg401AfterUpload_RetryPutsFromMemoHit (the 401-retry x memo interplay: pre-fix the retried leg silently skipped the PUT, counterfactual-proven 1-vs-2 PUT requests).
- [x] #4 dotnet build 0 errors, dotnet test green both TFMs, no new warnings. DONE: dotnet build Jellyfin.Plugin.AlexaSkill.sln 0 errors, only the pre-existing xUnit1030 pair (VideoAudioControllerTests.cs:1337, untouched); dotnet test -m:1 4990/4990 net9.0 AND net10.0 (baseline 4986 + 4 pins), exit 0.
<!-- DOD:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Closed by the orchestrator after the full cycle: worker commit 9b612d48 + gate-marker tail 284a3207, merged as 6bde26f9. The starved-locale wiring: shape 2 (wire the shared catalog) with per-payload memo keying ({type}:{catalogId}:{payloadHash} -> version), so the JF-513.3 skip still suppresses the redundant upload but returns the byte-identical earlier leg's minted version; the unchanged minted.Count > 0 gate fires the per-locale model PUT; 8 of 16 previously-never-wired locales become wired under the default config at model-PUT cost only. A second starvation shape (401 on the model PUT after the upload minted, retrying into a silently unwired success) found and fixed at the worker's own review with a counterfactual red proof. The vacuous legacy SeriesTests pin restored to a discriminating es pair. Worker gates green (simplify 6 applied, 2 filed as JF-725, 1 declined on merits; code-review high 5/5 applied). The orchestrator gate-marker verified all six scrutiny axes mechanically - re-running the red proofs itself (reverting the skip's return flipped exactly the three fix-pins; the worktree restored byte-clean) and confirming the main merge conflict-free via merge-tree; its 2 findings applied in the tail (the spacing flag set before the call so failed bursts still space; the fake's status map set-driven). Suites: worker and orchestrator independent 4990/4990 both TFMs, affected classes 16/16 after the tail, merged-tree 5011/5011 both TFMs exit 0 on both split legs. Production surface changed (LibrarySyncService, CatalogSyncTask): deployed in the post-closure deploy. The live verification (catalog ER activating in es/fr/en locales that never had it) rides the next catalog sync on the box.
<!-- SECTION:FINAL_SUMMARY:END -->
