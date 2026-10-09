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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

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

SCOPE EXTENSION (JF-817, same turn as the audiobook windowing landed): the
windowed serve family now includes the AUDIOBOOK prewrite rows (the
concurrent-encode guard and the first-fetch tail of StreamHlsAudiobook),
served through the same shared window computation and honor-band predicate
as the episode path. Both residuals above therefore also apply to the
audiobook resume path: a mid-encode book resume inside the band is served a
sliced listing whose MEDIA-SEQUENCE can decrease when the band flips to the
unsliced windowed serve (finding 1), and a resume a few entries behind the
window edge is discarded to 0 for the remainder of the encode window
(finding 3; an audiobook encode window is minutes for a copy concat, tens of
minutes for an AAC transcode). The device probes should cover a BOOK resume
alongside the episode ones.

THIRD RESIDUAL (JF-817 /code-review finding 2, same family, distinct shape):
the COMPLETION HANDOFF. When the band dropped a resume, the session plays
from 0 on base-0 windowed listings; the first post-completion reload of the
SAME URL (?start= still present) hits the ENDLIST cache-hit row, which
re-applies the slice at the original segment, so one reload flips the
listing base from 0 to S (a forward MEDIA-SEQUENCE jump) AND adds ENDLIST
while the user is mid-playback from 0. Media3 either seek-jumps the user to
the resume point (the intended self-heal, the JF-778 episode design's
documented "the next warm serve resumes at the position exactly") or
errors/resets; unverified on device. This is NOT findings 1/3 (both describe
during-encode band flips); the completion handoff needs its own device probe
(cold-cache book resume at minutes-deep, observe the transition when the
encode completes).

SCOPE EXTENSION (JF-819, same turn as the single-item windowing landed, the
gate-marker's finding 1): the family now also includes the SINGLE-ITEM
prewrite rows of StreamHlsVideoAudioCore (songs above the prewrite threshold
and the single-chapter audiobooks the JF-794 census found common), on the
same shared ComputePrewriteWindow window computation and honor-band
predicate. Findings 1 and 3 and the completion handoff therefore apply to
this family unchanged, and the device probe set must cover it or the
residuals get declared verified on families the probes never exercised. The
representative probe is the SINGLE-FILE-BOOK resume: cold-cache, resume at a
minutes-deep position on a single-chapter audiobook (the exact shape that
motivated the 1.0 blocker), run alongside the episode and concat-book
probes.
<!-- SECTION:NOTES:END -->
