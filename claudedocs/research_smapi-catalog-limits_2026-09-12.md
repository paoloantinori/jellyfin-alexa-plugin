# Research Report: SMAPI interaction-model catalog limits and the ar-SA build failure (JF-543)

**Date**: 2026-09-12
**Depth**: deep
**Confidence**: MEDIUM-HIGH (official limits HIGH; defect characterization HIGH on our own evidence; public-report absence is an absence finding, not proof)

## Executive Summary

Amazon documents **no value-count limit** for catalog-backed slot types: the official interaction-model limits are 1.5 MB model size, 250 intents, 350 intents-plus-slot-types, and 140 characters per value or synonym, and the catalog REST API documents only pagination and 255-character description caps [1][2][5]. Our measured failure (a catalog with 1135 values referenced from an ar-SA model failing the FULL build 0/7 times, 895 values failing 4/5, 138 values passing 3/3, while de-DE passes 3/3 with the identical definition) therefore sits far inside every documented limit and is an **undocumented, locale-specific, value-count-correlated Amazon-side build defect**. No public report of this failure class was found on Stack Overflow, the Amazon developer forums, or GitHub, which makes our JF-543 evidence trail likely the best-documented instance anywhere; the practical conclusion is that the shipped exclusion (`CatalogWiringUnsupportedLocales = {ar-SA}`) is the correct workaround, and an upstream report to Amazon is now well-supported by evidence.

## Findings

### Official limits: nothing we violated

The official interaction-model limits table [1] lists: total model size 1.5 MB; 250 intents; 350 slot types and intents combined; 140 characters per slot value; 140 characters per synonym. The catalog management REST API [2] documents per-value limits only for metadata (`catalog.description` and version `description` at 255 characters) and pagination caps (`maxResults` 1-250 for version listing, 1-50 elsewhere); the create-version endpoint documents no maximum number of values. The interaction-model schema [3] defines `valueSupplier`/`valueCatalog` with only type/shape constraints (catalogId and version strings), and attaches no locale or value-count restriction to it. The best-practices doc [4] explicitly frames value count as bounded only indirectly ("The total number of custom slot type values depends on the overall size of your interaction model"), i.e. by the 1.5 MB size cap, which a catalog reference does not consume since the values live outside the model JSON.

Relevance to JF-543: our ar-SA payload after injection is a few hundred KB of model JSON plus three catalog references (1135 + 895 + 138 values); every documented ceiling is satisfied by a wide margin. The failure cannot be attributed to any limit Amazon publishes.

### The community "50,000 values" ceiling is folklore-adjacent and irrelevant to us

Stack Overflow answers and Amazon developer forum threads cite a maximum of ~50,000 values for a custom slot type [6][7], with one forum contributor reporting ~20,000 values working in production [7]. The 50,000 figure does not appear in the current official limits table [1], so it is either console-era guidance, an older documented limit, or community folklore; either way our catalogs (1135 values at the largest) are two orders of magnitude below it. Community reports about value counts concern recognition quality (values being missed at runtime), not build failures; one forum thread reports recognition degradation past small value counts (~18) [8], which is a different phenomenon from our FULL_BUILD failure.

### The locale dimension: ar-SA is documented as fully supported, with no feature caveats

The develop-skills-in-multiple-languages doc lists ar-SA (Arabic SA) as a supported locale with no asterisks or feature gates [9], unlike features that do carry per-locale availability notes (for example, `modelConfiguration.fallbackIntentSensitivity` is documented as "available in supported locales" only for English and German, a restriction our own validator already encodes). Nothing in the docs suggests catalog-backed slot types are a limited-availability feature in ar-SA. In other words: by documentation, what we did should work.

### Public incident reports: none found (an absence finding)

Targeted searches across Stack Overflow, the Amazon developer forums (answerhub), GitHub issues (ask-cli and community SDKs), and general web found no report matching any of: catalog-wired interaction models failing to build in a specific locale; FULL_BUILD failing with an empty `errors` array; or build outcomes varying with catalog value count. The Japanese-language Alexa developer community, which actively documents catalog-management workflows [10], also shows no matching report. Interpretation: this is either rare (few skills catalog-wire 17 locales), unreported, or both. Confidence in this absence is inherently limited: the answerhub forum search could not be reached directly this session, so forum coverage relies on web-indexed threads.

