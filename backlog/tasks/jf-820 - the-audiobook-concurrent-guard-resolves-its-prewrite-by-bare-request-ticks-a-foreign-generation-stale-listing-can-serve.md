---
id: JF-820
title: >-
  JF-820 - the audiobook concurrent-encode guard resolves its prewrite by bare request
  ticks: a foreign generation's stale playlist-full can serve under a live encode
status: To Do
assignee: []
created_date: '2026-10-08'
labels:
  - bug
  - hls
  - edge-case
dependencies:
  - JF-778
references:
  - Jellyfin.Plugin.AlexaSkill/Controller/VideoAudioController.cs
priority: low
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-817 /code-review high gate (finding 5). PRE-EXISTING row
(the JF-817 windowing neither introduced nor worsened it; it only shrinks
what the row serves), filed because the review surfaced it against JF-817's
new during-encode serve invariant.

The audiobook concurrent-encode guard row gates on BARE registry presence
(`_activeAudiobookEncodes.TryGetValue(parentId, out _)`, any ticks) and then
resolves the prewrite as `GetHlsDirectoryPath(parentId, REQUEST art ticks) +
File.Exists`. When the request's art ticks differ from the live encode's
generation (the art-revert shape: cover refreshed A -> B mid-encode, then the
art read returns A again for a later request; or a library read returning
stale image metadata), the row can find and serve a FOREIGN generation's
leftover `playlist-full.m3u8` (the documented undeletable debris class: the
per-file debris backstop removes stream.m3u8 and segments but not
playlist-full.m3u8). A fully-encoded stale dir defeats the window cap
(head + 1 >= total, windowed = false) and serves the FULL stale listing
whose embedded JF-309 token is expired: every segment fetch it names 401s
for the rest of the encode window.

WHY THE EPISODE FIX DOES NOT TRANSPLANT: the episode prewrite probe's
liveness-aware resolver (JF-775, `ResolveHlsGenerationDirPaths(...,
ownGenerationLiveOrRegistering: true)`) protects the SAME-ticks two-root
shadow (the transient-vs-cache-root stale copy while the caller's own
generation IS live). It does NOT redirect a caller whose own ticks are dead:
the audiobook cross-ticks shape falls to the static root order either way, so
copying the episode probe changes nothing for this defect.

CANDIDATE FIXES (pick deliberately, this is a design decision): (a) resolve
the prewrite through the LIVE REGISTRATION's directory when any generation of
the key runs (serve the running encode's listing regardless of the caller's
art ticks; art only affects the cover/black-frame video track, so the concat
audio timeline is the same); or (b) gate the row on own-ticks liveness and
let a foreign-ticks caller take the lock/503 path instead of serving a
listing whose timeline it cannot verify. Either way, add the missing pin: a
foreign-ticks stale prewrite under a live encode must not serve.
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
