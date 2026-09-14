# Research Report: Real-world use of the Alexa device-api media interfaces and the Music Skill API (GitHub census)

**Date**: 2026-09-14
**Depth**: exhaustive (3 parallel GitHub agents: interface users, MSAPI/RSK/VSK implementations, SDK + manifest schema)
**Confidence**: HIGH on the census and the schema; the single residual unknown is server-side enforcement of the manifest interface enum

## Executive Summary

Nobody on public GitHub declares Alexa.SeekController or the smart-home Alexa.PlaybackController on a custom skill manifest: those interfaces exist only in smart-home discovery payloads, video-skill bridges, and AVS device clients. The SMAPI manifest schema enumerates the valid custom-skill interface types and no seek/playback device-api is among them, so the JF-560 experiment's likely outcome is a validation rejection (or acceptance with no routing); the only unverified link is whether the enum is enforced server-side. The complete public universe of Music Skill API implementations is six projects, two alive and working in 2026, both self-hosted and dev-stage: the architecture is reachable by any developer account, but no self-published skill has ever reached public distribution, and one 2026 project explicitly abandoned MSAPI calling it partner-gated, contradicting the "anyone can build a music skill (US)" doc sentence that must be reconciled before JF-561's go/no-go. Seek on speakers is obtained in practice by republishing a queue with a stream offset, the same principle our audiobook resume already uses.

## Findings

### 1. Who uses SeekController / smart-home PlaybackController / PlaybackStateReporter

- Complete classification: ~30 real projects, all in three worlds: smart-home skill adapters (Home Assistant implements all three interfaces; openHAB, Kodi-connect, Linn, homebridge-alexa, smarthomeNG, Sky/Chromecast adapters), video-skill/STB bridges (vestibule types and bridge, RDK set-top-box mapper), and AVS device clients (alexa/avs-device-sdk ExternalMediaPlayer capability agent with SEEKCONTROLLER_NAMESPACE; vendored copies in Starlink router firmware, YodaOS, Alexa Auto SDK). [1][2][3]
- Declaration paths in the wild: smart-home skills declare these capabilities per-endpoint in the Alexa.Discovery RESPONSE (the manifest smartHome api has NO interfaces array at all); AVS device clients register them as device capabilities. Never as a skill manifest entry. [1][4]
- Zero GitHub hits for SeekController/PlaybackController inside any skill.json/manifest.json custom interfaces; zero issue reports of a custom-skill declaration attempt being rejected. [1]
- Home-automation gets seek on Echo speakers by registering a FAKE Echo device whose capability payload includes Alexa.SeekController (aioamazondevices/alexapy, openHAB amazonechocontrol with SUPPORTS_SCRUBBING); that is a device-registration path, not a skill path. [1]

### 2. The live evaluation that matters for JF-560

- 312-dev/ma-alexa-music-skill (Music Assistant provider, active 2026-08, live-tested on real Echoes): the author evaluated the seek family and wrote: "There is no seek in alexapy at all, and Alexa.SeekController is a Video API (TV, streaming device, games console) that does not apply to a speaker. It works by republishing the queue with a start offset instead" (stream.offsetInMilliseconds rides the republished queue). Also: "Declaring PlayerFeature.SEEK is not what exposes the scrubber". [5]
- That offset-republish principle is exactly our audiobook sliced-playlist resume: independent confirmation it is THE practical seek mechanism available to non-partner skills.

### 3. The manifest schema: what a custom skill may declare

