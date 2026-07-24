---
id: JF-308
title: 'Security: Fix reflected XSS on account-linking login page (error query param)'
status: Done
assignee: []
created_date: '2026-07-12 14:56'
updated_date: '2026-07-15 07:35'
labels:
  - security
  - xss
milestone: m-6
dependencies: []
references:
  - 'Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs:142'
  - 'Jellyfin.Plugin.AlexaSkill/Controller/Pages/account_linking.html:45'
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The GET `account-linking` page reflects the `error` query parameter into the returned HTML with NO encoding: `AlexaSkillController.cs:144` does `page.Replace("{{ error }}", error)`. In the template (`Controller/Pages/account_linking.html:45,49`) the placeholder lands both inside a double-quoted JS string literal (`var error = "{{ error }}";`) and is then assigned to `errorInfo.innerHTML = error;` — two injection points (JS-string breakout and an innerHTML sink).

This page is the Jellyfin username/password entry form for account linking. An attacker who knows the skill's `AccountLinkingClientId` and one valid `redirect_uri` prefix (both travel to the victim's browser inside the legitimate Amazon account-linking URL and leak via history/referrer/sharing) can craft a link whose `error` value breaks out and runs attacker JS on the Jellyfin origin, exfiltrating the credentials the user is about to type. Verified against code 2026-07-12; the client_id/redirect_uri gate makes it medium-likelihood but the impact (credential theft on a login form) is high.

Root-cause fix: HTML/JS-encode `error` before substitution (e.g. `HttpUtility.JavaScriptStringEncode`), or drop the innerHTML/JS reflection entirely and render error text via `textContent` from a server-encoded value. Prefer a fixed set of server-side error messages keyed by an enum rather than reflecting arbitrary text.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The account-linking page no longer reflects arbitrary attacker-controlled text into HTML/JS; error display uses textContent or a properly encoded value
- [ ] #2 A payload such as error=\"};alert(document.domain)// does not execute script when the page is loaded
- [ ] #3 Error messages shown to the user are limited to a known server-side set (e.g. invalid credentials / unknown error) rather than free-form reflected input
- [ ] #4 Unit or integration test asserts the error output is encoded / not executable
- [ ] #5 No regression to the legitimate account-linking success and failure flows
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
XSS fix (WebUtility.HtmlEncode on the error param + template textContent instead of innerHTML) shipped in eea9cea; this pass added the missing AC #4 regression coverage: AccountLinkingXssTests (3 tests) exercising JS-breakout, HTML-tag, and no-error paths against the real controller endpoint. Closed in commit c4a5bd7.
<!-- SECTION:FINAL_SUMMARY:END -->

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
