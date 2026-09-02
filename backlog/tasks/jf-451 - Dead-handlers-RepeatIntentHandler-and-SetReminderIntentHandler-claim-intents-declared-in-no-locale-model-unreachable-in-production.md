---
id: JF-451
title: >-
  Dead handlers: RepeatIntentHandler and SetReminderIntentHandler claim intents
  declared in no locale model (unreachable in production)
status: Done
assignee: []
created_date: '2026-09-02 02:50'
updated_date: '2026-09-02 19:36'
labels:
  - routing
  - dead-code
  - interaction-model
dependencies: []
references:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/RepeatIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SetReminderIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchRoutingTests.cs
  - JF-272
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Found by the JF-433 dispatch harness (DispatchRoutingTests.EveryClaimedIntent_AppearsInSomeModel, allowlisted there until fixed). Two handlers claim intent names that NO locale model declares, so Amazon never routes those intents to the skill: the handler code cannot fire in production (the FallbackIntentHandler failure class).

1. RepeatIntentHandler (Alexa/Handler/Intent/RepeatIntentHandler.cs) claims the literal "AMAZON.RepeatIntent"; declared in 0 of 17 models. Every other handled built-in (PauseIntent, StopIntent, StartOverIntent, LoopOffIntent...) is declared in 13 to 17 models, so model declaration is the norm for built-ins here: without it the repeat utterance does not reach the skill.
2. SetReminderIntentHandler claims IntentNames.SetReminder ("SetReminderIntent"); declared in 0 of 17 models. The models declare SleepTimerIntent instead (handled by SleepTimerIntentHandler). Related existing task: JF-272 (SetReminder via Alexa Reminders API verification). Either the intent was meant to be in the models or the handler is leftover.

Also confirmed adjacent-but-alive: PlayIntentHandler claims "PlayIntent" (in no model) but stays reachable through the hardware PlaybackController Play button, so its intent-name branch is model-dead while the handler is not.

Decide per handler: add the intent to all 17 models (it-IT via the YAML template, others via JSON; then regenerate and redeploy) or delete the dead handler plus its locale strings. When fixed, remove the corresponding entries from notInModelAllowlist in Jellyfin.Plugin.AlexaSkill.Tests/Handler/DispatchRoutingTests.cs (and the "AMAZON.RepeatIntent" probe note in BuildAllProbes) so the tests enforce the fixed state.
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

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Both dead handlers resolved by opposite, evidence-based decisions. SetReminderIntent: the feature was fully built (handler, 17-locale strings, manifest permission, Alexa.NET.Reminders, unit tests) with only the model declaration missing, so it is now declared in ALL 17 models with duration_minutes (AMAZON.NUMBER, ItalianNumber in it-IT) + reminder_time (AMAZON.TIME); no message slot (AMAZON.SearchQuery cannot coexist, anti-pattern #2). The review found it-IT word numbers could never int.TryParse, so the new ItalianNumberWords parser (mirror-guarded: a test enumerates the embedded it-IT model's slot values and asserts each parses) is adopted by SetReminder AND the two other ItalianNumber consumers the review surfaced (GoToChapter chapter_number, PlayEpisode season/episode numbers). RepeatIntentHandler: DELETED; it was a semantic hijack (built-in repeat-that answered with now-playing info, MediaInfo's job) and declaring it would collide with the curated fr 'Répète' and it-IT Ripeti families; zero test references. NLU fixtures gained SetReminder routing entries in 11 locales plus an e2e it-IT routing entry (en-US e2e entry removed as unreliable per the documented simulate-skill limitation); 5 locales' fixture entries deferred to JF-400. Simulator list now derived from IntentNames (fixes pre-existing drift: FindSong was missing). MODEL DEPLOY REQUIRED (all 17 locales changed). Commit 5b8c49d7. Follow-ups on JF-272 (SleepTimer word numbers) and JF-400 (fixture gaps).
<!-- SECTION:FINAL_SUMMARY:END -->
