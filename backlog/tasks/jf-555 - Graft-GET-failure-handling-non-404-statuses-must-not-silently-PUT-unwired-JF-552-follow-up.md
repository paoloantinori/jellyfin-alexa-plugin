---
id: JF-555
title: >-
  Graft-GET failure handling: non-404 statuses must not silently PUT unwired
  (JF-552 follow-up)
status: Done
assignee: []
created_date: '2026-09-13 09:50'
updated_date: '2026-09-13 09:56'
labels:
  - bug
  - catalog
  - interaction-model
  - observability
dependencies: []
references:
  - JF-552
  - JF-554
  - Jellyfin.Plugin.AlexaSkill/Alexa/SmapiManagement.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Catalog/CatalogWiringGraft.cs
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-552 code review (2026-09-13, high effort; review verified the fix's happy path against the live Alexa.NET.Management 5.10.0 artifact and the full suite 3681/3681).

Finding (Important): SmapiManagement.GetLiveModelJsonAsync (SmapiManagement.cs:246-250) treats EVERY non-success status as "no live model": returns null with a LogDebug line ("No live model for locale {Locale} ({StatusCode}); nothing to graft"). Only 404 means that; a transient 429/500/503 (or 401) on the wiring GET makes PutLocaleModelPreservingWiringAsync PUT the embedded model UNWIRED, silently reintroducing the exact JF-552 regression (catalog-wired SeriesName/JellyfinArtist/AlbumName downgraded to the static seed) until the next 12h catalog sync. The log is invisible at the default Information level AND factually wrong for a 500 (the live model exists; the GET failed). Contrast: the same method's outer catch logs network-level GET failures at WARNING (SmapiManagement.cs:210-213) - two gradations of one failure class with inverted visibility.

Plausible trigger: UpdateInteractionModelsAsync now issues GET+PUT per locale (double request volume) with only 500ms spacing; SMAPI throttling is documented on this API surface (SMAPI_DELAY notes; the sync leg needed 503 retry for catalog builds).

Acceptance criteria:
- 404 -> null (debug): first-deploy case unchanged.
- Other non-success statuses -> throw HttpRequestException (status + body in message) so the existing RetryHelper wrapper retries the whole GET+PUT (recovers throttling/transient 5xx) and persistent failure lands in failedLocales with the locale's OLD wired model intact (safer than silent unwiring).
- Minimum acceptable fix if throw is rejected: WARNING-level log with an accurate message distinguishing GET-failure from no-model, so wiring loss is greppable.

Related observations from the same review (fold here or into JF-554's transport service):
1. Retry classification change: the old typed PUT failed with Refit.ApiException (RetryHelper retried only 412/429); the new raw PUT throws HttpRequestException for ALL non-success, so permanent 4xx (400 validation; 401 with no in-loop token rotation) now burn 3 retries x ~14s per locale with no chance of success (SmapiManagement.cs:227-228 + RetryHelper.IsTransient). Consider a non-transient exception type for 4xx-or-selectively.
2. CatalogWiringGraft.ExtractWiring robustness: JsonElement.TryGetProperty throws InvalidOperationException on non-object nodes, so "valueSupplier": null or a scalar "valueCatalog" violates the stated malformed-body-yields-null contract (CatalogWiringGraft.cs:60-61, 66-67); today the broad catch one frame up degrades it to the same unwired PUT, but with a scarier log. Add ValueKind==Object guards.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
FIXED in the JF-552 commit (2026-09-13, before deploy): GetLiveModelJsonAsync now returns null ONLY for 404 (no live model); every other non-success status throws HttpRequestException with status+body, and the best-effort try/catch around the wiring GET was REMOVED so the throw propagates to the RetryHelper wrapper (retries the whole GET+PUT; persistent failure lands the locale in failedLocales with the wired model left intact). Pinned by SmapiManagementWiringTests.PutLocaleModel_TransientGetFailure_ThrowsInsteadOfUnwiredPut (GET 500 -> throws, no PUT issued). S3 (TryGetProperty throwing on non-object valueSupplier/valueCatalog) also fixed in-diff with ValueKind guards + ExtractWiring_NullOrScalarValueSupplier_SkipsWithoutThrowing; S2 (permanent-4xx retry burn, ~14s/locale) remains tracked in JF-554's transport consolidation as the reviewer recommended.
<!-- SECTION:FINAL_SUMMARY:END -->
