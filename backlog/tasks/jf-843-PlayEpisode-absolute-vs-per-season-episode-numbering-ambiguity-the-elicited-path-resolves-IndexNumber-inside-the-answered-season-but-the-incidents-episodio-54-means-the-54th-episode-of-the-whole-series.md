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
