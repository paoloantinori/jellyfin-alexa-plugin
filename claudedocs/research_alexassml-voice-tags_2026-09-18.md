# Research Report: Alexa SSML Voice Tags for Media Announcements

**Date**: 2026-09-18
**Depth**: exhaustive (4 parallel agents, 15+ primary-source pages scraped live today; every load-bearing claim tied to the Aug 28, 2025 SSML Reference or a named study/repo)
**Confidence**: HIGH on the documented semantics of `emphasis`/`break`/`prosody`/`say-as` and on the it-IT availability limits; MEDIUM on the perceptual rankings (labelled below)

## Executive Summary

The `<emphasis level="moderate">` the user dislikes is DOCUMENTED to do exactly what they dislike: Amazon defines it as "louder and slower", and additionally warns that any span wrapped in `<emphasis>` is rendered by a LEGACY text-to-speech system "which might change the speech sound quality". It is the one prosodic tag to avoid on Alexa specifically. The evidence converges on a simple answer: the plugin's strings ALREADY carry a `<break>` before every title, so deleting the emphasis wrapper (keeping the breaks) yields the Amazon-endorsed pattern (plain title, pause-delimited) with zero new markup, zero legacy-TTS fallback, and no localization risk. For the "types for slots" follow-up: there is no media-specific `say-as` type on Alexa; the full `interpret-as` vocabulary is 14 generic values, and for Italian announcements the right choice for dates, durations, chapter and episode numbers is pre-formatted localized plain text, which is what the plugin already emits (the new date announce format is exactly this).

## Local inventory (what we ship today)

12 sites per locale wrap titles in `<emphasis level="moderate">{0}</emphasis>` (identical shape in en-US.json and it-IT.json, and by cross-locale-drift policy in all 17): `NowPlayingSsml`, `NowPlayingWithPositionSsml`, `DisambiguatePromptSsml`, `DisambiguateNextSsml`, `RecommendPlayingSsml`, `FuzzySuggestionPromptSsml`, `FuzzyAutoPlayAnnouncementSsml`, `CrossMediaArtistOfferSsml`, `ResumePromptSsml`, `ResumingSsml`, `FollowMeSuccessSsml`, `ResumingBookSsml`. Representative it-IT value:

```xml
In riproduzione<break time="300ms"/><emphasis level="moderate">{0}</emphasis>
```

Every one of the 12 already has a `<break time="150-300ms"/>` adjacent to the title. The delimiting work is already being done by the pauses; the emphasis wrapper is pure downside on this platform (see Finding A).

Side observation from the inventory: `ResumingBookSsml` in it-IT.json reads `<say-as interpret-as="interjection">resuming</say-as>…chapter {1}.` That is an English speechcon and the English word "chapter" inside the Italian locale file. Unrelated to this research but worth a backlog line.

## Findings

### A. What `<emphasis level="moderate">` actually does (the root of the disliked effect)

Quoted verbatim from the Alexa SSML Reference (last updated Aug 28, 2025):

- "Emphasis changes rate and volume of the speech. More emphasis is spoken louder and slower." `moderate` "Increase[s] the volume and slow[s] down the speaking rate, but not as much as when set to strong."
- "Note: When you modify the speech with the `<emphasis>` tag, Alexa uses a legacy text-to-speech system, which might change the speech sound quality." The identical warning attaches to `prosody` with the `pitch` attribute.

So the "rallentamento" the user dislikes is the documented definition of the tag, not a defect; and the slight voice-quality change many hear is the documented legacy-TTS fallback. Corroboration from the same engine family: an official AWS answer on re:Post states for Polly that emphasis "is only available for standard option"; neural voices reject it, which is why the Alexa service silently re-renders the span on the old engine. W3C SSML 1.1 (the normative spec Alexa implements) deliberately leaves emphasis realization processor-defined, which is why Google's docs warn their own engine should only see `emphasis` around a full sentence ("Enclosing words within a sentence may cause unwanted pauses") and Polly neural errors on it outright. `emphasis` is the least portable prosodic tag and the only one with a documented quality regression on the platform we ship to.

