# Runbook: browser-driven E2E testing of the Alexa skill

Written 2026-09-24 after the JF-624 round-6 session, where this workflow found a bug that three device rounds and all CLI tools could not.

## What it is

I (Claude) drive a real logged-in Chromium via the `dev-browser` CLI (sandboxed Playwright scripts; daemon-managed browser, pages persist across sessions). Paolo logs into the Amazon developer console ONCE in that browser window; the session survives across days. From there I can:

1. Open the skill's Test tab: `https://developer.amazon.com/alexa/console/ask/test/<skill-id>/development/<locale>`
2. Invoke the skill by typing utterances into the Alexa Simulator input (real NLU, real routing, our real endpoint).
3. Read the tabs the CLI cannot reach:
   - **Skill I/O**: full request/response JSON (same as simulate-skill, but interactive).
   - **Device Display**: the RENDERED APL document (DOM tree under `.askt-renderer__container`; components appear as `apl-b`/`apl-s`/`apl-t` elements).
   - **Device Log**: the directive/event delivery timeline (SpeechSynthesizer, AudioPlayer.Play, RenderDocument, PlaybackStarted, SynchronizeState), i.e. what the DEVICE did with our response, not just what we sent.
4. Drive the console UI itself (checkboxes, tabs, build pages), which Paolo sometimes has to enable by hand (2026-09-24: the "Device Display" render checkbox).

## When the browser beats the CLI

| Question | CLI (`ask smapi simulate-skill` / `profile-nlu`) | Browser (dev-browser + Test tab) |
|---|---|---|
| Which intent/slots does an utterance route to? | Equal (profile-nlu is better: no endpoint needed) | Equal |
| Is the response JSON correct? | Equal, and scriptable | Equal |
| Does the APL document RENDER? Any component silently dropped? | NO | YES: Device Display DOM tree |
| Which directives did the device ACCEPT, and which events came back? | NO (response only) | YES: Device Log timeline |
| Iterate a broken screen without device round-trips | NO | Partially (see caveats) |

The decisive case (JF-624 round 6): the progress-bar tick handler was malformed (`SetValue` properties directly on the handler object, no `commands` wrapper). The device showed a static bar with ZERO errors anywhere. The web renderer's DOM tree showed the bar and transport row MISSING from the render, which falsified my "the handler runs but the bar doesn't move" theory and pushed me back to the docs, where the property table showed `commands` is a REQUIRED wrapper. Three device rounds had not found it; one DOM dump did.

## Caveats (learned the hard way)

- **The web renderer is an approximation.** Texts measured 1px wide, the AlexaProgressBar rendered 0x0 even when correctly formed; the alexa-layouts package renders imperfectly. Never trust its PIXELS; its COMPONENT TREE (present vs missing) is the signal.
- The response `shouldEndSession=true` kills the document with the session: capture the display right after the invoke.
- The simulator competes with built-in skills exactly like `simulate-skill` does: it-IT is the reliable locale.
- The old standalone Authoring Tool URL (`/alexa/console/ask/auth-tool/index.html`) is DEAD ("Oh Snap" 404). The tool now lives inside each skill: Build tab > Multimodal Responses, and the Test tab simulator.
- Skill ID must be discovered dynamically (`ask smapi list-skills-for-vendor`); never reuse an old one.

## The workflow, concretely

```bash
# 1. discover the skill id (CLI)
ask smapi list-skills-for-vendor   # find the Jellyfin skill id

# 2. open the test tab in the instrumented browser (dev-browser CLI)
dev-browser <<'EOF'
const page = await browser.getPage("<page-id>") || await browser.newPage();
await page.goto("https://developer.amazon.com/alexa/console/ask/test/<skill-id>/development/it_IT");
EOF

# 3. invoke + inspect (repeatable block)
dev-browser <<'EOF'
const page = await browser.getPage("<page-id>");
const input = await page.$('input[placeholder="Type or click and hold the mic"]');
await input.fill("chiedi a mia collezione di riprodurre la canzone magnolia");
await input.press("Enter");
// then: Device Display DOM tree, Device Log lines, screenshots
EOF
```

Login state: if the page lands on "Oh Snap" or a sign-in, bring the window to front (`page.bringToFront()`) and Paolo types credentials in the visible window. Credentials never pass through the chat.

## Verdict (Paolo's question, answered)

The browser workflow is MORE significant than the CLI equivalent for exactly two things: render inspection (did the document render, which components materialized) and protocol-side device behavior (the Device Log event stream). For everything else (routing, response assertions, regressions at scale) the CLI is better: hermetic, scriptable, no login, runs in CI. The two are complements: CLI for the battery, browser for the failures the battery cannot see.
