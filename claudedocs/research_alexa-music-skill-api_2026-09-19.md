# Research Report: Jellyfin Music Skill via the Alexa Music Skill API (MSAPI)

**Date**: 2026-09-19
**Depth**: exhaustive (4 parallel agents, 300+ live page fetches from developer.amazon.com, Amazon help, bizmodeller.com, GitHub, forums; Wayback history back to 2019)
**Confidence**: HIGH on the blockers (multi-source primary quotes); the default-provider question stays MEDIUM (undocumented)
**Task**: JF-561 (investigation only, no code)

## Executive Summary

**NO-GO for this household today.** The Music Skill API is genuinely open to anyone for US public distribution (the 2026-09-14 observation is confirmed: the "Anyone can build a music skill" banner is on every doc page, and music has been self-serve since Nov 2018), and it IS the documented fix for our three routing wounds (bare stop/next/previous, name-free play). But the MSAPI interfaces support ONLY **en-US and es-US** (device-interfaces table, live Aug 2026; troubleshooting doc: "Currently, music skills support only US English"), so an it-IT household cannot use a self-serve MSAPI skill at all. That single wall is decisive regardless of every other finding. The adjacent facts worth knowing: a Jellyfin MSAPI skill is feasible for an en-US experimenter (one public hobbyist precedent, $0/month), but costs our entire test tooling, and the crux benefit (default-music-provider eligibility for a personal dev-stage skill) is undocumented and only a build-and-probe would settle it.

## Findings

### A. The routing prize is real (why we looked)

MSAPI moves the voice interaction model, queue, and transport routing to the Alexa side; the skill only resolves content and returns stream URIs.

- "Alexa, stop" is a first-class certified command during MSAPI playback (testing-guide functional table: "Stop (When music is playing) 'Alexa, stop.' Music stops playing.") - the exact command our custom skill loses to the default music service (CLAUDE.md, three on-device verifications).
- Next/previous arrive reliably from voice AND the Alexa app: "The item currently playing has the NEXT control enabled, and the user asks Alexa to skip to the next item, either by voice or in the Alexa app" (Alexa.Audio.PlayQueue, GetNextItem). App queue taps route to the skill (JumpToItem).
- The platform owns the queue and prefetches gaplessly: "Alexa uses a GetNextItem request to get the next track" when the first is almost finished - the JF-324 auto-advance problem solved by the platform instead of our PlaybackNearlyFinished handler.
- Shuffle/loop/repeat are directives the skill resolves; cross-device handoff with playbackSnapshot is the optional Reinitiate ("Alexa, move my music to the car" carrying mediaReference + offsetInMilliseconds) - the JF-375 follow-me feature, natively.
- Progress-bar seek events (ItemPlaybackStopped cause JUMP) and device telemetry arrive - data our AudioPlayer path can never get.
- Music alarms and multi-room integration: "Integrate your service with Alexa features like music alarms, multi-room music, and more".

### B. The locale wall (the decisive blocker)

Two agents verified this independently from different doc sets:

- The interfaces-and-languages table (developer.amazon.com/en-US/docs/alexa/device-apis/list-of-interfaces.html, live, "Last updated: Aug 11, 2026") lists Alexa.Media.Search, Alexa.Media.Playback, Alexa.Media.PlayQueue, Alexa.Audio.PlayQueue as supporting exactly "en-US, es-US" - nothing else.
- The MSAPI troubleshooting page (Sep 2024 revision, sentence present since at least Sept 2020): "Make sure your device language is set to English (United States). Currently, music skills support only US English." Also: "Select US from the available countries, and deselect other countries."
- The contradiction we suspected is real and now pinned: the API Components Reference (Oct 2025) enumerates 15 originatingLocale values INCLUDING it-IT - because invited partners (Spotify, Amazon Music) operate there. The self-serve path and the partner path are two different layers the docs never reconcile. The 2019-era page said it explicitly: "For radio providers or music providers in other locales, contact your Amazon Business Development representative."
- Podcast skills DO have a documented 15-locale list including it-IT - but podcast skills are explicitly in the invite-only developer preview ("radio and podcast skills are currently in developer preview").

**Consequence**: Paolo's it-IT Echo household cannot use a self-serve MSAPI music skill. Full stop. The 17-locale custom skill remains the only servable architecture for this household.

### C. Architecture obligations (if the locale wall ever falls)