No numeric magnitude for moderate-vs-strong is documented anywhere. No device-class (Echo Dot vs Show) restriction exists for any SSML tag; SSML is audio-path only. A search for a citable "emphasis sounds draggy" community thread found nothing indexed (the old Amazon dev forums are archived); the gap is flagged, but the docs' own definition plus the legacy note fully account for the perception.

### B. The full supported tag set (custom skills, Aug 2025 reference)

Supported: `speak`, `p`, `s`, `break`, `emphasis`, `prosody`, `phoneme`, `say-as`, `sub`, `w`, `voice`, `lang`, `audio`, plus `amazon:effect` (only `name="whispered"`), `amazon:emotion` (excited/disappointed with low/medium/high intensity; en-US, en-GB, de-DE, ja-JP only), `amazon:domain` (speaking styles, see below). Unsupported tags are silently STRIPPED, not rejected; malformed SSML produces `INVALID_RESPONSE`.

Ranges and rules that bound any alternative design:

| Constraint | Value |
|---|---|
| `break time` | 10s TOTAL maximum per response including consecutive breaks; more silence means the whole SSML is not rendered |
| `prosody rate` | n%, floor 20%; labels x-slow through x-fast |
| `prosody pitch` | clamps +50% and -33.3%; carries the same legacy-TTS warning as emphasis |
| `prosody volume` | +/-ndB, maximum about +4.08dB |
| `voice` | fixed per-locale Polly name table only (it-IT: Carla, Giorgio, Bianca); no arbitrary neural voices |
| `lang` | supports it-IT among 15 locales |
| `w role` | `amazon:VB/VBD/NN/SENSE_1` only, English-only |
| `audio` | 48kbps MP3, 22050/24000/16000 Hz, 240s maximum, 5 files maximum per response |
| Incompatible on one span | `emphasis`, `prosody pitch`, `voice`, `amazon:emotion`, `amazon:domain`, speechcons are mutually exclusive with each other |

`amazon:domain name="music"` is Amazon's own style "for talking about music, video, or other multi-media content", and it is available ONLY in en-US, en-CA, en-GB, and de-DE skills. NOT it-IT. `conversational` is the only style listed for Italian skills, but the same doc's incompatible-tags section says conversational "doesn't work on its own without `<voice>`" and names only the en-US voices Matthew and Joanna; the doc never reconciles this contradiction, so it-IT `conversational` must be treated as UNVERIFIED on-device before any use. There is no sfx or soundbank tag; sound effects go through `<audio>` (ASK Sound Library MP3s).

### C. Ranked alternatives for title delimiting (the core question)

The plugin's titles appear in carrier sentences ("In riproduzione X", "Riprendo X", "Intendevi X?"). Ranked by evidence weight, with exact snippets:

**1. Drop the emphasis, keep the existing breaks: PLAIN + PAUSE (recommended).**
```xml
In riproduzione<break time="300ms"/>{0}
```
Evidence: (a) Amazon's own best practices say responses stay plain and "Alexa uses punctuation to alter the intonation of words and add a slight delay"; SSML is positioned as an optional layer, reserved for pauses and pronunciation. (b) It is the ONLY technique with a measured comprehension benefit: Elmers et al., ESSV 2021 (n=15, Amazon Polly), found recall of the item immediately following a pause improved significantly at 500ms (not at 200ms). Our 300ms now-playing break is in the effective band's vicinity; if the announce ever feels rushed, raising the title-preceding break toward 400-500ms is the evidence-backed dial. (c) A survey of real skills (the official `alexa-samples/skill-sample-nodejs-audio-player` with its plain "Playing the audio stream.", the community `infinityofspace/jellyfin_alexa_skill` with zero `emphasis`/`prosody` hits in the whole repo, and a podcast skill) found ZERO prosodic markup in any now-playing announce. (d) Zero legacy-TTS fallback: the whole response stays on the neural pipeline. Cost: the title loses its loudness bump; if titles then feel lost, option 3 is the upgrade path. Localization cost: a mechanical string edit in 17 locale files (the JSONs already differ only in prose).

