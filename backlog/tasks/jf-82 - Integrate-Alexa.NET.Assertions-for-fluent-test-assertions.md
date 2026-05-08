---
id: JF-82
title: Integrate Alexa.NET.Assertions for fluent test assertions
status: Done
assignee: []
created_date: '2026-05-06 19:20'
updated_date: '2026-05-06 20:58'
labels:
  - testing
  - high-priority
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add the `Alexa.NET.Assertions` NuGet package to the test project and migrate existing tests from manual SkillResponse inspection to fluent typed assertions.

Current tests manually inspect response objects (e.g., checking `response.Response.OutputSpeech`). Alexa.NET.Assertions provides:
```csharp
response.Should().HaveSpeech("Now playing...");
response.Should().EndSession();
response.Should().HaveDirective<AudioPlayerPlayDirective>();
```

This is a low-effort, high-value improvement that makes tests more readable and catches response structure errors at compile time.

Files: `Jellyfin.Plugin.AlexaSkill.Tests/Jellyfin.Plugin.AlexaSkill.Tests.csproj` + all 33 handler test files.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 5.1
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Alexa.NET.Assertions NuGet package added to test csproj
- [ ] #2 At least 10 test files migrated to use fluent assertions as proof of pattern
- [ ] #3 All existing tests pass with new assertion library
- [ ] #4 No reduction in test coverage
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Migrated 13 test files to use Alexa.NET.Assertions extension methods (Tells&lt;T&gt;, Asks&lt;T&gt;, HasDirective&lt;T&gt;) replacing verbose multi-line assertion patterns. Net reduction of 23 lines while improving readability. Added NuGet package Alexa.NET.Assertions v3.0.0. Remaining manual assertions are for video/audio-player responses with null OutputSpeech where Tells/Asks would incorrectly assert speech existence.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
