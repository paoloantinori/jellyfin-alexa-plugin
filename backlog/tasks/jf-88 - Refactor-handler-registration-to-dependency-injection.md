---
id: JF-88
title: Refactor handler registration to dependency injection
status: Done
assignee: []
created_date: '2026-05-06 19:22'
updated_date: '2026-05-06 22:08'
labels:
  - code-quality
  - refactoring
milestone: m-2
dependencies: []
references:
  - claudedocs/research_alexa_best_practices_2026-05-06.md
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Refactor handler registration from manual instantiation in the controller constructor to proper dependency injection. Currently all 40+ handlers are instantiated in `AlexaSkillController` constructor via `new` — a ~140-line constructor that's hard to test, hard to extend, and tightly couples the controller to every handler's constructor signature.

Target: Register handlers in `SkillStartup.cs` with `services.AddTransient<BaseHandler, PlayIntentHandler>()` etc., and inject `IEnumerable<BaseHandler>` into the controller.

This is a refactoring, not a feature change, but it significantly improves testability and maintainability for future handler additions.

Files: `SkillStartup.cs`, `Controller/AlexaSkillController.cs`, potentially handler constructors if they need DI parameters.

Research source: `claudedocs/research_alexa_best_practices_2026-05-06.md` section 5.2
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 All 40+ handlers registered in SkillStartup.cs via services.AddTransient<BaseHandler, THandler>()
- [ ] #2 AlexaSkillController injects IEnumerable<BaseHandler> instead of manually instantiating 40+ handlers
- [ ] #3 Controller constructor reduced from ~140 lines to minimal DI injection
- [ ] #4 CanHandle/HandleAsync routing logic preserved — handler resolution still works correctly
- [ ] #5 All existing unit and integration tests pass
- [ ] #6 No behavioral change — pure refactoring
<!-- AC:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Replaced 58 manual handler instantiations in AlexaSkillController constructor with DI-injected IEnumerable&lt;BaseHandler&gt;. Added reflection-based auto-discovery in Registrator.cs with ReflectionTypeLoadException handling and deterministic ordering. Handlers registered as singletons (stateless pattern). Controller constructor reduced from 8 to 6 parameters.
<!-- SECTION:FINAL_SUMMARY:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 /simplify
<!-- DOD:END -->
