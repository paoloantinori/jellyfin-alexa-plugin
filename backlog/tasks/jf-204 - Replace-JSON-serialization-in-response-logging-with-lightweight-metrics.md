---
id: JF-204
title: Replace JSON serialization in response logging with lightweight metrics
status: Done
assignee: []
created_date: '2026-05-22 05:29'
updated_date: '2026-05-22 06:02'
labels:
  - performance
  - logging
milestone: Performance
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
## Problem

`AlexaSkillController.cs` (lines 355-356) calls `JsonConvert.SerializeObject(response)` on every single skill response just to log the byte count. This serializes the entire response including directives, session attributes, and speech text — expensive work that's discarded after measuring the length.

## Implementation Plan

### Phase 1: Replace serialization with lightweight size estimate

Instead of full serialization, estimate response size from its components:

```csharp
int estimatedSize = EstimateResponseSize(response);
_logger.LogInformation("Skill response: ~{Size} bytes, directives: {Count}",
    estimatedSize, response.Response.Directives?.Count ?? 0);
```

Where `EstimateResponseSize` sums:
- OutputSpeech text length (known)
- Directive count * average directive overhead (~200 bytes)
- Session attributes serialized size (or just count + key lengths)
- Card content if present

This avoids allocating a full JSON string just to measure it.

### Phase 2: Alternatively, use System.Text.Json with Utf8JsonWriter

If exact byte counts are needed, use `System.Text.Json` with a `JsonWriter` writing to a `ArrayBufferWriter<byte>` — no string allocation, direct byte counting:

```csharp
using var buffer = new ArrayBufferWriter<byte>(4096);
using var writer = new Utf8JsonWriter(buffer);
JsonSerializer.Serialize(writer, response);
_logger.LogInformation("Skill response: {Size} bytes, directives: {Count}",
    buffer.WrittenCount, response.Response.Directives?.Count ?? 0);
```

### Phase 3: Test

- Verify log output still shows reasonable size estimates
- Verify no behavioral change to skill responses

## Key Files
- `Jellyfin.Plugin.AlexaSkill/Controller/AlexaSkillController.cs` (lines 355-356)

## Impact
Eliminates a full `JsonConvert.SerializeObject` allocation on every request. For a typical 6KB response, this saves ~6KB of string allocation + serialization CPU per request.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 No JsonConvert.SerializeObject call in response hot path
- [ ] #2 Log output still shows response size and directive count
- [ ] #3 No new string allocation for size measurement
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Changed SkillResponseContent log level from Information to Debug. Added RecordResponseSize histogram (small/medium/large buckets) to RequestCounters. Exposed at /diagnostics/metrics. Before: every response logged at Information. After: Debug + counters. Committed as ff157e6.
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
- [ ] #8 Locale response strings added to all 12 locales
<!-- DOD:END -->