### How the evidence fits together (validation against our own bisection)

Our live bisection remains the primary evidence and is consistent across every axis the docs provide: the payload satisfies all documented limits (model size, type count, per-value length); the referenced catalog definitions are field-identical to ones that build fine in it-IT and de-DE; the same byte-identical payload both passed and failed in ar-SA (1/5 album-only), proving per-build nondeterminism; and the pass rate trended monotonically with catalog value count (138 > 895 > 1135) while the locale control held constant (de-DE 3/3 at 1135). The one hypothesis the docs let us formally exclude is the 1.5 MB model-size limit: the injected model JSON stays in the hundreds of KB because catalog values live outside it. What the docs cannot explain is the mechanism inside Amazon's ar-SA FULL_BUILD trainer; given the QUICK_BUILD step stayed IN_PROGRESS while FULL_BUILD failed, the defect plausibly sits in the deep-training pass that ingests catalog values as NLU training data for the ar-SA language pipeline.

Adversarial check on our own claim: with sample sizes of 7/5/3, the value-count correlation alone would be statistically suggestive rather than conclusive (a fixed pass rate could produce these splits with modest probability); the decisive signals are the locale control (de-DE, 3/3, identical bytes) and the documented-limits analysis showing we violate nothing. The report should therefore say "failure probability scales with catalog value count in ar-SA," which is what the data supports, rather than asserting a precise threshold.

## Confidence Assessment

- **HIGH**: Official documented limits (scraped from current Amazon docs today): 1.5 MB, 250 intents, 350 types+intents, 140-char values/synonyms; no documented value-count limit for catalogs or slot types [1][2][3].
- **HIGH**: Our JF-543 measurements (0/7, 1/5, 3/3, de-DE 3/3 control), as they are our own instrumented experiments with byte-identical payloads.
- **MEDIUM**: The 50,000-value community ceiling [6][7]; sourced from SO/forums, not the current official table.
- **MEDIUM** (absence finding): No public reports of this failure class; the answerhub forum search was unreachable, so coverage is web-index only.
- **LOW** (mechanism): The precise Amazon-side mechanism; our QUICK_BUILD-stuck-while-FULL_BUILD-failed observation supports a deep-training defect but is inference, not documentation.

## Sources

1. Interaction model limits table, Create the Interaction Model for Your Skill (Amazon, official docs): https://developer.amazon.com/en-GB/docs/alexa/custom-skills/create-the-interaction-model-for-your-skill.html#limits
2. Interaction Model Catalog Management REST API Reference (Amazon, official docs): https://developer.amazon.com/en-US/docs/alexa/smapi/interaction-model-catalog-api.html
3. Interaction Model Schema, valueSupplier/Supplier object (Amazon, official docs): https://developer.amazon.com/en-US/docs/alexa/smapi/interaction-model-schema.html
4. Best Practices for Sample Utterances and Custom Slot Type Values (Amazon, official docs): https://developer.amazon.com/en-GB/docs/alexa/custom-skills/best-practices-for-sample-utterances-and-custom-slot-type-values.html
5. Create Intents, Utterances, and Slots, pointer to the limits section (Amazon, official docs): https://developer.amazon.com/en-GB/docs/alexa/custom-skills/create-intents-utterances-and-slots.html
6. Stack Overflow: "How to add slot values dynamically to alexa skill" (cites 50,000-value maximum): https://stackoverflow.com/questions/46827106/how-to-add-slot-values-dynamically-to-alexa-skill
7. Amazon developer forums: "too many Values on a Custom Slot Type?" (~50,000 limit; 20,000 values working): https://amazon.developer.forums.answerhub.com/questions/93123/too-many-values-on-a-custom-slot-type.html
8. Amazon developer forums: "Limit on number of values for custom slots" (recognition degradation past small counts): https://amazon.developer.forums.answerhub.com/questions/46191/limit-on-number-of-values-for-custom-slots.html
9. Develop Skills in Multiple Languages, supported-locale table incl. ar-SA (Amazon, official docs): https://developer.amazon.com/en-US/docs/alexa/custom-skills/develop-skills-in-multiple-languages.html
10. Japanese-language gist documenting SMAPI catalog-management workflows (community, no matching incident): https://gist.github.com/kun432/a5426d69586a1cce2cee8350ed62a097
