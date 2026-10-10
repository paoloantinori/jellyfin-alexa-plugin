---
id: JF-771
title: >-
  JF-771 - hi-IN: the two remaining फिल्म-carried short-form failures found by
  the JF-770 probe map (फिल्म {title} खोजो reads Fallback; फिल्म {title} चलाओ is
  stolen by PlaySong's bare slot-initial {song} चलाओ)
status: Done
assignee: []
created_date: '2026-10-05'
labels:
  - interaction-model
  - nlu
  - hi-IN
dependencies:
  - JF-770
references:
  - backlog/tasks/jf-770 - hi-IN-qualified-फिल्म-देखो-anchor-lost-routing-after-the-JF-766-bare-removal-exact-sample-present-identical-rebuild-does-not-recover.md
  - backlog/tasks/jf-459*.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
GUARD (2026-10-10, JF-853): any backlog CLI/MCP edit on this file FAILS with ENAMETOOLONG AND DELETES it (pre-existing upstream bug; mechanism and issue draft in JF-853). Hand-edit only, never MCP-edit.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
FOLDED FROM RAW TAIL (2026-10-09, the truncation sweep; content verbatim, previously outside the managed sections):

Filed 2026-10-05 by the JF-770 worktree agent: two real routing failures its
family-strength probe surfaced that are OUTSIDE JF-770's देखो scope (same-turn
filing per the review-recommendation discipline).

EVIDENCE (live profile-nlu 2026-10-05, skill discovered fresh, 4x repeats,
selectedIntent-first, on the live post-JF-766 model whose content was verified
equal to the committed model before probing):

1. `फिल्म इंसेप्शन खोजो` → AMAZON.FallbackIntent 4/4. The खोजो tail
   (`फिल्म {title} खोजो`) is a single-member qualified family, the same thin
   shape as the देखो tail JF-770 fixes, and it fails the same way.
2. `फिल्म इंसेप्शन चलाओ` → PlaySongIntent 4/4 with song='फिल्म इंसेप्शन' (the
   whole movie phrase absorbed). Mechanism: PlaySong's bare slot-initial
   `{song} चलाओ` sample, the JF-459/JF-761 absorption class on the PlaySong
   side.

CONTEXT AND THE REASON THESE ARE FILED, NOT FIXED HERE:

- JF-770's probe map shows the failures are फिल्म-carried-short-form-specific:
  every non-फिल्म short form routes clean (वीडियो/चलो/क्या तुम carriers), and
  the long मैं फिल्म form routes too. ja's 映画 analogs all route.
