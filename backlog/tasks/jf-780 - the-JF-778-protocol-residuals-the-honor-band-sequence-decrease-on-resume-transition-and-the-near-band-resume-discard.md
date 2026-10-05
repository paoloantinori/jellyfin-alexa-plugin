---
id: JF-780
title: >-
  JF-780 - the JF-778 protocol residuals: the honor band's MEDIA-SEQUENCE
  decrease on the resume transition, and the near-band resume discard
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - streaming
  - hls
  - protocol
dependencies:
  - JF-778
priority: medium
---

## Description

Filed 2026-10-05 by the orchestrator from the JF-778 gate-marker (findings 1
and 3; both are protocol-level residuals of the brand-new windowed serve that
the unit pins cannot discriminate and the device round must).

FINDING 1 (the RFC 8216 sequence decrease): a mid-encode resume inside the
honor band is served a SLICED listing (MEDIA-SEQUENCE rebased to the start
segment); as the window grows, windowSegments minus startSegment exceeds the
LEAD and the band flips the SAME URL to the unsliced windowed listing with
MEDIA-SEQUENCE:0. The media sequence DECREASES between reloads of the same
live playlist, which RFC 8216 forbids; a player enforcing the rule on the
live-merge path errors or resets mid-playback. ExoPlayer's behavior for this
shape is UNVERIFIED.

FINDING 3 (the near-band discard): the binary honor band drops resume
positions 4-8 entries behind the window edge that a slice would honor to
within seconds, restarting the episode from 0 for the whole encode window
(~10min for a 45min episode at 4.4x). The design record's "minutes off the
requested position" justification covers only the far band.

DEVICE PROBES (add to the JF-778 AC#7 retest list, they discriminate both):
1. Cold-play an episode, pause a few seconds in, resume DURING the encode
   window: playback must continue near the pause point, not restart, and not
   error/reset mid-playback (the sequence-decrease shape).
2. The same with the resume 10-20 seconds behind the live edge (the
   near-band shape): a restart to 0 here confirms finding 3 on device.

FIX SHAPES (only if a probe fires): for 1, keep the served listing's
MEDIA-SEQUENCE monotonic across the band flip (rebase the windowed listing's
sequence to the slice's continuation, or hold the sliced shape until the
encode completes); for 3, a graded band (honor while the drift is under a
tolerance, e.g. 2 entries, drop only beyond it).
