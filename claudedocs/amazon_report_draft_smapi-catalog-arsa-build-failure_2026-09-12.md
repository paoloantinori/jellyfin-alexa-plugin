# DRAFT Upstream Report: ar-SA interaction-model full build fails with catalog-backed slot types (no error detail)

Status: DRAFT, not submitted. Posting is an outward-facing action; Paolo submits or explicitly authorizes sending. Suggested venue: Amazon Developer Forums ("Alexa Skills Kit" category, bug report) and/or the Alexa developer support "Contact Us" path, since a forum post creates a public searchable record.

---

## Title

ar-SA interaction model build fails (LANGUAGE_MODEL_FULL_BUILD, no error detail) when a custom slot type references a catalog; failure rate scales with catalog value count

## Body

We maintain a skill live in 17 locales that syncs the user's media library into SMAPI interaction-model catalogs (JellyfinArtist ~1135 values, AlbumName ~895, SeriesName ~138) and references them from each locale's interaction model via `valueSupplier` / `CatalogValueSupplier` (catalogId + version), exactly as documented in the Interaction Model Catalog Management REST API and the interaction model schema.

**Observed in ar-SA only:** after the model PUT is accepted (HTTP 202), the asynchronous build's `LANGUAGE_MODEL_FULL_BUILD` step fails while `DIALOG_MODEL_BUILD` and `NAME_FREE_INTERACTION_BUILD` succeed, and `lastUpdateRequest.errors` is EMPTY, so the failure carries no diagnostic at all. The same catalog-wired model builds reliably in every other locale.

**Isolated bisection (2026-09-12, direct REST PUTs, outside any sync):** we rebuilt the exact catalog-wired ar-SA model from the live model plus the same injection the sync performs, then bisected it. Results (identical payload per row, N = repeat PUTs of the SAME JSON):

| Referenced catalog | Values | Builds passed |
|---|---|---|
| Artist catalog | 1135 | 0 of 7 |
| Album catalog | 895 | 1 of 5 (the same bytes passed once, then failed 4 times) |
| Series catalog | 138 | 3 of 3 |
| Artist catalog definition, de-DE control | 1135 | 3 of 3 |

The album row proves per-build nondeterminism on byte-identical payloads; the de-DE row proves the payload shape is valid (it is also field-identical to the live, working it-IT definitions). The embedded ar-SA model, with plain AMAZON.Musician slots, builds reliably in the same skill. We confirmed all documented limits are satisfied by a wide margin (model size, 250 intents, 350 types+intents, 140-char values and synonyms; no value-count limit is documented for catalogs or slot types).

**Workaround we shipped:** we exclude ar-SA from catalog wiring and keep its embedded model (with AMAZON.Musician slots), which builds and serves normally. So this is a report, not a support request: the per-locale build trainer appears to fail when deep-training on catalog values in ar-SA, with a failure probability that grows with the value count, and it reports no error detail. QUick build step stays IN_PROGRESS in the failing runs, which suggests the failure is in the full NLU training pass rather than validation.

Happy to share skill ID, catalog IDs, exact payload JSON, and the full request timeline with Amazon engineers. Ask: is this a known limitation of the ar-SA build pipeline, is there a per-locale value-count threshold we should stay under, and could failed builds include the violating detail in `errors`?

---

## Attachments to prepare before submitting

1. Skill ID + vendor ID (from `ask smapi list-skills-for-vendor`; do not bake a stale one in).
2. Catalog IDs and versions for the three catalogs (from `list-catalogs-for-skill` + the versions endpoint).
3. The failing payload JSON (reconstructable from the JF-543 harness: `/tmp/jf543_inject.py` + the live model; regenerate fresh at submission time).
4. Timeline excerpt: the two natural-sync failures (2026-09-12 02:08 and 05:36 CEST) + the bisection table above.

## Notes

- The report deliberately leads with reproducible facts and the evidence table; no speculation about internals beyond the clearly-labeled "suggests".
- Fix the "QUick" typo before sending (left as a marker to re-read the paragraph cold before posting; verify the QUICK_BUILD observation wording against /tmp/jf543_put_out.json logs at submission time).
- After posting, record the forum thread URL in JF-543's task notes.
