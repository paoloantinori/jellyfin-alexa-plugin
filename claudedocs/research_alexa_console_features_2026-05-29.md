# Research: Alexa Developer Console Features We Haven't Explored

**Date**: 2026-05-29
**Confidence**: High (primary Amazon documentation sources)

---

## Executive Summary

Three features were investigated: **NLU Annotation Sets**, **Custom Tasks**, and **Testing Tools**. The most immediately actionable finding is that NLU Annotation Sets are **deprecated** — we should not invest time there. Custom Tasks with Routines integration has moderate potential for the Jellyfin skill. Several testing tools we already use via SMAPI have console equivalents worth knowing about.

---

## 1. NLU Annotation Sets (NLU Evaluation Tool)

### Status: DEPRECATED

The [NLU Annotation Set API reference page](https://developer.amazon.com/en-US/docs/alexa/smapi/nlu-annotation-api-reference.html) now redirects to the [Deprecated Features page](https://developer.amazon.com/en-US/docs/alexa/ask-overviews/deprecated-features.html#nlu-evaluation-tool). Amazon has deprecated this feature.

### What it was

The NLU Evaluation Tool allowed batch testing of interaction model NLU accuracy. You would create an **annotation set** — a JSON file mapping utterances to expected intents and slots — then run it against your model to get pass/fail results. It was essentially what our `tests/integration/fixtures/<locale>.yaml` NLU test fixtures do via `ask smapi simulate-skill`, but at the model level only (no skill backend involved).

### Why it doesn't matter

We already have equivalent (and arguably better) coverage:
- Our `run_nlu_tests.sh` uses SMAPI `simulate-skill` to test NLU resolution with real model builds
- Our `validate_interaction_models.py` validates structural integrity of all 17 models
- Our `run_e2e_tests.sh` tests the full pipeline (NLU + backend handler)

The tool was deprecated because Amazon shifted toward the **Utterance Profiler** (still active) for model-level testing.

### Relevance to Jellyfin Plugin: NONE (deprecated)

---

## 2. Custom Tasks

### Status: Public Beta (active)

Custom Tasks are a way to define named, typed actions that your skill can perform, exposed through three channels:

1. **Skill Connections** — Other skills can invoke your tasks
2. **Quick Links** — URL-based deep links that launch a specific task in your skill
3. **Alexa Routines** — Users can add your task as an action in their Alexa Routines

### How it works

1. Create a task definition file (`{TaskName}.{Version}.json`) using OpenAPI 3.0 schema
2. Place it in a `tasks/` folder in the skill package
3. Register the task in `skill.json` under `apis.custom.tasks`
4. Implement a handler that checks `request.task.name` on LaunchRequest
5. For Routines: add `x-amzn-display-details` with localized titles/descriptions
6. Requires skill certification and publishing for Routines/Skill Connections

### Routines Integration Details

- Tasks appear as actions in the Alexa app under **More > Routines > Add action > Skills**
- Supports three task types:
  - No input parameters (e.g., "Play my favorites")
  - Single-string input (e.g., "Play artist {name}")
  - Predefined enum inputs (e.g., "Play {genre}" where genre is a fixed list)
- Max 1 input parameter for Routines integration
- Requires `x-amzn-alexa-access-scope: public`
- Requires localized `x-amzn-display-details` per locale (all 15 supported locales)

### Quick Links

Quick Links are HTTPS URLs like `https://alexa-skills.com/c?{skill-id}&task={TaskName}` that:
- Open the Alexa app and deep-link to a specific task
- Can be embedded in websites, emails, QR codes
- Support passing input parameters

### Relevance to Jellyfin Plugin: MODERATE

**Potential use cases:**
- "Play my recently added music" as a Routine action (no input parameter)
- "Play artist {name}" as a Routine action (single-string input)
- "Play {genre}" with predefined genres (enum input)
- Quick Links for "play my favorites" or "shuffle all music"

**Challenges:**
- Requires skill certification and publishing (our skill is self-hosted, not in the Alexa Skill Store)
- Currently requires ASK CLI v2 skill package format (we use SMAPI directly)
- Routines only support max 1 input parameter
- Tasks cannot include audio/video player or APL directives in their response — this is a **major blocker** for a media skill, since our core functionality IS audio playback
- Beta status means APIs could change

**Verdict**: The AudioPlayer directive restriction makes Custom Tasks largely incompatible with a media playback skill like ours. The response to a Custom Task invocation **cannot include AudioPlayer directives**, which means we can't start playback as a task response. This is a fundamental architectural limitation.

---

## 3. Testing Tools in the Developer Console

### 3a. Utterance Profiler (ACTIVE — we should use this more)

**What**: Tests what intent and slots a given utterance routes to, independent of skill backend code.

**Access**: Developer Console > Build tab > Utterance Profiler button (upper right)
**API**: `ask smapi profile-utterance` or the [Utterance Profiler REST API](https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html)

**Why it's useful for us**: We can test NLU routing without deploying the model or running simulate-skill. This is faster than our current NLU test pipeline for quick spot-checks.

**We could**:
- Use the Utterance Profiler API to add a lightweight "does this utterance resolve to the right intent?" check before full NLU test runs
- Use it interactively in the console when adding new utterances to quickly verify routing
- Integrate it into our `validate_interaction_models.py` script for a pre-deployment sanity check

**Limitation**: Only tests intent/slot resolution, not the full request pipeline. Our simulate-skill NLU tests remain necessary for end-to-end validation.

### 3b. Intent History (ACTIVE — useful for live skill optimization)

**What**: Shows aggregated, anonymized frequent utterances that real users sent to the skill, along with resolved intents, confidence levels, and slot values.

**Access**: Developer Console > Build tab > Intent History (left sidebar)
**API**: [Intent Request History API](https://developer.amazon.com/en-US/docs/alexa/smapi/intent-requests-history.html) and `ask smapi intent-requests-history`

**Key details**:
- Requires 10+ unique users per day per locale for data to appear
- Shows HIGH/MEDIUM/LOW confidence levels
- LOW confidence utterances trigger a reprompt instead of being sent to the skill
- Can map unmapped utterances directly to intents from the console
- Available for both development and live skills
- Export feature for offline analysis

**Why it's useful for us**:
- We have 17 locales — this would show which locales have NLU issues with real users
- We could discover utterances users say that we haven't accounted for (especially in non-English locales)
- LOW confidence mappings tell us exactly where NLU is failing

**Challenge**: Our skill likely doesn't have 10+ unique users per day per locale, so data may be sparse. This tool is more valuable for published skills with significant user bases.

### 3c. Alexa Simulator (ACTIVE — we already use the API equivalent)

The console's Simulator is the GUI version of what our `run_e2e_tests.sh` does via `ask smapi simulate-skill`. No new capability here.

### 3d. Skill Validation API (ACTIVE — useful pre-certification check)

**What**: Runs a suite of validation checks on the skill before certification submission.

**Access**: Developer Console > Certification tab > Run
**API**: `ask smapi validate-skill` or the [Skill Validation REST API](https://developer.amazon.com/en-US/docs/alexa/smapi/skill-validation-api.html)

**What it checks**:
- Interaction model structure
- Skill manifest completeness
- Endpoint accessibility
- SSL certificate validation
- Session handling

**Why it's useful**: We could add a validation step to our CI pipeline or release process as a pre-flight check before any model deployment.

### 3e. ASR Evaluation Tool (ACTIVE — specialized for speech recognition)

Tests whether audio files are transcribed correctly (speech-to-text accuracy). This is about ASR, not NLU. Not relevant to our text-based testing pipeline.

---

## Summary: What's Worth Pursuing

| Feature | Status | Relevance | Action |
|---------|--------|-----------|--------|
| NLU Annotation Sets | **Deprecated** | None | Skip |
| Custom Tasks — Routines | Beta | **Low** (AudioPlayer restriction) | Skip for now |
| Custom Tasks — Quick Links | Beta | Low (needs certification) | Skip |
| Custom Tasks — Skill Connections | Beta | Low (needs certification) | Skip |
| Utterance Profiler | Active | **High** | Integrate into dev workflow |
| Intent History | Active | **Medium** | Check periodically for real-user data |
| Skill Validation API | Active | **Medium** | Add to release process |
| Alexa Simulator | Active | Already using (via SMAPI) | No change |

### Top Recommendation

**Utterance Profiler API** is the highest-value unexplored feature. We could:
1. Add a `scripts/profile_utterances.sh` script that runs batches of utterances through the profiler
2. Use it as a fast pre-check before full NLU test runs (no model deployment needed)
3. Integrate it into the CI advisory pipeline alongside `validate_interaction_models.py`

---

## Sources

- [NLU Annotation Set API Reference](https://developer.amazon.com/en-US/docs/alexa/smapi/nlu-annotation-api-reference.html) — redirects to deprecated features
- [Deprecated Features](https://developer.amazon.com/en-US/docs/alexa/ask-overviews/deprecated-features.html)
- [Implement Custom Tasks in Your Skill](https://developer.amazon.com/en-US/docs/alexa/custom-skills/implement-custom-tasks-in-your-skill.html)
- [Integrate Custom Task with Alexa Routines](https://developer.amazon.com/en-US/docs/alexa/custom-skills/integrate-custom-task-with-alexa-routines.html)
- [Test the Design of the Interaction Model](https://developer.amazon.com/en-US/docs/alexa/interaction-model-design/test-the-design-of-the-interaction-model-for-your-skill.html)
- [Utterance Profiler REST API](https://developer.amazon.com/en-US/docs/alexa/smapi/utterance-profiler-api.html)
- [Intent Request History API](https://developer.amazon.com/en-US/docs/alexa/smapi/intent-request-history.html)
- [Skill Validation REST API](https://developer.amazon.com/en-US/docs/alexa/smapi/skill-validation-api.html)
- [Review the Intent History](https://developer.amazon.com/en-US/docs/alexa/custom-skills/review-intent-history-devconsole.html)
- [Improving NLU Accuracy of Your Alexa Skills](https://developer.amazon.com/en-IN/blogs/alexa/alexa-skills-kit/2020/01/improving-nlu-accuracy-of-alexa-skills)
- [Quick Links for Alexa](https://developer.amazon.com/en-US/blogs/alexa/alexa-skills-kit/2020/07/quick-links-custom-tasks-isp)
