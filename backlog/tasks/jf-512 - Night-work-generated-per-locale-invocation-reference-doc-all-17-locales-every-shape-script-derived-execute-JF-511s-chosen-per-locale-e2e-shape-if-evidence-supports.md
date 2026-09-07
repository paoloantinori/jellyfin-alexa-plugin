---
id: JF-512
title: >-
  Night work: generated per-locale invocation reference doc (all 17 locales,
  every shape, script-derived) + execute JF-511's chosen per-locale e2e shape if
  evidence supports
status: To Do
assignee: []
created_date: '2026-09-06 20:13'
updated_date: '2026-09-07 00:51'
labels:
  - documentation
  - i18n
  - e2e
  - night-work
dependencies: []
references:
  - JF-511
  - JF-510
  - VOICE_COMMANDS.md
  - 'CLAUDE.md anti-pattern #11'
  - LocaleInvocationNames
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Overnight directive from Paolo (2026-09-06 evening): once the current queue clears (JF-510 e2e refresh in progress), the night's focus is (1) per-locale e2e and (2) a complete invocation reference doc. Related: JF-511 is the design study for per-locale e2e - if its evidence gathering supports it, EXECUTE the chosen shape this night rather than leaving it a study; JF-510's refreshed it-IT fixtures are the pattern source.

