---
id: JF-350
title: >-
  BuildOutputSpeech forwards escaped args to plain-text fallback (latent &amp;
  bug)
status: Done
assignee: []
created_date: '2026-07-18 14:27'
updated_date: '2026-07-18 19:52'
labels: []
milestone: m-8
dependencies: []
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by /code-review high (finding C2). `BuildOutputSpeech` (BaseHandler) forwards its `params object[] args` to BOTH `GetSsml` (SSML, wants escaped) AND `ResponseStrings.Get(plainKey, ...)` (plain text, wants raw). `YesIntentHandler` passes already-escaped values (`EscapeXml(item.Name)`), so if the plain-text fallback fires (SSML key missing for a locale), the user hears `&amp;` / `&lt;` instead of `&` / `<`.

Latent — all 17 locales define both SSML+plain keys, so the fallback rarely fires. Pre-existing (YesIntent did this before JF-323). Proper fix: BuildOutputSpeech should take RAW args and escape internally for the SSML path only — same root issue as GetSsml escaping (blocked by SSML-fragment args that can't be blanket-escaped). Track until a clean escaping-layer refactor.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 BuildOutputSpeech takes raw (unescaped) args and escapes for the SSML path only, leaving the plain-text fallback raw
- [x] #2 A test where the SSML key is missing forces the plain fallback and asserts the user hears '&' not '&amp;'
- [x] #3 No regression: existing YesIntent resume announcements unchanged when both keys exist
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-07-18 19:52
---
Closed via commit 341859c. /code-review high: 0 blocking correctness bugs (two fresh-context finders verified the diff clean -- null-arg fallthrough, double-escape, format-string injection, wrong-variable all checked against actual code); 1 non-blocking ALTITUDE finding (GetSsml 'caller must pre-escape' vs BuildOutputSpeech 'raw in' -- opposite, undocumented contracts) mitigated by documenting GetSsml's escaping contract in its XML doc; 1 style nit (LINQ one-liner) dropped -- explicit loop matches the file's mixed LINQ/loop convention. Multi-level tests: UNIT -- 2 tests (SSML path escapes reserved chars + yields well-formed XML via XDocument.Parse; plain fallback keeps a raw '&') + full suite 2527/2527 green incl. YesIntent audiobook guard. NLU -- N/A: BuildOutputSpeech is response formatting, not intent/slot routing, so no interaction-model/utterance change for NLU fixtures to cover. E2E -- N/A in practice: the bug is latent (the plain-text fallback never fires in production because all 17 locales define both SSML+plain keys), so there is no reachable E2E scenario; the existing E2E SSML-well-formedness framework (JF-323) already guards the normal SSML path, and the unit test exercises the fallback by passing a missing SSML key (the only way to reach it).
---
<!-- COMMENTS:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
BuildOutputSpeech no longer forwards escaped args to the plain-text fallback. It now escapes string args for the SSML path only (EscapeStringArgs helper) and leaves the plain-text path raw; the two YesIntent call sites pass RAW names. Fixes the latent '&amp;' spoken on the plain-fallback path (JF-323 code-review finding C2). Also documented GetSsml's caller-must-pre-escape contract to address the code-review-high altitude finding on the two helpers' opposite escaping contracts. Unit tests cover both paths; full suite 2527/2527.
<!-- SECTION:FINAL_SUMMARY:END -->