- NO pre-removal datum exists for either leg (the JF-766 matrix never probed
  खोजो or the फिल्म+चलाओ shape), so unlike the देखो anchor (which has the
  JF-766 worker's pre-removal 4/4 PlayVideo routing proof) these may be
  LONG-STANDING failures, not JF-766 regressions. That question is answerable
  with one git-history probe if a pre-removal model snapshot exists, else it
  stays open.
- JF-770's remediation (three qualified देखो variants) does not touch these
  tails by construction; fixing them inside JF-770 would have doubled the
  unverifiable surface (its verification bar covers the देखो anchor only).

CANDIDATE SHAPES to weigh WHEN PICKING UP (probe-first, same bar as JF-770):

- खोजो leg: the JF-770 shape generalized, i.e. qualified खोजो variants
  (`वीडियो {title} खोजो`, `मूवी {title} खोजो`), IF the JF-770 post-deploy
  matrix (its leg 7) shows the देखो fix shifts the फिल्म-carrier competition
  generally or leaves it unchanged. If `फिल्म इंसेप्शन खोजो` turns green after
  the JF-770 deploy with no खोजो-side change, close this leg as
  collateral-fixed and record it.
- चलाओ leg: harder. The absorbing sample is PlaySong's bare `{song} चलाओ`, a
  load-bearing song form (JF-761 kept the ja song-family bare form
  deliberately: different slot type, no recorded steal then; this is the
  recorded steal now, in hi-IN only). Options to weigh: qualified-only
  PlayVideo चलाओ reinforcement (the same family-strengthening play), or
  accepting the steal as the JF-459-class cost with the शुरू करो/वीडियो
  carriers as the user-facing workaround. Do NOT remove the PlaySong bare
  form without a live steal probe battery on the song side first (removing it
  would risk the JF-399 PlaySong enrichment recall).

VERIFICATION BAR: whatever shape is chosen, both legs probe 4/4 post-deploy
with the JF-770 and JF-766 guards still green (the देखो anchor PlayVideo, the
episode forms PlayEpisode, the bare-movie guard).

## Finding 3 (appended 2026-10-05 from the JF-770 code review): the class is
CROSS-LOCALE, de-DE confirmed live

The JF-770 review flagged that no other locale was audited for the
carrier-crossed single-member watch-tail shape. de-DE PlayVideoIntent ships
exactly that: the watch-verb tails schauen (`Lass uns {title} schauen`),
anschauen (`Ich will {title} anschauen`), and zeigen (`Kannst du {title}
zeigen`) are each single-member (sehen and ansehen have 2 members each; a
BARE `{title} ansehen` also survives there, unlike hi-IN's देखो).

LIVE PROBE (profile-nlu de-DE, same session, skill discovered fresh, 4x
repeats, selectedIntent-first):
1. `Film Inception anschauen` (qualified Film carrier crossed with the
   single-member anschauen tail, the exact JF-770 analog) → NO_SELECTION 4/4.
   REPRODUCED: the carrier×tail crossing does not generalize.
2. `Ich will Inception anschauen` (the sample's own shape) → PlayVideoIntent,
   title='inception', 4/4. The single-member tail itself is healthy, exactly
   the hi-IN वीडियो/मैं pattern.
3. `Inception ansehen` (bare, 2-member tail incl. the bare sample) →
   PlayVideoIntent clean 3/3 (1 parse hiccup on the reader side, not the
   probe).

So the failure class is NOT hi-IN-specific: a qualified movie carrier crossed
with a single-member watch-verb tail fails in de-DE too (reading NO_SELECTION
rather than Fallback). Candidate shape when picked up: the same
family-strengthening play (qualified crossings of the thin tails, e.g.
`Film {title} anschauen`, `Film {title} schauen`), plus a one-pass audit of
the other 14 locales for the same single-member-tail shape (the ja 映画
3-tail family and hi-IN's post-JF-770 देखो family are the healthy
counterparts to compare against).
2026-10-10 PROBE MAP + CHOSEN SHAPES (the worker's report, landed by the orchestrator per the hand-off; live profile-nlu, skill discovered fresh, live model verified sample-equal to committed, 4x repeats, selectedIntent-first, RAW slot values):

- JF-771 legs reconfirmed: film-carried khojo -> Fallback 4/4; film-carried chalao -> PlaySong whole-phrase absorption 4/4.
- KHOJO TAIL MAP (new datum: comprehensively broken, not film-specific): video+khojo NO_SELECTION 4/4 (strongest carrier does not generalize without a sample), movie+khojo Fallback, bare khojo NO_SELECTION, and even SearchMedia's EXACT 'ek film {query} khojo' sample reads Fallback - the film-carried khojo form is trainer-anomalous like dekho.
- CHALAO TAIL MAP: video+chalao routes PlayVideo 4/4; movie+chalao STOLEN by PlaySong (no movie+chalao sample existed); bare movie title+chalao NO_SELECTION (not stolen); the bare '{song} chalao' is LOAD-BEARING for natural song asks (bohemian rhapsody chalao -> PlaySong 4/4) so removing/narrowing it was REJECTED on evidence; movie+laga stole identically (the review-round catch, probed before the fix).
- CONTROLS: movie+dekho routes PlayVideo 4/4 (JF-770's added sample, first live confirmation, never in JF-770's matrix); movie+shuru-karo routes via the bare sample with whole-absorption (left as is, the JF-770 main-film class).

CHOSEN SHAPES: khojo leg = video/movie {title} khojo added to PlayVideoIntent (the missing-pattern fix, JF-770 L1b precedent double-verified); chalao leg = movie {title} chalao + movie {title} laga added (family-strengthening on the sample-absent crossings) + a guard comment at PlaySong's bare sample + guard pins; the film-carried forms stay accept-and-document (trainer anomaly, two-failed-fixes rule honored). Files: templates/hi-IN.yaml +4 samples, model regenerated 366->370 exactly, VOICE_COMMANDS_BY_LOCALE regenerated, fixtures +6 (4 red-until-deploy per the JF-766 convention, 2 green guards incl. video+chalao). Validators at baseline (291 warnings unchanged), dry-run 8 passed/1158 skipped. Gates: /simplify (efficiency CLEAN; comment compression + the altitude catch that found laga applied) + /code-review high (3 applied; finding 3 REFUTED by a simulator control - the whole Inception corpus is an NLU-layer probe vehicle since not even Latin 'Inception' resolves in the library; findings 1-2 landed as this note + the cross-locale task). COMMIT 4c09f8b1 on jf771. CLOSURE rides the post-deploy matrix (11 legs, recorded in the orchestrator's checkpoint; the 4 fixture pins go green when the rebuild deploys - the JF-770 closure shape). UNTRIED DOOR, low priority: a handler-side video-noun strip in the greedy-slot entity fallback (the TryEntityFallbackAsync/JF-463 pattern) would be trainer-immune for the chalao steal.

POST-DEPLOY MATRIX OUTCOME (2026-10-10 13:05, orchestrator; deploy f2832df9 + hi-IN rebuild SUCCEEDED): ALL 11 LEGS EXACTLY AS PREDICTED. LEG1-4 (the fix bar): video/movie+khojo and movie+chalao/laga ALL route PlayVideoIntent 4/4 - the khojo missing-pattern fix and the chalao steal fix work LIVE. LEG7 song guards green (bare '{song} chalao' still routes bohemian rhapsody 4/4 - no regression on the load-bearing form). LEG8 family stability 4/4 across video+chalao/dekho, movie+dekho/shuru-karo. LEG9 the JF-766 steal-fix guards hold (both episode addresses route PlayEpisodeIntent 4/4 - the family growth did not reopen the steal). LEG5-6 (record only): the film-carried forms stay Fallback / PlaySong-steal - the trainer anomaly persists, logged as JF-770's FIFTH-rebuild data point. The 4 red-until-deploy fixture pins are now live-green by construction (the matrix is the proof; the NLU suite will carry them durably). TASK CLOSED on the full bar.
<!-- SECTION:NOTES:END -->
