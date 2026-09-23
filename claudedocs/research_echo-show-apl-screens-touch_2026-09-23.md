# Research Report: Better-looking, more informative app screens on Echo Show (APL), including touch interactions

**Date**: 2026-09-23
**Depth**: exhaustive
**Confidence**: HIGH (platform primitives and their properties verified against official docs; community examples corroborate)

## Executive Summary

The plugin's visual ceiling is far above where we are: the official `alexa-layouts` package (v1.7.0) ships ~25 responsive components/templates we currently hand-roll or ignore, including `AlexaBackground` with native `backgroundBlur`, `AlexaTransportControls` (play/pause/next/previous buttons), `AlexaProgressBar`, `AlexaRating`, `AlexaPaginatedList`, and `AlexaDetail`. Touch interaction is fully supported and we ALREADY implement the hard half (an `AplUserEventHandler` that receives `Alexa.Presentation.APL.UserEvent` requests and plays tapped items): extending touch to the NowPlaying and list screens is wiring `TouchWrapper`/`primaryAction` + `SendEvent` into documents we already render, not new architecture. The main platform constraints to respect: screen content has a session lifetime (documents expire when the session ends; there is no push channel to update a screen from outside a request), `AlexaTransportControls` renders buttons but CANNOT control the platform AudioPlayer from APL (each tap is a UserEvent round-trip to us, and pause/next taps are already documented to arrive as `PlaybackController.*CommandIssued` instead), and the Echo Show's own full-screen player takes over during raw AudioPlayer playback unless we re-render.

## Findings

### 1. The responsive component catalog (alexa-layouts 1.7.0): the biggest visual lever

Import once per document: `{"import": [{"name": "alexa-layouts", "version": "1.7.0"}]}`. The catalog maps directly onto our screens [S1, S2]:

| Our screen today | Hand-rolled as | Official replacement | What we gain |
|---|---|---|---|
| NowPlaying (album art + title + subtitle) | custom Container/Image/Text | `AlexaDetail` (image + text, scrolls) or keep custom + `AlexaBackground` w/ `backgroundBlur: true` + `AlexaProgressBar` | blurred art background (the Spotify look), progress bar, consistent spacing/typography across viewports |
| Carousel (browse) | custom horizontal Sequence | `AlexaLists`/`AlexaTextList`/`AlexaPaginatedList` with `primaryAction` | built-in selection states, pagination dots (`AlexaPaginationDots`), toolbar/back button |
| Disambiguation "which one?" lists | spoken only | `AlexaTextList` with `primaryAction: SendEvent` | tap-to-choose instead of saying "il secondo" |
| Welcome/help | spoken only | `AlexaHeadline` | title + background + hint text |
| Chapter/track pick | not visual | `AlexaPaginatedList` (page counter included) | paged tap lists |

`AlexaBackground` natively supports `backgroundBlur` (verified parameter table [S3]): the single cheapest "looks professional" upgrade for NowPlaying is the same album-art URL we already bind, blurred behind content. Our current templates are hand-built JSON strings in `AplHelper` (NowPlayingTemplate literal); adopting the import package does not require a build-time dependency (the device resolves it at render time), only document JSON changes.

`AlexaTransportControls` is exactly the play/pause/next/previous button row [S4]: parameters `primaryControlPlayAction`/`primaryControlPauseAction`/`secondaryControls` accept arbitrary Commands, which for us means `SendEvent` with arguments like `["transport", "next"]`. Two constraints from its own docs: in widgets it must launch a skill (irrelevant for us), and its default `ControlMedia` command targets the APL `Video` component, which we do not use, so every button must be explicitly wired to `SendEvent`.

`AlexaProgressBar` and `AlexaRating` round out the "more informative" kit (progress with `progressValue`/`totalValue`; star ratings for the favorites/rate-this flow).

### 2. Touch interactions: the plumbing already exists in our codebase

The full documented chain [S5, S6]: wrap any component in a `TouchWrapper` (or set `primaryAction` on a responsive list component) whose `Press` handler runs `{"type": "SendEvent", "arguments": [...]}`. The device sends `{"type": "Alexa.Presentation.APL.UserEvent", "arguments": [...], "source": {"type": "TouchWrapper", "id": "...", "handler": "Press"}, "token": "<RenderDocument token>"}` to the skill endpoint. The `token` round-trips the value we set on `RenderDocument`, so the handler can context-switch per screen.

We already implement the receiving side: `AplUserEventHandler` (`CanHandle: request is AplUserEventRequest`) resolves tapped carousel items to playable media. Extending touch is therefore: (a) wrap the NowPlaying art/controls in `TouchWrapper`s with distinct `id`s (e.g. `npArt`, `npNext`, `npFavorite`) and `SendEvent` arguments; (b) route those arguments in the existing handler; (c) keep the JF-623 auto-attach chokepoint unchanged.

Gestures beyond tap exist: `DoublePress` and `LongPress` gesture handlers on touchable components [S5]. A long-press on art could map to "add to favorites", a double-tap to "play similar" (radio), with zero new voice vocabulary.

### 3. Platform constraints that shape the design (all verified)

