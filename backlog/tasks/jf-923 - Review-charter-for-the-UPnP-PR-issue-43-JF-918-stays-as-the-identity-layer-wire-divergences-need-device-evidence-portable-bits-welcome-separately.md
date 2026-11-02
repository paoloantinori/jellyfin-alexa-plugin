---
id: JF-923
title: >-
  Review charter for the UPnP PR (issue #43): JF-918 stays as the identity
  layer; wire divergences need device evidence; portable bits welcome separately
status: To Do
assignee: []
created_date: '2026-11-02 13:21'
labels: []
dependencies: []
references:
  - 'https://github.com/paoloantinori/jellyfin-alexa-plugin/issues/43'
  - 'https://github.com/Tergin87/jellyfin-alexa-plugin'
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Review charter for the upcoming UPnP PR from Tergin87 (issue #43 tracking; his fork 1.1.0.1 recon'd 2026-11-02, remote 'tergin' fetched locally). MAINTAINER-APPROVED STARTING POSITION (Paolo, 2026-11-02):

1. IDENTITY LAYER: our JF-918 funnel canonicalization (shipped 1.0.2.2, confirmed working by the reporter's own logs) STAYS as the one split-endpoint identity mechanism. The PR must build the UPnP path ON it, not replace it. His fork currently REMOVES CanonicalizeSplitDeviceIdentity and our DeviceAliasFunnelTests (307 lines) in favor of his PlaybackDeviceAliases (token->issuer re-keying) + SplitEndpointCompatibility (learned-device wire-shape layer): that is the main design collision to resolve in review.

2. WIRE DIVERGENCES NEED DEVICE EVIDENCE: his SplitEndpointCompatibility reproduces 4 divergences of his old Python skill for learned devices: (a) shouldEndSession OMITTED on play responses (contradicts the JF-299 play-true rule); (b) "N"-format stream tokens instead of dashed GUIDs (needs translation at the controller boundary, his fork does this); (c) /Audio/{id}/universal URLs instead of /stream?static=true (contradicts the Content-Type/Range gotcha); (d) metadata without backgroundImage. His own evidence shows the Sonos ACCEPTS our standard ReplaceAll shape (track 1 always played), so each divergence must be individually justified by on-device evidence on CURRENT firmware, or dropped.

3. TEST RESTORATION: the funnel tests he deleted must come back or be replaced by equivalent coverage of whichever mechanism ships.

4. CROSS-LOCALE DISCIPLINE: his fr-FR template additions (natural shuffle wording + infinitive wrapper twins) must be mirrored in ALL 17 locales if they ride the PR.

5. PORTABLE BITS WELCOME SEPARATELY: SpokenNumbers (358 lines + 97 test lines, U2/M83/Blink-182 ASR word/spelled recognition) + ArtistSearch integration + catalog synonym changes are clean standalone improvements, good candidates to merge independently of the Sonos mechanism question.

6. REVIEW MECHANICS: when the PR lands, review per the standing gates (/simplify, code-review high, peer-review-loop for external review; never merge without explicit APPROVED). Also remember his constraint note: during UPnP playback the Alexa AudioPlayer context in voice requests is UNRELIABLE (reports STOPPED with no token while the speaker plays); his transport path queries UPnP state instead: check our flat transport gates do not misfire for UPnP-managed devices (a discriminator will be needed).

REFERENCES: fork remote 'tergin' fetched locally (tag 1.1.0.1, his main); issue #43 comments 2026-11-01T19:14Z (his status) and the recon assessment in this session; memories split-device-identity-alias and household-playback-silent-death-signatures.
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
