---
id: JF-843
title: >-
  PlayEpisode absolute-vs-per-season episode numbering ambiguity: the elicited
  path resolves IndexNumber inside the answered season, but the incident's
  "episodio 54" means the 54th episode of the whole series
status: To Do
labels:
  - nlu
  - bug
priority: medium
---

## Description

Filed from the JF-814 gate-marker (F5, 2026-10-09). The JF-814 fix elicits the
season when the user gives an episode number without one ("l'episodio 54 di
sailor moon"), and after the answer the handler resolves
`IndexNumber == episodeNumber` INSIDE `ParentIndexNumber == seasonNumber` (the
per-season Jellyfin indexing). But the incident utterance denotes an ABSOLUTE
index: the user's own library holds one long continuous Sailor Moon run (200
episodes total, per-season IndexNumber inside Jellyfin; the 54th episode ever
aired is stored as S2E8, not S2E54). After the season elicit answers "2", the
per-season query S2E54 does not exist and the EXACT incident phrase lands on
"season 2 episode 54 not found". The fix that made the phrase routable still
fails the user's actual ask.

Fix shapes to evaluate (gate-marker wording):

1. On a per-season query miss, fall back to ABSOLUTE resolution: rank all
   seasons' episodes by (ParentIndexNumber, IndexNumber) and play the Nth
   episode of the whole series, with the found episode ANNOUNCED (name +
   season/episode) so the user hears what landed.
2. Or offer the season question only AFTER an absolute miss (try absolute
   first; elicit the season only when the absolute index is itself ambiguous).

Live context from the incident (include in any implementation notes): the
library is the maintainer's own (it-IT household), Sailor Moon stored as one
series with 200 continuously-numbered episodes across seasons; the user asked
"chiedi a mia collezione di mettere l'episodio 54 di sailor moon" (live
2026-10-08, profile-nlu confirmed) and expects the 54th episode of the run.

## Implementation notes (2026-10-09, shape (a) shipped)

Fix shape (a), the ABSOLUTE FALLBACK ON PER-SEASON MISS, as decided by the
orchestrator. Live incident context preserved from the filing: it-IT household,
one 200-episode continuous Sailor Moon run stored per-season in Jellyfin
(absolute 54 = S2E8), the user's phrase "chiedi a mia collezione di mettere
l'episodio 54 di sailor moon" expects the 54th episode of the run.

- Resolution order in the season-ed path (both the direct season-ed ask and the
  post-elicit arrival, which share the same explicit-numbers code path because
  the JF-614 slotValues echo threads episode_number through the season elicit;
  pinned by HandleAsync_EpisodeNumberWithoutSeason_ElicitsSeasonNumber):
  (1) per-season IndexNumber==N query as today; (2) on a miss,
  TvNextUpService.GetEpisodeByAbsoluteNumberAsync fetches the Nth episode of the
  run with a DB-paged air-order query (StartIndex N-1, Limit 1; one row fetched);
  (3) the launch announces the mapping UNCONDITIONALLY (PlayingEpisodeByAbsoluteNumber,
  args: asked number, resolved season, resolved episode, title), never gated on
  AnnounceNowPlaying, because a silent substitution is the wrong-item class this
  task closes; (4) an empty page (index beyond the run) keeps the existing
  NotFoundEpisode Tell. Guards: n < 1 resolves null; season-0 specials and
  virtual items are excluded from the absolute count (same exclusion the episode
  auto-advance applies; specials are not part of the aired run a viewer counts).
- Ranking helper decision: NO shared helper existed to reuse. The task guessed
  "the ordering the queue uses for audiobooks" but the audiobook queue
  deliberately ranks by SortName (JF-672, the composite was probe-REFUTED for
  chapters); the (ParentIndexNumber, IndexNumber) composite exists as
  QueueContinuationFetcher.AlbumTrackOrder (disc/track-named, album docs) and
  inline in PlaybackNearlyFinishedEventHandler's candidatesQuery. Per the
  simplify/altitude gate round, a per-domain NAMED constant was created:
  QueueContinuationFetcher.TvEpisodeAirOrder beside its twins, consumed by
  GetEpisodeByAbsoluteNumberAsync; the auto-advance's inline twin migration is
  filed same-turn as JF-845 (that file sat outside this task's surface, another
  worker active). The coupling is user-visible: the announced mapping derives
  from this order, so a tiebreak added at one site only would make the spoken
  mapping diverge from next-episode succession.
