---
id: JF-766
title: >-
  hi-IN PlayVideo bare "{title} देखो" carries the same SearchQuery absorption
  class as JF-761 (committed episode sample shares the tail verb)
status: To Do
assignee: []
created_date: '2026-10-05 00:00'
labels:
  - interaction-model
  - nlu
  - hi-IN
dependencies: []
references:
  - JF-761
  - JF-551
  - JF-459
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-761 /simplify altitude review (2026-10-05, cross-locale sweep of all 17 templates).

The ja-JP steal JF-761 just fixed (bare `{title} を見たい` on PlayVideoIntent absorbing the committed PlayEpisodeIntent episode address wholesale, catalog-independent, live 4/4) has ONE structural twin: hi-IN.

- `templates/hi-IN.yaml` ~line 185: PlayVideoIntent carries the BARE carrier `{title} देखो` (`title: AMAZON.SearchQuery`, lines ~194-196).
- `templates/hi-IN.yaml` ~line 465: PlayEpisodeIntent carries the committed sample `{series_name} सीज़न {season_number} एपिसोड {episode_number} देखो`, ending in the SAME tail verb देखो.

Same subsumption surface as the ja bug: the bare carrier is empty-prefix + SearchQuery + tail verb, so the whole episode address can land in `title` and route PlayVideoIntent (guaranteed miss, since PlayVideoIntentHandler searches Movie+Episode by SearchTerm and the absorbed span is not a title). NOTE: this is STRUCTURAL analysis, not a live probe; hi-IN has never been probed for this shape (the JF-551 closure probe covered ja only for this class).

Checked and CLEAR: every other locale's PlayEpisodeIntent samples end in the series name or episode_number, never a tail verb shared with their locale's bare video carrier (en-* watch/play verbs sit at the FRONT of episode samples; fr/es/nl/it/de/ar/pt checked). hi-IN's sibling bare `{title} शुरू करो` is safe, the episode sample ends देखो not शुरू करो.

OTHER BARE SearchQuery VIDEO CARRIERS, audited and DISPOSITIONED (recorded here so the class audit is never redone): ar-SA carries bare `شاهد {title}` and nl-NL carries bare `kijk {title}` (both AMAZON.SearchQuery), but neither locale's PlayEpisodeIntent samples end in a verb those carriers share (ar/nl episode forms end in the series name or episode number), so the subsumption surface of the ja/hi class is ABSENT there. Structural analysis only, no live probe; if a live ar/nl episode steal is ever observed, start from this note, not from scratch.

FIX SHAPE (per the JF-551/JF-761 playbook): remove or qualify the bare `{title} देखो` carrier; the anchored `फिल्म {title} देखो` form (~line 191) already keeps the watch-verb carrier. Requires a live profile-nlu probe first (does the steal reproduce on the live model with a library series?) and the JF-459-style regression guards after (bare movie watch must not newly route PlayEpisode).

OPTIONAL STRUCTURAL GUARD: a validator check for the class (bare SearchQuery-terminated carrier on PlayVideoIntent vs any same-locale PlayEpisode sample sharing the tail verb) would have caught both ja and hi mechanically; the JF-761 ja fix closed the only other instance, so the check's yield today is zero, file it only if the class recurs.

Evidence source: the JF-761 altitude review agent (2026-10-05), line numbers from the worktree diff review; re-verify line numbers against main before editing.
<!-- SECTION:DESCRIPTION:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 Live profile-nlu probe reproduces or refutes the steal (4 repeats, library series + uncataloged series)
- [ ] #2 If reproduced: bare `{title} देखो` removed/qualified in templates/hi-IN.yaml, model regenerated, mirrors regenerated
- [ ] #3 Regression guards probed post-deploy (anchored फिल्म form clean; bare movie watch must not route PlayEpisode)
- [ ] #4 NLU fixture pin added per the JF-761 ja convention (red until deploy, exact-value series_name)
- [ ] #5 Validators pass with no new warnings
<!-- DOD:END -->