- **Lambda is mandatory**: "You host your skill code as an AWS Lambda function" (prerequisites); the manifest's music.endpoint.uri is a REQUIRED Lambda ARN; no HTTPS-endpoint option exists anywhere in the music-skills doc set (contrast: custom skills have "Host a Custom Skill as a Web Service"). Console doc confirms HTTPS hosting "is available only for skills that use the custom voice interaction model". The shape would be: Alexa calls Lambda, Lambda calls the home Jellyfin over the internet, Lambda replies. Each household deploys its own Lambda (AWS+IAM friction per user) or we operate a shared multi-tenant bridge (the MyMedia cloud-proxy shape; new trust surface).
- **Latency budgets are certification-relevant and brutal for that shape**: Initiate p50 100ms / p90 250ms / p99 400ms ("Your skill may fail certification if its response time is too long"); GetPlayableContent "within 0.5 seconds" under 2 rps. A synchronous Lambda-to-home-Jellyfin call over residential broadband essentially cannot meet p50 100ms; a realistic design needs the Lambda answering from its own cached state.
- **Catalog obligation**: six catalog types (MusicGroup/MusicRecording/MusicAlbum/MusicPlaylist/Genre/BroadcastChannel), one per skill, uploaded as JSON via ASK CLI/REST (presigned S3), 500K entity cap, stable opaque IDs (Jellyfin GUIDs map directly), required popularity field even for personal libraries, explicit deleted:true tombstones, lastUpdatedTime discipline ("or the changes might be ignored"), weekly recurrence expectation, ingestion under 30 min but SLU modeling "can take several weeks", and "The music catalogs that you upload are shared by your development and published skills" (always live). Functionally mandatory for invocation: "If no match is found in your catalogs, your skill isn't invoked."
- **Our tooling dies**: "ask simulate: This feature isn't supported for music, radio, or podcast skills"; the same for simulate-skill, get-simulation, invoke-skill, update-model. The entire simulate-skill E2E harness and profile-nlu workflow cannot test an MSAPI skill; the documented loop is the console simulator plus a real device.
- **Streaming**: plain HTTPS file URIs are the documented norm (bare .mp3 examples); no codec/bitrate requirement; validUntil + the GetItem refresh directive anticipate our JF-309 signed expiring tokens; native offsetInMilliseconds resume; DRM and premium audio are opt-in partnership features, not obligations.
- **Account linking**: optional, standard OAuth 2.0 (not LWA-as-IdP; our LWA use is for SMAPI, a separate concern), token per request in payload.requestContext.user.accessToken. Dev-stage skills run indefinitely; certification applies only at publication.
- **Certification content risk (undocumented)**: the prerequisite "Permission to stream the content that your skill or service makes available to users" is a licensing attestation; how certification treats a skill whose content is each user's private library is stated nowhere - the single largest unknown for any future PUBLIC Jellyfin MSAPI skill.

### D. The default-provider crux stays open (but is moot behind the locale wall)

- The Default Services setting exists per Alexa profile (Amazon help: "Select Your Default Services... the services that you want Alexa to play from").
- Whether a personal, development-stage MSAPI skill appears in that picker is UNDOCUMENTED (help page JS-gated; developer docs only reference "the user's default music provider" as a concept). Only building a dev MSAPI skill and looking at the picker settles it.
- Name-free play would reach an MSAPI skill that IS the default provider; a non-default one is reachable by alias ("Alexa, play top hits from awesome music", music.locales.aliases).
- Physical Echo tap buttons are not separately addressed in the MSAPI docs (voice and app are; button routing is reasonable inference, not documented).

### E. Precedents (all roads lead away from MSAPI for personal libraries)

- **MyMedia for Alexa** (the one commercially successful personal-library product): a CUSTOM AudioPlayer skill today ("Alexa, ask My Media..."; intents; multi-turn ARH workaround - their own June 2026 page), hit by EXACTLY our hijacking problem: "We escalated the route hijacking to Amazon who analyzed sample user sessions and confirmed it is a bug Amazon side and not in our skill. They have proposed a 2026Q3 fix timeframe." Not a default provider (their routines guide exists because bare phrases do not reach the skill).
- **Plex**: custom skill, discontinued (store removals from 2026-04-06, disable emails mid-April, "expected to be deprecated and discontinued in June 2026" per bizmodeller; no primary Plex statement found - thin evidence, labeled).
- **Open-source MSAPI**: essentially none. GitHub's top personal-library skills are ALL custom AudioPlayer (youtube-music 157 stars, asknavidrome 119, asksonic 75, two Jellyfin ones); alexa-samples has AudioPlayer samples but no Music-model sample; one 2019 hobbyist MSAPI walkthrough (andrei-ace/music-cloud, en-US, DynamoDB+Dropbox) is the lone worked public example.
- **The community perception** ("invite-only Music Skill API partners", NittanySeaLion/jellyfin-alexa README) matches our own CLAUDE.md pre-research belief - both now outdated for music-in-the-US, both correct in spirit for every non-US locale.

### F. Cost (if ever needed)