- The Amazon-generated SMAPI model (ask-smapi-model TS v1.24.0 and the OpenAPI spec) enumerates InterfaceType for apis.custom.interfaces: 18 values (AUDIO_PLAYER, VIDEO_APP, RENDER_TEMPLATE, GAME_ENGINE, GADGET_CONTROLLER, CAN_FULFILL_INTENT_REQUEST, ALEXA_PRESENTATION_APL, camera controllers, CUSTOM_INTERFACE, ALEXA_EXTENSION, APP_LINKS, ALEXA_SEARCH, data-store...). No seek or playback device-api type exists. The public skill-manifest doc narrows it to 7 values with the wording "interfaces supported for custom skills". [6]
- Caveat: the spec's Interface node is a free string with a discriminator; nothing in the machine schema hard-refs the enum, so server-side enforcement strength is UNVERIFIED. The only direct test remains the live probe (update a dev skill manifest with an out-of-enum type and read the validation error, or its silence). [6]
- No skill SDK ships device-api types: Alexa.NET has zero seek references and its directive registry has no Alexa.* media directives; ask-sdk-model (TS 1.86.0, Py 1.82.0, Java 1.80.0, all generated 2023-08-21 and still latest) contains only the four PlaybackController.*CommandIssued custom-skill REQUESTS; Amazon's SDK team states ASK SDK is "only compatible with custom skills". [7]
- Client-side declaration in our stack is trivial (CustomApiInterfaceConverter.InterfaceLookup is a public static we already extend in ManifestSkill for AUDIO_PLAYER/VIDEO_APP/ALEXA_PRESENTATION_APL); the question is purely whether SMAPI accepts it. [7]

### 4. The Music Skill API universe (complete census: six implementations)

- 312-dev/ma-alexa-music-skill (2026-08, the most complete): full queue/shuffle/loop/repeat, catalog sync (stations mapped to AMAZON.MusicPlaylist because no station entity exists for non-partners), no Lambda (skill created via ask smapi against a personal account). Operational findings measured live: the skill-provider binding DECAYS silently within hours (green lights everywhere, playback falls back to the default provider; keep-alive cycles every 4h); a catalog upload SILENTLY UNBINDS the skill (the documented fix is delete+set enablement); the real ingestion gate is ER_INGESTION, not SLU_MODELING ("PENDING for weeks, never blocks playback"); "play X radio" is first-party-reserved. [5]
- andystumpf/bock-media (2026-08): self-hosted Flask server with a working experimental MSAPI endpoint NEXT TO its custom AudioPlayer skill (the dual architecture JF-561 contemplates): full apis.music manifest, GetPlayableContent with fuzzy fallback for stale-catalog entity resolution, ALEXA_AUDIO_PLAYER_QUEUE with SHUFFLE/LOOP controls, GetItem for expired stream URIs. [8]
- mlctrez/lexstream (Go, stalled 2023, "plays a single song on loop"), andrei-ace/music-cloud (the 2019 Medium tutorial, archived "due to API changes"), ryanlowdermilk's 2019 snippet, one Java model library. [9]
- The counter-verdict: vireonxi-om/alexa-music-cloud (2026-07) ABANDONED MSAPI: "Amazon's Music Skill Kit is gated to approved music partners; a self-published music skill will never get native 'play X' routing. This project instead uses a custom skill with AudioPlayer... the only path that actually works for a personal, self-published skill." This CONTRADICTS the live official sentence "Anyone can build a music skill for public distribution in the United States" (understand-the-music-skill-api.html). Reconciliation hypothesis to verify in JF-561: skill CREATION and dev-stage playback are open to any account (all six projects prove it); PUBLIC DISTRIBUTION is what remains gated, and the doc sentence may be newer than, or narrower than, the gate the 2026 author hit. [9][10]
- Radio Skills Kit: no-code console, no implementation repos exist by design. Video Skill API: only Amazon's archived sample. [11]

### 5. Direct implications for our tasks

- JF-560 (SeekController probe): expected outcome downgraded to near-certain negative (enum closed in every artifact; the interface is discovery-declared smart-home territory; the one live evaluation rules it out for speakers). The live manifest probe remains worth ONE cheap attempt solely because server-side enum enforcement is unverified: add the type to the dev manifest via SMAPI, read the validation response. Rollback is trivial (the manifest is regenerated from the embedded resource).
- JF-561 (Jellyfin Music skill): the two living implementations are the blueprints (queue handling, catalog entity mapping, binding keep-alive, enablement cycling, the handoff-phrase trick). The go/no-go hinges on the distribution-gate reconciliation, now sharply framed. The hybrid pattern (ma-alexa: MSAPI queues plus account-based device control) is a third architecture worth considering.
- The seek problem: keep the offset-republish approach (already our audiobook mechanism); no new lever exists without partnership.

