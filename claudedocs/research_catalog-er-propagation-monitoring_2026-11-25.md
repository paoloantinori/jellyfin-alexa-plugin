# Research Report: Monitoring catalog ER index propagation in SMAPI

**Date**: 2026-11-25
**Depth**: exhaustive
**Confidence**: MEDIUM (the negative finding, no dedicated ER-readiness API, is documented absence plus empirical probing; the propagation-lag mechanism is inference from our own incident, not Amazon-documented)

## Executive Summary

There is NO documented SMAPI API that exposes the readiness of a catalog version's values for entity resolution. The only status surface Amazon documents for interaction model catalogs is the create/update request status (`FAILED`/`IN_PROGRESS`/`SUCCEEDED`), which reflects the version RESOURCE build, not the downstream NLU/ER index. The practical monitors available are: (1) the values-read API (`get-interaction-model-catalog-values`) proving the values landed, (2) a skill model rebuild referencing the version (whose build SUCCEEDED status at least forces the NLU pipeline to consume the catalog), and (3) profile-nlu itself as the end-to-end readiness probe. In our live incident, the values landed and the series catalog (160 values) went ER-live while the artist catalog (1,137 values with the synonym family, 8 versions minted in rapid succession) did not within ~1.5 hours.

## Findings

### 1. The documented status surface covers the version resource, not the ER index

The Interaction Model Catalog Management REST API (developer.amazon.com, docs/alexa/smapi/interaction-model-catalog-api.html, scraped 2026-11-25) exposes exactly one status operation: "Get catalog status" (`GET /v1/skills/api/custom/interactionModel/catalogs/{catalogId}/updateRequest/{updateRequestId}`, where updateRequestId accepts the literal `~current`). Its response carries `lastUpdateRequest.status` with valid values `FAILED`, `IN_PROGRESS`, `SUCCEEDED`, plus the version number. This is the status of the create-or-update REQUEST (the catalog version resource being accepted and its values ingested). Nothing in the document exposes any later-stage readiness: no ER index state, no NLU propagation flag, no sub-resource statuses beyond the request itself. The doc contains no timing statement at all about when values become available to entity resolution.

Empirical note from our incident: this endpoint currently returns HTTP 500 for both our fresh catalogs (artist and series alike) via the ask CLI (`get-interaction-model-catalog-update-status ... --update-request-id "~current"`), so it is also unreliable as a probe right now; the metadata endpoint (`get-interaction-model-catalog-definition`) works and shows totalVersions but no status.

### 2. The values-read API proves ingestion, not ER activation

`GET .../catalogs/{catalogId}/versions/{version}/values` (CLI: `get-interaction-model-catalog-values -c <id> --vers <n>`) returns the ingested values, paginated 50 per page alphabetically. We verified the artist catalog v8 first page holds real library artists in order, which proves Amazon ingested the values correctly. This is the strongest "the data is there" monitor available, but it reads the catalog store, not whatever index the NLU/ER pipeline consults; a version can pass this check and still not resolve in profile-nlu (exactly our artist state).

CLI gotchas documented during the probe: the flag is `--vers` (not `--version`); `--max-results` makes the CLI emit garbage (the string "2.30.7"); the `--next-token` expects the real pagination token from the previous response, not a synthetic page string.

### 3. No documented delay for catalog ER availability, anywhere

Neither the catalog API reference, the validate-slot-values doc, the dynamic-entities doc, nor the ASK CLI reference states how long catalog values take to become ER-active after version creation. Community sources (Stack Overflow threads on dynamic entities not updating) concern `Dialog.UpdateDynamicEntities` (the per-request directive), a different mechanism that replaces entities per request and has no persistence lag; no authoritative thread on interactionModel catalog ER lag surfaced. Amazon's developer console "Test Your Utterances" flow implies immediacy in normal operation but documents nothing about large catalogs. Conclusion: the delay characteristics are undocumented; our size-dependent observation (160 values live in ~40 min; 1,137 values not live at ~1.5 h) is our own empirical data point, not corroborated by documentation.

