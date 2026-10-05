---
id: JF-770
title: >-
  JF-770 - hi-IN: the qualified फिल्म {title} देखो anchor lost routing after the
  JF-766 bare removal (exact sample present in the live model; identical-content
  rebuild does NOT recover: not the trainer-nondeterminism class)
status: To Do
assignee: []
created_date: '2026-10-05'
labels:
  - interaction-model
  - nlu
  - hi-IN
  - regression
dependencies:
  - JF-766
references:
  - backlog/tasks/jf-766 - hi-IN-PlayVideo-bare-title-देखो-carries-the-same-SearchQuery-absorption-class-as-JF-761-committed-episode-sample-shares-the-tail-verb.md
  - backlog/tasks/jf-459*.md
priority: high
---

## Description

Filed 2026-10-05 by the orchestrator from JF-766's post-deploy probe matrix (its
DoD #3 regression guard FAILURE, the file-not-weaken discipline).

EVIDENCE (all live profile-nlu, hi-IN, 4x repeats per step, selectedIntent-first,
skill discovered fresh):

1. PRE-REMOVAL (the JF-766 worker's probe on the old model):
   `फिल्म इंसेप्शन देखो` → PlayVideoIntent, title='इंसेप्शन', 4/4. CLEAN.
2. POST-REMOVAL model 1 (after the JF-766 DLL deploy + hi-IN rebuild):
   `फिल्म इंसेप्शन देखो` → AMAZON.FallbackIntent, 5/5 readings (1 in the matrix
   + 4 in the stability re-probe). REGRESSED.
3. The exact sample `फिल्म {title} देखो` IS in the live model's PlayVideoIntent
   (verified via get-interaction-model after the rebuild; the only देखो sample
   remaining, 11 total samples). The model content is CORRECT.
4. IDENTICAL-CONTENT REBUILD (the nlu_trainer_nondeterminism discrimination
   step): rebuilt hi-IN again, SUCCEEDED; re-probed 4x: Fallback 4/4. The
   classic trainer flip RECOVERS on identical-content rebuild; this does not.

The JF-766 steal fix itself is verified working (the lead/library/sibling probes
all route PlayEpisodeIntent clean), so this is NOT a revert case: it is a RECALL
LOSS on the qualified video-watch form, and per the matrix's own guard language
a new finding to file, not a fixture to weaken. Both video-watch forms are now
unreachable in hi-IN (bare इंसेप्शन देखो reads NO_SELECTION, the qualified form
reads Fallback), a larger JF-459-class recall cost than the filing anticipated.

HYPOTHESIS (for whoever picks it up): removing the bare sample removed the
trainer's only other देखो-tailed PlayVideo example, and the remaining single
qualified sample is not enough signal for the intent to win Fallback
competition. Candidate remediations to weigh: (a) strengthen the qualified
family (add 2-3 more qualified देखो variants: मूवी {title} देखो, फिल्म देखो
{title} word-order twin, a titled literal example), (b) re-add the bare sample
with a NARROWER slot type than SearchQuery if the model layer allows, (c) accept
the recall cost and document the workaround (the शुरू करो sibling still routes
video). Option (a) is the JF-459-discipline shape. Probe-first: verify the
family-strength hypothesis by probing the OTHER qualified PlayVideo forms
(फिल्म {title} चलाओ etc.) before choosing.

VERIFICATION BAR: the chosen shape deploys, the फिल्म anchor routes PlayVideo
4/4, the JF-766 steal fix stays closed (the episode forms keep routing
PlayEpisode), and the bare-movie guard still passes (no PlayEpisode steal).