**2. Punctuation alone.**
```xml
In riproduzione, {0}
```
A comma at the carrier boundary; Alexa's punctuation handling inserts the delay itself. Equivalent in spirit to option 1 with one less tag, but slightly less control over pause length. The existing `<break>` tags already do this work more precisely, so this option mainly matters for NEW strings.

**3. Mild `prosody rate` on the title only.**
```xml
In riproduzione<break time="300ms"/><prosody rate="95%">{0}</prosody>
```
A `rate` below 100% is the safe, artifact-free way to keep a faint "title is set apart" cue WITHOUT the legacy engine: only `pitch` triggers the fallback, and rate/volume prosody "combines with all other tags". Caveats: no study measures rate changes localized to a title span; a 5% change is below any documented perceptual threshold (expect subtle); Polly neural voices support rate/volume only partially (that is Polly, not the Alexa skill service, but it bounds expectations). Use 90-95% only if option 1 proves too flat on device. W3C warns against "casually" mixing explicit prosody with engine-determined prosody, so do NOT stack break + rate + anything else.

**4. `amazon:domain name="music"`.**
```xml
<amazon:domain name="music">Here's Sweet Child O' Mine by Guns N' Roses.</amazon:domain>
```
This is Amazon's ENDORSED pattern for talking about media (whole-response neural style), and its own doc example is a song announcement with the title inside. BLOCKED as a default: en-US/en-CA/en-GB/de-DE only. If ever used it would be a per-locale opt-in for those four locales; it cannot combine with `<voice>`, it is whole-response rather than title-localized, and Amazon's own pattern delimits the title by phrasing and pausing inside the style rather than by marking the span.

**5. `emphasis` (what we have now): keep nowhere.** Documented louder+slower plus legacy-TTS quality risk, unportable across engines, mutually exclusive with the style/voice family. The user's dislike is the tag working as specified.

**6. `sub` aliasing / `phoneme`: a different problem, the right tool when it appears.**
```xml
<sub alias="dé pecche mode">Depeche Mode</sub>
```
Changes pronunciation, never pacing. This is the standard answer if a SPECIFIC title is mispronounced (the catalog phonetic-synonym machinery already addresses this at the ASR layer; `sub` is the TTS-side counterpart). Not a delimiting device.

Anti-recommendations from the same evidence: `say-as` cannot delimit anything (W3C: "Indicating the content type or format does not necessarily affect the way the information is pronounced"; unknown values are ignored). `whispered` (`amazon:effect`) has no documented device caveats but no documented benefit for titles either. Speechcons (`interjection`) are locale-specific word lists; we already use "eccoci" correctly in it-IT.

### D. `say-as` vocabulary for media speech (the "tags per i tipi" question)

The complete Alexa `interpret-as` list (14 values, two alias pairs): `characters`/`spell-out`, `cardinal`/`number`, `ordinal`, `digits`, `fraction`, `unit`, `date` (with `format` mdy/dmy/ymd/md/dm/ym/my/d/m/y), `time`, `telephone`, `address`, `interjection`, `expletive`. There is NO media type, NO title type, NO dotted `unit.duration`/`unit.currency` family (those belong to third-party TTS aggregators; on Alexa they are silently stripped). Mapped to our six spoken patterns:

| Our pattern | Right choice | Note |
|---|---|---|
| Episode date "mercoledì 17 settembre" | PLAIN TEXT (already done: `FormatEpisodeAnnounceTitle` emits localized words, and the docs' own guidance is that Alexa "attempts to interpret the provided text correctly based on the formatting even without this tag") | `say-as date` only if feeding a numeric string: `<say-as interpret-as="date" format="ymd">20260917</say-as>`; exact Italian phrasing is undocumented, probe the voice simulator before relying on it |
| Playback position "12 minuti e 34 secondi" | Self-emitted words (recommended) | `say-as time` exists with the `1'21"` format, but degenerate forms are reported inconsistently (`5'` read as "five minutes" by one source, "five apostrophe" by another); writing Italian words avoids the ambiguity entirely |
| Chapter number "capitolo 3" | Plain (cardinal after the noun is the natural reading) | `<say-as interpret-as="cardinal">3</say-as>` only for bare numeric strings |
| Season/episode "stagione 3, episodio 2" | Plain cardinals, or `ordinal` for "terza stagione" | no combined type exists |
| Duration "28 minuti" | Plain words | the docs' own ambiguity test: tag only where plain text is ambiguous |
| Year "2026" | `cardinal` forces the full-number reading | `date format="y"` exists but the docs do not state "twenty twenty-six" vs "two thousand twenty-six"; unverified |

Net: our templates already emit pre-formatted localized words for every one of these patterns, which is the docs-sanctioned default. The one place `say-as` earns its keep is bare numeric strings we might someday interpolate (for example a raw position in seconds); `cardinal` there is cheap insurance.

### E. Screen/speech division (Echo Show synergy)

Documented design rule, directly quotable: "Avoid simply reading what's shown on screen, and instead have Alexa speak about the main idea and allow the user to look at the visuals for additional context." The platform-sanctioned carrier for title/artist/album detail on screen devices is `audioItem.metadata` (`title`, `subtitle`, `art`, `backgroundImage`) on the `AudioPlayer.Play` directive; it is display-only, never spoken, and cached per stream token up to 5 days. Our existing APL NowPlaying template serves the same role for richer screens. The practical consequence for the announce: the SPEECH can stay minimal precisely because the screen carries the detail; this points the same direction as option 1 (plain spoken title) and argues against compensating for plain speech by speaking MORE metadata.

### F. What first-party and real skills do

Amazon Music "Song ID" (the feature that announces title and artist before each song, live since July 2019) is universally described as a PLAIN reading in the standard voice at normal rate with natural pauses; no source documents any emphasis or style on it (the acoustic characterization is community-observed and flagged as such). No documentation exists for Audible or Prime Video announce prosody. The three open-source skills surveyed (official audio-player sample, community Jellyfin skill, podcast skill) all emit plain one-line sentences with zero SSML beyond the `<speak>` wrapper, and no speech at all on AudioPlayer events. Certification criteria mention no SSML rules at all: "Answers the user's request in a concise, terse manner", "Written for the ear, not the eye".

## Adversarial verification (exhaustive requirement)

- "Pause alone suffices to set a title apart", CHALLENGED: the ESSV study measured digit recall, not titles, with n=15; the transfer to titles is inference only. Counter-evidence considered: real skills ship plain announces in production (three repos), and Amazon's own music-style example delimits by phrasing. VERDICT: holds as a recommendation; confidence MEDIUM on the perceptual claim, HIGH on "no worse than industry practice".
- "Emphasis degrades quality", DOCUMENTED verbatim by Amazon and corroborated by the Polly standard-only constraint via an official AWS answer. CONFIRMED.
- Doc contradiction surfaced, NOT resolved: the `amazon:domain` intro says the tag family is available in en/de/ja, while the style table lists `conversational` for Italian skills, and the incompatible-tags section simultaneously requires conversational to ride en-US Matthew or Joanna. Treat it-IT `conversational` as unverified.
- Doc contradiction surfaced: emphasis is listed as combinable with "all other tags except incompatible" (so it stacks with `prosody rate`), while W3C SSML 1.1 section 1.2 warns authors not to casually mix explicit prosody with engine prosody. Both readings are quoted faithfully; the design guidance above follows the more conservative W3C reading (one explicit device per response).
- `say-as time` reliability, CHALLENGED: two Stack Overflow answers disagree on the `5'` degenerate form; treated as unreliable, hence the self-emitted-words recommendation.

## Confidence Assessment

- HIGH: `emphasis` means louder+slower plus legacy-TTS fallback (Amazon doc, verbatim); the tag set, ranges, and incompatible-combination table; locale availability of `amazon:domain music` (not it-IT); the 14-value `interpret-as` list; `audioItem.metadata` as the screen channel; real skills use no prosodic markup in announces.
- MEDIUM: the perceptual ranking of alternatives (pause first, rate second) rests on one small ESSV study plus inference; the "Song ID is plain" characterization is community-observed.
- LOW / unverified: it-IT `amazon:domain conversational` (doc self-contradiction); exact Italian rendering of `say-as date` and `date format="y"` (needs a voice-simulator probe); whether emphasis renders identically on legacy vs neural paths (docs imply always-legacy for the wrapped span).

## Sources

1. Alexa SSML Reference (custom skills), last updated Aug 28, 2025, full page scraped, all verbatim quotes: https://developer.amazon.com/en-US/docs/alexa/custom-skills/speech-synthesis-markup-language-ssml-reference.html
2. W3C SSML 1.1 Recommendation (2010-09-07): https://www.w3.org/TR/speech-synthesis11/
3. Elmers, Werner, Muhlack, Möbius, Trouvain, "Evaluating the effect of pauses on number recollection in synthesized speech", ESSV 2021: https://www.essv.de/essv2021/pdfs/26_elmers.pdf
4. Best Practices for Text Responses: https://developer.amazon.com/en-US/docs/alexa/custom-skills/best-practice-text-response.html
5. Voice Interface and User Experience Testing for a Custom Skill: https://developer.amazon.com/en-US/docs/alexa/custom-skills/voice-interface-and-user-experience-testing-for-a-custom-skill.html
6. AudioPlayer Interface Reference (`audioItem.metadata`): https://developer.amazon.com/en-US/docs/alexa/custom-skills/audioplayer-interface-reference.html
7. ASK Sound Library: https://developer.amazon.com/en-US/docs/alexa/custom-skills/ask-soundlibrary.html
8. Speechcons index: https://developer.amazon.com/en-US/docs/alexa/custom-skills/speechcon-reference-interjections.html
9. New Alexa Emotions and Speaking Styles (Nov 2019 blog; NTTS statements; music style "84% more natural"): https://developer.amazon.com/en-US/blogs/alexa/alexa-skills-kit/2019/11/new-alexa-emotions-and-speaking-styles
10. AWS re:Post, official answer that emphasis is standard-only on Polly: https://repost.aws/questions/QUoRC-2Q2JRVaQXPOmx4OdHw/emphasize-tag-not-working-on-console
11. Google Cloud TTS SSML docs (word-level emphasis warning): https://docs.cloud.google.com/text-to-speech/docs/ssml
12. Amazon Polly supported tags table: https://docs.aws.amazon.com/polly/latest/dg/supportedtags.html
13. Voice Design Guide "How Alexa Responds" (one-breath test; screen/speech division; the EN path 404s, content verified via https://developer.amazon.com/fr/designing-for-voice/what-alexa-says/) plus successor hub pages https://developer.amazon.com/en-US/alexa/alexa-haus/natural-speech and https://developer.amazon.com/en-US/alexa/alexa-haus/intro-to-apl
14. One-breath test blog (Blankenburg, 2018): https://developer.amazon.com/en-US/blogs/alexa/post/531ffdd7-acf3-43ca-9831-9c375b08afe0/things-every-alexa-skill-should-do-pass-the-one-breath-test
15. Real-skill speech code: https://github.com/alexa-samples/skill-sample-nodejs-audio-player/blob/master/lambda/index.js ; https://github.com/infinityofspace/jellyfin_alexa_skill ; https://github.com/cachafla/alexa-podcasts-skill/blob/master/handlers.js
16. `say-as time` degenerate-form disagreement: https://stackoverflow.com/questions/54027892
17. z.tools cross-TTS SSML survey (2026-05-07; neural-era minimalism framing; secondary industry blog, used only for context)