PART 2 - THE DOC (new, this task's deliverable): create docs/VOICE_COMMANDS_BY_LOCALE.md (or extend the docs-site structure per the existing conventions) listing EVERY supported invocation shape for ALL 17 locales, sourced from the models themselves (generate, do not hand-write: a script that reads model_*.json and emits the table per locale, intent by intent, mirroring VOICE_COMMANDS.md's per-locale rows but complete and auto-derived so it cannot drift - the VOICE_COMMANDS.md manual mirror went stale twice, JF-459/JF-494). Include: per locale, each intent's sample set with slot placeholders rendered readably, grouped by what the user wants to do (play music / play video / episodes / search / queue control / favorites / radio / books), and a per-locale invocation-name note (it-IT 'mia collezione' vs the others 'jellyfin player' per the LocaleInvocationNames config). The doc is user-facing: plain language, no internal handler/intent names in the body (an appendix may map intent names for maintainers). One-shot forms documented per locale where the infinitive convention exists (JF-493's per-locale analysis already classified which locales have them). Wire it into the docs-site if the existing docs mirrors require it (anti-pattern #11 obligations), and make the generator script part of the repo (scripts/generate_voice_reference.py) so the next model change regenerates it.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
PART 2 (the doc), worker implementation 2026-09-07; status stays To Do, orchestrator closes.

DELIVERED:
1. `scripts/generate_voice_reference.py`: reads all 17 `Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_*.json` and emits `docs/VOICE_COMMANDS_BY_LOCALE.md`. Stable grouping map in-script (ordered GROUPS list of (heading, [(intent, label, is_builtin)])); any intent present in a model but missing from the map FAILS generation, and any slot referenced by a sample without a localized hint in SLOT_HINTS[lang] fails too, so a new intent/slot forces a doc decision instead of silently degrading. `--check` regenerates in memory and exits 1 on drift (verified: exit 1 with a stale file, exit 0 after regen); nothing wired into CI by design.
2. `docs/VOICE_COMMANDS_BY_LOCALE.md` (generated, 11,632 lines, 7,939 phrases). Per locale: invocation-name note, one-shot note, slot-placeholder legend (per-locale, per-slot localized hints, 10 languages), then every sample of every intent grouped by user goal (17 groups incl. a "Playback control" group that carries the AMAZON.* built-ins: it-IT StopIntent renders its 6 samples, every other built-in renders a single "no custom phrases in this language, Alexa understands these natively" note). Body has zero intent/handler/class names; the appendix maps group headings and command labels back to intent names. Idempotent: two consecutive runs md5-identical (eb8cc311be0b7bff78240d8b815ecdec after the /simplify wording cleanup of the generic one-shot note).
3. Docs wiring (anti-pattern #11 survey): the docs-site is the D3 Diagram Explorer (index.html + graphs.json deployed by pages.yml; data.json an embedded mermaid mirror) with NO nav or document listing, so a non-diagram doc has no docs-site obligation; the repo's only document-listing convention is the README TOC. Added README TOC entry 16 (renumbered License to 17) plus a pointer sentence next to the VOICE_COMMANDS.md reference in Supported Languages. VOICE_COMMANDS.md itself untouched (stays hand-maintained; the generated doc supersedes its row style).
4. Verification (all run, all green): per-locale doc bullet count == model sample count == declared count for all 17 (total 7,939); independent set-match of every rendered sample re-derived from each model using the legend parsed FROM the doc (exact match all locales); it-IT spot-checks: JF-490 carriers present (`un album che si chiama <titolo dell'album>` etc.) and JF-504 film carriers present (`Riproduci il film <titolo del video o del film>` etc.); `--check` stale path exits 1.

DECISIONS / FINDINGS:
- Invocation-name source of truth is `Jellyfin.Plugin.AlexaSkill/Config.cs` (Config.LocaleInvocationNames["it-IT"]="mia collezione", Config.InvocationName="jellyfin player"), NOT PluginConfiguration.cs as the task text guessed; the doc and script cite Config.cs.
- One-shot note per JF-493 classification: 9 locales carry an infinitive layer (it-IT "Di", en-* "to", de-DE "Zu", fr-FR/fr-CA "De"); those get the marker named in their locale section, with a concrete carrier example only for it-IT and en (the documented forms). The other 8 languages (es/ja/nl/pt/ar/hi) get the generic invocation-prefix sentence; no infinitive examples were invented for them.
- Grouping superset: the task's 11 groups extended with "Podcasts", "Browse and discover", "Resume and continue", "Timers and reminders", "Account and voice", "Session and conversation" so all 61 distinct intents (union across locales; per-locale sets differ: en-US 61, de/fr/it 60, rest 59) land in exactly one group.
- Labels are English for all locales (VOICE_COMMANDS.md precedent); slot hints are localized per language.
- Doc has no timestamps, so regeneration is byte-stable.
- Gates note: doc + standalone-script change (no product code); /simplify done inline by the worker, and the full /code-review high gate runs at orchestrator closure as usual. No SMAPI calls, no test/ or CI edits (tests owned by the concurrent worker).

CODE-REVIEW GATE (P-threshold 80), part-2 stream, 2026-09-07: ONE Important finding + two minor items, none applied by the gate (findings-only mandate).

IMPORTANT (85) - locale-set validation one-directional in scripts/generate_voice_reference.py:438-441: load_models checks LOCALE_ORDER-is-a-subset-of-models but never models-is-a-subset-of-LOCALE_ORDER, and generation emits only LOCALE_ORDER (line 654) while the intro hardcodes 'all 17 supported locales' (line 628). Sandbox-proven: adding model_pt-PT.json (copy of pt-BR) passes BOTH validators (pt hints exist), emits 17 sections, and --check exits 0 forever. Same silent-drift class the script exists to kill; new intent/slot abort and force a decision, a new locale in an existing language family (pt-PT, es-AR, en-IE, fr-BE, de-AT) needs zero script edits and skips every guard. Fix: exit on extra locales in load_models (or emit from the glob and drop the hardcoded 17).

MINOR (cut at cap, filed here per landing rule): reverse GROUPS direction unenforced - an intent removed from all 17 models leaves a stale GROUPS entry that still renders an appendix row (appendix iterates GROUPS unconditionally, scripts/generate_voice_reference.py:597-611). Verified today GROUPS == model union exactly (64/64), so no current phantom rows.

MINOR (notes accuracy): Implementation Notes say 'all 61 distinct intents (union across locales)'; the true union is 64 (61 is the en-US count). Verified mechanically.

Gate verification (all green): --check exit 0 against current models; 7,939 bullets == 7,939 model samples; zero samples contain backtick/pipe/angle/newline so markdown code spans cannot break; new-slot abort proven (names locale + slot: 'it-IT: slot recording_studio has no hint'); unmapped-intent abort proven; appendix 64 rows == 64 GROUPS entries == union; 0 'Intent' leaks before the appendix; it-IT che-si-chiama + il-film carriers present; it-IT Stop renders 6 samples (JF-402 exception preserved); invocation-name claims match Config.cs:21,29-32,44-53; README TOC 1-17 correct; determinism confirmed (all output-affecting iteration over LOCALE_ORDER/GROUPS lists or sorted()).

Gate incident (disclosed, resolved): the gate's sandbox symlinked the 17 real model files and its abort-path test wrote through a symlink, minifying and contaminating Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json; restored via git checkout to HEAD (file had no other uncommitted changes) and --check re-verified green. No residual diff.

Formal review dispositions (2026-09-07, orchestrator): Important-85 APPLIED (the locale-set validation is now bidirectional: a NEW locale model file fails generation loudly instead of being silently omitted while the doc claims completeness); the reverse-GROUPS guard APPLIED (an intent removed from every model aborts instead of rendering a phantom appendix row); the notes' union count corrected (64 distinct intents, not 61 - 61 was the en-US count). Reviewer's sandbox incident verified resolved: model_it-IT.json is byte-identical to HEAD (no residual diff). The doc regenerated after hardening: md5 unchanged (eb8cc311...), confirming the guards are pure validation with no output change. Remaining Pyright hints are intentional underscore unpacking placeholders.
<!-- SECTION:NOTES:END -->