- **Screen lifetime**: an APL document lives within the skill session; when the session ends the document is dismissed (this is why our PlaybackStopped handler deliberately ends the session "to dismiss APL screen"). There is no background push to update a rendered document: updates require `RenderDocument` (full re-render) or data-store commands inside a NEW request's response. A "live" progress bar that ticks during AudioPlayer playback is therefore NOT achievable server-side; the documented pattern is client-side data binding on time-based expressions where the runtime supports it, or re-render at track boundaries (we already re-render NowPlaying on each ReplaceAll play since JF-623).
- **AudioPlayer tap conflicts**: screen taps on pause/next/play arrive as `PlaybackController.*CommandIssued` requests (we already handle Pause/Next/Previous/Resume/Play command types; the CLAUDE.md reference documents this). If we render our OWN transport buttons, those taps arrive as OUR UserEvents instead, which sidesteps the default-music-service arbitration entirely. That is a genuinely useful property: a visible Next button that reliably reaches us where voice "avanti" does not.
- **The Echo's own full-screen player**: during raw AudioPlayer playback the device may show its own player; our JF-623 finding (stale metadata on that surface) plus this research point the same direction: render our own screen on every play (already shipped tonight).
- **Viewport responsiveness**: the responsive components are profile-aware (`alexa-viewport-profiles`); our household device is a 1024x600 hub (the `viewport` object we already log). Hand-rolled documents must keep the `when ${viewport.shape == 'round'}` style guards we already carry; the import package removes that burden.

### 4. GitHub examples worth mining

- `alexa-samples/skill-sample-nodejs-responsive-layouts` [S7]: the official tutorial walking each responsive component; JSON directly transcribable into our `AplHelper` string templates (Node sample but the documents are pure JSON).
- apl.ninja [S8]: community gallery with live-preview documents (search "AlexaTransportControls example", "AlexaPaginatedList example"); good for eyeballing a design before transcribing.
- `kstephens1/AlexPlusDinnerList` [S9]: a full widget + data-store example (C# skill); relevant only if we ever want a home-screen "now playing/queue" widget.
- `alexa-games/litexa` screens docs [S10]: concise statement of the touch-event model we follow.
- The sibling `infinityofspace/jellyfin_alexa_skill` [S11] shows only title/artist/cover (no APL beyond that): no visual features to adopt from there; we are already ahead.

### 5. The Alexa.NET.APL nuance

Our stack hand-rolls the APL JSON (AplHelper string templates) and the UserEvent plumbing (custom `AplUserEventRequest` + converter). The community `Alexa.NET.APL` NuGet [S12] provides typed RenderDocument/TouchWrapper/UserEvent objects, but our string-template approach already works and avoids a new dependency whose risk profile we have not assessed (the NU1904 lesson). A migration is optional polish, not a prerequisite.

## Recommended implementation order (for when this becomes a task)

1. `AlexaBackground` w/ `backgroundBlur` + `AlexaProgressBar` on NowPlaying (pure document JSON change in `AplHelper`; biggest visual win per line of code).
2. Transport row via `AlexaTransportControls` or three icon `TouchWrapper`s on NowPlaying, wired to UserEvents into the existing handlers (gives a reliable on-screen Next that voice cannot deliver, JF-595-adjacent value).
3. `AlexaTextList` + `primaryAction` on the disambiguation paths (choose-by-tap instead of ordinals).
4. `AlexaPaginatedList` for chapter/episode picking.
5. Optional: LongPress gestures (favorites), `AlexaRating` (the visual twin of the JF-326 star-ratings feature), widget + data store (home-screen presence).

## Confidence Assessment

- HIGH: component catalog and parameters (official docs scraped, property tables read); UserEvent/touch chain (official docs plus our own working carousel implementation); transport-controls parameters; backgroundBlur; the no-push-update constraint (multiple docs plus our own JF-299/JF-623 history).
- MEDIUM: visual quality judgments ("looks professional"): subjective, backed by the official design guide's recommendations (backgrounds, hierarchy, glanceability [S13]) but not measurable.
- LOW/unverified: exact render performance of `alexa-layouts` imports on the 1024x600 hub (no on-device measurement yet); whether double/long-press gestures feel natural on the Show's touch layer (no device test).

## Sources

1. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-layouts-overview.html (responsive components/templates catalog, import syntax, BodyTemplate mapping)
2. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-alexa-paginated-list-layout.html (AlexaPaginatedList, primaryAction, toolbar back button)
3. https://developer.amazon.com/ar-SA/docs/alexa/alexa-presentation-language/apl-alexa-background-layout.html (AlexaBackground, backgroundBlur parameter)
4. https://developer.amazon.com/en-IN/docs/alexa/alexa-presentation-language/apl-transport-controls-layout.html (AlexaTransportControls, primary/secondary control actions)
5. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-touchwrapper.html (TouchWrapper, Press/SendEvent, UserEvent payload shape, gestures)
6. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-send-event-command.html (SendEvent command; referenced from S5)
7. https://github.com/alexa-samples/skill-sample-nodejs-responsive-layouts (official responsive-layouts tutorial skill)
8. https://apl.ninja/ (community APL document gallery with live preview)
9. https://github.com/kstephens1/AlexPlusDinnerList (widget + data store example skill, C#)
10. https://github.com/alexa-games/litexa/blob/master/docs/book/screens.md (touch-event model summary)
11. https://github.com/infinityofspace/jellyfin_alexa_skill (sibling Jellyfin Alexa skill; title/artist/cover only)
12. https://www.nuget.org/packages/Alexa.NET.APL/4.0.13 (Alexa.NET APL typed helpers)
13. https://developer.amazon.com/en-US/alexa/alexa-haus/alexa-presentation-language (Alexa design guide + APL design checklist)
14. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-best-practices-for-developers.html (architecture/performance best practices)
15. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-ext-data-store-extension.html (data store extension: widgets, background data)
16. https://developer.amazon.com/en-US/docs/alexa/alexa-presentation-language/apl-authoring-tool.html (authoring tool for previewing documents without a device)
