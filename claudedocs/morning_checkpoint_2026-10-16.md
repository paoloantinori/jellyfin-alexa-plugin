# Morning checkpoint, 2026-10-16

Overnight orchestrator run, from the 07:30 resumption through ~14:45. Nine tasks
shipped end to end (merged, CI green, deployed where production code changed,
live-verified, recorded). The backlog has no dispatchable work left: everything
remaining is device-gated, a maintainer decision, or external.

## Shipped overnight

1. **JF-875** (sunny-dancer movie steal): closed by its own re-probe protocol.
   PlaySongIntent on two consecutive live builds, 3/3 today plus the wicked
   control. Scratch-only trainer anomaly; the merged guard (1a8e6f06) stays as
   the belt.
2. **JF-888** (JF-891 SetPlaybackSpeed row): record backfill; the code merged
   long ago as 395eaf7c, the Done transition was lost to compaction.
3. **JF-871 + JF-872** (seek-mode music stop wording): MusicStoppedByVoice in
   all 17 locales, three-way medium switch in PauseIntentHandler, activity-
   invariant theory plus the live-TV-keeps-video-line pin. Merged 676e3bc5,
   deployed, active-DLL verified.
4. **JF-892** (exact artist shadowed by a coincidental containment row): the
   exact-first reorder at the TryEntityFallbackAsync acceptance point. Merged
   afde1c90, deployed, the "jones" ask verified byte-identical.
5. **JF-893** (reliability rows vs simulate latency spikes): ambient-noise SKIP
   after a correct iteration, real failures still fail with evidence. Merged
   125d14d7.
6. **JF-894** (remapped Audio-typed chapters classified as music): launch-time
   verdict mint (LaunchRoute.VideoAppAudiobook) instead of a classification-
   time climb, which was proven not music-safe. Merged 605eb146, deployed.
7. **JF-895** (the tie auto-select pin never fired): a genuine firing fixture
   (Jonas rival-pool shape) plus a weak-rival twin proving the fair-margin
   machinery decides swap-vs-ask. Merged f822cc8a.
8. **JF-896** (non-exact-rival subclass landed clean-miss): the downgrade now
   consults the qualified-rival scanner and upgrades to the ask; the review
   round caught a real winner-ranking bug pre-merge. Merged ad65f139,
   deployed, "jones" ask byte-identical.
9. **JF-897** (harness polish from JF-893): shared smapi_timeouts module,
   alarm scope extended to non-simulate phases, dilution visible in the
   summary. Merged add4fd44.

## Review rounds were load-bearing today

Three of the nine shipped changes had a real defect caught at review or probe:
the JF-894 pin that tested the wrong leg (JF-895's reason to exist), the
fair-vs-raw winner ranking that would have headlined the JF-896 ask with the
demoted match, and the JF-893 failures-guard (a timeout after a real mismatch
could have gone green). The gate discipline paid for itself again.

## Still open

- **JF-898** (rejected-climb chapters still classify as music): the last
  non-device task. Needs Paolo's call: mint a route at the rejected arm or
  accept and document the corner, decided together with the JF-799 residual
  family.
- **Device retests for Paolo** (nothing blocking, all fixes live):
  - "chiedi a mia collezione musica si nora jones" (typo): should now ask
    listing the Jones candidates instead of auto-playing Danko Jones (JF-894).
  - "attiva il loop" family on the loop device: the JF-889 fixes went live;
    the exact 17:16 utterance from 2026-10-14 is still unknown, the Alexa app
    voice history would settle it.
  - Loop on an idle device: the nothing-playing line (JF-890 fix, live since
    2026-10-15).
  - Seek-mode music stop (opt-in only): now says the music line (JF-871/872),
    only relevant if NativeControlsForAudio gets turned on.
- **fr/es simulate**: still degraded at 14:40 (probed again, raw client, both
  fail while it-IT works; Amazon-side class, unchanged since 2026-10-14). The
  four pink-floyd smoke rows for fr-CA/fr-FR/es-ES/es-MX stay red until it
  clears; the en-family rows were verified transient.
- **e2e**: of the three duty rows, "canzone dei pink floyd" and "waltz for
  koop" pass; "riproduci i miei preferiti" passes its functional row, and its
  reliability-variant red is the Amazon-side latency that JF-893 now handles.

## CI and deploys

Every merge today went green on the full battery (build-and-test, all six
validators, CodeQL) before or within minutes of the deploy. Four deploys to
minix (JF-871/872, JF-892, JF-894, JF-896), each with a fresh config backup,
active-DLL md5 verification, and a live probe battery; config survived every
deploy (1 user). No model-surface changes, so no interaction-model deploys
were needed.
