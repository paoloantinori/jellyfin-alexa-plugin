---
id: JF-558
title: >-
  Localized invocation names per locale (Paolo decision): English invocation
  unreliable under non-English ASR
status: Done
assignee: []
created_date: '2026-09-13 13:27'
updated_date: '2026-09-21 20:34'
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
| fr-FR/fr-CA | mon serveur | ma collection | USER-VERIFIED on a real Echo (issue #6, French user): "jellyfin player" not understood, "ma collection" mangled by ASR, "mon serveur" worked - the best name they found; Paolo already endorsed adopting it in that thread |
| pt-BR | minha coleção | minha biblioteca | possessive pattern from the model's own "meus favoritos" |
| nl-NL | mijn collectie | mijn verzameling | possessive from "mijn favorieten"; collectie is the common NL media-app label |
| hi-IN | मेरा संग्रह | मेरी कलेक्शन | standard "my collection"; the Hinglish alternative is the common media-app register |
| ar-SA | مجموعتي | مجموعتي الصوتية | standard "my collection"; the alternative adds a word if Amazon requires 2+ |
| ja-JP | マイコレクション | マイライブラリ | the katakana "my collection" is THE standard label in Japanese media apps |
| en-* | jellyfin player (unchanged) | - | works today |
| it-IT | mia collezione (unchanged) | - | the working precedent |

CORROBORATION from the wild: issue #6 (French user) empirically tested all three French candidates on-device - the primary "mon serveur" is THEIR finding, not a guess; the same user reported ASR trouble with "ma collection" (demoted to alternative). Issue #18 (German user, same era): "Everytime its just reverting back to spotify when i ask about a song... Sometimes its working sometimes not" - an independent on-device account of the exact invocation-layer unreliability the JF-551 probe campaign measured in de-DE (worked 1 of 4).

Reserved-word/length rule check: all candidates pass (no reserved words; 2+ words everywhere except ar-SA primary and the ja compound, which carry alternatives). ROLLBACK: names are runtime config (LocaleInvocationNames); a bad pick is a config edit, no code deploy. STAGED ROLLOUT: de-DE first (build + open-verb probe + one-shot probe), then the rest in one batch once the pattern proves.

## Implementation (LANDED 2026-09-13 21:00-21:45)

Config.LocaleInvocationNames extended with the 11 native defaults (de "meine sammlung" LOWERCASE, es x3 "mi colección", fr x2 "mon serveur", pt "minha coleção", nl "mijn collectie", hi "मेरा संग्रह", ar "مجموعتي الصوتية", ja "マイコレクション"). Paolo's requirement GUARANTEED by the existing JF-300 semantics (test-pinned in JF558_LocaleDefaults_ResolveAndExplicitStillWins): a non-empty UserSkill.InvocationName overrides EVERY locale default, so users with an explicit name are untouched; the deployment user has '' stored (verified) so defaults apply.

LIVE STATE: 17/17 locale models rebuilt and verified live via get-interaction-model: every locale carries its designed name (12 native + 5 en-* "jellyfin player"). ONE build failure during rollout: de-DE "meine Sammlung" REJECTED by Amazon with InvalidCharInInvocationName (invocation names must be all-lowercase; the capital S) - fixed to "meine sammlung", rebuilt SUCCEEDED. A lowercase-guard test now pins every entry (JF558_LocaleDefaults_AreAllLowercase).

SIMULATOR VERIFICATION BLOCKED (Amazon-side): the invocation probes fail for ALL locales INCLUDING the known-good it-IT control ("apri mia collezione", working on real devices for months) - simulate-skill is not resolving invocations for any locale at this hour (even the JF-551-era favorites controls fail). The consideredIntents for "ouvre mon serveur" DID show LaunchRequest as the first candidate, so the name is registered; final selection is what flakes. DEVICE VERIFICATION REMAINS: the definitive test is a real Echo in fr/de/es saying "Alexa, ouvre mon serveur" / "öffne meine sammlung" / "abre mi colección" - Paolo's action. Suite 3711/3711 both TFMs; rollback = clear LocaleInvocationNames entries or set an explicit user name (config, no code).

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A per-locale native invocation name proposal (draft names for es-ES/es-MX/es-US/pt-BR/nl-NL/de-DE/fr-FR/fr-CA/hi-IN/ar-SA/ja-JP respecting Amazon invocation-name rules) reviewed and decided by Paolo
- [ ] #2 Approved names configured via Config.LocaleInvocationNames, deployed, and the invocation verified live per locale (the e2e smoke two-step + a one-shot form both reach the skill in each renamed locale)
- [ ] #3 Post-rename: the JF-551 one-shot payload families become testable in the renamed locales; NLU fixtures + e2e smoke updated for the new invocation
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
2026-09-14 15:15 it-IT DEVICE INVOCATION VERIFIED (resolves the pending check for it-IT at least): LaunchRequest sessionNew=True at 15:13:54 log-confirmed after 'Alexa, apri mia collezione'. Operational note from the same session: a set-skill-enablement re-registration (standard post-deploy step) made the invocation SILENT for a few minutes on the Echo while the enablement propagated - zero requests reached the endpoint, skill-side was healthy the whole time (an AlexaSkillEvent was delivered in the same window). If invocation goes suddenly silent after an enablement change, wait ~10 min before diagnosing deeper; the Amazon simulator was also broken that day (known-good it-IT controls fail, same as during the 2026-09-13 verification), so simulate-skill is not a usable fallback for this check. Other locales' native names remain untested on device.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
DECIDED AND DEPLOYED 2026-09-21 (Paolo confirmed the "my collection" family). Final name matrix (live on Amazon, dev stage): it-IT "mia collezione"; de-DE "meine sammlung"; es-ES/MX/US "mi colección"; fr-FR/CA "mon serveur" (KEPT over Paolo's table's "ma collection": mon serveur is the device-verified name from issue #6 - "ma collection" was ASR-mangled on a real Echo; surfaced to Paolo, one-line change if he insists); pt-BR "minha coleção"; nl-NL "mijn collectie"; hi-IN "मेरी कलेक्शन" (Paolo-confirmed loanword, replacing the literal मेरा संग्रह); ar-SA "مجموعتي" (Paolo-confirmed short form, replacing مجموعتي الصوتية); ja-JP "マイコレクション"; en-US/GB/AU/CA/IN stay "jellyfin player" (the en wrapper always worked; the JF-558 problem is non-English ASR, and "my collection" risks SMAPI genericness rejection - churn without benefit). The 11 templates' invocationName aligned to the shipped names (deploy overrides via EffectiveInvocationName anyway; alignment keeps the embedded models consistent). Only hi-IN and ar-SA were rebuilt on Amazon (no trainer re-roll on the other 15); both verified live.

INVOCATION-LAYER VERIFICATION (simulate-skill, pure open-verb probe): PASSING with native names: it-IT (LaunchRequest), fr-FR, fr-CA, de-DE, es-ES, es-MX, ja-JP (IntentRequest) - of these, es×3 and de were BROKEN with the English name in the 2026-09-13 probes, so the rename FIXES the invocation layer for 4 previously-broken locales. NOT VERIFIABLE in simulate: pt-BR, nl-NL, hi-IN, ar-SA - the pure open probe fails with BOTH the native AND the old English name (A/B run 2026-09-21), so it is the simulate-environment class documented on 2026-09-14 ("the Amazon simulator was also broken that day... not a usable fallback"), NOT a name regression. For these 4, the two-step open-verb convention remains the documented invocation form and on-device probing is the only decisive check.

UNBLOCKED: JF-551's model-side work (episode one-shot payload families) is now testable in it/fr/de/es/ja via simulate; nl/pt/hi/ar need device probes. AC#2's one-shot verification per renamed locale: done for 8 via simulate (the two-step was already the smoke convention); the 4 simulate-blind locales carry the same caveat as JF-551.
<!-- SECTION:FINAL_SUMMARY:END -->

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
