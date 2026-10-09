---
id: JF-826
title: >-
  After JF-823 the AudiobookTitle type is catalog-backed, but the session
  Dialog.UpdateDynamicEntities push still replaces its vocabulary with the
  budget-truncated library list at turn 2+
status: To Do
labels: [audiobooks, catalog-sync, dynamic-entities]
---

## Description


## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed by the JF-823 worker (2026-10-09) from the /code-review high gate, finding F1.

JF-823 made `AudiobookTitle` catalog-backed (valueSupplier in the saved model:
library titles + the 22-value it-IT seed). `DynamicEntityBuilder`'s per-session
push (the arm around line 197/221, untouched by JF-823) still writes a STATIC
values block onto the same type in the turn-2+ response. Two competing
vocabularies, never reconciled:

- Post-first-sync, turn-1 book selection resolves against the catalog (library +
  seed, catalog-version backed).
- A turn-2+ response carrying the audiobook dynamic block REPLACES that
  vocabulary for the session with the `baseBudget`-truncated library list, so a
  seed-only title that routed at turn 1 can silently stop resolving later in the
  same session.

Series has the same shape (proven-tolerated precedent, weakened severity), and
the same question now applies to it. Decide per type: drop the dynamic push for
catalog-backed types (the catalog already carries the same library names plus
the seed), keep it, or make the push seed-aware. Needs the JF-684
selection-gating lens (a catalog type with a vocabulary the NLU misses selects
NO intent; a dynamic block that REMOVES vocabulary has the same risk
in-session).

References: Jellyfin.Plugin.AlexaSkill/Alexa/DynamicEntities/DynamicEntityBuilder.cs
(BuildSlotValues audiobook/series arms), Alexa/Catalog/CatalogSlotTypes.cs
(CatalogSlotTypeNames).

GATE-MARKER CONFIRMATION (2026-10-09, JF-823 marker finding 6, sharper
scenario): the overwrite is MID-CONVERSATION, not just turn-2-vs-turn-1: a
catalog-wired title routes turn 1, the session's DynamicEntities directive
then replaces the catalog supplier with the budget list, and catalog titles
outside that budget stop routing one-shot FOR THE REST OF THE SESSION. Any
fix must consider suppressing the audiobook dynamic push entirely once the
catalog wiring is live (the Series precedent: check what Series does today
and mirror its disposition or fix both).

## Resolution (2026-10-09, the JF-826 worker)

SERIES PRECEDENT ESTABLISHED: Series suffers the SAME overwrite silently; there
was no suppression, no carve-out, and no budget exception for it.
`CatalogSlotTypes.Names` line 44 targets `[CatalogType.Series] = "SeriesName"`
(catalog-wired everywhere since JF-493, CatalogSlotTypeNames line 106),
`DynamicEntityBuilder.Build` line 220 pushed it mid-session whenever the intent
was TV-context (DynamicEntitiesInterceptor line 105), and (WORSE than the filed
scenario) the last-played distribution mapped Episodes AND Movies onto
SeriesName (`GetSlotTypeForItem`, lines 449-457), so every NEW session after
watching TV replaced the whole catalog (138 values live) with the 1-5 recent
names from turn 2. The same last-played arm put ONE recently played book onto
AudiobookTitle on every fresh session after listening (`IsAudioBook` mapping,
line 461), independent of the includeAudiobooks flag. So the fix had to cover
the type-name level (AddSlotType inputs AND DistributeLastPlayed), not just the
includeSeries/includeAudiobooks arms. One mechanism, both types, per the JF-823
arity lesson: AudiobookTitle AND SeriesName fixed together (plus JellyfinArtist
on the JF-415 catalog-backed musician locales, whose resolved target is a wired
name with a live catalog).

