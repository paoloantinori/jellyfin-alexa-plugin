---
id: JF-841
title: >-
  PlayEpisode season-only asks still fall to NextUp: the JF-814 gate keys on the
  episode side; a future season-only sample family must extend the gate
status: To Do
labels:
  - nlu
priority: low
---

## Description

Filed from the JF-814 simplify/altitude review (2026-10-09, worktree
agent-af77920eafddbeb3a). The JF-814 guard in `PlayEpisodeIntentHandler.HandleAsync`
is `if (episodeParsed && !seasonParsed) -> elicit season_number`. The SYMMETRIC
shape, a season number with no episode number ("gioca la stagione 2 di X"),
still falls silently to the NextUp core, the same wrong-item class JF-814
closes (the next-up episode is substituted for the requested one).

Mitigations, deliberate: no locale template carries a season-only PlayEpisode
sample today, so the shape is NLU-unreachable, and the orchestrator's fixed
JF-814 design decision scoped exactly the episode-side branch. Residual risk: a
future locale adding "play season {season_number} of {series_name}" silently
re-opens the substitution bug.

Fix shape when it bites: generalize the gate to "exactly one number present ->
elicit the missing one" (`episodeParsed != seasonParsed`, per-side elicit key
and slot), with the fully-numberless series-only request still keeping the
NextUp fallback. Ship the gate extension in the SAME change that adds the first
season-only sample.

AMENDMENT (gate-marker F3, 2026-10-09): the same extension must also cover the
UNPARSEABLE-BUT-FILLED episode_number shape. Today a non-empty episode_number
delivery that ItalianNumberWords cannot parse ("episodio cinquantaquantesimo"
arriving as a word the helper rejects, or a mangled ASR fragment) makes
episodeParsed false, so the JF-814 gate does NOT fire and the request falls
silently to the NextUp core, the same wrong-item substitution. When the gate is
generalized, a non-empty-but-unparseable number slot must be treated as an
elicit (the "quale stagione/episodio?" ask), never as a fall-through: empty and
unparseable are different user intents (absent vs heard-but-garbled).


Marker: a grep for `season_number}` inside PlayEpisodeIntent sample lists in
`Alexa/InteractionModel/templates/*.yaml` (season-ed families carry it alongside
`episode_number`, so the signal is a sample with season but without episode)
finds nothing today; that is the invariant this task protects.