### 4. A model rebuild referencing the version is the documented consumption trigger, and the NLU build status is checkable

Catalog values reach ER through the interaction model that references them via valueSupplier: after the model update (PUT with the catalog reference), the skill's model build status (`get-skill-status` per locale) reports the NLU build SUCCEEDED. That build logically consumes the catalog version. Our recovery sync's model PUTs all reported SUCCEEDED builds referencing the fresh versions, so the NLU pipeline has consumed them; the artist gap therefore sits in whatever runtime index profile-nlu/ER consults after the model build, which no API surfaces. A model rebuild does NOT appear to force or accelerate the runtime ER index beyond its own build completing (our identical-content rebuild of en-US changed nothing), consistent with the index being populated asynchronously downstream of the build.

One incidental finding from the versions list: the sync minted EIGHT versions of the artist catalog in rapid succession (v1 15:09 through v8 15:33, one per locale leg because per-locale phonetic synonyms differ), while the series catalog versions show the same per-locale pattern but far fewer values each. Rapid version churn on a large catalog is a plausible confounder for slow ER settlement: the index may repeatedly invalidate. No documentation addresses this either.

### 5. profile-nlu evaluates "the most recent interaction model", same stage the device uses for development

The Utterance Profiler API doc (developer.amazon.com, docs/alexa/smapi/utterance-profiler-api.html) states it profiles "an utterance against the most recent interaction model for your skill" at the given stage, without calling the skill backend. There is no documented distinction between a "fresher" or "staler" ER index for the profiler versus the device: both consume the stage's built model. Our own cross-checks agree: when series ER went live, profile-nlu and our expectations aligned immediately; the profiler is the same-dev-stage index the simulator uses (the simulator adds ASR and invocation layers, not a different ER store).

## Confidence Assessment

- HIGH: no dedicated ER-readiness API exists in the documented catalog surface (the operations table is exhaustive and contains only the request status); values-read proves ingestion only; profile-nlu targets the most recent dev-stage model.
- MEDIUM: the propagation-lag mechanism itself (size-dependent, downstream of the NLU build) is inference from our single incident plus the absence of any documented immediate-availability guarantee; the rapid-version-churn confounder is untested.
- LOW/unverified: whether the current 500s on the update-status endpoint are transient or related to our catalogs' youth; any undocumented internal timing SLAs.

## Practical monitoring recipe (what we can actually do)

1. Values check: `ask smapi get-interaction-model-catalog-values -c <id> --vers <v>` first page confirms ingestion.
2. Build check: the model PUT statuses (our sync ledger) confirm the NLU builds consumed the references.
3. ER readiness: there is no API; probe end-to-end with profile-nlu on one exact-name utterance per catalog family (cheap, ~6 calls for all families).
4. If a large catalog stays dead for hours after (1)-(3) pass: re-mint a version (a values-changed payload forces a new version) and/or contact Amazon developer support, since at that point the documented surface says everything is healthy.

## Sources

1. Interaction Model Catalog Management REST API Reference (developer.amazon.com/en-GB/docs/alexa/smapi/interaction-model-catalog-api.html): operations table, Get catalog status section, status enum FAILED/IN_PROGRESS/SUCCEEDED, no timing statements.
2. ASK CLI Command Reference (developer.amazon.com/en-GB/docs/alexa/smapi/ask-cli-command-reference.html): get-interaction-model-catalog-update-status delegates to the REST reference.
3. Utterance Profiler REST API Reference (developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html): "most recent interaction model", no ER-index distinction.
4. Validate Slot Values (developer.amazon.com/en-US/docs/alexa/custom-skills/validate-slot-values.html): validation semantics only, no availability timing.
5. Live probes against our vendor account (2026-11-25): update-status 500s on both fresh catalogs; values-read v8 first page real; definition endpoint totalVersions=8 with per-locale version churn.