- Announce key: MINTED, not reused. SeasonEpisode ({series}, season, episode:
  {title}) names the destination but not the asked number, so the user never
  hears the 54 -> S2E8 mapping the filing calls load-bearing. New key
  PlayingEpisodeByAbsoluteNumber in all 17 locale files (each mirrors that
  locale's SeasonEpisode season/episode nouns + NowPlaying verb prefix), plus
  the Ssml twin carrying the same text verbatim (added in the code-review
  round: the sibling announce families all carry twins, and a missing Ssml key
  logs a locale-key WARNING on every absolute play before degrading to plain),
  ledger row added to ResponseStringsTests.AllExpectedKeys (the JF-821
  convention). validate_locales.py PASS, no new gaps.
- Red-green: HandleAsync_PerSeasonMiss_AbsoluteIndexExists_PlaysAnnouncedAbsoluteEpisode
  run against the UNMODIFIED handler first, red on both TFMs
  (DirectiveMissingException: No directives of type "VideoAppLaunchDirective",
  the old not-found Tell); then green after the fix. Guards: the per-season HIT
  path never issues the absolute query (Times.Never pin) and keeps the plain
  NowPlaying announce; the beyond-total case keeps the NotFoundEpisode Tell.
  Test-harness note: the absolute query is DB-paged, so its mock must honor
  StartIndex/Limit (a naive full-list mock silently picks S1E1 and the announce
  asserts caught it).
- Gate round (simplify, 4 angles): applied the log-block fold into the fallback
  branch (the compiler needs the null check on the item, not the flag, for
  CS8602), the named IsAbsoluteEpisodeQuery predicate across the three test
  sites, the incident-story de-triplication (service doc is the single home),
  the announce switched from hand-rolled PlainTextOutputSpeech to
  SpeechBuilder.BuildOutputSpeech with the episode-announce title contract
  (FormatEpisodeAnnounceTitle ?? Name; byte-identical in the 16 non-it-IT
  locales since the formatter is it-IT+PremiereDate-gated), and the
  TvEpisodeAirOrder constant. Skipped with reason: the BareEpisode-to-TestHelpers
  hoist (cross-file drive-by into test files outside this task's surface); no
  Ssml twin key minted (the emphasis ban holds and BuildOutputSpeech makes it a
  locale-file-only addition if ever wanted). Efficiency angle clean (zero
  hit-path cost). Honest trade surface (decided semantics, disclosed by the
  announce): the trigger fires on ANY per-season miss, so a deliberately
  per-season ask beyond one season's span ("season 3 episode 5" of a 4-episode
  season) jumps to S1E5 with the mapping spoken; tightening that would ADD
  special cases against the decided shape.
- Code-review gate (high, 5 findings): F1 REJECTED (trigger breadth on a
  nonexistent season is the decided semantics, the announce speaks the
  resolved season so the substitution is audible; tightening adds special
  cases). F2 APPLIED as documentation (the ParentIndexNumberNotEquals
  NULL-season exclusion documented on the constant + the core, the
  AlbumTrackOrder known-limit pattern). F3 APPLIED (trailing SortName axis on
  TvEpisodeAirOrder pins (season, episode) ties so a replay launches and
  ANNOUNCES the same title). F4 APPLIED (Ssml twins in all 17 locales, same
  text verbatim; the per-play locale-key WARNING is gone). F5 APPLIED
  (IsAbsoluteEpisodeQuery now pins the air-order constant BY REFERENCE, so a
  same-shaped query with a different order cannot pass the pins).
- Scope kept: no model files, no fixtures (nothing routed differently at the NLU
  layer; the model side of the JF-814 flow is already live).

LIVE FINDING (2026-10-09 ~13:10, the maintainer's device round): the absolute
fallback WORKS on device (12:58:22: "per-season S2E54 miss ... resolved
ABSOLUTELY to 'Il debutto di Rea' (S2E8)", VideoApp launch, the episode
played), but the UNCONDITIONAL announce was NOT SPOKEN and is absent from the
final response body (directive-only). Evidence chain: the handler log proves
absoluteFallback=true and the announce construction is unconditional on that
flag; the active DLL (md5 b5ffdea6, UTF-16 string heap verified) contains the
Ssml key, the progressive log line, and the absolute-resolution line;
SpeakVideoLaunchAnnounceAsync's gate (announce non-null, IntentRequest,
context non-null, DeviceSupportsVideoApp true - the VideoApp path proves the
last) requires the progressive send, whose seam logs at Debug BEFORE sending
(the 12:38 e2e pings show BaseHandler Debug lines flowing) - yet NO
SendProgressiveResponse line exists at 12:58:22. Static analysis exhausted:
code and log contradict. The plain builder attaches OutputSpeech, so an
early-gate return would have left the speech on the final response (also
absent). NEXT STEP: one live repeat of the flow ("episodio 54 di sailor
moon" -> "due") with a live log tail; if the progressive line appears and the
speech still vanishes, the send returns true but the device drops it (a
progressive-after-elicit platform behavior); if the line never appears, the
gate has an environmental input the static read misses (dump the gate's four
values at runtime with a temporary Debug line).