$0/month at household scale: Lambda free tier covers 1M requests (a household's few hundred directives are orders of magnitude inside); the audio never transits AWS (the Echo fetches the stream URI directly from the home HTTPS endpoint); CloudWatch cents. The real cost is friction (per-household AWS account + IAM + us-east-1 Lambda, or an operated shared bridge) and the loss of the custom skill's zero-moving-parts backend.

## Go/No-Go recommendation

**NO-GO, today, for this project.** Ranked reasons:

1. **Locale wall (decisive)**: en-US/es-US only. The shipping skill serves 17 locales; the household is it-IT. An MSAPI skill could not even be trialed meaningfully at home.
2. **Lambda-only hosting** breaks the plugin's zero-moving-parts architecture and adds per-user AWS friction.
3. **Latency budgets** (Initiate p99 400ms at the Lambda) structurally fight the Lambda-to-home-Jellyfin hop.
4. **Tooling loss**: no simulate-skill/profile-nlu for music skills; our whole verification pipeline dies with it.
5. **Undocumented crux**: default-provider eligibility for dev-stage personal skills is unknown; even a probe is blocked by reason 1.
6. **Effort**: roughly a catalog pipeline port + an ID-addressable queue state machine + a Lambda bridge + directive handlers - weeks of new surface for an en-US-only parallel skill.

**Re-evaluation triggers** (in order of plausibility):
1. **Amazon extends MSAPI locales** beyond en-US/es-US (watch the device-interfaces table's Supported languages column; the podcast preview's 15-locale list is the shape music would take).
2. **The Amazon-side route-hijacking fix** promised to MyMedia for 2026Q3: if it landed and also relieves CUSTOM skills' stop/next hijacking, the main motivation for MSAPI evaporates. CHECK THIS FIRST (bizmodeller.com/my-media-for-alexa/alexa-plus.html tracks it; Q3 2026 ends in days - a re-read of that page plus an on-device stop test during our own playback would settle it for our skill).
3. A partnered path (Business Development contact) - not realistic for a hobby project.

**Effort estimate if ever green-lit** (en-US-only parallel skill, dev-stage): catalog pipeline ~1-2 weeks; queue/content-ID state machine ~1-2 weeks; Lambda bridge + deploy tooling ~1 week; directive handlers ~1 week; total ~4-6 weeks part-time, reusing LWA linking, stream endpoints, and the library catalog source.

## Confidence Assessment

- HIGH: Lambda-only hosting, en-US/es-US interface support, required interface set, catalog obligations/mechanics, HTTPS plain-file streaming, tooling exclusions, MyMedia-is-custom, self-serve-US-openness. All multi-quoted from primary docs fetched live.
- MEDIUM: default-provider semantics for non-default skills (alias routing documented; the picker membership is inference + one staff forum answer about Apple Music).
- LOW/unverified: dev-stage skill in the Default Services picker; certification treatment of private user libraries; Plex deprecation (no primary statement); physical-button routing.

## Sources (primary, all fetched live 2026-09-19)

1. developer.amazon.com/en-US/docs/alexa/music-skills/understand-the-music-skill-api.html (Sep 10, 2024)
2. .../steps-to-create-a-music-skill.html (Sep 10, 2024)
3. .../api-reference-overview.html (Nov 27, 2023)
4. .../api-components-reference.html (Oct 07, 2025)
5. .../upload-catalogs.html and .../catalog-reference.html (Nov 27, 2023)
6. .../testing-guide.html and .../troubleshooting.html (Sep 10, 2024)
7. .../premium-audio-badging-drm.html and .../event-subscriptions.html (Nov 27, 2023)
8. developer.amazon.com/en-US/docs/alexa/device-apis/list-of-interfaces.html (Aug 11, 2026)
9. .../alexa-media-playback.html, .../alexa-media-search.html, .../alexa-audio-playqueue.html, .../alexa-media-playqueue.html
10. developer.amazon.com/en-US/docs/alexa/account-linking/add-account-linking-logic-music.html (Jul 14, 2026)
11. Amazon help: Manage Music and Podcast Settings (T8EtR5cKyy3j2sC4Sn), About Music Streaming Services (GWLBLGQ8HUJP5DWU)
12. bizmodeller.com docs: alexa-plus.html (June 2026), voice-commands.html, faq.html, how-to-playlists-via-alexa-routines.html
13. Community: Sonos community teardown (2021), Plex forum deprecation thread, amazonforum staff answer (2021), Music Assistant prototype + issue 24
14. GitHub: alexa-samples enumeration; OverloadUT/alexa-plex; NittanySeaLion/jellyfin-alexa; andrei-ace/music-cloud + Medium walkthrough (2019)
15. Wayback: understand-the-music-skill-api.html 2019/2020/2022 snapshots (locale history); GA blog Oct 31, 2018
