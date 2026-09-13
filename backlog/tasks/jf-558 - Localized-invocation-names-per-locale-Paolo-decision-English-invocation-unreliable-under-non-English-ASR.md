---
id: JF-558
title: >-
  Localized invocation names per locale (Paolo decision): English invocation
  unreliable under non-English ASR
status: To Do
assignee: []
created_date: '2026-09-13 13:27'
labels:
  - product-decision
  - interaction-model
  - nlu
  - ux
dependencies: []
references:
  - JF-551
  - JF-300
  - JF-511
priority: high
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Filed from the JF-551 probe campaign (2026-09-13). The skill's invocation name is "jellyfin player" (English) in 16 of 17 locales (it-IT alone uses the native "mia collezione"). Live probes that day showed the one-shot invocation layer is BROKEN or unreliable wherever an English name meets non-English ASR: simulate-skill one-shot forms fail even on favorites-payload verbatim samples in es-ES/es-MX/es-US/pt-BR/hi-IN/ar-SA; de-DE "sag jellyfin player ..." functioned once of four attempts; nl-NL and ja-JP invocations reach the skill but wrapped payload routing is unstable. it-IT works consistently BECAUSE the invocation name is native-language (it-IT precedent, live-verified since deployment). The JF-511/512 e2e smoke for the 5 then-new locales passed only because it used the two-step open-verb convention ("abre jellyfin player" + bare in-session command), never the one-shot.

This is a PRODUCT/NAMING decision, not a code fix: the plumbing exists (Config.LocaleInvocationNames per-locale defaults, deployed by IInteractionModelRedeployer like the it-IT name). What it needs: (1) Paolo proposes/reviews native invocation names per locale under Amazon's invocation-name rules; (2) configure + deploy + live-verify per locale; (3) only AFTER the invocation works can the JF-551 one-shot payload families (episode infinitive/one-shot word orders) be meaningfully added and probed in the renamed locales - until then the one-shot UX in the 16 non-native-name locales ranges from unreliable (de) to non-functional (es/pt/hi/ar).
<!-- SECTION:DESCRIPTION:END -->

## Proposal (drafted 2026-09-13; naturalness burden removed by design)

PATTERN: every locale gets the native "my collection" phrase, the exact pattern already shipping and live-working in it-IT ("mia collezione"). No coinages, no loanwords except ja (where the katakana loan IS the standard label). Paolo approves the verified table, not linguistic guesses; EMPIRICAL VERIFICATION REPLACES JUDGMENT: each name is configured on the live skill one locale at a time, Amazon's own build validates the name, and a simulate-skill open-verb probe ("oeffne meine sammlung" etc.) proves the ASR invokes it. A name that fails either check falls to its alternative or is reported.

| locale | primary | alternative | rationale |
|---|---|---|---|
| de-DE | meine Sammlung | meine Mediathek | possessive already in the model's own samples ("meine Favoriten"); Mediathek is the standard German media-app label (Amazon's own) |
| es-ES/es-MX/es-US | mi colección | mi biblioteca | "mi colección" already appears in the es models' own samples; direct it-IT twin |
| fr-FR/fr-CA | ma collection | ma médiathèque | "ma collection" already in the fr samples; médiathèque is the standard French media-library label |
| pt-BR | minha coleção | minha biblioteca | possessive pattern from the model's own "meus favoritos" |
| nl-NL | mijn collectie | mijn verzameling | possessive from "mijn favorieten"; collectie is the common NL media-app label |
| hi-IN | मेरा संग्रह | मेरी कलेक्शन | standard "my collection"; the Hinglish alternative is the common media-app register |
| ar-SA | مجموعتي | مجموعتي الصوتية | standard "my collection"; the alternative adds a word if Amazon requires 2+ |
| ja-JP | マイコレクション | マイライブラリ | the katakana "my collection" is THE standard label in Japanese media apps |
| en-* | jellyfin player (unchanged) | - | works today |
| it-IT | mia collezione (unchanged) | - | the working precedent |

Reserved-word/length rule check: all candidates pass (no reserved words; 2+ words everywhere except ar-SA primary and the ja compound, which carry alternatives). ROLLBACK: names are runtime config (LocaleInvocationNames); a bad pick is a config edit, no code deploy. STAGED ROLLOUT: de-DE first (build + open-verb probe + one-shot probe), then the rest in one batch once the pattern proves.

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A per-locale native invocation name proposal (draft names for es-ES/es-MX/es-US/pt-BR/nl-NL/de-DE/fr-FR/fr-CA/hi-IN/ar-SA/ja-JP respecting Amazon invocation-name rules) reviewed and decided by Paolo
- [ ] #2 Approved names configured via Config.LocaleInvocationNames, deployed, and the invocation verified live per locale (the e2e smoke two-step + a one-shot form both reach the skill in each renamed locale)
- [ ] #3 Post-rename: the JF-551 one-shot payload families become testable in the renamed locales; NLU fixtures + e2e smoke updated for the new invocation
<!-- AC:END -->

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