## Confidence Assessment

- HIGH: the census (complete public MSAPI universe; zero custom-manifest declarations of device-api interfaces); the manifest enum contents (two Amazon-generated artifacts plus docs); SDK coverage; the smart-home discovery declaration path; the offset-republish seek mechanism; the operational hazards (binding decay, catalog-upload unbind) as documented by a working implementation.
- MEDIUM: the distribution-gate reconciliation (both sources are credible and dated 2026; the contradiction is real and unresolved).
- LOW/unverified: server-side enforcement of the out-of-enum interface type (the one thing the JF-560 probe still tests); whether MSAPI skill creation works on non-US vendor accounts (ma-alexa author locale unknown; our vendor is EU).

## Sources

1. Agent census: home-assistant/core (components/alexa handlers.py, capabilities.py), openhab/openhab-alexa, kodi-connect/kodi-connect, linn/linn-api-alexa-smart-home, NorthernMan54/homebridge-alexa, smarthomeNG/plugins alexa4p3, ndg63276/alexa-sky-hd, mgi2212/AlexaPremise, thehappydinoa/AshsSDK, ckhmer1/node-red-contrib-alexa-virtual-smarthome; AVS: alexa/avs-device-sdk ExternalMediaPlayer.cpp, vendored copies (SpaceExplorationTechnologies/starlink-wifi-gen2, yodaos-project/voice-interface-avs, matrix-io guide); device-registration spoofers: chemelli74/aioamazondevices capabilities.py, openhab/openhab-addons amazonechocontrol registration_capabilities.json; forensics: frankwxu/digital-forensics-lab, Super-Crab/com.amazon.dee.app
2. rym002/vestibule-alexa-video-skill-types, rym002/vestibule-bridge-assistant-alexa, kavia-common/alexa_skill_mapper-251300 (RDK)
3. alexa-samples/alexa-smarthome (validation schemas + sample messages)
4. developer.amazon.com/en-US/docs/alexa/smapi/skill-manifest.html (smartHome api has no interfaces array)
5. https://github.com/312-dev/ma-alexa-music-skill (README, provider.py, binding.py, setup_smapi.py, catalog_sync.py)
6. alexa/alexa-apis-for-nodejs ask-smapi-model (index.ts InterfaceType enum + spec.json definition walk); skill-manifest.html CustomInterface enumeration
7. timheuer/alexa-skills-dotnet (tree + Alexa.NET.dll strings; DirectiveConverter.cs; PlaybackControllerRequest.cs), stoiveyp/Alexa.NET.Management CustomApiInterfaceConverter.cs (+ our ManifestSkill.cs extension precedent), ask-sdk-model artifacts TS/Py/Java, alexa/alexa-skills-kit-sdk-for-nodejs issue #672
8. https://github.com/andystumpf/bock-media (music-manifest.json, server.py MSAPI endpoint, build_msp_catalog.py, poll_msp_slu.py)
9. mlctrez/lexstream, andrei-ace/music-cloud (archived), ryanlowdermilk/alexa-music-skill-example, f18a14c09s/alexa-music-skill-model-4j, vireonxi-om/alexa-music-cloud (the 2026 abandonment verdict)
10. developer.amazon.com/en-US/docs/alexa/music-skills/understand-the-music-skill-api.html ("Anyone can build a music skill for public distribution in the United States")
11. RSK (developer.amazon.com/alexa/alexa-skills-kit/radio-skills-kit, no-code; zero implementation repos), alexa-samples/alexa-video-multimodal (archived)