SHAPE CHOSEN (c-refined): skip the dynamic push per SLOT-TYPE NAME once (a) the
user's stored catalog id for that type exists (the sync's write-back:
ArtistCatalogId/SeriesCatalogId/AudiobookCatalogId) AND (b) the locale can host
catalog wiring at all (`CatalogManager.IsCatalogWiringSupported`, the JF-543
ar-SA gate; the ids exist there too because catalogs are type-scoped, minted
by the other locales' sync legs). Implemented as
`DynamicEntityBuilder.ResolveCatalogWiredTypeNames`; the suppression set keys on
the RESOLVED target names, so the 11 built-in-musician locales keep their push
(AMAZON.Musician is not a wired name) and a future ResolveMusicianSlotType flip
joins by construction. Rejected (a) pure-static removal: it would strand
unlinked/pre-first-sync users, whose saved model is still the static seed and
for whom the library list is strictly better. Rejected duplicating the
CatalogSyncLocales scope parse (a second owner of
LibrarySyncService.ResolveSyncLocalesAsync's rule): accepted residual, a
deliberately narrowed CatalogSyncLocales leaves unlisted locales static-seeded
while their type ids still exist, so they lose the push too; their sessions
become consistent with their turn-1 (seed-only) vocabulary instead of
turn-2-only library recall. Album stays pushed: its dynamic target (AMAZON.Album)
is a wired name in NO locale (JF-332), so the suppression set deliberately omits
it. The interceptor is untouched (the Dialog.* coexistence skip and the
AudioPlayer/PlaybackControl skips are unchanged); an everything-suppressed
build returns null and the interceptor simply adds no directive, leaving the
session on the model's catalog vocabulary.

The structural IL ledger
(LibrarySyncServiceStructureTests.StoredCatalogIdAccessors_AreCalledOnlyByTheWiringTableLambdas)
was amended in the same change per the JF-824 ledger convention: getters now
carry AT MOST one sanctioned runtime reader (the suppression predicate;
setters stay closed to the wiring-table lambda). Residual accepted there: the
ledger cannot require the predicate to read every getter (Album's target is
not wired), so the predicate's per-type completeness is pinned behaviorally in
DynamicEntityBuilderCatalogSuppressionTests instead.

Post-fix reality on the live box (default CatalogSyncLocales `*`): 16 wired
locales keep the full catalog vocabulary for the whole session on every type;
ar-SA keeps the dynamic push as its only library vocabulary. One note on
binding semantics: on a device bound to one library, the push's per-library
narrowing of wired types is gone, but turn-1 vocabulary was ALREADY the
unscoped catalog, so the session is consistent with turn 1 rather than newly
exposed; handler-side library filters still scope playback.

GATES (2026-10-09): /simplify (4 agents) found one real item, applied: the
redundant `Count > 0` guard around the last-played filter dropped (the filter
itself is the identity on an empty set); the log-line guard kept (it suppresses
a per-session debug line, a real output concern). Reuse/efficiency/altitude
agents: clean; the altitude agent verified Build is the single choke point (the
only `new DynamicEntitiesDirective()` in the assembly, the interceptor its only
caller), so the suppression covers every present and future push path.
/code-review high returned 6 findings, dispositioned same-turn:
F1 APPLIED: the wired-type skip moved INTO the last-played selection loop
(a post-hoc filter let wired-type recency consume the 5-slot cap, the
seenNames dedup, and budget before the filter ran, crowding pushable recency
out of the same 15-item window; new pin
Build_CatalogWiredAudiobook_LastPlayedWiredItemsDoNotCrowdOutPushable covers
the five-books-then-a-song shape; reasoned, not run: that pin is red against
the pre-fix post-hoc-filter shape).
F2 APPLIED: the raw-SMAPI-PUT-strips-wiring window documented as an accepted
residual on the predicate (no runtime per-locale per-type wiring-present
signal exists; the JF-721 no-ledger-parsing rule forecloses the ledger as one).
F3 REJECTED: comparing the resolved musician target against
CatalogSlotTypeNames[Artist] IS the uniform target-equals-wired-name rule, not
a duplication; the feared JF-415 deployed-vs-committed divergence makes the
push inert there (it targets AMAZON.Musician, a name no live slot references),
not defective.
F4 REJECTED as already covered: a fifth synced type fails
CatalogSlotTypesTests.CatalogSlotTypeNames_IsExactlyTheFourSyncedTypes loudly,
which is exactly the review event that must also extend
ResolveCatalogWiredTypeNames; the obligation is now named on the predicate's
doc.
F5 APPLIED: the ledger's predicate whitelist resolved by reflection to the
exact MethodBase with a not-null tripwire, so renaming the predicate fails the
pin loudly instead of silently vacating the getter allowance.
F6 APPLIED: the banned "word - word" separator removed from the new test's
prose (pre-existing prose in the structure-test file left untouched, no
drive-by edits).
<!-- SECTION:NOTES:END -->
