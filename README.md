# Jellyfin Alexa Plugin

Control your Jellyfin media server with Alexa voice commands. Play music, videos, playlists, manage favorites, and more.

---

[![dev build](https://github.com/paoloantinori/jellyfin-alexa-plugin/actions/workflows/dev-build.yml/badge.svg)](https://github.com/paoloantinori/jellyfin-alexa-plugin/actions/workflows/dev-build.yml) ![GitHub all releases](https://img.shields.io/github/downloads/paoloantinori/jellyfin-alexa-plugin/total?label=total%20downloads)

---

_This is a fork of the [original project by infinityofspace](https://github.com/infinityofspace/jellyfin-alexa-plugin), migrated to Jellyfin 10.11.x with additional features and bug fixes._

_Alpha software: features may change between releases. Always back up your configuration before updating._

### Table of Contents

1. [About](#about)
2. [Features](#features)
3. [Prerequisites](#prerequisites)
4. [Installation](#installation)
5. [Amazon Developer Setup](#amazon-developer-setup)
6. [Plugin Configuration](#plugin-configuration)
7. [LWA Authorization](#lwa-authorization)
8. [Account Linking](#account-linking)
9. [Testing](#testing)
10. [Supported Languages](#supported-languages)
11. [Troubleshooting](#troubleshooting)
12. [Third Party Notices](#third-party-notices)
13. [All Voice Commands by Language](#all-voice-commands-by-language)
14. [License](#license)

## About

A Jellyfin plugin that creates a personal Alexa skill to play and control media from your Jellyfin server using voice commands. Each Jellyfin user gets their own Alexa skill with a customizable invocation name.

## Features

- **Playback control**: play specific songs, albums, artists, videos, channels, and playlists
- **Queue management**: next, previous, shuffle, repeat, start over
- **Favorites**: play favorites, add/remove from favorites
- **Media info**: ask what's currently playing
- **Recently added**: play recently added media
- **Multi-user**: each Jellyfin user can have their own skill
- **Multi-language**: 17 locale variants across 11 languages with full custom utterances
- **Audio and video**: supports both audio playback and video launching

## Prerequisites

Before you begin, you need:

1. **Jellyfin 10.11.x** server (earlier versions are not supported)
2. **Publicly accessible HTTPS URL** for your Jellyfin server with a valid SSL certificate
   - Options: wildcard certificate, trusted CA certificate, or self-signed certificate
   - Your server must be reachable from the internet (Amazon's servers need to reach it)
3. **Amazon Developer account** (free) — [create one here](https://developer.amazon.com/en-US/docs/alexa/ask-overviews/create-developer-account.html)

## Installation

### Option 1: Plugin Repository (Recommended)

1. Open the admin dashboard of your Jellyfin server
2. Go to **Plugins** and select the **Repositories** tab
3. Add a new repository with this URL (name can be anything):
   ```
   https://raw.githubusercontent.com/paoloantinori/jellyfin-alexa-plugin/main/manifest.json
   ```
4. Go to the **Catalog** tab and find **AlexaSkill** under the **General** category
5. Install the plugin and restart your Jellyfin server

### Option 2: Manual Installation

1. Download the latest release from the [releases page](https://github.com/paoloantinori/jellyfin-alexa-plugin/releases)
2. Extract the ZIP file
3. Create a folder named `Jellyfin.Plugin.AlexaSkill` inside your Jellyfin server's `plugins` directory
4. Copy the extracted files into that folder
5. Restart your Jellyfin server

### Option 3: Build from Source

```bash
git clone https://github.com/paoloantinori/jellyfin-alexa-plugin.git
cd jellyfin-alexa-plugin
git checkout <version>      # use the latest release tag
dotnet publish --configuration Release
```

Copy the contents of `Jellyfin.Plugin.AlexaSkill/bin/Release/net9.0/publish/` to your Jellyfin `plugins/Jellyfin.Plugin.AlexaSkill/` folder, then restart Jellyfin.

## Amazon Developer Setup

The plugin uses **Login with Amazon (LWA)** to create and manage your Alexa skill. You need to create a Security Profile in your Amazon Developer account.

### Step 1: Create a Security Profile

1. Go to the [Amazon Developer Security Profiles page](https://developer.amazon.com/settings/console/securityprofile)
2. Click **Create a New Security Profile**
3. Fill in the details:
   - **Security Profile Name**: something like "Jellyfin Alexa Plugin"
   - **Security Profile Description**: "LWA profile for my Jellyfin Alexa skill"
   - **Privacy Policy URL**: you can use your Jellyfin server URL
4. Click **Save**

### Step 2: Get Your Client ID and Client Secret

1. In the Security Profile you just created, click **Web Settings** (or the gear icon)
2. Click **Edit**
3. Note down the **Client ID** — you'll need this in plugin configuration
4. Click **Show Secret** and note down the **Client Secret**
5. Under **Allowed Return URLs**, add your Jellyfin server's callback URL:
   ```
   https://YOUR-SERVER-URL/alexaskill/lwa/callback
   ```
   Replace `YOUR-SERVER-URL` with your actual public HTTPS address (e.g., `https://jellyfin.example.com/alexaskill/lwa/callback`)
6. Click **Save**

## Plugin Configuration

![Plugin Configuration](screenshots/settings.png)

1. Open your Jellyfin admin dashboard
2. Go to **Plugins** and find **AlexaSkill** in the installed plugins list
3. Click on the plugin to open its configuration page

### General Settings

| Setting | Description |
|---------|-------------|
| **Server Address** | Your Jellyfin server's public HTTPS URL (e.g., `https://jellyfin.example.com`) |
| **SSL Certificate Type** | The type of your SSL certificate: Wildcard, Trusted, or SelfSigned |
| **LWA Client ID** | The Client ID from your Amazon Security Profile |
| **LWA Client Secret** | The Client Secret from your Amazon Security Profile |

### Adding a User Skill

1. In the plugin configuration, you'll see a table of users
2. Click **Add** to create a new skill for a Jellyfin user
3. Select the Jellyfin user from the dropdown
4. Optionally customize the **invocation name** (default: "Jellyfin Player")

## LWA Authorization

After adding a user, you need to authorize with Amazon:

1. In the plugin configuration, click **Authorize** next to the user
2. A new browser tab opens to the Amazon login page
3. Sign in with your Amazon account and approve the access request
4. You'll be redirected back to your Jellyfin server
5. The plugin automatically creates the Alexa skill and uploads the interaction models

The status column shows the current state:
- **LWA Auth Pending**: waiting for Amazon login
- **Skill Creating**: skill is being created in Amazon Developer Console
- **Account Link Pending**: skill is ready, waiting for account linking in Alexa app
- **Ready**: fully configured and operational

## Account Linking

The final step links your Jellyfin account to the Alexa skill:

1. Open the **Alexa app** on your phone
2. Go to **Skills & Games** and search for your skill's invocation name
3. Or go directly to your skills at [alexa.amazon.com](https://alexa.amazon.com)
4. Enable the skill — you'll be prompted to link your account
5. Enter your **Jellyfin username and password** on the linking page
6. After successful linking, the skill is ready to use

## Testing

### Automated NLU Tests

Validate that Alexa's NLU resolves utterances to the correct intents using the SMAPI `profile-nlu` endpoint:

```bash
./scripts/run_nlu_tests.sh                  # all locales
./scripts/run_nlu_tests.sh -k "it-IT"       # single locale
./scripts/run_nlu_tests.sh --dry-run         # validate fixture structure only
```

Requires the `ask` CLI authenticated and either `~/.ask/ask_states.json` with a skill ID or the `ASK_SKILL_ID` environment variable. Test fixtures live in `tests/integration/fixtures/*.yaml`.

### Automated E2E Tests

Full-chain integration tests that send utterances through Alexa's complete pipeline (NLU + skill execution + Jellyfin API) via SMAPI `simulate-skill`:

```bash
./scripts/run_e2e_tests.sh                                         # requires live Jellyfin server
./scripts/run_e2e_tests.sh --dry-run                               # validate fixtures only
```

E2E tests are auto-skipped if no Jellyfin server is configured. Provide connection details via CLI flags or environment variables:

| Flag | Env Var | Description |
|------|--------|-------------|
| `--jellyfin-url` | `JELLYFIN_URL` | Jellyfin server base URL (e.g. `https://jellyfin.example.com`) |
| `--jellyfin-api-key` | `JELLYFIN_API_KEY` | Jellyfin API key |
| `--jellyfin-user` | `JELLYFIN_USER` | Jellyfin username |

E2E test fixtures are in `tests/integration/fixtures/e2e_*.yaml`. Note that `simulate-skill` routes through Alexa's full NLU which competes with built-in Amazon skills, making some locales (especially en-US) unreliable for automated testing.

### Using the Alexa Simulator

1. Go to the [Alexa Developer Console](https://developer.amazon.com/alexa/console/ask)
2. Find your skill and click **Test**
3. Switch to **Development** mode
4. Use the simulator to type or speak commands, e.g.:
   - "Alexa, tell Jellyfin Player to play songs by Daft Punk"
   - "Alexa, ask Jellyfin Player what's playing"

### Using Your Echo Device

Once account linking is complete, try:
- "Alexa, open Jellyfin Player"
- "Alexa, tell Jellyfin Player to play the album Discovery"
- "Alexa, ask Jellyfin Player to play my favorites"

## Supported Languages

The skill supports **17 locales** across **11 languages**, each with full custom utterances in the interaction model files at [`Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/).

| Language | Locale | Interaction Model |
|----------|--------|-------------------|
| Arabic | ar-SA | [`model_ar-SA.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_ar-SA.json) |
| Dutch | nl-NL | [`model_nl-NL.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_nl-NL.json) |
| English (US) | en-US | [`model_en-US.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-US.json) |
| English (UK) | en-GB | [`model_en-GB.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-GB.json) |
| English (Australia) | en-AU | [`model_en-AU.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-AU.json) |
| English (Canada) | en-CA | [`model_en-CA.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-CA.json) |
| English (India) | en-IN | [`model_en-IN.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_en-IN.json) |
| French (France) | fr-FR | [`model_fr-FR.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_fr-FR.json) |
| French (Canada) | fr-CA | [`model_fr-CA.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_fr-CA.json) |
| German | de-DE | [`model_de-DE.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_de-DE.json) |
| Hindi | hi-IN | [`model_hi-IN.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_hi-IN.json) |
| Italian | it-IT | [`model_it-IT.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_it-IT.json) |
| Japanese | ja-JP | [`model_ja-JP.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_ja-JP.json) |
| Portuguese (Brazil) | pt-BR | [`model_pt-BR.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_pt-BR.json) |
| Spanish (Spain) | es-ES | [`model_es-ES.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_es-ES.json) |
| Spanish (Mexico) | es-MX | [`model_es-MX.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_es-MX.json) |
| Spanish (US) | es-US | [`model_es-US.json`](Jellyfin.Plugin.AlexaSkill/Alexa/InteractionModel/model_es-US.json) |

Each JSON file contains all 52 intents with locale-specific sample utterances. To see the complete list of voice commands for any language, open the corresponding interaction model file and look at the `samples` arrays within each intent.

## Troubleshooting

### "There was a problem with the requested skill's response"

- Verify your Jellyfin server is publicly accessible at the configured URL
- Check that your SSL certificate is valid
- Ensure the skill endpoint in the Alexa Developer Console matches your server URL

### Authorization fails or token expires

- Go back to plugin configuration and click **Authorize** again
- Check that your LWA Client ID and Client Secret are correct
- Verify the **Allowed Return URL** in your Amazon Security Profile matches `https://YOUR-SERVER-URL/alexaskill/lwa/callback`

### Interaction model build fails

- Check the Alexa Developer Console for error messages
- Ensure no other skills use the same invocation name
- Try deleting and re-creating the user skill in plugin configuration

### Account linking fails in the Alexa app

- Verify your Jellyfin credentials are correct
- Check that the plugin's **Account Linking Client ID** is set (auto-generated on first configuration)
- Ensure your Jellyfin server's account linking endpoint is reachable

### Plugin not appearing in Jellyfin

- Confirm the plugin repository URL is correct
- Check the Jellyfin logs for errors during plugin loading
- Verify you're running Jellyfin 10.11.x or later

### Configuration file

The plugin stores its configuration at `plugins/configurations/Jellyfin.Plugin.AlexaSkill.xml` in your Jellyfin data directory.

## Third Party Notices

| Module | License | Project |
|--------|---------|---------|
| Alexa.NET | [License](https://raw.githubusercontent.com/timheuer/alexa-skills-dotnet/master/LICENSE) | [Project](https://github.com/timheuer/alexa-skills-dotnet) |
| Alexa.NET.Management | [License](https://raw.githubusercontent.com/stoiveyp/Alexa.NET.Management/main/LICENSE) | [Project](https://github.com/stoiveyp/Alexa.NET.Management) |
| Amazon.Lambda.Core | [License](https://raw.githubusercontent.com/aws/aws-lambda-dotnet/master/LICENSE) | [Project](https://github.com/aws/aws-lambda-dotnet/tree/master/Libraries/src/Amazon.Lambda.Core) |
| Amazon.Lambda.Serialization.Json | [License](https://raw.githubusercontent.com/aws/aws-lambda-dotnet/master/LICENSE) | [Project](https://github.com/aws/aws-lambda-dotnet/tree/master/Libraries/src/Amazon.Lambda.Serialization.Json) |
| Refit | [License](https://raw.githubusercontent.com/reactiveui/refit/main/LICENSE) | [Project](https://github.com/reactiveui/refit) |
| Jellyfin.Controller | [License](https://raw.githubusercontent.com/jellyfin/jellyfin/master/LICENSE) | [Project](https://github.com/jellyfin/jellyfin) |

## All Voice Commands by Language

Complete utterance lists from the interaction model files, grouped by language.

### Language Index

- **[Arabic](#arabic)**: [ar-SA](#ar-sa)
- **[Dutch](#dutch)**: [nl-NL](#nl-nl)
- **[English](#english)**: [en-AU](#en-au), [en-CA](#en-ca), [en-GB](#en-gb), [en-IN](#en-in), [en-US](#en-us)
- **[French](#french)**: [fr-CA](#fr-ca), [fr-FR](#fr-fr)
- **[German](#german)**: [de-DE](#de-de)
- **[Hindi](#hindi)**: [hi-IN](#hi-in)
- **[Italian](#italian)**: [it-IT](#it-it)
- **[Japanese](#japanese)**: [ja-JP](#ja-jp)
- **[Portuguese](#portuguese)**: [pt-BR](#pt-br)
- **[Spanish](#spanish)**: [es-ES](#es-es), [es-MX](#es-mx), [es-US](#es-us)

### <a id="ar-sa"></a>Arabic (ar-SA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `أضف {song} إلى قائمة الانتظار` · `أضف {song} لـ {musician} إلى قائمة الانتظار` · `ضع {song} في قائمة الانتظار` · `أضف {song} إلى القائمة` |
| Browse Library | `تصفح {browse_category}` · `أرني {browse_category}` · `أعرض {browse_category}` |
| Clear Queue | `امسح قائمة الانتظار` · `أفرغ قائمة الانتظار` · `أزل كل شيء من قائمة الانتظار` |
| Continue Watching | `أكمل المشاهدة` · `أكمل الاستماع` · `أكمل من حيث توقفت` · `ما كنت أشاهده` · `أكمل` |
| Follow Me | `تابعني` · `استمر في التشغيل` · `انقل التشغيل` |
| Go To Chapter | `الفصل التالي` · `الفصل السابق` · `اذهب إلى الفصل {chapter_number}` · `انتقل إلى الفصل {chapter_number}` · `تخطى فصلاً` |
| In Progress Media List | `ماذا كنت أستمع` · `ماذا كنت أشاهد` · `ما الذي قيد التقدم` · `أظهر تقدمي` · `ما كنت ألعب` · `ما الذي بدأته` |
| Learn My Voice | `تعلم صوتي` · `تذكر صوتي` · `تعرف علي` · `اربط صوتي` · `هذا صوتي` · `اضبط ملفي الصوتي` · `اربط صوتي` |
| List Queue | `ماذا في قائمة الانتظار` · `ماذا يأتي بعد ذلك` · `أظهر قائمة الانتظار` · `ماذا سيشغل بعد ذلك` |
| Loop Song On | `كرر هذه الأغنية` · `كرر هذه الأغنية دائماً` · `أعد الأغنية` · `أعد هذه الأغنية` · `أعد هذه الأغنية دائماً` |
| Mark Favorite | `أعجبني هذا` · `أعجبني الفيديو` · `أعجبني الأغنية` · `أعجبني الموسيقى` · `أضف الفيديو إلى المفضلة` · `أضف الأغنية إلى المفضلة` · `احفظ هذا في المفضلة` · `أضف هذا إلى المفضلة` |
| Media Info | `ما اسم الأغنية` · `ما اسم الفيديو` · `ما الذي يشغل الآن` · `ما {media_info_type} هذا` · `أخبرني عن {media_info_type}` · `من يغني هذا` · `ما هو هذا الفنان` · `ما هو {media_info_type}` · `كم مدة هذه الأغنية` · `ما هو الجنس الموسيقي` · `متى صدر هذا` · `أخبرني عن هذا الفنان` · `من أي ألبوم هذا` |
| Play Album | `شغل {album}` · `شغل الألبوم {album}` · `شغل ألبوم {album}` · `شغل {album} لـ {musician}` · `شغل الألبوم {album} لـ {musician}` · `شغل ألبوم {album} لـ {musician}` · `استمع إلى {album}` · `استمع إلى الألبوم {album}` · `استمع إلى {album} لـ {musician}` · `أريد سماع {album}` · `أريد سماع {album} لـ {musician}` · `هل يمكنك تشغيل {album}` · `ابدأ تشغيل {album}` |
| Play Artist Songs | `شغل أغاني {musician}` · `شغل موسيقى {musician}` · `شغل أغانٍ لـ {musician}` · `استمع إلى {musician}` · `استمع إلى أغاني {musician}` · `استمع إلى موسيقى {musician}` · `أريد سماع {musician}` · `هل يمكنك تشغيل {musician}` · `ابدأ تشغيل {musician}` · `اخلط أغاني {musician}` · `شغل {musician}` · `شغل بعض {musician}` |
| Play By Decade | `شغل أغانٍ من {decade}` · `شغل أغاني {decade}` · `شغل موسيقى {decade}` · `شغل أفضل {decade}` · `شغل {genre} من {decade}` · `أريد سماع موسيقى {decade}` · `استمع إلى {decade}` |
| Play By Genre | `شغل موسيقى {genre}` · `شغل أغاني {genre}` · `شغل {genre}` · `أريد الاستماع إلى {genre}` · `أعطني موسيقى {genre}` · `شغل {genre}` · `هل يمكنك تشغيل {genre}` |
| Play Channel | `شغل القناة {channel}` · `شغل الراديو {channel}` |
| Play Episode | `شغل الموسم {season_number} الحلقة {episode_number} من {series_name}` · `شغل {series_name} الموسم {season_number} الحلقة {episode_number}` · `شاهد الموسم {season_number} الحلقة {episode_number} من {series_name}` · `شاهد {series_name} الموسم {season_number} الحلقة {episode_number}` |
| Play Favorites | `شغل مفضلاتي` · `شغل {media_type} المفضلة` · `شغل أغاني المفضلة` · `شغل مفضلات {username}` · `استمع إلى مفضلات {username}` · `شغل {media_type} المفضلة لـ {username}` |
| Play Last Added | `شغل آخر ما أضيف من {media_type}` · `شغل ما أضيف مؤخراً من {media_type}` · `شغل {media_type} جديدة` · `شغل {media_type} المضافة {time_period}` · `شغل آخر ما أضيف من أغاني` · `شغل ما أضيف مؤخراً` · `ما الجديد في {media_type}` · `شغل أحدث {media_type}` |
| Play Mood Music | `شغل موسيقى {mood}` · `شغل {mood}` · `شغل أغانٍ {mood}` · `أريد موسيقى {mood}` · `شغل موسيقى مريحة` · `شغل موسيقى صباحية` · `شغل موسيقى مسائية` · `شغل موسيقى للتمارين` · `شغل موسيقى للتركيز` · `شغل موسيقى للحفلات` |
| Play Next | `شغل {song} بعد ذلك` · `شغل {song} لـ {musician} بعد ذلك` · `أريد سماع {song} بعد ذلك` · `شغل {song} بعد هذا` |
| Play Playlist | `شغل قائمة التشغيل {playlist}` · `شغل قائمة تشغيلي {playlist}` · `ابدأ قائمة التشغيل {playlist}` · `استمع إلى قائمة التشغيل {playlist}` · `هل يمكنك تشغيل قائمة التشغيل {playlist}` · `أريد سماع قائمة التشغيل {playlist}` |
| Play Podcast | `شغل البودكاست {podcast_name}` · `استمع إلى البودكاست {podcast_name}` · `شغل آخر حلقة من {podcast_name}` · `شغل أحدث حلقة من {podcast_name}` · `ابدأ البودكاست {podcast_name}` |
| Play Radio | `شغل الراديو` · `شغل وضع الراديو` · `ابدأ الراديو` · `شغل موسيقى مشابهة` · `شغل أغانٍ مشابهة` · `ابدأ محطة راديو` |
| Play Random | `شغل {media_type} عشوائي` · `اخلط {media_type}` · `شغل شيء عشوائي` · `شغل {media_type} عشوائي من {genre}` · `شغل أغانٍ عشوائية` · `شغل موسيقى عشوائية` · `شغل {genre} عشوائي` · `فاجئني ببعض {media_type}` · `أريد شيء عشوائي` |
| Play Song | `شغل {song}` · `شغل الأغنية {song}` · `شغل أغنية {song}` · `شغل {song} لـ {musician}` · `شغل الأغنية {song} لـ {musician}` · `شغل أغنية {song} لـ {musician}` · `استمع إلى {song}` · `استمع إلى الأغنية {song}` · `استمع إلى {song} لـ {musician}` · `استمع إلى الأغنية {song} لـ {musician}` · `أريد سماع {song}` · `أريد سماع {song} لـ {musician}` · `هل يمكنك تشغيل {song}` · `هل يمكنك تشغيل {song} لـ {musician}` · `ابدأ تشغيل {song}` · `شغل الأغنية المسماة {song}` · `شغل الأغنية المسماة {song} لـ {musician}` |
| Play Video | `شغل الفيديو {title}` · `شغل {title}` · `شاهد {title}` · `هل يمكنك تشغيل {title}` · `أريد مشاهدة {title}` · `شغل الفيلم {title}` · `ابدأ تشغيل الفيديو {title}` · `أريد أن أرى {title}` |
| Query Artist Library | `ما الأغاني لدينا لـ {musician}` · `ما الألبومات لدينا لـ {musician}` · `ما الذي لدينا لـ {musician}` · `أعرض الأغاني لـ {musician}` · `أعرض الألبومات لـ {musician}` · `ما {query_type} لدينا لـ {musician}` · `أعرض {query_type} لـ {musician}` |
| Query Recently Added | `ما الجديد` · `ما الذي أضيف مؤخراً` · `ما الجديد في مكتبتي` · `أرني ما أضيف مؤخراً` · `هل هناك شيء جديد` · `ما الذي تمت إضافته مؤخراً` · `أعرض آخر العناصر المضافة` · `ما هي أحدث العناصر` |
| Recommend | `أوصني بشيء` · `أوصني بموسيقى` · `أوصني بفيلم` · `اقترح شيئاً لمشاهدته` · `اقترح {media_type}` · `شغل شيئاً قد يعجبني` |
| Search Media | `ابحث عن {query}` · `جد {query}` · `ابحث {query}` · `هل لديك {query}` · `أريد أن أجد {query}` · `هل يمكنك أن تجد {query}` |
| Sleep Timer | `أوقف التشغيل بعد {duration_minutes} دقيقة` · `اضبط مؤقت النوم لـ {duration_minutes} دقيقة` · `مؤقت نوم {duration_minutes} دقيقة` · `أوقف بعد {duration_minutes} دقيقة` |
| Turn Radio Off | `أوقف وضع الراديو` · `عطّل وضع الراديو` · `وضع الراديو متوقف` · `أوقف الراديو` |
| Turn Radio On | `شغل وضع الراديو` · `فعّل وضع الراديو` · `وضع الراديو قيد التشغيل` · `شغل الراديو` |
| Unmark Favorite | `لم يعجبني هذا` · `لم يعجبني الفيديو` · `لم يعجبني الأغنية` · `أزل الفيديو من المفضلة` · `أزل الأغنية من المفضلة` |
| Who Am I | `من أنا` · `أي حساب هذا` · `ما الحساب الذي أستخدمه` · `من يتحدث` · `أي ملف نشط` |

### <a id="de-de"></a>German (de-DE)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Füge {song} zur Wiedergabeliste hinzu` · `Füge {song} von {musician} zur Wiedergabeliste hinzu` · `Setze {song} auf die Warteschlange` · `Setze {song} von {musician} auf die Warteschlange` · `Stelle {song} hinten an` · `Füge {song} hinzu` · `Nimm {song} in die Warteschlange auf` · `Nimm {song} von {musician} in die Warteschlange auf` |
| Browse Library | `durchsuche {browse_category}` · `zeige mir {browse_category}` · `liste {browse_category}` |
| Clear Queue | `Lösche meine Warteschlange` · `Lösche die Warteschlange` · `Leere meine Warteschlange` · `Leere die Warteschlange` · `Entferne alles aus der Warteschlange` · `Lösche meine Playlist` |
| Continue Watching | `Weiter schauen` · `Weiter hören` · `Mach da weiter wo ich war` · `Weiter` |
| Follow Me | `folge mir` · `weiterhören` · `Wiedergabe fortsetzen` · `Wiedergabe übernehmen` · `weiter abspielen` · `musik mitnehmen` |
| Go To Chapter | `Nächstes Kapitel` · `Vorheriges Kapitel` · `Gehe zu Kapitel {chapter_number}` · `Springe zu Kapitel {chapter_number}` · `Ein Kapitel vor` · `Ein Kapitel zurück` |
| In Progress Media List | `was höre ich gerade` · `was schaue ich gerade` · `was ist in bearbeitung` · `zeige meinen fortschritt` · `was habe ich angefangen` |
| Learn My Voice | `Lerne meine Stimme` · `Erkenne meine Stimme` · `Verknüpfe meine Stimme` · `Das ist meine Stimme` · `Erkenne mich` · `Richte mein Stimmprofil ein` · `Assoziiere meine Stimme` |
| List Queue | `Was ist in meiner Warteschlange` · `Was steht als Nächstes an` · `Was kommt als Nächstes` · `Zeige meine Warteschlange` · `Zeige die Warteschlange` · `Was wird als Nächstes gespielt` · `Liste meine Warteschlange auf` |
| Loop All Off | `Wiederholung aus` · `Schleife aus` |
| Loop All On | `Wiederholung an` · `Schleife an` |
| Mark Favorite | `Das gefaellt mir` · `Das Video gefaellt mir` · `Das Lied gefaellt mir` · `Die Musik gefaellt mir` · `Fuege das Video zu meinen Favoriten hinzu` · `Fuege das Lied zu meinen Favoriten hinzu` |
| Media Info | `Was ist der {media_info_type}` · `Was ist die {media_info_type}` · `Sag mir den {media_info_type}` · `Sag mir die {media_info_type}` · `Welcher {media_info_type} ist das` · `Welche {media_info_type} ist das` · `Was läuft gerade` · `Was spielt gerade` · `Wie heißt das Lied` · `Wer singt das` · `Von wem ist das` · `Welches Album ist das` · `Welches Jahr ist das` · `Wann wurde das veröffentlicht` · `Wie lange dauert das` · `Welches Genre ist das` · `Erzähl mir von dem Künstler` · `Infos über den Künstler` · `Welches Lied läuft gerade` · `Wie heißt der Titel` · `Welche Band ist das` · `Wer hat das Lied gemacht` · `Sag mir den Albumnamen` · `Wann kam das Album raus` · `Welches Genre hat das Lied` · `Gib mir Infos zu diesem Titel` · `Nenne mir den Künstler` · `Von welchem Album ist das` |
| Play Album | `Spiele {album}` · `Spiele das Album {album}` · `Spiele Album {album}` · `Spiele {album} von {musician}` · `Spiele das Album {album} von {musician}` · `Spiele Album {album} von {musician}` · `Höre {album}` · `Höre das Album {album}` · `Höre {album} von {musician}` · `Höre das Album {album} von {musician}` · `Gib {album} wieder` · `Gib das Album {album} wieder` · `Gib {album} von {musician} wieder` · `Ich möchte {album} hören` · `Spiele die Platte {album}` · `Spiele die Platte {album} von {musician}` · `Starte {album}` · `Kannst du {album} spielen` · `Lass uns {album} hören` · `Mach {album} an` |
| Play Artist Songs | `Spiele Lieder von {musician}` · `Spiele Musik von {musician}` · `Spiele Titel von {musician}` · `Spiele Songs von {musician}` · `Spiele {musician}` · `Spiele etwas von {musician}` · `Höre {musician}` · `Höre Lieder von {musician}` · `Höre Musik von {musician}` · `Höre Titel von {musician}` · `Gib {musician} wieder` · `Gib Lieder von {musician} wieder` · `Gib Musik von {musician} wieder` · `Mach {musician} an` · `Lass uns {musician} hören` · `Ich möchte {musician} hören` · `Ich will {musician} hören` · `Starte {musician}` · `Spiele {musician} Musik` · `Schalte {musician} ein` · `Starte Musik von {musician}` · `Gib etwas von {musician} wieder` |
| Play By Decade | `Spiele Lieder aus den {decade}` · `Spiele Titel aus den {decade}` · `Spiele Hits aus den {decade}` · `Spiele Musik aus den {decade}` · `Spiele {decade} Hits` · `Spiele {decade} Lieder` · `Spiele {genre} aus den {decade}` · `Spiele {genre} Lieder aus den {decade}` · `Ich möchte {decade} Musik hören` · `Spiele etwas aus den {decade}` · `Höre {decade} Musik` · `Gib mir {decade} Lieder` · `Mische {decade} Musik` |
| Play By Genre | `Spiele {genre} Musik` · `Spiele {genre}` · `Spiele etwas {genre}` · `Ich möchte {genre} hören` · `Spiele mir {genre} Musik` |
| Play Channel | `Kanal {channel}` · `Spiele Radio {channel}` · `Radio {channel}` |
| Play Episode | `spiele staffel {season_number} folge {episode_number} von {series_name}` · `spiele {series_name} staffel {season_number} folge {episode_number}` · `schau staffel {season_number} folge {episode_number} von {series_name}` |
| Play Favorites | `Spiele meine Lieblings {media_type}` · `Spiele meine {media_type} Favoriten` · `Spiele meine Favoriten` · `spiele {username}s favoriten` · `spiele die favoriten von {username}` · `hör {username}s favoriten` · `spiele {username}s lieblings {media_type}` · `spiele {username}s lieblingslieder` |
| Play Last Added | `Spiele neu hinzugefuegte {media_type}` · `Spiele kuerzlich hinzugefuegte {media_type}` · `Spiele neue Medien` |
| Play Mood Music | `spiele {mood} musik` · `spiele etwas {mood}` · `ich möchte {mood} musik` · `spiele mir etwas {mood}` |
| Play Next | `Spiele {song} als Nächstes` · `Spiele {song} von {musician} als Nächstes` · `Spiele {song} danach` · `Spiele {song} von {musician} danach` · `Ich möchte {song} als Nächstes hören` · `Setze {song} als Nächstes` |
| Play Playlist | `Spiele die Playlist {playlist}` · `Spiele meine Playlist {playlist}` |
| Play Podcast | `Spiele den Podcast {podcast_name}` · `Spiele Podcast {podcast_name}` · `Höre den Podcast {podcast_name}` · `Höre Podcast {podcast_name}` · `Spiele die neueste Folge von {podcast_name}` · `Starte den Podcast {podcast_name}` · `Ich möchte den Podcast {podcast_name} hören` · `Spiele {podcast_name} Podcast` · `Öffne {podcast_name} Podcast` · `Spiele die letzte Folge von {podcast_name}` · `Höre die neueste Folge von {podcast_name}` · `Setze den Podcast {podcast_name} fort` · `Spiele mir einen Podcast vor {podcast_name}` · `Ich höre gerne den Podcast {podcast_name}` · `Mach mit dem Podcast {podcast_name} weiter` · `Starte den neuesten Podcast {podcast_name}` · `Spiele die aktuelle Folge von {podcast_name}` · `Weiter mit dem Podcast {podcast_name}` · `Lass uns den Podcast {podcast_name} hören` |
| Play Radio | `Spiele Radio` · `Starte Radio` · `Spiele den Radiomodus` · `Spiele ähnliche Musik` · `Spiele ähnliche Lieder` · `Starte einen Radiosender` · `Weiter mit ähnlicher Musik` · `Spiele Lieder wie dieses` |
| Play Random | `Spiele zufällige {media_type}` · `Spiele eine zufällige {media_type}` · `Zufällige {media_type} abspielen` · `Spiele etwas zufälliges` · `Spiele zufällige {media_type} aus {genre}` · `Überrasche mich mit {media_type}` |
| Play Song | `Spiele {song}` · `Spiele das Lied {song}` · `Spiele Lied {song}` · `Spiele {song} von {musician}` · `Spiele das Lied {song} von {musician}` · `Spiele Lied {song} von {musician}` · `Höre {song}` · `Höre das Lied {song}` · `Höre {song} von {musician}` · `Höre das Lied {song} von {musician}` · `Gib {song} wieder` · `Gib das Lied {song} wieder` · `Gib {song} von {musician} wieder` · `Mach {song} an` · `Ich möchte {song} hören` · `Spiele den Titel {song}` · `Spiele den Titel {song} von {musician}` · `Lass uns {song} hören` · `Starte {song}` · `Spiele das Stück {song}` · `Kannst du {song} spielen` |
| Play Video | `Spiele das Video {title}` |
| Query Artist Library | `Welche Titel haben wir von {musician}` · `Welche Lieder haben wir von {musician}` · `Welche Alben haben wir von {musician}` · `Was haben wir von {musician}` · `Zeige Titel von {musician}` · `Zeige Alben von {musician}` · `Liste Titel von {musician} auf` · `Liste Alben von {musician} auf` · `Welche {query_type} haben wir von {musician}` · `Zeige {query_type} von {musician}` · `Welche Titel gibt es von {musician}` · `Welche Alben gibt es von {musician}` |
| Query Recently Added | `was ist neu` · `was wurde kürzlich hinzugefügt` · `zeige mir die Neuzugänge` · `gibt es etwas Neues` · `was ist neu in meiner Bibliothek` · `die neuesten Elemente` · `zuletzt hinzugefügte Inhalte` |
| Recommend | `empfehle etwas` · `empfehle musik` · `empfehle einen film` · `schlage etwas vor` · `spiele etwas das mir gefällt` · `empfehle {media_type}` |
| Repeat Single On | `Lied wiederholen` · `Titel wiederholen` · `Video wiederholen` · `Das wiederholen` |
| Search Media | `Suche nach {query}` · `Finde {query}` · `Suche {query}` · `Hast du {query}` · `Ich möchte {query} finden` · `Kannst du {query} finden` · `Finde mir {query}` · `Suche {query} in der Mediathek` · `Suche in meiner Mediathek nach {query}` · `Durchsuche die Mediathek nach {query}` · `Suche in der Bibliothek nach {query}` · `Finde {query} in meiner Bibliothek` · `Gibt es {query} in der Mediathek` · `Ich suche {query} in meiner Sammlung` · `Prüfe ob {query} vorhanden ist` · `Hast du {query} in der Bibliothek` |
| Sleep Timer | `stoppe in {duration_minutes} minuten` · `schlaf-timer {duration_minutes} minuten` · `stoppe nach {duration_minutes} minuten` · `ausschalten in {duration_minutes} minuten` |
| Turn Radio Off | `Schalte den Radiomodus aus` · `Deaktiviere den Radiomodus` · `Radiomodus aus` · `Schalte Radio aus` · `Deaktiviere Radio` · `Stoppe den Radiomodus` |
| Turn Radio On | `Schalte den Radiomodus ein` · `Aktiviere den Radiomodus` · `Radiomodus an` · `Schalte Radio ein` · `Aktiviere Radio` |
| Unmark Favorite | `Das gefaellt mir nicht` · `Das Video gefaellt mir nicht` · `Das Lied gefaellt mir nicht` · `Die Musik gefaellt mir nicht` · `Entferne das Video aus meinen Favoriten` · `Entferne das Lied aus meinen Favoriten` |
| Who Am I | `Wer bin ich` · `Welches Konto ist das` · `Welches Konto benutze ich` · `Wer spricht` · `Welches Profil ist aktiv` · `Bin ich erkannt` |

### <a id="en-au"></a>English - Australia (en-AU)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} to the queue` · `add {song} by {musician} to my queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` · `put {song} in my queue` · `add {song} to my playlist` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `keep playing` · `pick up where I left off` |
| Go To Chapter | `Next chapter` · `Previous chapter` · `Go to chapter {chapter_number}` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` · `Skip chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` · `list my in progress media` · `what have i started` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` · `associate my voice` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` · `what's playing next` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat the song` · `Repeat this song` · `Repeat this song forever` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` · `What is the title of the music` · `What is currently playing` · `What {media_info_type} is this` · `What {media_info_type} is this from` · `Tell me the {media_info_type}` · `Tell me about the {media_info_type}` · `What is the {media_info_type}` · `What is the {media_info_type} of this song` · `Who is the {media_info_type}` · `How long is this song` · `Who sings this` · `Who performs this` · `What genre is this` · `What year was this released` · `Tell me about this artist` · `What album is this from` · `tell me what song this is` · `what track is playing` · `what is the name of this track` · `who made this song` · `what band is this` · `tell me the album name` · `when was this song released` · `what is the genre of this` · `give me info about this track` · `tell me the artist name` |
| Play Album | `play {album}` · `play the album {album}` · `play album {album}` · `play {album} by {musician}` · `play the album {album} by {musician}` · `play album {album} by {musician}` · `listen to {album}` · `listen to the album {album}` · `listen to {album} by {musician}` · `listen to the album {album} by {musician}` · `put on {album}` · `put on the album {album}` · `I want to hear {album}` · `I want to hear {album} by {musician}` · `can you play {album}` · `start playing {album}` · `play the record {album}` · `play the record {album} by {musician}` · `let's hear {album}` · `give me {album}` · `stream {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` · `listen to {musician}` · `listen to songs by {musician}` · `listen to music by {musician}` · `listen to songs from {musician}` · `put on {musician}` · `put on some {musician}` · `I want to hear {musician}` · `I want to listen to {musician}` · `let's hear {musician}` · `let's listen to {musician}` · `can you play {musician}` · `start playing {musician}` · `shuffle {musician}` · `shuffle songs by {musician}` · `play {musician}` · `play some {musician}` · `play {musician} songs` · `play {musician} music` · `give me {musician}` · `stream {musician}` |
| Play By Decade | `play songs from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` · `play {decade} songs` · `play {decade} tracks` · `play {decade} music` · `play {genre} from the {decade}` · `play {genre} songs from the {decade}` · `I want to hear {decade} music` · `play something from the {decade}` · `listen to {decade} music` · `give me {decade} songs` · `shuffle {decade} music` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my {media_type} favorite` · `Play my favorites` · `play {username}'s favorites` · `play {username}'s favourite songs` · `play {username}'s favourite music` · `play {username}'s favorites playlist` · `listen to {username}'s favorites` · `play {username}'s favorite {media_type}` · `play {username}'s favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play new media` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` · `play {song} after this` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` · `play the latest {podcast_name}` · `hear the podcast {podcast_name}` · `start the podcast {podcast_name}` · `I want to listen to {podcast_name} podcast` · `play {podcast_name} podcast` · `open {podcast_name} podcast` · `play my podcast {podcast_name}` · `resume the podcast {podcast_name}` · `continue the podcast {podcast_name}` · `listen to the latest episode of {podcast_name}` · `start listening to the podcast {podcast_name}` · `I want to hear the podcast {podcast_name}` · `let us listen to the podcast {podcast_name}` · `play {podcast_name} episodes` · `catch up on the podcast {podcast_name}` |
| Play Radio | `play radio` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` · `play similar songs` · `start a radio station` · `play songs like this` |
| Play Random | `Play a random {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Surprise me with some {media_type}` · `Shuffle {genre} {media_type}` |
| Play Song | `play {song}` · `play the song {song}` · `play song {song}` · `play {song} by {musician}` · `play the song {song} by {musician}` · `play song {song} by {musician}` · `listen to {song}` · `listen to the song {song}` · `listen to {song} by {musician}` · `listen to the song {song} by {musician}` · `put on {song}` · `put on the song {song}` · `I want to hear {song}` · `I want to hear {song} by {musician}` · `can you play {song}` · `start playing {song}` · `play the track {song}` · `play the track {song} by {musician}` · `let's hear {song}` · `give me {song}` · `stream {song}` · `play that song {song}` |
| Play Video | `Play the video {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` · `what albums are available from {musician}` · `what do we have by {musician}` · `show me tracks by {musician}` · `show me albums by {musician}` · `list tracks by {musician}` · `list albums by {musician}` · `which {query_type} do we have by {musician}` · `what {query_type} are available from {musician}` · `show me {query_type} by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` · `what's been added recently` · `list recently added items` · `what are the newest items` |
| Recommend | `recommend something` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` · `play something i would like` · `recommend {media_type}` · `suggest {media_type}` |
| Search Media | `Search for {query}` · `Find {query}` · `Look for {query}` · `Look up {query}` · `Do you have {query}` · `I want to find {query}` · `Can you find {query}` · `Search {query}` · `Find me {query}` · `search my library for {query}` · `look in my media library for {query}` · `search jellyfin for {query}` · `do I have {query} in my library` · `is {query} in my collection` · `search my collection for {query}` · `find {query} in my library` · `check if {query} is available` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-ca"></a>English - Canada (en-CA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} to the queue` · `add {song} by {musician} to my queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` · `put {song} in my queue` · `add {song} to my playlist` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `keep playing` |
| Go To Chapter | `Next chapter` · `Previous chapter` · `Go to chapter {chapter_number}` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` · `Skip chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` · `list my in progress media` · `what have i started` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` · `associate my voice` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` · `what's playing next` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat the song` · `Repeat this song` · `Repeat this song forever` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` · `What is the title of the music` · `What is currently playing` · `What {media_info_type} is this` · `What {media_info_type} is this from` · `Tell me the {media_info_type}` · `Tell me about the {media_info_type}` · `What is the {media_info_type}` · `What is the {media_info_type} of this song` · `Who is the {media_info_type}` · `How long is this song` · `Who sings this` · `Who performs this` · `What genre is this` · `What year was this released` · `Tell me about this artist` · `What album is this from` · `tell me what song this is` · `what track is playing` · `what is the name of this track` · `who made this song` · `what band is this` · `tell me the album name` · `when was this song released` · `what is the genre of this` · `give me info about this track` · `tell me the artist name` |
| Play Album | `play {album}` · `play the album {album}` · `play album {album}` · `play {album} by {musician}` · `play the album {album} by {musician}` · `play album {album} by {musician}` · `listen to {album}` · `listen to the album {album}` · `listen to {album} by {musician}` · `listen to the album {album} by {musician}` · `put on {album}` · `put on the album {album}` · `I want to hear {album}` · `I want to hear {album} by {musician}` · `can you play {album}` · `start playing {album}` · `play the record {album}` · `play the record {album} by {musician}` · `let's hear {album}` · `give me {album}` · `stream {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` · `listen to {musician}` · `listen to songs by {musician}` · `listen to music by {musician}` · `listen to songs from {musician}` · `put on {musician}` · `put on some {musician}` · `I want to hear {musician}` · `I want to listen to {musician}` · `let's hear {musician}` · `let's listen to {musician}` · `can you play {musician}` · `start playing {musician}` · `shuffle {musician}` · `shuffle songs by {musician}` · `play {musician}` · `play some {musician}` · `play {musician} songs` · `play {musician} music` · `give me {musician}` · `stream {musician}` |
| Play By Decade | `play songs from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` · `play {decade} songs` · `play {decade} tracks` · `play {decade} music` · `play {genre} from the {decade}` · `play {genre} songs from the {decade}` · `I want to hear {decade} music` · `play something from the {decade}` · `listen to {decade} music` · `give me {decade} songs` · `shuffle {decade} music` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my {media_type} favorite` · `Play my favorites` · `play {username}'s favorites` · `play {username}'s favourite songs` · `play {username}'s favourite music` · `play {username}'s favorites playlist` · `listen to {username}'s favorites` · `play {username}'s favorite {media_type}` · `play {username}'s favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play new media` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` · `play {song} after this` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` · `play the latest {podcast_name}` · `hear the podcast {podcast_name}` · `start the podcast {podcast_name}` · `I want to listen to {podcast_name} podcast` · `play {podcast_name} podcast` · `open {podcast_name} podcast` · `play my podcast {podcast_name}` · `resume the podcast {podcast_name}` · `continue the podcast {podcast_name}` · `listen to the latest episode of {podcast_name}` · `start listening to the podcast {podcast_name}` · `I want to hear the podcast {podcast_name}` · `let us listen to the podcast {podcast_name}` · `play {podcast_name} episodes` · `catch up on the podcast {podcast_name}` |
| Play Radio | `play radio` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` · `play similar songs` · `start a radio station` · `play songs like this` |
| Play Random | `Play a random {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Surprise me with some {media_type}` · `Shuffle {genre} {media_type}` |
| Play Song | `play {song}` · `play the song {song}` · `play song {song}` · `play {song} by {musician}` · `play the song {song} by {musician}` · `play song {song} by {musician}` · `listen to {song}` · `listen to the song {song}` · `listen to {song} by {musician}` · `listen to the song {song} by {musician}` · `put on {song}` · `put on the song {song}` · `I want to hear {song}` · `I want to hear {song} by {musician}` · `can you play {song}` · `start playing {song}` · `play the track {song}` · `play the track {song} by {musician}` · `let's hear {song}` · `give me {song}` · `stream {song}` · `play that song {song}` |
| Play Video | `Play the video {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` · `what albums are available from {musician}` · `what do we have by {musician}` · `show me tracks by {musician}` · `show me albums by {musician}` · `list tracks by {musician}` · `list albums by {musician}` · `which {query_type} do we have by {musician}` · `what {query_type} are available from {musician}` · `show me {query_type} by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` · `what's been added recently` · `list recently added items` · `what are the newest items` |
| Recommend | `recommend something` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` · `play something i would like` · `recommend {media_type}` · `suggest {media_type}` |
| Search Media | `Search for {query}` · `Find {query}` · `Look for {query}` · `Look up {query}` · `Do you have {query}` · `I want to find {query}` · `Can you find {query}` · `Search {query}` · `Find me {query}` · `search my library for {query}` · `look in my media library for {query}` · `search jellyfin for {query}` · `do I have {query} in my library` · `is {query} in my collection` · `search my collection for {query}` · `find {query} in my library` · `check if {query} is available` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-gb"></a>English - UK (en-GB)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} to the queue` · `add {song} by {musician} to my queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` · `put {song} in my queue` · `add {song} to my playlist` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `move playback here` · `keep playing` · `continue from other room` · `transfer playback` · `pick up where I left off` · `carry on playing` |
| Go To Chapter | `Next chapter` · `Previous chapter` · `Go to chapter {chapter_number}` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` · `Skip chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` · `list my in progress media` · `what have i started` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` · `associate my voice` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` · `what's playing next` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat the song` · `Repeat this song` · `Repeat this song forever` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` · `What is the title of the music` · `What is currently playing` · `What {media_info_type} is this` · `What {media_info_type} is this from` · `Tell me the {media_info_type}` · `Tell me about the {media_info_type}` · `What is the {media_info_type}` · `What is the {media_info_type} of this song` · `Who is the {media_info_type}` · `How long is this song` · `Who sings this` · `Who performs this` · `What genre is this` · `What year was this released` · `Tell me about this artist` · `What album is this from` · `tell me what song this is` · `what track is playing` · `what is the name of this track` · `who made this song` · `what band is this` · `tell me the album name` · `when was this song released` · `what is the genre of this` · `give me info about this track` · `tell me the artist name` |
| Play Album | `play {album}` · `play the album {album}` · `play album {album}` · `play {album} by {musician}` · `play the album {album} by {musician}` · `play album {album} by {musician}` · `listen to {album}` · `listen to the album {album}` · `listen to {album} by {musician}` · `listen to the album {album} by {musician}` · `put on {album}` · `put on the album {album}` · `I want to hear {album}` · `I want to hear {album} by {musician}` · `can you play {album}` · `start playing {album}` · `play the record {album}` · `play the record {album} by {musician}` · `let's hear {album}` · `give me {album}` · `stream {album}` · `stick on {album}` · `stick on {album} by {musician}` · `bang on the album {album}` · `bang on the album {album} by {musician}` · `I fancy hearing {album}` · `I fancy hearing {album} by {musician}` · `let's have {album}` · `let's have {album} by {musician}` · `whack on {album}` · `whack on {album} by {musician}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` · `listen to {musician}` · `listen to songs by {musician}` · `listen to music by {musician}` · `listen to songs from {musician}` · `put on {musician}` · `put on some {musician}` · `I want to hear {musician}` · `I want to listen to {musician}` · `let's hear {musician}` · `let's listen to {musician}` · `can you play {musician}` · `start playing {musician}` · `shuffle {musician}` · `shuffle songs by {musician}` · `play {musician}` · `play some {musician}` · `play {musician} songs` · `play {musician} music` · `give me {musician}` · `stream {musician}` · `stick on some {musician}` · `bang on {musician}` · `I fancy listening to {musician}` · `I fancy hearing {musician}` · `whack on some {musician}` · `let's have some {musician}` |
| Play By Decade | `play songs from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` · `play {decade} songs` · `play {decade} tracks` · `play {decade} music` · `play {genre} from the {decade}` · `play {genre} songs from the {decade}` · `I want to hear {decade} music` · `play something from the {decade}` · `listen to {decade} music` · `give me {decade} songs` · `shuffle {decade} music` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` · `give me {genre} music` · `put on some {genre}` · `let's hear some {genre}` · `I feel like {genre} music` · `start playing {genre}` · `can you play {genre}` · `stream {genre} music` · `I want to hear {genre}` · `play {genre} tracks` · `let's listen to {genre}` · `bang on some {genre}` · `stick on {genre} music` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my {media_type} favorite` · `Play my favorites` · `Play my favourite {media_type}` · `Play my {media_type} favourite` · `Play my favourites` · `put on my favourites` · `let's hear my favourite {media_type}` · `give me my favourite {media_type}` · `start playing my favourites` · `I want to hear my favourite {media_type}` · `can you play my favourites` · `play what I like` · `play my best of` · `stick on my favourite {media_type}` · `play {username}'s favourites` · `play {username}'s favourite songs` · `play {username}'s favourite music` · `play {username}'s favourites playlist` · `listen to {username}'s favourites` · `play {username}'s favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play new media` · `what's new in {media_type}` · `play the latest {media_type}` · `play my newest {media_type}` · `play fresh {media_type}` · `show me new {media_type}` · `give me the latest {media_type}` · `let's hear something new` · `put on the newest {media_type}` · `start playing new {media_type}` · `I want to hear new {media_type}` · `play {media_type} I just added` · `play recently added` · `play new arrivals` · `anything new lately` · `play what's just been added` · `have we got anything new` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` · `play {song} after this` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `put on the playlist {playlist}` · `put on my playlist {playlist}` · `start the playlist {playlist}` · `start my playlist {playlist}` · `queue up the playlist {playlist}` · `queue up my playlist {playlist}` · `let's hear the playlist {playlist}` · `let's hear my playlist {playlist}` · `can you play the playlist {playlist}` · `can you play my playlist {playlist}` · `I want to hear the playlist {playlist}` · `I want to hear my playlist {playlist}` · `give me the playlist {playlist}` · `give me my playlist {playlist}` · `stream the playlist {playlist}` · `stream my playlist {playlist}` · `listen to the playlist {playlist}` · `listen to my playlist {playlist}` · `stick on the playlist {playlist}` · `stick on my playlist {playlist}` · `whack on the playlist {playlist}` · `whack on my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` · `play the latest {podcast_name}` · `hear the podcast {podcast_name}` · `start the podcast {podcast_name}` · `I want to listen to {podcast_name} podcast` · `play {podcast_name} podcast` · `open {podcast_name} podcast` · `play my podcast {podcast_name}` · `resume the podcast {podcast_name}` · `continue the podcast {podcast_name}` · `listen to the latest episode of {podcast_name}` · `start listening to the podcast {podcast_name}` · `I want to hear the podcast {podcast_name}` · `let us listen to the podcast {podcast_name}` · `play {podcast_name} episodes` · `catch up on the podcast {podcast_name}` |
| Play Radio | `play radio` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` · `play similar songs` · `start a radio station` · `play songs like this` |
| Play Random | `Play a random {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Surprise me with some {media_type}` · `Shuffle {genre} {media_type}` · `give me a random {media_type}` · `put on something random` · `I want something random` · `shuffle some {media_type}` · `let's hear something random` · `surprise me with {genre} {media_type}` · `pick a random {media_type}` · `mix up my {media_type}` · `play a surprise {media_type}` · `anything will do for {media_type}` |
| Play Song | `play {song}` · `play the song {song}` · `play song {song}` · `play {song} by {musician}` · `play the song {song} by {musician}` · `play song {song} by {musician}` · `listen to {song}` · `listen to the song {song}` · `listen to {song} by {musician}` · `listen to the song {song} by {musician}` · `put on {song}` · `put on the song {song}` · `I want to hear {song}` · `I want to hear {song} by {musician}` · `can you play {song}` · `start playing {song}` · `play the track {song}` · `play the track {song} by {musician}` · `let's hear {song}` · `give me {song}` · `stream {song}` · `play that song {song}` · `stick on {song}` · `stick on {song} by {musician}` · `bang on {song}` · `I fancy hearing {song}` · `I fancy hearing {song} by {musician}` · `let's have {song}` · `let's have {song} by {musician}` · `whack on {song}` · `whack on {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` · `let's watch {title}` · `stream {title}` · `give me the video {title}` · `show me {title}` · `play the movie {title}` · `put on the movie {title}` · `can you play the video {title}` · `start the video {title}` · `I want to see {title}` · `stick on {title}` · `stick on the film {title}` · `bang on the video {title}` · `whack on {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` · `what albums are available from {musician}` · `what do we have by {musician}` · `show me tracks by {musician}` · `show me albums by {musician}` · `list tracks by {musician}` · `list albums by {musician}` · `which {query_type} do we have by {musician}` · `what {query_type} are available from {musician}` · `show me {query_type} by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` · `what's been added recently` · `list recently added items` · `what are the newest items` |
| Recommend | `recommend something` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` · `play something i would like` · `recommend {media_type}` · `suggest {media_type}` |
| Search Media | `Search for {query}` · `Find {query}` · `Look for {query}` · `Look up {query}` · `Do you have {query}` · `I want to find {query}` · `Can you find {query}` · `Search {query}` · `Find me {query}` · `search my library for {query}` · `look in my media library for {query}` · `search jellyfin for {query}` · `do I have {query} in my library` · `is {query} in my collection` · `search my collection for {query}` · `find {query} in my library` · `check if {query} is available` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-in"></a>English - India (en-IN)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} to the queue` · `add {song} by {musician} to my queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` · `put {song} in my queue` · `add {song} to my playlist` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` |
| Go To Chapter | `Next chapter` · `Previous chapter` · `Go to chapter {chapter_number}` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` · `Skip chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` · `list my in progress media` · `what have i started` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` · `associate my voice` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` · `what's playing next` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat the song` · `Repeat this song` · `Repeat this song forever` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `Add the video to my favorites` · `Add the song to my favorites` |
| Media Info | `What is the name of the song` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` · `What is the title of the music` · `What is currently playing` · `What {media_info_type} is this` · `What {media_info_type} is this from` · `Tell me the {media_info_type}` · `Tell me about the {media_info_type}` · `What is the {media_info_type}` · `What is the {media_info_type} of this song` · `Who is the {media_info_type}` · `How long is this song` · `Who sings this` · `Who performs this` · `What genre is this` · `What year was this released` · `Tell me about this artist` · `What album is this from` · `tell me what song this is` · `what track is playing` · `what is the name of this track` · `who made this song` · `what band is this` · `tell me the album name` · `when was this song released` · `what is the genre of this` · `give me info about this track` · `tell me the artist name` |
| Play Album | `play {album}` · `play the album {album}` · `play album {album}` · `play {album} by {musician}` · `play the album {album} by {musician}` · `play album {album} by {musician}` · `listen to {album}` · `listen to the album {album}` · `listen to {album} by {musician}` · `listen to the album {album} by {musician}` · `put on {album}` · `put on the album {album}` · `I want to hear {album}` · `I want to hear {album} by {musician}` · `can you play {album}` · `start playing {album}` · `play the record {album}` · `play the record {album} by {musician}` · `let's hear {album}` · `give me {album}` · `stream {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` · `listen to {musician}` · `listen to songs by {musician}` · `listen to music by {musician}` · `listen to songs from {musician}` · `put on {musician}` · `put on some {musician}` · `I want to hear {musician}` · `I want to listen to {musician}` · `let's hear {musician}` · `let's listen to {musician}` · `can you play {musician}` · `start playing {musician}` · `shuffle {musician}` · `shuffle songs by {musician}` · `play {musician}` · `play some {musician}` · `play {musician} songs` · `play {musician} music` · `give me {musician}` · `stream {musician}` |
| Play By Decade | `play songs from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` · `play {decade} songs` · `play {decade} tracks` · `play {decade} music` · `play {genre} from the {decade}` · `play {genre} songs from the {decade}` · `I want to hear {decade} music` · `play something from the {decade}` · `listen to {decade} music` · `give me {decade} songs` · `shuffle {decade} music` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my {media_type} favorite` · `Play my favorites` · `play {username}'s favorites` · `play {username}'s favourite songs` · `play {username}'s favourite music` · `play {username}'s favorites playlist` · `listen to {username}'s favorites` · `play {username}'s favorite {media_type}` · `play {username}'s favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play new media` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` · `play {song} after this` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` · `play the latest {podcast_name}` · `hear the podcast {podcast_name}` · `start the podcast {podcast_name}` · `I want to listen to {podcast_name} podcast` · `play {podcast_name} podcast` · `open {podcast_name} podcast` · `play my podcast {podcast_name}` · `resume the podcast {podcast_name}` · `continue the podcast {podcast_name}` · `listen to the latest episode of {podcast_name}` · `start listening to the podcast {podcast_name}` · `I want to hear the podcast {podcast_name}` · `let us listen to the podcast {podcast_name}` · `play {podcast_name} episodes` · `catch up on the podcast {podcast_name}` |
| Play Radio | `play radio` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` · `play similar songs` · `start a radio station` · `play songs like this` |
| Play Random | `Play a random {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Surprise me with some {media_type}` · `Shuffle {genre} {media_type}` |
| Play Song | `play {song}` · `play the song {song}` · `play song {song}` · `play {song} by {musician}` · `play the song {song} by {musician}` · `play song {song} by {musician}` · `listen to {song}` · `listen to the song {song}` · `listen to {song} by {musician}` · `listen to the song {song} by {musician}` · `put on {song}` · `put on the song {song}` · `I want to hear {song}` · `I want to hear {song} by {musician}` · `can you play {song}` · `start playing {song}` · `play the track {song}` · `play the track {song} by {musician}` · `let's hear {song}` · `give me {song}` · `stream {song}` · `play that song {song}` |
| Play Video | `Play the video {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` · `what albums are available from {musician}` · `what do we have by {musician}` · `show me tracks by {musician}` · `show me albums by {musician}` · `list tracks by {musician}` · `list albums by {musician}` · `which {query_type} do we have by {musician}` · `what {query_type} are available from {musician}` · `show me {query_type} by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` · `what's been added recently` · `list recently added items` · `what are the newest items` |
| Recommend | `recommend something` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` · `play something i would like` · `recommend {media_type}` · `suggest {media_type}` |
| Search Media | `Search for {query}` · `Find {query}` · `Look for {query}` · `Look up {query}` · `Do you have {query}` · `I want to find {query}` · `Can you find {query}` · `Search {query}` · `Find me {query}` · `search my library for {query}` · `look in my media library for {query}` · `search jellyfin for {query}` · `do I have {query} in my library` · `is {query} in my collection` · `search my collection for {query}` · `find {query} in my library` · `check if {query} is available` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="en-us"></a>English - US (en-US)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `add {song} to my queue` · `add {song} to the queue` · `add {song} by {musician} to my queue` · `add {song} by {musician} to the queue` · `queue {song}` · `queue {song} by {musician}` · `put {song} in my queue` · `add {song} to my playlist` |
| Browse Library | `browse {browse_category}` · `show me {browse_category}` · `list {browse_category}` · `what {browse_category} do i have` |
| Clear Queue | `clear my queue` · `clear the queue` · `empty my queue` · `empty the queue` · `remove everything from my queue` · `clear my playlist` |
| Continue Watching | `Continue watching` · `Continue listening` · `Resume where I left off` · `What was I watching` · `Keep playing` · `Continue` |
| Follow Me | `follow me` · `continue playing` · `resume from where I left off` · `take over playback` · `move playback here` · `keep playing` · `continue from other room` · `transfer playback` · `pick up where I left off` · `carry on playing` |
| Go To Chapter | `Next chapter` · `Previous chapter` · `Go to chapter {chapter_number}` · `Skip to chapter {chapter_number}` · `Go forward a chapter` · `Go back a chapter` · `Skip chapter` |
| In Progress Media List | `what am i listening to` · `what am i watching` · `what's in progress` · `what is in progress` · `show my progress` · `what was i playing` · `list my in progress media` · `what have i started` |
| Learn My Voice | `learn my voice` · `remember my voice` · `recognize me` · `link my voice` · `this is my voice` · `set up my voice profile` · `associate my voice` |
| List Queue | `what's in my queue` · `what's in the queue` · `what's coming up` · `what's up next` · `show my queue` · `list my queue` · `what's playing next` |
| Loop Song On | `loop this song` · `loop this song forever` · `Repeat the song` · `Repeat this song` · `Repeat this song forever` |
| Mark Favorite | `I like that` · `I like the video` · `I like the song` · `I like the music` · `I like this song` · `I like this` · `Add the video to my favorites` · `Add the song to my favorites` · `Save this to my favorites` · `Favorite this` |
| Media Info | `What is the name of the song` · `What is the name of the video` · `What is the name of the music` · `What is the title of the song` · `What is the title of the video` · `What is the title of the music` · `What is currently playing` · `What {media_info_type} is this` · `What {media_info_type} is this from` · `Tell me the {media_info_type}` · `Tell me about the {media_info_type}` · `What is the {media_info_type}` · `What is the {media_info_type} of this song` · `Who is the {media_info_type}` · `How long is this song` · `Who sings this` · `Who performs this` · `What genre is this` · `What year was this released` · `Tell me about this artist` · `What album is this from` |
| Play Album | `play {album}` · `play the album {album}` · `play album {album}` · `play {album} by {musician}` · `play the album {album} by {musician}` · `play album {album} by {musician}` · `listen to {album}` · `listen to the album {album}` · `listen to {album} by {musician}` · `listen to the album {album} by {musician}` · `put on {album}` · `put on the album {album}` · `I want to hear {album}` · `I want to hear {album} by {musician}` · `can you play {album}` · `start playing {album}` · `play the record {album}` · `play the record {album} by {musician}` · `let's hear {album}` · `give me {album}` · `stream {album}` |
| Play Artist Songs | `play songs by {musician}` · `play music by {musician}` · `play tracks by {musician}` · `play tunes by {musician}` · `play songs from {musician}` · `play music from {musician}` · `listen to {musician}` · `listen to songs by {musician}` · `listen to music by {musician}` · `listen to songs from {musician}` · `put on {musician}` · `put on some {musician}` · `I want to hear {musician}` · `I want to listen to {musician}` · `let's hear {musician}` · `let's listen to {musician}` · `can you play {musician}` · `start playing {musician}` · `shuffle {musician}` · `shuffle songs by {musician}` · `play {musician}` · `play some {musician}` · `play {musician} songs` · `play {musician} music` · `give me {musician}` · `stream {musician}` |
| Play By Decade | `play songs from the {decade}` · `play tracks from the {decade}` · `play hits from the {decade}` · `play music from the {decade}` · `play {decade} hits` · `play {decade} songs` · `play {decade} tracks` · `play {decade} music` · `play {genre} from the {decade}` · `play {genre} songs from the {decade}` · `I want to hear {decade} music` · `play something from the {decade}` · `listen to {decade} music` · `give me {decade} songs` · `shuffle {decade} music` · `play me some {decade} hits` · `I want to hear {decade} hits` · `play me {decade} hits` · `listen to {decade} hits` · `give me {decade} hits` · `play some {decade} hits` · `let's hear {decade} hits` · `play hits of the {decade}` · `play the hits from the {decade}` · `play the {decade} hits` · `stream {decade} hits` · `play {decade} greatest hits` · `play greatest hits of the {decade}` |
| Play By Genre | `Play some {genre} music` · `Play {genre} songs` · `Play {genre} music` · `Play me some {genre}` · `I want to listen to {genre}` · `Play {genre}` · `give me {genre} music` · `put on some {genre}` · `let's hear some {genre}` · `I feel like {genre} music` · `start playing {genre}` · `can you play {genre}` · `stream {genre} music` · `I want to hear {genre}` · `play {genre} tracks` · `let's listen to {genre}` |
| Play Channel | `Play channel {channel}` · `Play radio {channel}` |
| Play Episode | `play season {season_number} episode {episode_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `play episode {episode_number} of season {season_number} of {series_name}` · `play {series_name} season {season_number} episode {episode_number}` · `watch season {season_number} episode {episode_number} of {series_name}` · `watch {series_name} season {season_number} episode {episode_number}` |
| Play Favorites | `Play my favorite {media_type}` · `Play my {media_type} favorite` · `Play my favorites` · `Play my favorite songs` · `Play my favorite music` · `Play my favorites playlist` · `Play my favourite songs` · `Play my favourite music` · `play {username}'s favorites` · `play {username}'s favourite songs` · `play {username}'s favourite music` · `play {username}'s favorites playlist` · `listen to {username}'s favorites` · `play {username}'s favorite {media_type}` · `play {username}'s favourite {media_type}` |
| Play Last Added | `Play last added {media_type}` · `Play recently added {media_type}` · `Play newly added {media_type}` · `Play new media` · `Play something new` · `Play {media_type} added {time_period}` · `Play recently added {media_type} from {time_period}` · `Play new {media_type} from {time_period}` · `Play {media_type} added {time_period}` · `Play last added songs` · `Play recently added songs` · `Play last added music` · `what's new in {media_type}` · `play the latest {media_type}` · `play my newest {media_type}` · `play fresh {media_type}` · `show me new {media_type}` · `give me the latest {media_type}` · `let's hear something new` · `put on the newest {media_type}` · `start playing new {media_type}` · `I want to hear new {media_type}` · `play {media_type} I just added` · `play recently added` · `play new arrivals` |
| Play Mood Music | `play {mood} music` · `play something {mood}` · `play {mood} songs` · `i want {mood} music` · `play me something {mood}` · `play morning music` · `play evening music` · `play dinner music` · `play workout music` · `play focus music` · `play party music` · `play relaxing music` · `i want something chill` |
| Play Next | `play {song} next` · `play {song} by {musician} next` · `play {song} up next` · `play {song} by {musician} up next` · `I want to hear {song} next` · `hear {song} next` · `play {song} after this` |
| Play Playlist | `Play the playlist {playlist}` · `Play my playlist {playlist}` · `put on the playlist {playlist}` · `put on my playlist {playlist}` · `start the playlist {playlist}` · `start my playlist {playlist}` · `queue up the playlist {playlist}` · `queue up my playlist {playlist}` · `let's hear the playlist {playlist}` · `let's hear my playlist {playlist}` · `can you play the playlist {playlist}` · `can you play my playlist {playlist}` · `I want to hear the playlist {playlist}` · `I want to hear my playlist {playlist}` · `give me the playlist {playlist}` · `give me my playlist {playlist}` · `stream the playlist {playlist}` · `stream my playlist {playlist}` · `listen to the playlist {playlist}` · `listen to my playlist {playlist}` |
| Play Podcast | `play the podcast {podcast_name}` · `play podcast {podcast_name}` · `listen to the podcast {podcast_name}` · `listen to podcast {podcast_name}` · `play the latest episode of {podcast_name}` · `play the newest episode of {podcast_name}` · `play the latest {podcast_name}` · `hear the podcast {podcast_name}` · `start the podcast {podcast_name}` · `I want to listen to {podcast_name} podcast` · `play {podcast_name} podcast` · `open {podcast_name} podcast` |
| Play Radio | `play radio` · `play radio mode` · `start radio` · `play more like this` · `keep playing similar music` · `play similar songs` · `start a radio station` · `play songs like this` |
| Play Random | `Play a random {media_type}` · `Play random {media_type}` · `Shuffle my {media_type}` · `Play something random` · `Play a random {media_type} from {genre}` · `Play random {genre} {media_type}` · `Surprise me with some {media_type}` · `Shuffle {genre} {media_type}` · `Play random songs` · `Play random music` · `Play random {genre} songs` · `Play random {genre} music` · `Play a random song` · `Play some random {genre} songs` · `give me a random {media_type}` · `put on something random` · `I want something random` · `shuffle some {media_type}` · `let's hear something random` · `surprise me with {genre} {media_type}` · `pick a random {media_type}` · `mix up my {media_type}` · `play a surprise {media_type}` |
| Play Song | `play {song}` · `play the song {song}` · `play song {song}` · `play {song} by {musician}` · `play the song {song} by {musician}` · `play song {song} by {musician}` · `listen to {song}` · `listen to the song {song}` · `listen to {song} by {musician}` · `listen to the song {song} by {musician}` · `put on {song}` · `put on the song {song}` · `I want to hear {song}` · `I want to hear {song} by {musician}` · `I want to hear the song {song} by {musician}` · `can you play {song}` · `can you play {song} by {musician}` · `can you play the song {song} by {musician}` · `start playing {song}` · `play the track {song}` · `play the track {song} by {musician}` · `let's hear {song}` · `give me {song}` · `stream {song}` · `play that song {song}` · `play that song {song} by {musician}` · `put on {song} by {musician}` · `put on the song {song} by {musician}` · `start playing {song} by {musician}` · `stream {song} by {musician}` · `let's hear {song} by {musician}` · `give me {song} by {musician}` · `play a song called {song}` · `play the song called {song}` · `play a song called {song} by {musician}` · `play the song called {song} by {musician}` |
| Play Video | `Play the video {title}` · `put on the video {title}` · `start playing {title}` · `watch {title}` · `can you play {title}` · `I want to watch {title}` · `let's watch {title}` · `stream {title}` · `give me the video {title}` · `show me {title}` · `play the movie {title}` · `put on the movie {title}` · `can you play the video {title}` · `start the video {title}` · `I want to see {title}` |
| Query Artist Library | `which tracks do we have by {musician}` · `which songs do we have by {musician}` · `what tracks are available from {musician}` · `what songs are available from {musician}` · `which albums do we have by {musician}` · `what albums are available from {musician}` · `what do we have by {musician}` · `show me tracks by {musician}` · `show me albums by {musician}` · `list tracks by {musician}` · `list albums by {musician}` · `which {query_type} do we have by {musician}` · `what {query_type} are available from {musician}` · `show me {query_type} by {musician}` |
| Query Recently Added | `what's new` · `what was recently added` · `what's new in my library` · `show me recently added` · `what's on deck` · `anything new lately` · `what's been added recently` · `list recently added items` · `what are the newest items` |
| Recommend | `recommend something` · `recommend some music` · `recommend a movie` · `suggest something to watch` · `suggest some music` · `play something i would like` · `recommend {media_type}` · `suggest {media_type}` |
| Search Media | `Search for {query}` · `Find {query}` · `Look for {query}` · `Look up {query}` · `Do you have {query}` · `I want to find {query}` · `Can you find {query}` · `Search {query}` · `Find me {query}` |
| Sleep Timer | `stop playing in {duration_minutes} minutes` · `set a sleep timer for {duration_minutes} minutes` · `sleep timer {duration_minutes} minutes` · `stop after {duration_minutes} minutes` · `turn off in {duration_minutes} minutes` · `set sleep timer {duration_minutes}` |
| Turn Radio Off | `turn off radio mode` · `disable radio mode` · `radio mode off` · `turn off radio` · `disable radio` · `stop radio mode` |
| Turn Radio On | `turn on radio mode` · `enable radio mode` · `radio mode on` · `turn on radio` · `enable radio` |
| Unmark Favorite | `I don't like this` · `I don't like the video` · `I don't like song` · `I don't like music` · `Remove the video from my favorites` · `Remove the song from my favorites` |
| Who Am I | `who am i` · `which account is this` · `what account am i using` · `who is speaking` · `which profile is active` · `am i recognized` |

### <a id="es-es"></a>Spanish - Spain (es-ES)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` · `Agrega {song} a mi lista` · `Agrega {song} de {musician} a mi lista` |
| Browse Library | `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` · `transferir la música` |
| Go To Chapter | `Siguiente capítulo` · `Capítulo anterior` · `Ir al capítulo {chapter_number}` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` · `Asocia mi voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` · `Qué se reproduce después` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` · `Qué está sonando` · `Qué canción es esta` · `Quién canta` · `Quién canta esta canción` · `De qué álbum es` · `Qué año es` · `Cuándo se lanzó` · `Cuánto dura` · `Cuánto dura esta canción` · `Qué género es` · `Cuéntame sobre el artista` · `Háblame del artista` · `qué canción está sonando` · `cuál es el nombre de esta canción` · `quién hizo esta canción` · `qué grupo es este` · `dime el nombre del álbum` · `cuándo salió esta canción` · `qué género tiene esta canción` · `dame información de esta canción` · `dime el nombre del artista` · `de qué álbum es esta canción` |
| Play Album | `Reproduce {album}` · `Reproduce el álbum {album}` · `Reproduce álbum {album}` · `Reproduce {album} de {musician}` · `Reproduce el álbum {album} de {musician}` · `Escucha {album}` · `Escucha el álbum {album}` · `Escucha {album} de {musician}` · `Escucha el álbum {album} de {musician}` · `Pon {album}` · `Pon el álbum {album}` · `Pon {album} de {musician}` · `Quiero escuchar {album}` · `Toca {album}` · `Pon el disco {album}` · `Reproduce el disco {album}` · `Quiero oír {album}` · `Inicia {album}` · `Dame {album}` · `Suelta {album}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` · `Escucha música de {musician}` · `Escucha temas de {musician}` · `Pon {musician}` · `Pon canciones de {musician}` · `Pon música de {musician}` · `Pon temas de {musician}` · `Quiero escuchar {musician}` · `Quiero oír {musician}` · `Toca {musician}` · `Toca canciones de {musician}` · `Inicia {musician}` · `Empieza con {musician}` · `Dame {musician}` · `Pon algo de {musician}` · `Reproduce algo de {musician}` · `Dime algo de {musician}` · `Suelta canciones de {musician}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` · `Reproduce {decade} canciones` · `Reproduce {genre} de los {decade}` · `Reproduce {genre} canciones de los {decade}` · `Quiero escuchar música de los {decade}` · `Reproduce algo de los {decade}` · `Escucha música de los {decade}` · `Dame canciones de los {decade}` · `Mezcla música de los {decade}` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `Reproduce mi {media_type} favorito` · `reproduce los favoritos de {username}` · `pon los favoritos de {username}` · `escucha los favoritos de {username}` · `reproduce las canciones favoritas de {username}` · `reproduce {media_type} favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` · `Reproduce contenidos nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` · `reproduce algo {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` · `reproduce {podcast_name} podcast` · `abre el podcast {podcast_name}` · `reproduce el último episodio de {podcast_name}` · `continúa el podcast {podcast_name}` · `escucha el episodio más reciente de {podcast_name}` · `pon el último episodio del podcast {podcast_name}` · `quiero oír el podcast {podcast_name}` · `sigue con el podcast {podcast_name}` · `reanuda el podcast {podcast_name}` · `escucha la última entrega de {podcast_name}` · `pon el episodio más nuevo de {podcast_name}` · `arranca el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` · `Inicia una estación de radio` · `Sigue con música similar` · `Reproduce canciones como esta` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` · `Escucha la canción {song}` · `Escucha {song} de {musician}` · `Escucha la canción {song} de {musician}` · `Pon {song}` · `Pon la canción {song}` · `Pon {song} de {musician}` · `Quiero escuchar {song}` · `Toca {song}` · `Toca la canción {song}` · `Pon el tema {song}` · `Reproduce el tema {song}` · `Quiero oír {song}` · `Inicia {song}` · `Dame {song}` · `Suelta {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` · `Muestra canciones de {musician}` · `Muestra álbumes de {musician}` · `Lista canciones de {musician}` · `Lista álbumes de {musician}` · `Qué {query_type} tenemos de {musician}` · `Muestra {query_type} de {musician}` · `Qué canciones hay de {musician}` · `Qué álbumes hay de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` · `listar los elementos recientes` |
| Recommend | `recomienda algo` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` · `recomienda {media_type}` |
| Search Media | `Busca {query}` · `Encuentra {query}` · `Buscar {query}` · `Tienes {query}` · `Quiero encontrar {query}` · `Puedes encontrar {query}` · `Busca el contenido {query}` · `busca en mi biblioteca {query}` · `buscar en mi colección {query}` · `encuentra {query} en mi biblioteca` · `tengo {query} en mi colección` · `existe {query} en la biblioteca` · `busca {query} en mi mediateca` · `quiero buscar {query} en la biblioteca` · `consulta si existe {query}` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="es-mx"></a>Spanish - Mexico (es-MX)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` · `Agrega {song} a mi lista` · `Agrega {song} de {musician} a mi lista` |
| Browse Library | `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` |
| Go To Chapter | `Siguiente capítulo` · `Capítulo anterior` · `Ir al capítulo {chapter_number}` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` · `Asocia mi voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` · `Qué se reproduce después` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` · `Qué está sonando` · `Qué canción es esta` · `Quién canta` · `Quién canta esta canción` · `De qué álbum es` · `Qué año es` · `Cuándo se lanzó` · `Cuánto dura` · `Cuánto dura esta canción` · `Qué género es` · `Cuéntame sobre el artista` · `Háblame del artista` · `qué canción está sonando` · `cuál es el nombre de esta canción` · `quién hizo esta canción` · `qué grupo es este` · `dime el nombre del álbum` · `cuándo salió esta canción` · `qué género tiene esta canción` · `dame información de esta canción` · `dime el nombre del artista` · `de qué álbum es esta canción` |
| Play Album | `Reproduce {album}` · `Reproduce el álbum {album}` · `Reproduce álbum {album}` · `Reproduce {album} de {musician}` · `Reproduce el álbum {album} de {musician}` · `Escucha {album}` · `Escucha el álbum {album}` · `Escucha {album} de {musician}` · `Escucha el álbum {album} de {musician}` · `Pon {album}` · `Pon el álbum {album}` · `Pon {album} de {musician}` · `Quiero escuchar {album}` · `Toca {album}` · `Pon el disco {album}` · `Reproduce el disco {album}` · `Quiero oír {album}` · `Inicia {album}` · `Dame {album}` · `Suelta {album}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` · `Escucha música de {musician}` · `Escucha temas de {musician}` · `Pon {musician}` · `Pon canciones de {musician}` · `Pon música de {musician}` · `Pon temas de {musician}` · `Quiero escuchar {musician}` · `Quiero oír {musician}` · `Toca {musician}` · `Toca canciones de {musician}` · `Inicia {musician}` · `Empieza con {musician}` · `Dame {musician}` · `Pon algo de {musician}` · `Reproduce algo de {musician}` · `Dime algo de {musician}` · `Suelta canciones de {musician}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` · `Reproduce {decade} canciones` · `Reproduce {genre} de los {decade}` · `Reproduce {genre} canciones de los {decade}` · `Quiero escuchar música de los {decade}` · `Reproduce algo de los {decade}` · `Escucha música de los {decade}` · `Dame canciones de los {decade}` · `Mezcla música de los {decade}` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `Reproduce mi {media_type} favorito` · `reproduce los favoritos de {username}` · `pon los favoritos de {username}` · `escucha los favoritos de {username}` · `reproduce las canciones favoritas de {username}` · `reproduce {media_type} favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` · `Reproduce contenidos nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` · `reproduce algo {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` · `reproduce {podcast_name} podcast` · `abre el podcast {podcast_name}` · `reproduce el último episodio de {podcast_name}` · `continúa el podcast {podcast_name}` · `escucha el episodio más reciente de {podcast_name}` · `pon el último episodio del podcast {podcast_name}` · `quiero oír el podcast {podcast_name}` · `sigue con el podcast {podcast_name}` · `reanuda el podcast {podcast_name}` · `escucha la última entrega de {podcast_name}` · `pon el episodio más nuevo de {podcast_name}` · `arranca el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` · `Inicia una estación de radio` · `Sigue con música similar` · `Reproduce canciones como esta` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` · `Escucha la canción {song}` · `Escucha {song} de {musician}` · `Escucha la canción {song} de {musician}` · `Pon {song}` · `Pon la canción {song}` · `Pon {song} de {musician}` · `Quiero escuchar {song}` · `Toca {song}` · `Toca la canción {song}` · `Pon el tema {song}` · `Reproduce el tema {song}` · `Quiero oír {song}` · `Inicia {song}` · `Dame {song}` · `Suelta {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` · `Muestra canciones de {musician}` · `Muestra álbumes de {musician}` · `Lista canciones de {musician}` · `Lista álbumes de {musician}` · `Qué {query_type} tenemos de {musician}` · `Muestra {query_type} de {musician}` · `Qué canciones hay de {musician}` · `Qué álbumes hay de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` · `listar los elementos recientes` |
| Recommend | `recomienda algo` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` · `recomienda {media_type}` |
| Search Media | `Busca {query}` · `Encuentra {query}` · `Buscar {query}` · `Tienes {query}` · `Quiero encontrar {query}` · `Puedes encontrar {query}` · `Busca el contenido {query}` · `busca en mi biblioteca {query}` · `buscar en mi colección {query}` · `encuentra {query} en mi biblioteca` · `tengo {query} en mi colección` · `existe {query} en la biblioteca` · `busca {query} en mi mediateca` · `quiero buscar {query} en la biblioteca` · `consulta si existe {query}` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="es-us"></a>Spanish - US (es-US)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Añade {song} a mi cola` · `Añade {song} a la cola` · `Añade {song} de {musician} a mi cola` · `Añade {song} de {musician} a la cola` · `Pon {song} en la cola` · `Pon {song} de {musician} en la cola` · `Agrega {song} a mi lista` · `Agrega {song} de {musician} a mi lista` |
| Browse Library | `explorar {browse_category}` · `muéstrame {browse_category}` · `lista {browse_category}` |
| Clear Queue | `Borra mi cola` · `Borra la cola` · `Vacía mi cola` · `Vacía la cola` · `Quita todo de mi cola` · `Limpia mi lista` |
| Continue Watching | `Continuar viendo` · `Continuar escuchando` · `Seguir donde lo dejé` · `Continuar` |
| Follow Me | `sígueme` · `continuar reproduciendo` · `seguir escuchando` · `retomar reproducción` |
| Go To Chapter | `Siguiente capítulo` · `Capítulo anterior` · `Ir al capítulo {chapter_number}` · `Saltar al capítulo {chapter_number}` · `Avanzar un capítulo` · `Retroceder un capítulo` |
| In Progress Media List | `qué estoy escuchando` · `qué estoy viendo` · `qué está en progreso` · `muestra mi progreso` · `qué he empezado` |
| Learn My Voice | `Aprende mi voz` · `Reconoce mi voz` · `Vincula mi voz` · `Esta es mi voz` · `Reconóceme` · `Configura mi perfil de voz` · `Asocia mi voz` |
| List Queue | `Qué hay en mi cola` · `Qué hay en la cola` · `Qué viene después` · `Qué sigue` · `Muestra mi cola` · `Lista mi cola` · `Qué se reproduce después` |
| Loop Song On | `Repite esta canción` · `Repite esta canción siempre` · `Repetir la canción` · `Repetir esta canción` · `Repetir esta canción siempre` |
| Mark Favorite | `Me gusta` · `Me gusta el vídeo` · `Me gusta la canción` · `Me gusta la música` · `Añade el vídeo a mis favoritos` · `Añade la canción a mis favoritos` |
| Media Info | `Cuál es el {media_info_type}` · `Cuál es la {media_info_type}` · `Dime el {media_info_type}` · `Dime la {media_info_type}` · `Qué {media_info_type} es` · `Qué está sonando` · `Qué canción es esta` · `Quién canta` · `Quién canta esta canción` · `De qué álbum es` · `Qué año es` · `Cuándo se lanzó` · `Cuánto dura` · `Cuánto dura esta canción` · `Qué género es` · `Cuéntame sobre el artista` · `Háblame del artista` · `qué canción está sonando` · `cuál es el nombre de esta canción` · `quién hizo esta canción` · `qué grupo es este` · `dime el nombre del álbum` · `cuándo salió esta canción` · `qué género tiene esta canción` · `dame información de esta canción` · `dime el nombre del artista` · `de qué álbum es esta canción` |
| Play Album | `Reproduce {album}` · `Reproduce el álbum {album}` · `Reproduce álbum {album}` · `Reproduce {album} de {musician}` · `Reproduce el álbum {album} de {musician}` · `Escucha {album}` · `Escucha el álbum {album}` · `Escucha {album} de {musician}` · `Escucha el álbum {album} de {musician}` · `Pon {album}` · `Pon el álbum {album}` · `Pon {album} de {musician}` · `Quiero escuchar {album}` · `Toca {album}` · `Pon el disco {album}` · `Reproduce el disco {album}` · `Quiero oír {album}` · `Inicia {album}` · `Dame {album}` · `Suelta {album}` |
| Play Artist Songs | `Reproduce canciones de {musician}` · `Reproduce música de {musician}` · `Reproduce temas de {musician}` · `Reproduce {musician}` · `Escucha {musician}` · `Escucha canciones de {musician}` · `Escucha música de {musician}` · `Escucha temas de {musician}` · `Pon {musician}` · `Pon canciones de {musician}` · `Pon música de {musician}` · `Pon temas de {musician}` · `Quiero escuchar {musician}` · `Quiero oír {musician}` · `Toca {musician}` · `Toca canciones de {musician}` · `Inicia {musician}` · `Empieza con {musician}` · `Dame {musician}` · `Pon algo de {musician}` · `Reproduce algo de {musician}` · `Dime algo de {musician}` · `Suelta canciones de {musician}` |
| Play By Decade | `Reproduce canciones de los {decade}` · `Reproduce temas de los {decade}` · `Reproduce éxitos de los {decade}` · `Reproduce música de los {decade}` · `Reproduce {decade} éxitos` · `Reproduce {decade} canciones` · `Reproduce {genre} de los {decade}` · `Reproduce {genre} canciones de los {decade}` · `Quiero escuchar música de los {decade}` · `Reproduce algo de los {decade}` · `Escucha música de los {decade}` · `Dame canciones de los {decade}` · `Mezcla música de los {decade}` |
| Play By Genre | `Reproduce música {genre}` · `Reproduce {genre}` · `Pon {genre}` · `Quiero escuchar {genre}` · `Reproduce canciones de {genre}` |
| Play Channel | `Pon el canal {channel}` · `Pon la radio {channel}` |
| Play Episode | `reproduce la temporada {season_number} episodio {episode_number} de {series_name}` · `reproduce {series_name} temporada {season_number} episodio {episode_number}` · `ver temporada {season_number} episodio {episode_number} de {series_name}` |
| Play Favorites | `Reproduce mis {media_type} favoritos` · `Reproduce mis favoritos` · `Reproduce mi {media_type} favorito` · `reproduce los favoritos de {username}` · `pon los favoritos de {username}` · `escucha los favoritos de {username}` · `reproduce las canciones favoritas de {username}` · `reproduce {media_type} favoritos de {username}` |
| Play Last Added | `Reproduce los últimos {media_type} añadidos` · `Reproduce {media_type} añadidos recientemente` · `Reproduce {media_type} nuevos` · `Reproduce contenidos nuevos` |
| Play Mood Music | `reproduce música {mood}` · `reproduce algo {mood}` · `quiero música {mood}` · `reproduce algo {mood}` |
| Play Next | `Reproduce {song} a continuación` · `Reproduce {song} de {musician} a continuación` · `Reproduce {song} después` · `Reproduce {song} de {musician} después` · `Quiero escuchar {song} a continuación` · `Pon {song} como siguiente` |
| Play Playlist | `Reproduce la lista de reproducción {playlist}` · `Reproduce mi lista de reproducción {playlist}` |
| Play Podcast | `reproduce el podcast {podcast_name}` · `escucha el podcast {podcast_name}` · `pon el podcast {podcast_name}` · `reproduce podcast {podcast_name}` · `escucha el último episodio de {podcast_name}` · `quiero escuchar el podcast {podcast_name}` · `reproduce {podcast_name} podcast` · `abre el podcast {podcast_name}` · `reproduce el último episodio de {podcast_name}` · `continúa el podcast {podcast_name}` · `escucha el episodio más reciente de {podcast_name}` · `pon el último episodio del podcast {podcast_name}` · `quiero oír el podcast {podcast_name}` · `sigue con el podcast {podcast_name}` · `reanuda el podcast {podcast_name}` · `escucha la última entrega de {podcast_name}` · `pon el episodio más nuevo de {podcast_name}` · `arranca el podcast {podcast_name}` |
| Play Radio | `Reproduce radio` · `Inicia radio` · `Reproduce modo radio` · `Reproduce música similar` · `Reproduce canciones similares` · `Inicia una estación de radio` · `Sigue con música similar` · `Reproduce canciones como esta` |
| Play Random | `Reproduce {media_type} aleatoria` · `Reproduce {media_type} al azar` · `Pon {media_type} aleatoria` · `Reproduce algo aleatorio` · `Reproduce {media_type} aleatoria de {genre}` · `Sorpréndeme con {media_type}` |
| Play Song | `Reproduce {song}` · `Reproduce la canción {song}` · `Reproduce canción {song}` · `Reproduce {song} de {musician}` · `Reproduce la canción {song} de {musician}` · `Escucha {song}` · `Escucha la canción {song}` · `Escucha {song} de {musician}` · `Escucha la canción {song} de {musician}` · `Pon {song}` · `Pon la canción {song}` · `Pon {song} de {musician}` · `Quiero escuchar {song}` · `Toca {song}` · `Toca la canción {song}` · `Pon el tema {song}` · `Reproduce el tema {song}` · `Quiero oír {song}` · `Inicia {song}` · `Dame {song}` · `Suelta {song}` |
| Play Video | `Reproduce el vídeo {title}` · `Mete el vídeo {title}` · `Pon el vídeo {title}` · `Ver el vídeo {title}` · `Quiero ver el vídeo {title}` · `Reproduce la película {title}` |
| Query Artist Library | `Qué canciones tenemos de {musician}` · `Qué temas tenemos de {musician}` · `Qué álbumes tenemos de {musician}` · `Qué discos tenemos de {musician}` · `Qué tenemos de {musician}` · `Muestra canciones de {musician}` · `Muestra álbumes de {musician}` · `Lista canciones de {musician}` · `Lista álbumes de {musician}` · `Qué {query_type} tenemos de {musician}` · `Muestra {query_type} de {musician}` · `Qué canciones hay de {musician}` · `Qué álbumes hay de {musician}` |
| Query Recently Added | `qué hay de nuevo` · `qué se añadió recientemente` · `muéstrame las novedades` · `hay algo nuevo` · `cuáles son los últimos añadidos` · `qué hay de nuevo en mi biblioteca` · `listar los elementos recientes` |
| Recommend | `recomienda algo` · `recomienda música` · `recomienda una película` · `sugiere algo` · `reproduce algo que me guste` · `recomienda {media_type}` |
| Search Media | `Busca {query}` · `Encuentra {query}` · `Buscar {query}` · `Tienes {query}` · `Quiero encontrar {query}` · `Puedes encontrar {query}` · `Busca el contenido {query}` · `busca en mi biblioteca {query}` · `buscar en mi colección {query}` · `encuentra {query} en mi biblioteca` · `tengo {query} en mi colección` · `existe {query} en la biblioteca` · `busca {query} en mi mediateca` · `quiero buscar {query} en la biblioteca` · `consulta si existe {query}` |
| Sleep Timer | `detener en {duration_minutes} minutos` · `temporizador {duration_minutes} minutos` · `parar después de {duration_minutes} minutos` · `apagar en {duration_minutes} minutos` |
| Turn Radio Off | `Desactiva el modo radio` · `Apaga el modo radio` · `Modo radio apagado` · `Desactiva la radio` · `Apaga la radio` · `Detén el modo radio` |
| Turn Radio On | `Activa el modo radio` · `Enciende el modo radio` · `Modo radio encendido` · `Activa la radio` · `Enciende la radio` |
| Unmark Favorite | `No me gusta esto` · `No me gusta el vídeo` · `No me gusta la canción` · `No me gusta la música` · `Quita el vídeo de mis favoritos` · `Quita la canción de mis favoritos` |
| Who Am I | `Quién soy` · `Qué cuenta es esta` · `Qué cuenta estoy usando` · `Quién está hablando` · `Qué perfil está activo` · `Estoy reconocido` |

### <a id="fr-ca"></a>French - Canada (fr-CA)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Ajoute {song} à ma file d'attente` · `Ajoute {song} à la file d'attente` · `Ajoute {song} de {musician} à ma file d'attente` · `Ajoute {song} de {musician} à la file d'attente` · `Mets {song} dans la file d'attente` · `Mets {song} de {musician} dans la file d'attente` · `Ajoute {song} à ma liste` · `Ajoute {song} de {musician} à ma liste` |
| Browse Library | `parcourir {browse_category}` · `montre-moi {browse_category}` · `lister {browse_category}` |
| Clear Queue | `Efface ma file d'attente` · `Efface la file d'attente` · `Vide ma file d'attente` · `Vide la file d'attente` · `Supprime tout de ma file d'attente` · `Efface ma liste` |
| Continue Watching | `Continuer à regarder` · `Continuer à écouter` · `Reprendre où j'en étais` · `Continuer` |
| Follow Me | `suis-moi` · `continuer la lecture` · `reprendre la lecture` · `transférer la lecture` |
| Go To Chapter | `Chapitre suivant` · `Chapitre précédent` · `Aller au chapitre {chapter_number}` · `Sauter au chapitre {chapter_number}` · `Avancer d'un chapitre` · `Reculer d'un chapitre` |
| In Progress Media List | `qu'est-ce que j'écoute` · `qu'est-ce que je regarde` · `quoi en cours` · `afficher ma progression` · `qu'ai-je commencé` |
| Learn My Voice | `Apprends ma voix` · `Reconnais ma voix` · `Lie ma voix` · `C'est ma voix` · `Reconnais-moi` · `Configure mon profil vocal` · `Associe ma voix` |
| List Queue | `Qu'est-ce qu'il y a dans ma file d'attente` · `Qu'est-ce qu'il y a dans la file d'attente` · `Qu'est-ce qui vient après` · `Qu'est-ce qui suit` · `Affiche ma file d'attente` · `Liste ma file d'attente` · `Qu'est-ce qui passe ensuite` |
| Loop All Off | `Désactive la boucle` · `Désactive la répétition` |
| Loop All On | `Active la boucle` · `Active la répétition` |
| Mark Favorite | `J'aime bien` · `J'aime cette vidéo` · `J'aime cette chanson` · `J'aime cette musique` · `Ajoute la vidéo aux favoris` · `Ajoute la chanson aux favoris` |
| Media Info | `Quel est le {media_info_type}` · `Quelle est la {media_info_type}` · `Dis-moi le {media_info_type}` · `Dis-moi la {media_info_type}` · `Quel {media_info_type} est-ce` · `Quelle {media_info_type} est-ce` · `Qu'est-ce qui joue` · `Quelle est cette chanson` · `Qui chante` · `Qui chante cette chanson` · `De quel album` · `De quelle année` · `Quand est-ce sorti` · `Combien de temps` · `Combien de temps dure cette chanson` · `Quel genre est-ce` · `Parle-moi de l'artiste` · `Raconte-moi l'artiste` · `quelle chanson est en train de jouer` · `quel est le nom de cette chanson` · `qui a fait cette chanson` · `quel est le nom de l'album` · `quand est sortie cette chanson` · `quel est le genre de cette musique` · `donne-moi des infos sur ce morceau` · `dis-moi le nom de l'artiste` · `de quel album vient cette chanson` · `qui est l'artiste de ce morceau` |
| Play Album | `Lis {album}` · `Lis l'album {album}` · `Lis album {album}` · `Lis {album} de {musician}` · `Lis l'album {album} de {musician}` · `Écoute {album}` · `Écoute l'album {album}` · `Écoute {album} de {musician}` · `Écoute l'album {album} de {musician}` · `Mets {album}` · `Mets l'album {album}` · `Mets {album} de {musician}` · `Je veux écouter {album}` · `Lance {album}` · `Lance l'album {album}` · `Passe {album}` · `Diffuse {album}` · `Je veux entendre {album}` · `Mets le disque {album}` · `Commence {album}` |
| Play Artist Songs | `Lis les chansons de {musician}` · `Lis la musique de {musician}` · `Lis les titres de {musician}` · `Lis les morceaux de {musician}` · `Lis {musician}` · `Écoute {musician}` · `Écoute les chansons de {musician}` · `Écoute la musique de {musician}` · `Écoute les titres de {musician}` · `Mets {musician}` · `Mets les chansons de {musician}` · `Mets la musique de {musician}` · `Je veux écouter {musician}` · `Je veux entendre {musician}` · `Lance {musician}` · `Lance les chansons de {musician}` · `Lance la musique de {musician}` · `Diffuse {musician}` · `Diffuse les chansons de {musician}` · `Mets des titres de {musician}` · `Passe {musician}` · `Passe les chansons de {musician}` · `Commence {musician}` |
| Play By Decade | `Lis des chansons des {decade}` · `Lis des titres des {decade}` · `Lis des succès des {decade}` · `Lis de la musique des {decade}` · `Lis les succès des {decade}` · `Lis les chansons des {decade}` · `Lis du {genre} des {decade}` · `Lis des chansons {genre} des {decade}` · `Je veux entendre de la musique des {decade}` · `Lis quelque chose des {decade}` · `Écoute de la musique des {decade}` · `Donne-moi des chansons des {decade}` · `Mélange de la musique des {decade}` |
| Play By Genre | `Joue de la musique {genre}` · `Joue du {genre}` · `Je veux écouter du {genre}` · `Mets du {genre}` · `Joue des chansons {genre}` |
| Play Channel | `Chaîne {channel}` · `Lis la radio {channel}` · `Radio {channel}` |
| Play Episode | `joue la saison {season_number} épisode {episode_number} de {series_name}` · `joue {series_name} saison {season_number} épisode {episode_number}` · `regarde la saison {season_number} épisode {episode_number} de {series_name}` |
| Play Favorites | `Lis mes {media_type} préférés` · `Lis mes favoris` · `joue les favoris de {username}` · `mets les favoris de {username}` · `écoute les favoris de {username}` · `joue les chansons favorites de {username}` · `joue {media_type} favoris de {username}` |
| Play Last Added | `Lis les derniers {media_type} ajoutés` · `Lis les nouveautés {media_type}` · `Lis les nouveaux médias` |
| Play Mood Music | `joue de la musique {mood}` · `joue quelque chose de {mood}` · `je veux de la musique {mood}` · `joue-moi quelque chose de {mood}` |
| Play Next | `Lis {song} ensuite` · `Lis {song} de {musician} ensuite` · `Lis {song} après` · `Lis {song} de {musician} après` · `Je veux entendre {song} ensuite` · `Passe {song} ensuite` |
| Play Playlist | `Lis la playlist {playlist}` · `Lis ma playlist {playlist}` |
| Play Podcast | `joue le podcast {podcast_name}` · `écoute le podcast {podcast_name}` · `lance le podcast {podcast_name}` · `joue podcast {podcast_name}` · `écoute le dernier épisode de {podcast_name}` · `je veux écouter le podcast {podcast_name}` · `joue {podcast_name} podcast` · `ouvre le podcast {podcast_name}` · `joue le dernier épisode de {podcast_name}` · `continue le podcast {podcast_name}` · `écoute l'épisode le plus récent de {podcast_name}` · `reprends le podcast {podcast_name}` · `mets le dernier épisode du podcast {podcast_name}` · `je veux entendre le podcast {podcast_name}` · `reprends le podcast {podcast_name} où j'en étais` · `écoute le dernier épisode du podcast {podcast_name}` · `lance le dernier épisode de {podcast_name}` · `fais jouer le podcast {podcast_name}` |
| Play Radio | `Lis la radio` · `Démarre la radio` · `Lis le mode radio` · `Lis de la musique similaire` · `Lis des chansons similaires` · `Démarre une station de radio` · `Continue avec de la musique similaire` · `Lis des chansons comme celle-ci` |
| Play Random | `Joue un {media_type} aléatoire` · `Joue des {media_type} au hasard` · `Lance un {media_type} aléatoire` · `Joue quelque chose au hasard` · `Joue un {media_type} aléatoire de {genre}` · `Surprends-moi avec des {media_type}` |
| Play Song | `Lis {song}` · `Lis la chanson {song}` · `Lis chanson {song}` · `Lis {song} de {musician}` · `Lis la chanson {song} de {musician}` · `Écoute {song}` · `Écoute la chanson {song}` · `Écoute {song} de {musician}` · `Écoute la chanson {song} de {musician}` · `Mets {song}` · `Mets la chanson {song}` · `Mets {song} de {musician}` · `Je veux écouter {song}` · `Lance {song}` · `Lance la chanson {song}` · `Passe {song}` · `Diffuse {song}` · `Je veux entendre {song}` · `Mets le morceau {song}` · `Lis le titre {song}` · `Commence {song}` |
| Play Video | `Lis la vidéo {title}` |
| Query Artist Library | `Quelles chansons avons-nous de {musician}` · `Quels titres avons-nous de {musician}` · `Quels albums avons-nous de {musician}` · `Quels disques avons-nous de {musician}` · `Qu'avons-nous de {musician}` · `Affiche les chansons de {musician}` · `Affiche les albums de {musician}` · `Liste les chansons de {musician}` · `Liste les albums de {musician}` · `Quels {query_type} avons-nous de {musician}` · `Affiche {query_type} de {musician}` · `Quelles chansons y a-t-il de {musician}` · `Quels albums y a-t-il de {musician}` |
| Query Recently Added | `quoi de neuf` · `qu'est-ce qui a été ajouté récemment` · `montre-moi les nouveautés` · `y a-t-il du nouveau` · `quels sont les derniers ajouts` · `quoi de neuf dans ma bibliothèque` · `lister les éléments récents` |
| Recommend | `recommande quelque chose` · `recommande de la musique` · `recommande un film` · `suggère quelque chose` · `joue quelque chose que j'aimerais` · `recommande {media_type}` |
| Repeat Single On | `Répète la chanson` · `Répète le morceau` · `Répète la vidéo` · `Répète` |
| Search Media | `Cherche {query}` · `Trouve {query}` · `Recherche {query}` · `Est-ce que tu as {query}` · `Je veux trouver {query}` · `Peux-tu trouver {query}` · `Cherche le contenu {query}` · `cherche dans ma bibliothèque {query}` · `recherche dans ma médiathèque {query}` · `trouve {query} dans ma bibliothèque` · `est-ce que {query} est dans ma bibliothèque` · `j'ai {query} dans ma collection` · `cherche {query} dans ma collection` · `recherche {query} dans ma médiathèque` · `vérifie si {query} est disponible` |
| Sleep Timer | `arrêter dans {duration_minutes} minutes` · `minuterie {duration_minutes} minutes` · `arrêter après {duration_minutes} minutes` · `éteindre dans {duration_minutes} minutes` |
| Turn Radio Off | `Désactive le mode radio` · `Éteins le mode radio` · `Mode radio désactivé` · `Désactive la radio` · `Éteins la radio` · `Arrête le mode radio` |
| Turn Radio On | `Active le mode radio` · `Allume le mode radio` · `Mode radio activé` · `Active la radio` · `Allume la radio` |
| Unmark Favorite | `Je n'aime pas ça` · `Je n'aime pas cette vidéo` · `Je n'aime pas cette chanson` · `Je n'aime pas cette musique` · `Retire la vidéo des favoris` · `Retire la chanson des favoris` |
| Who Am I | `Qui suis-je` · `Quel compte est-ce` · `Quel compte j'utilise` · `Qui parle` · `Quel profil est actif` · `Suis-je reconnu` |

### <a id="fr-fr"></a>French - France (fr-FR)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Ajoute {song} à ma file d'attente` · `Ajoute {song} à la file d'attente` · `Ajoute {song} de {musician} à ma file d'attente` · `Ajoute {song} de {musician} à la file d'attente` · `Mets {song} dans la file d'attente` · `Mets {song} de {musician} dans la file d'attente` · `Ajoute {song} à ma liste` · `Ajoute {song} de {musician} à ma liste` |
| Browse Library | `parcourir {browse_category}` · `montre-moi {browse_category}` · `lister {browse_category}` |
| Clear Queue | `Efface ma file d'attente` · `Efface la file d'attente` · `Vide ma file d'attente` · `Vide la file d'attente` · `Supprime tout de ma file d'attente` · `Efface ma liste` |
| Continue Watching | `Continuer à regarder` · `Continuer à écouter` · `Reprendre où j'en étais` · `Continuer` |
| Follow Me | `suis-moi` · `continuer la lecture` · `reprendre la lecture` · `transférer la lecture` · `reprendre où j'en étais` |
| Go To Chapter | `Chapitre suivant` · `Chapitre précédent` · `Aller au chapitre {chapter_number}` · `Sauter au chapitre {chapter_number}` · `Avancer d'un chapitre` · `Reculer d'un chapitre` |
| In Progress Media List | `qu'est-ce que j'écoute` · `qu'est-ce que je regarde` · `quoi en cours` · `afficher ma progression` · `qu'ai-je commencé` |
| Learn My Voice | `Apprends ma voix` · `Reconnais ma voix` · `Lie ma voix` · `C'est ma voix` · `Reconnais-moi` · `Configure mon profil vocal` · `Associe ma voix` |
| List Queue | `Qu'est-ce qu'il y a dans ma file d'attente` · `Qu'est-ce qu'il y a dans la file d'attente` · `Qu'est-ce qui vient après` · `Qu'est-ce qui suit` · `Affiche ma file d'attente` · `Liste ma file d'attente` · `Qu'est-ce qui passe ensuite` |
| Loop All Off | `Désactive la boucle` · `Désactive la répétition` |
| Loop All On | `Active la boucle` · `Active la répétition` |
| Mark Favorite | `J'aime bien` · `J'aime cette vidéo` · `J'aime cette chanson` · `J'aime cette musique` · `Ajoute la vidéo aux favoris` · `Ajoute la chanson aux favoris` |
| Media Info | `Quel est le {media_info_type}` · `Quelle est la {media_info_type}` · `Dis-moi le {media_info_type}` · `Dis-moi la {media_info_type}` · `Quel {media_info_type} est-ce` · `Quelle {media_info_type} est-ce` · `Qu'est-ce qui joue` · `Quelle est cette chanson` · `Qui chante` · `Qui chante cette chanson` · `De quel album` · `De quelle année` · `Quand est-ce sorti` · `Combien de temps` · `Combien de temps dure cette chanson` · `Quel genre est-ce` · `Parle-moi de l'artiste` · `Raconte-moi l'artiste` · `quelle chanson est en train de jouer` · `quel est le nom de cette chanson` · `qui a fait cette chanson` · `quel est le nom de l'album` · `quand est sortie cette chanson` · `quel est le genre de cette musique` · `donne-moi des infos sur ce morceau` · `dis-moi le nom de l'artiste` · `de quel album vient cette chanson` · `qui est l'artiste de ce morceau` |
| Play Album | `Lis {album}` · `Lis l'album {album}` · `Lis album {album}` · `Lis {album} de {musician}` · `Lis l'album {album} de {musician}` · `Écoute {album}` · `Écoute l'album {album}` · `Écoute {album} de {musician}` · `Écoute l'album {album} de {musician}` · `Mets {album}` · `Mets l'album {album}` · `Mets {album} de {musician}` · `Je veux écouter {album}` · `Lance {album}` · `Lance l'album {album}` · `Passe {album}` · `Diffuse {album}` · `Je veux entendre {album}` · `Mets le disque {album}` · `Commence {album}` |
| Play Artist Songs | `Lis les chansons de {musician}` · `Lis la musique de {musician}` · `Lis les titres de {musician}` · `Lis les morceaux de {musician}` · `Lis {musician}` · `Écoute {musician}` · `Écoute les chansons de {musician}` · `Écoute la musique de {musician}` · `Écoute les titres de {musician}` · `Mets {musician}` · `Mets les chansons de {musician}` · `Mets la musique de {musician}` · `Je veux écouter {musician}` · `Je veux entendre {musician}` · `Lance {musician}` · `Lance les chansons de {musician}` · `Lance la musique de {musician}` · `Diffuse {musician}` · `Diffuse les chansons de {musician}` · `Mets des titres de {musician}` · `Passe {musician}` · `Passe les chansons de {musician}` · `Commence {musician}` |
| Play By Decade | `Lis des chansons des {decade}` · `Lis des titres des {decade}` · `Lis des succès des {decade}` · `Lis de la musique des {decade}` · `Lis les succès des {decade}` · `Lis les chansons des {decade}` · `Lis du {genre} des {decade}` · `Lis des chansons {genre} des {decade}` · `Je veux entendre de la musique des {decade}` · `Lis quelque chose des {decade}` · `Écoute de la musique des {decade}` · `Donne-moi des chansons des {decade}` · `Mélange de la musique des {decade}` |
| Play By Genre | `Joue de la musique {genre}` · `Joue du {genre}` · `Je veux écouter du {genre}` · `Mets du {genre}` · `Joue des chansons {genre}` |
| Play Channel | `Chaîne {channel}` · `Lis la radio {channel}` · `Radio {channel}` |
| Play Episode | `joue la saison {season_number} épisode {episode_number} de {series_name}` · `joue {series_name} saison {season_number} épisode {episode_number}` · `regarde la saison {season_number} épisode {episode_number} de {series_name}` |
| Play Favorites | `Lis mes {media_type} préférés` · `Lis mes favoris` · `joue les favoris de {username}` · `mets les favoris de {username}` · `écoute les favoris de {username}` · `joue les chansons favorites de {username}` · `joue {media_type} favoris de {username}` |
| Play Last Added | `Lis les derniers {media_type} ajoutés` · `Lis les nouveautés {media_type}` · `Lis les nouveaux médias` |
| Play Mood Music | `joue de la musique {mood}` · `joue quelque chose de {mood}` · `je veux de la musique {mood}` · `joue-moi quelque chose de {mood}` |
| Play Next | `Lis {song} ensuite` · `Lis {song} de {musician} ensuite` · `Lis {song} après` · `Lis {song} de {musician} après` · `Je veux entendre {song} ensuite` · `Passe {song} ensuite` |
| Play Playlist | `Lis la playlist {playlist}` · `Lis ma playlist {playlist}` |
| Play Podcast | `joue le podcast {podcast_name}` · `écoute le podcast {podcast_name}` · `lance le podcast {podcast_name}` · `joue podcast {podcast_name}` · `écoute le dernier épisode de {podcast_name}` · `je veux écouter le podcast {podcast_name}` · `joue {podcast_name} podcast` · `ouvre le podcast {podcast_name}` · `joue le dernier épisode de {podcast_name}` · `continue le podcast {podcast_name}` · `écoute l'épisode le plus récent de {podcast_name}` · `reprends le podcast {podcast_name}` · `mets le dernier épisode du podcast {podcast_name}` · `je veux entendre le podcast {podcast_name}` · `reprends le podcast {podcast_name} où j'en étais` · `écoute le dernier épisode du podcast {podcast_name}` · `lance le dernier épisode de {podcast_name}` · `fais jouer le podcast {podcast_name}` |
| Play Radio | `Lis la radio` · `Démarre la radio` · `Lis le mode radio` · `Lis de la musique similaire` · `Lis des chansons similaires` · `Démarre une station de radio` · `Continue avec de la musique similaire` · `Lis des chansons comme celle-ci` |
| Play Random | `Joue un {media_type} aléatoire` · `Joue des {media_type} au hasard` · `Lance un {media_type} aléatoire` · `Joue quelque chose au hasard` · `Joue un {media_type} aléatoire de {genre}` · `Surprends-moi avec des {media_type}` |
| Play Song | `Lis {song}` · `Lis la chanson {song}` · `Lis chanson {song}` · `Lis {song} de {musician}` · `Lis la chanson {song} de {musician}` · `Écoute {song}` · `Écoute la chanson {song}` · `Écoute {song} de {musician}` · `Écoute la chanson {song} de {musician}` · `Mets {song}` · `Mets la chanson {song}` · `Mets {song} de {musician}` · `Je veux écouter {song}` · `Lance {song}` · `Lance la chanson {song}` · `Passe {song}` · `Diffuse {song}` · `Je veux entendre {song}` · `Mets le morceau {song}` · `Lis le titre {song}` · `Commence {song}` |
| Play Video | `Lis la vidéo {title}` |
| Query Artist Library | `Quelles chansons avons-nous de {musician}` · `Quels titres avons-nous de {musician}` · `Quels albums avons-nous de {musician}` · `Quels disques avons-nous de {musician}` · `Qu'avons-nous de {musician}` · `Affiche les chansons de {musician}` · `Affiche les albums de {musician}` · `Liste les chansons de {musician}` · `Liste les albums de {musician}` · `Quels {query_type} avons-nous de {musician}` · `Affiche {query_type} de {musician}` · `Quelles chansons y a-t-il de {musician}` · `Quels albums y a-t-il de {musician}` |
| Query Recently Added | `quoi de neuf` · `qu'est-ce qui a été ajouté récemment` · `montre-moi les nouveautés` · `y a-t-il du nouveau` · `quels sont les derniers ajouts` · `quoi de neuf dans ma bibliothèque` · `lister les éléments récents` |
| Recommend | `recommande quelque chose` · `recommande de la musique` · `recommande un film` · `suggère quelque chose` · `joue quelque chose que j'aimerais` · `recommande {media_type}` |
| Repeat Single On | `Répète la chanson` · `Répète le morceau` · `Répète la vidéo` · `Répète` |
| Search Media | `Cherche {query}` · `Trouve {query}` · `Recherche {query}` · `Est-ce que tu as {query}` · `Je veux trouver {query}` · `Peux-tu trouver {query}` · `Cherche le contenu {query}` · `cherche dans ma bibliothèque {query}` · `recherche dans ma médiathèque {query}` · `trouve {query} dans ma bibliothèque` · `est-ce que {query} est dans ma bibliothèque` · `j'ai {query} dans ma collection` · `cherche {query} dans ma collection` · `recherche {query} dans ma médiathèque` · `vérifie si {query} est disponible` |
| Sleep Timer | `arrêter dans {duration_minutes} minutes` · `minuterie {duration_minutes} minutes` · `arrêter après {duration_minutes} minutes` · `éteindre dans {duration_minutes} minutes` |
| Turn Radio Off | `Désactive le mode radio` · `Éteins le mode radio` · `Mode radio désactivé` · `Désactive la radio` · `Éteins la radio` · `Arrête le mode radio` |
| Turn Radio On | `Active le mode radio` · `Allume le mode radio` · `Mode radio activé` · `Active la radio` · `Allume la radio` |
| Unmark Favorite | `Je n'aime pas ça` · `Je n'aime pas cette vidéo` · `Je n'aime pas cette chanson` · `Je n'aime pas cette musique` · `Retire la vidéo des favoris` · `Retire la chanson des favoris` |
| Who Am I | `Qui suis-je` · `Quel compte est-ce` · `Quel compte j'utilise` · `Qui parle` · `Quel profil est actif` · `Suis-je reconnu` |

### <a id="hi-in"></a>Hindi (hi-IN)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `{song} कतार में जोड़ो` · `{musician} का {song} कतार में जोड़ो` · `{song} कतार में डालो` · `{musician} का {song} कतार में डालो` |
| Browse Library | `{browse_category} ब्राउज़ करो` · `मुझे {browse_category} दिखाओ` · `{browse_category} की लिस्ट दो` |
| Clear Queue | `कतार साफ़ करो` · `कतार खाली करो` · `कतार से सब हटाओ` |
| Continue Watching | `देखना जारी रखो` · `सुनना जारी रखो` · `जहाँ छोड़ा था वहाँ से फिर से शुरू करो` · `मैं क्या देख रहा था` · `जारी रखो` · `जारी` |
| Follow Me | `मेरे साथ आओ` · `चलाना जारी रखो` · `जहां छोड़ा थे वहां से शुरू करो` |
| Go To Chapter | `अगला चैप्टर` · `पिछला चैप्टर` · `चैप्टर {chapter_number} पर जाओ` · `चैप्टर {chapter_number} पर स्किप करो` · `एक चैप्टर आगे जाओ` · `एक चैप्टर पीछे जाओ` · `चैप्टर स्किप करो` |
| In Progress Media List | `मैं क्या सुन रहा हूँ` · `मैं क्या देख रहा हूँ` · `क्या प्रगति पर है` · `मेरी प्रगति दिखाओ` · `मैं क्या चला रहा था` · `मेरी प्रगति पर मीडिया दिखाओ` |
| Learn My Voice | `मेरी आवाज़ सीखो` · `मेरी आवाज़ याद रखो` · `मुझे पहचानो` · `मेरी आवाज़ लिंक करो` · `यह मेरी आवाज़ है` · `मेरा वॉइस प्रोफाइल सेटअप करो` · `मेरी आवाज़ जोड़ो` |
| List Queue | `कतार में क्या है` · `कतार में क्या है` · `क्या आने वाला है` · `क्या अगला है` · `कतार दिखाओ` · `अगला क्या चलेगा` |
| Loop Song On | `इस गाने को लूप करो` · `इस गाने को हमेशा लूप करो` · `गाना दोहराओ` · `इस गाने को दोहराओ` · `इस गाने को हमेशा दोहराओ` |
| Mark Favorite | `मुझे यह पसंद है` · `मुझे वीडियो पसंद है` · `मुझे गाना पसंद है` · `मुझे म्यूज़िक पसंद है` · `मुझे यह गाना पसंद है` · `मुझे यह पसंद है` · `वीडियो को पसंदीदा में जोड़ो` · `गाने को पसंदीदा में जोड़ो` · `इसे पसंदीदा में सेव करो` · `इसे पसंदीदा करो` |
| Media Info | `गाने का नाम क्या है` · `वीडियो का नाम क्या है` · `म्यूज़िक का नाम क्या है` · `गाने का शीर्षक क्या है` · `अभी क्या चल रहा है` · `यह {media_info_type} क्या है` · `मुझे {media_info_type} बताओ` · `{media_info_type} क्या है` · `यह गाना कितना लंबा है` · `यह कौन गा रहा है` · `यह कौन कर रहा है` · `यह किस शैली का है` · `यह कब रिलीज़ हुआ` · `इस कलाकार के बारे में बताओ` · `यह किस एल्बम का है` |
| Play Album | `{album} चलाओ` · `एल्बम {album} चलाओ` · `{musician} का {album} चलाओ` · `एल्बम {album} {musician} का चलाओ` · `{album} सुनो` · `एल्बम {album} सुनो` · `{musician} का {album} सुनो` · `{album} लगाओ` · `मैं {album} सुनना चाहता हूँ` · `क्या तुम {album} चला सकते हो` · `{album} शुरू करो` |
| Play Artist Songs | `{musician} के गाने चलाओ` · `{musician} की म्यूज़िक चलाओ` · `{musician} के ट्रैक चलाओ` · `{musician} सुनो` · `{musician} के गाने सुनो` · `{musician} की म्यूज़िक सुनो` · `{musician} लगाओ` · `मैं {musician} सुनना चाहता हूँ` · `क्या तुम {musician} चला सकते हो` · `{musician} शुरू करो` · `{musician} को शफल करो` · `{musician} चलाओ` · `कुछ {musician} चलाओ` |
| Play By Decade | `{decade} के गाने चलाओ` · `{decade} के ट्रैक चलाओ` · `{decade} की म्यूज़िक चलाओ` · `{decade} हिट्स चलाओ` · `{genre} {decade} से चलाओ` · `मैं {decade} म्यूज़िक सुनना चाहता हूँ` · `{decade} से कुछ चलाओ` · `{decade} म्यूज़िक सुनो` · `{decade} हिट्स सुनो` |
| Play By Genre | `{genre} म्यूज़िक चलाओ` · `{genre} गाने चलाओ` · `{genre} म्यूज़िक चलाओ` · `मुझे {genre} म्यूज़िक चलाओ` · `मैं {genre} सुनना चाहता हूँ` · `{genre} चलाओ` · `मुझे {genre} म्यूज़िक दो` · `{genre} म्यूज़िक सुनो` |
| Play Channel | `चैनल {channel} चलाओ` · `रेडियो {channel} चलाओ` |
| Play Episode | `{series_name} सीज़न {season_number} एपिसोड {episode_number} चलाओ` · `सीज़न {season_number} एपिसोड {episode_number} {series_name} का चलाओ` · `{series_name} सीज़न {season_number} एपिसोड {episode_number} देखो` |
| Play Favorites | `मेरी पसंदीदा {media_type} चलाओ` · `मेरी पसंदीदा चलाओ` · `मेरे पसंदीदा गाने चलाओ` · `मेरी पसंदीदा म्यूज़िक चलाओ` · `{username} की पसंदीदा चलाओ` · `{username} की पसंदीदा {media_type} चलाओ` |
| Play Last Added | `आखिरी जोड़े {media_type} चलाओ` · `हाल ही में जोड़े {media_type} चलाओ` · `नया मीडिया चलाओ` · `कुछ नया चलाओ` · `{media_type} जोड़े {time_period} चलाओ` · `हाल ही में जोड़े {media_type} {time_period} चलाओ` · `नए {media_type} {time_period} चलाओ` · `{media_type} में क्या नया है` · `ताज़ा {media_type} चलाओ` · `नए {media_type} दिखाओ` |
| Play Mood Music | `{mood} म्यूज़िक चलाओ` · `कुछ {mood} चलाओ` · `{mood} गाने चलाओ` · `मुझे {mood} म्यूज़िक चाहिए` · `सुबह की म्यूज़िक चलाओ` · `शाम की म्यूज़िक चलाओ` · `वर्कआउट म्यूज़िक चलाओ` · `फोकस म्यूज़िक चलाओ` · `पार्टी म्यूज़िक चलाओ` · `रिलैक्सिंग म्यूज़िक चलाओ` |
| Play Next | `{song} अगला चलाओ` · `{musician} का {song} अगला चलाओ` · `मैं {song} अगला सुनना चाहता हूँ` · `{song} इसके बाद चलाओ` |
| Play Playlist | `प्लेलिस्ट {playlist} चलाओ` · `मेरी प्लेलिस्ट {playlist} चलाओ` · `प्लेलिस्ट {playlist} लगाओ` · `प्लेलिस्ट {playlist} शुरू करो` · `प्लेलिस्ट {playlist} सुनो` · `क्या तुम प्लेलिस्ट {playlist} चला सकते हो` · `मैं प्लेलिस्ट {playlist} सुनना चाहता हूँ` |
| Play Podcast | `पॉडकास्ट {podcast_name} चलाओ` · `पॉडकास्ट {podcast_name} सुनो` · `{podcast_name} का ताज़ा एपिसोड चलाओ` · `पॉडकास्ट {podcast_name} शुरू करो` · `मैं {podcast_name} पॉडकास्ट सुनना चाहता हूँ` |
| Play Radio | `रेडियो चलाओ` · `रेडियो मोड चलाओ` · `रेडियो शुरू करो` · `ऐसा ही और चलाओ` · `समान म्यूज़िक चलाते रहो` · `समान गाने चलाओ` · `रेडियो स्टेशन शुरू करो` |
| Play Random | `रैंडम {media_type} चलाओ` · `अपने {media_type} शफल करो` · `कुछ रैंडम चलाओ` · `रैंडम {media_type} {genre} से चलाओ` · `रैंडम गाने चलाओ` · `रैंडम म्यूज़िक चलाओ` · `मुझे रैंडम {media_type} दो` · `मुझे कुछ रैंडम चाहिए` · `{media_type} शफल करो` · `{media_type} से मुझे चौंकाओ` |
| Play Song | `{song} चलाओ` · `गाना {song} चलाओ` · `{song} गाना चलाओ` · `{musician} का {song} चलाओ` · `गाना {song} {musician} का चलाओ` · `{song} सुनो` · `गाना {song} सुनो` · `{musician} का {song} सुनो` · `{song} लगाओ` · `मैं {song} सुनना चाहता हूँ` · `क्या तुम {song} चला सकते हो` · `{song} शुरू करो` · `ट्रैक {song} चलाओ` · `{musician} का ट्रैक {song} चलाओ` |
| Play Video | `वीडियो {title} चलाओ` · `वीडियो {title} लगाओ` · `{title} शुरू करो` · `{title} देखो` · `क्या तुम {title} चला सकते हो` · `मैं {title} देखना चाहता हूँ` · `चलो {title} देखते हैं` · `फिल्म {title} चलाओ` · `वीडियो {title} शुरू करो` |
| Query Artist Library | `{musician} के कौन से ट्रैक हैं` · `{musician} के कौन से गाने हैं` · `{musician} के कौन से एल्बम हैं` · `{musician} के पास क्या है` · `{musician} के ट्रैक दिखाओ` · `{musician} के एल्बम दिखाओ` · `{musician} के {query_type} दिखाओ` · `{musician} के कौन से {query_type} हैं` |
| Query Recently Added | `क्या नया है` · `हाल ही में क्या जोड़ा गया` · `मेरी लाइब्रेरी में क्या नया है` · `हाल ही में जोड़े गए दिखाओ` · `कुछ नया है क्या` · `नए जोड़े गए आइटम दिखाओ` · `सबसे नए आइटम क्या हैं` |
| Recommend | `कुछ सुझाव दो` · `कुछ म्यूज़िक सुझाओ` · `एक फिल्म सुझाओ` · `देखने के लिए कुछ सुझाओ` · `{media_type} सुझाओ` |
| Search Media | `{query} खोजो` · `{query} ढूंढो` · `{query} खोजो` · `{query} देखो` · `क्या तुम्हारे पास {query} है` · `मैं {query} खोजना चाहता हूँ` · `क्या तुम {query} ढूंढ सकते हो` · `{query} खोजो` · `मुझे {query} ढूंढो` |
| Sleep Timer | `{duration_minutes} मिनट में बंद करो` · `स्लीप टाइमर {duration_minutes} मिनट सेट करो` · `स्लीप टाइमर {duration_minutes} मिनट` · `{duration_minutes} मिनट बाद बंद करो` |
| Turn Radio Off | `रेडियो मोड बंद करो` · `रेडियो मोड डिसेबल करो` · `रेडियो मोड ऑफ` · `रेडियो बंद करो` · `रेडियो डिसेबल करो` |
| Turn Radio On | `रेडियो मोड चालू करो` · `रेडियो मोड एनेबल करो` · `रेडियो मोड ऑन` · `रेडियो चालू करो` · `रेडियो एनेबल करो` |
| Unmark Favorite | `मुझे यह पसंद नहीं है` · `मुझे वीडियो पसंद नहीं है` · `मुझे गाना पसंद नहीं है` · `मुझे म्यूज़िक पसंद नहीं है` · `वीडियो को पसंदीदा से हटाओ` · `गाने को पसंदीदा से हटाओ` |
| Who Am I | `मैं कौन हूँ` · `यह कौन सा खाता है` · `मैं कौन सा खाता इस्तेमाल कर रहा हूँ` · `कौन बोल रहा है` · `कौन सा प्रोफाइल एक्टिव है` |

### <a id="it-it"></a>Italian (it-IT)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `Aggiungi {song} alla coda` · `Aggiungi {song} di {musician} alla coda` · `Metti {song} in coda` · `Metti {song} di {musician} in coda` · `Accoda {song}` · `Accoda {song} di {musician}` · `Aggiungi {song} alla mia lista` · `Inserisci {song} nella coda` |
| Browse Library | `Sfoglia {browse_category}` · `Mostra {browse_category}` · `Elenca {browse_category}` |
| Clear Queue | `Cancella la coda` · `Svuota la coda` · `Pulisci la coda` · `Elimina tutto dalla coda` · `Cancella la mia lista` · `Svuota la mia lista` |
| Continue Watching | `Continua a guardare` · `Riprendi a guardare` · `Continua il video` · `Riprendi il video` |
| Follow Me | `continua da qui` · `segui la riproduzione` · `continua ad ascoltare` · `riprendi da dove ero rimasto` · `seguimi` · `trasferisci la riproduzione` · `continua la musica` · `riprendi l'ascolto` |
| Go To Chapter | `Vai al capitolo {chapter_number}` · `Vai al capitolo {direction}` · `Salta al capitolo {chapter_number}` · `Salta al capitolo {direction}` · `Capitolo {chapter_number}` · `Capitolo {direction}` |
| In Progress Media List | `Cosa stavo guardando` · `Mostrami i video in corso` · `Riprendi a guardare` · `Cosa ho lasciato a metà` · `Cosa ho lasciato a meta` |
| Learn My Voice | `Impara la mia voce` · `Riconosci la mia voce` · `Associa la mia voce` · `Collega la mia voce` · `Impara chi sono` · `Riconoscimi` · `Impara questa voce` |
| List Queue | `Cosa c'è nella coda` · `Cosa c'è in coda` · `Cosa viene dopo` · `Cosa c'è dopo` · `Mostra la coda` · `Elenca la coda` · `Cosa suona dopo` |
| Loop All Off | `Disattiva loop` · `Disattiva ripetizione` |
| Loop All On | `Attiva loop` · `Attiva ripetizione` |
| Mark Favorite | `Aggiungi ai preferiti` · `Metti nei preferiti` · `Segna come preferito` · `Mi piace` · `Aggiungi questo ai preferiti` · `Metti questo nei preferiti` · `Segna questo come preferito` · `Questo mi piace` · `Mi piace questa canzone` · `Salva nei preferiti` |
| Media Info | `Cosa sta suonando` · `Cosa sta suonando adesso` · `Che brano è questo` · `Che canzone è questa` · `Chi è questo artista` · `Info su questo brano` · `Informazioni su questa canzone` · `Dimmi cosa sta suonando` · `Che cosa suona` · `Chi suona adesso` · `Qual è il {media_info_type}` · `Qual è la {media_info_type}` · `Dimmi il {media_info_type}` · `Dimmi la {media_info_type}` · `Che {media_info_type} è` · `Chi canta` · `Chi canta questa canzone` · `Di che album è questo brano` · `Che anno è` · `Quando è uscito` · `Quanto dura` · `Quanto dura questa canzone` · `Che genere è` · `Che genere di musica è` · `Parlami dell'artista` · `Raccontami di questo artista` |
| Play Album | `Suona {album}` · `Metti {album}` · `Ascolta {album}` · `Fai partire {album}` · `Riproduci {album}` · `Pleia {album}` · `Di suonare {album}` · `Di mettere {album}` · `Di ascoltare {album}` · `Di far partire {album}` · `Di riprodurre {album}` · `Di pleiare {album}` · `Riproduci album {album}` · `Riproduci disco {album}` · `Suona album {album}` · `Suona disco {album}` · `Metti album {album}` · `Metti disco {album}` · `Pleia album {album}` · `Pleia disco {album}` · `Di riprodurre album {album}` · `Di riprodurre disco {album}` · `Di suonare album {album}` · `Di suonare disco {album}` · `Di mettere album {album}` · `Di mettere disco {album}` · `Di pleiare album {album}` · `Di pleiare disco {album}` · `Riproduci album {album} di {musician}` · `Riproduci album {album} dei {musician}` · `Riproduci album {album} degli {musician}` · `Riproduci album {album} delle {musician}` · `Riproduci disco {album} di {musician}` · `Riproduci disco {album} dei {musician}` · `Riproduci disco {album} degli {musician}` · `Riproduci disco {album} delle {musician}` · `Suona album {album} di {musician}` · `Suona album {album} dei {musician}` · `Suona album {album} degli {musician}` · `Suona album {album} delle {musician}` · `Suona disco {album} di {musician}` · `Suona disco {album} dei {musician}` · `Suona disco {album} degli {musician}` · `Suona disco {album} delle {musician}` · `Metti album {album} di {musician}` · `Metti album {album} dei {musician}` · `Metti album {album} degli {musician}` · `Metti album {album} delle {musician}` · `Metti disco {album} di {musician}` · `Metti disco {album} dei {musician}` · `Metti disco {album} degli {musician}` · `Metti disco {album} delle {musician}` · `Pleia album {album} di {musician}` · `Pleia album {album} dei {musician}` · `Pleia album {album} degli {musician}` · `Pleia album {album} delle {musician}` · `Pleia disco {album} di {musician}` · `Pleia disco {album} dei {musician}` · `Pleia disco {album} degli {musician}` · `Pleia disco {album} delle {musician}` · `Di riprodurre album {album} di {musician}` · `Di riprodurre album {album} dei {musician}` · `Di riprodurre album {album} degli {musician}` · `Di riprodurre album {album} delle {musician}` · `Di riprodurre disco {album} di {musician}` · `Di riprodurre disco {album} dei {musician}` · `Di riprodurre disco {album} degli {musician}` · `Di riprodurre disco {album} delle {musician}` · `Di suonare album {album} di {musician}` · `Di suonare album {album} dei {musician}` · `Di suonare album {album} degli {musician}` · `Di suonare album {album} delle {musician}` · `Di suonare disco {album} di {musician}` · `Di suonare disco {album} dei {musician}` · `Di suonare disco {album} degli {musician}` · `Di suonare disco {album} delle {musician}` · `Di mettere album {album} di {musician}` · `Di mettere album {album} dei {musician}` · `Di mettere album {album} degli {musician}` · `Di mettere album {album} delle {musician}` · `Di mettere disco {album} di {musician}` · `Di mettere disco {album} dei {musician}` · `Di mettere disco {album} degli {musician}` · `Di mettere disco {album} delle {musician}` · `Di pleiare album {album} di {musician}` · `Di pleiare album {album} dei {musician}` · `Di pleiare album {album} degli {musician}` · `Di pleiare album {album} delle {musician}` · `Di pleiare disco {album} di {musician}` · `Di pleiare disco {album} dei {musician}` · `Di pleiare disco {album} degli {musician}` · `Di pleiare disco {album} delle {musician}` · `Fai suonare l'album {album}` · `Fai suonare album {album}` · `Metti su l'album {album}` · `Metti su album {album}` · `Riproduci l'album {album}` · `Suona l'album {album}` · `Ascolta l'album {album}` · `Ascolta album {album}` · `Pleia l'album {album}` · `Vorrei ascoltare l'album {album}` · `Vorrei ascoltare album {album}` · `Fai partire l'album {album}` · `Fai partire album {album}` · `Di suonare l'album {album}` · `Di riprodurre l'album {album}` · `Di mettere l'album {album}` · `Di ascoltare l'album {album}` · `Di far partire l'album {album}` · `Di pleiare l'album {album}` · `Fai suonare l'album {album} di {musician}` · `Fai suonare l'album {album} dei {musician}` · `Fai suonare l'album {album} degli {musician}` · `Fai suonare l'album {album} delle {musician}` · `Metti su l'album {album} di {musician}` · `Metti su l'album {album} dei {musician}` · `Metti su l'album {album} degli {musician}` · `Metti su l'album {album} delle {musician}` · `Riproduci l'album {album} di {musician}` · `Riproduci l'album {album} dei {musician}` · `Riproduci l'album {album} degli {musician}` · `Riproduci l'album {album} delle {musician}` · `Suona l'album {album} di {musician}` · `Suona l'album {album} dei {musician}` · `Suona l'album {album} degli {musician}` · `Suona l'album {album} delle {musician}` · `Di suonare l'album {album} di {musician}` · `Di suonare l'album {album} dei {musician}` · `Di suonare l'album {album} degli {musician}` · `Di suonare l'album {album} delle {musician}` · `Fai suonare il disco {album}` · `Fai suonare disco {album}` · `Metti su il disco {album}` · `Riproduci il disco {album}` · `Suona il disco {album}` · `Ascolta il disco {album}` · `Di suonare il disco {album}` · `Di riprodurre il disco {album}` |
| Play Artist Songs | `Suona {musician}` · `Metti {musician}` · `Ascolta {musician}` · `Fai partire {musician}` · `Riproduci {musician}` · `Pleia {musician}` · `Di suonare {musician}` · `Di mettere {musician}` · `Di ascoltare {musician}` · `Di far partire {musician}` · `Di riprodurre {musician}` · `Di pleiare {musician}` · `Riproduci brani di {musician}` · `Riproduci brani dei {musician}` · `Riproduci brani degli {musician}` · `Riproduci brani delle {musician}` · `Riproduci canzoni di {musician}` · `Riproduci canzoni dei {musician}` · `Riproduci canzoni degli {musician}` · `Riproduci canzoni delle {musician}` · `Riproduci musica di {musician}` · `Riproduci musica dei {musician}` · `Riproduci musica degli {musician}` · `Riproduci musica delle {musician}` · `Riproduci un brano di {musician}` · `Riproduci un brano dei {musician}` · `Riproduci un brano degli {musician}` · `Riproduci un brano delle {musician}` · `Riproduci una canzone di {musician}` · `Riproduci una canzone dei {musician}` · `Riproduci una canzone degli {musician}` · `Riproduci una canzone delle {musician}` · `Riproduci un pezzo di {musician}` · `Riproduci un pezzo dei {musician}` · `Riproduci un pezzo degli {musician}` · `Riproduci un pezzo delle {musician}` · `Riproduci una traccia di {musician}` · `Riproduci una traccia dei {musician}` · `Riproduci una traccia degli {musician}` · `Riproduci una traccia delle {musician}` · `Suona brani di {musician}` · `Suona brani dei {musician}` · `Suona brani degli {musician}` · `Suona brani delle {musician}` · `Suona canzoni di {musician}` · `Suona canzoni dei {musician}` · `Suona canzoni degli {musician}` · `Suona canzoni delle {musician}` · `Suona musica di {musician}` · `Suona musica dei {musician}` · `Suona musica degli {musician}` · `Suona musica delle {musician}` · `Suona un brano di {musician}` · `Suona un brano dei {musician}` · `Suona un brano degli {musician}` · `Suona un brano delle {musician}` · `Suona una canzone di {musician}` · `Suona una canzone dei {musician}` · `Suona una canzone degli {musician}` · `Suona una canzone delle {musician}` · `Suona un pezzo di {musician}` · `Suona un pezzo dei {musician}` · `Suona un pezzo degli {musician}` · `Suona un pezzo delle {musician}` · `Suona una traccia di {musician}` · `Suona una traccia dei {musician}` · `Suona una traccia degli {musician}` · `Suona una traccia delle {musician}` · `Metti brani di {musician}` · `Metti brani dei {musician}` · `Metti brani degli {musician}` · `Metti brani delle {musician}` · `Metti canzoni di {musician}` · `Metti canzoni dei {musician}` · `Metti canzoni degli {musician}` · `Metti canzoni delle {musician}` · `Metti musica di {musician}` · `Metti musica dei {musician}` · `Metti musica degli {musician}` · `Metti musica delle {musician}` · `Metti un brano di {musician}` · `Metti un brano dei {musician}` · `Metti un brano degli {musician}` · `Metti un brano delle {musician}` · `Metti una canzone di {musician}` · `Metti una canzone dei {musician}` · `Metti una canzone degli {musician}` · `Metti una canzone delle {musician}` · `Metti un pezzo di {musician}` · `Metti un pezzo dei {musician}` · `Metti un pezzo degli {musician}` · `Metti un pezzo delle {musician}` · `Metti una traccia di {musician}` · `Metti una traccia dei {musician}` · `Metti una traccia degli {musician}` · `Metti una traccia delle {musician}` · `Pleia brani di {musician}` · `Pleia brani dei {musician}` · `Pleia brani degli {musician}` · `Pleia brani delle {musician}` · `Pleia canzoni di {musician}` · `Pleia canzoni dei {musician}` · `Pleia canzoni degli {musician}` · `Pleia canzoni delle {musician}` · `Pleia musica di {musician}` · `Pleia musica dei {musician}` · `Pleia musica degli {musician}` · `Pleia musica delle {musician}` · `Pleia un brano di {musician}` · `Pleia un brano dei {musician}` · `Pleia un brano degli {musician}` · `Pleia un brano delle {musician}` · `Pleia una canzone di {musician}` · `Pleia una canzone dei {musician}` · `Pleia una canzone degli {musician}` · `Pleia una canzone delle {musician}` · `Pleia un pezzo di {musician}` · `Pleia un pezzo dei {musician}` · `Pleia un pezzo degli {musician}` · `Pleia un pezzo delle {musician}` · `Pleia una traccia di {musician}` · `Pleia una traccia dei {musician}` · `Pleia una traccia degli {musician}` · `Pleia una traccia delle {musician}` · `Di riprodurre brani di {musician}` · `Di riprodurre brani dei {musician}` · `Di riprodurre brani degli {musician}` · `Di riprodurre brani delle {musician}` · `Di riprodurre canzoni di {musician}` · `Di riprodurre canzoni dei {musician}` · `Di riprodurre canzoni degli {musician}` · `Di riprodurre canzoni delle {musician}` · `Di riprodurre musica di {musician}` · `Di riprodurre musica dei {musician}` · `Di riprodurre musica degli {musician}` · `Di riprodurre musica delle {musician}` · `Di riprodurre un brano di {musician}` · `Di riprodurre un brano dei {musician}` · `Di riprodurre un brano degli {musician}` · `Di riprodurre un brano delle {musician}` · `Di riprodurre una canzone di {musician}` · `Di riprodurre una canzone dei {musician}` · `Di riprodurre una canzone degli {musician}` · `Di riprodurre una canzone delle {musician}` · `Di riprodurre un pezzo di {musician}` · `Di riprodurre un pezzo dei {musician}` · `Di riprodurre un pezzo degli {musician}` · `Di riprodurre un pezzo delle {musician}` · `Di riprodurre una traccia di {musician}` · `Di riprodurre una traccia dei {musician}` · `Di riprodurre una traccia degli {musician}` · `Di riprodurre una traccia delle {musician}` · `Di suonare brani di {musician}` · `Di suonare brani dei {musician}` · `Di suonare brani degli {musician}` · `Di suonare brani delle {musician}` · `Di suonare canzoni di {musician}` · `Di suonare canzoni dei {musician}` · `Di suonare canzoni degli {musician}` · `Di suonare canzoni delle {musician}` · `Di suonare musica di {musician}` · `Di suonare musica dei {musician}` · `Di suonare musica degli {musician}` · `Di suonare musica delle {musician}` · `Di suonare un brano di {musician}` · `Di suonare un brano dei {musician}` · `Di suonare un brano degli {musician}` · `Di suonare un brano delle {musician}` · `Di suonare una canzone di {musician}` · `Di suonare una canzone dei {musician}` · `Di suonare una canzone degli {musician}` · `Di suonare una canzone delle {musician}` · `Di suonare un pezzo di {musician}` · `Di suonare un pezzo dei {musician}` · `Di suonare un pezzo degli {musician}` · `Di suonare un pezzo delle {musician}` · `Di suonare una traccia di {musician}` · `Di suonare una traccia dei {musician}` · `Di suonare una traccia degli {musician}` · `Di suonare una traccia delle {musician}` · `Di mettere brani di {musician}` · `Di mettere brani dei {musician}` · `Di mettere brani degli {musician}` · `Di mettere brani delle {musician}` · `Di mettere canzoni di {musician}` · `Di mettere canzoni dei {musician}` · `Di mettere canzoni degli {musician}` · `Di mettere canzoni delle {musician}` · `Di mettere musica di {musician}` · `Di mettere musica dei {musician}` · `Di mettere musica degli {musician}` · `Di mettere musica delle {musician}` · `Di mettere un brano di {musician}` · `Di mettere un brano dei {musician}` · `Di mettere un brano degli {musician}` · `Di mettere un brano delle {musician}` · `Di mettere una canzone di {musician}` · `Di mettere una canzone dei {musician}` · `Di mettere una canzone degli {musician}` · `Di mettere una canzone delle {musician}` · `Di mettere un pezzo di {musician}` · `Di mettere un pezzo dei {musician}` · `Di mettere un pezzo degli {musician}` · `Di mettere un pezzo delle {musician}` · `Di mettere una traccia di {musician}` · `Di mettere una traccia dei {musician}` · `Di mettere una traccia degli {musician}` · `Di mettere una traccia delle {musician}` · `Di pleiare brani di {musician}` · `Di pleiare brani dei {musician}` · `Di pleiare brani degli {musician}` · `Di pleiare brani delle {musician}` · `Di pleiare canzoni di {musician}` · `Di pleiare canzoni dei {musician}` · `Di pleiare canzoni degli {musician}` · `Di pleiare canzoni delle {musician}` · `Di pleiare musica di {musician}` · `Di pleiare musica dei {musician}` · `Di pleiare musica degli {musician}` · `Di pleiare musica delle {musician}` · `Di pleiare un brano di {musician}` · `Di pleiare un brano dei {musician}` · `Di pleiare un brano degli {musician}` · `Di pleiare un brano delle {musician}` · `Di pleiare una canzone di {musician}` · `Di pleiare una canzone dei {musician}` · `Di pleiare una canzone degli {musician}` · `Di pleiare una canzone delle {musician}` · `Di pleiare un pezzo di {musician}` · `Di pleiare un pezzo dei {musician}` · `Di pleiare un pezzo degli {musician}` · `Di pleiare un pezzo delle {musician}` · `Di pleiare una traccia di {musician}` · `Di pleiare una traccia dei {musician}` · `Di pleiare una traccia degli {musician}` · `Di pleiare una traccia delle {musician}` |
| Play By Decade | `Riproduci musica degli anni {decade}` · `Suona musica degli anni {decade}` · `Metti musica degli anni {decade}` · `Riproduci brani degli anni {decade}` · `Suona brani degli anni {decade}` · `Riproduci canzoni degli anni {decade}` · `Suona canzoni degli anni {decade}` · `Metti brani degli anni {decade}` · `Ascolta musica degli anni {decade}` · `Musica degli anni {decade}` · `Brani degli anni {decade}` · `Canzoni degli anni {decade}` · `Metti su musica degli anni {decade}` · `Fai suonare musica degli anni {decade}` · `Vorrei ascoltare musica degli anni {decade}` · `Mi andrebbe musica degli anni {decade}` · `Comincia a suonare brani degli anni {decade}` · `Pleia musica degli anni {decade}` · `Pleia brani degli anni {decade}` · `Metti canzoni degli anni {decade}` · `Fai partire canzoni degli anni {decade}` · `Suonami qualcosa degli anni {decade}` |
| Play By Genre | `Riproduci musica {genre}` · `Suona musica {genre}` · `Metti musica {genre}` · `Pleia musica {genre}` · `Riproduci brani {genre}` · `Suona brani {genre}` · `Metti brani {genre}` · `Riproduci genere {genre}` · `Suona genere {genre}` · `Metti genere {genre}` · `Di riprodurre musica {genre}` · `Di suonare musica {genre}` · `Di riprodurre genere {genre}` · `Di suonare genere {genre}` · `Riproduci {genre}` · `Suona {genre}` · `Metti {genre}` · `Pleia {genre}` · `Di riprodurre {genre}` |
| Play Channel | `Canale {channel}` · `Riproduci radio {channel}` · `di riprodurre la radio {channel}` · `di mettere la radio {channel}` · `Radio {channel}` |
| Play Episode | `Riproduci {series_name} stagione {season_number} episodio {episode_number}` · `Suona {series_name} stagione {season_number} episodio {episode_number}` · `Metti {series_name} stagione {season_number} episodio {episode_number}` |
| Play Favorites | `Riproduci i miei preferiti` · `Riproduci {media_type} preferiti` · `Suona i miei preferiti` · `Suona {media_type} preferiti` · `Metti i miei preferiti` · `Metti {media_type} preferiti` · `Pleia i miei preferiti` · `Pleia {media_type} preferiti` · `Di riprodurre i miei preferiti` · `Di riprodurre {media_type} preferiti` · `Di suonare i miei preferiti` · `Di suonare {media_type} preferiti` · `Di mettere i miei preferiti` · `Di mettere {media_type} preferiti` · `Di pleiare i miei preferiti` · `Di pleiare {media_type} preferiti` · `suona i preferiti di {username}` · `metti i preferiti di {username}` · `riproduci i preferiti di {username}` · `suona {media_type} preferiti di {username}` · `metti {media_type} preferiti di {username}` · `di suonare i preferiti di {username}` · `di mettere i preferiti di {username}` |
| Play Last Added | `Riproduci novità {media_type}` · `Riproduci ultimi {media_type} aggiunti` · `Riproduci nuovi media` · `Suona novità {media_type}` · `Suona ultimi {media_type} aggiunti` · `Suona nuovi media` · `Metti novità {media_type}` · `Metti ultimi {media_type} aggiunti` · `Metti nuovi media` · `Pleia novità {media_type}` · `Pleia ultimi {media_type} aggiunti` · `Pleia nuovi media` · `Di riprodurre novità {media_type}` · `Di riprodurre ultimi {media_type} aggiunti` · `Di riprodurre nuovi media` · `Di suonare novità {media_type}` · `Di suonare ultimi {media_type} aggiunti` · `Di suonare nuovi media` · `Di mettere novità {media_type}` · `Di mettere ultimi {media_type} aggiunti` · `Di mettere nuovi media` · `Di pleiare novità {media_type}` · `Di pleiare ultimi {media_type} aggiunti` · `Di pleiare nuovi media` · `Riproduci {media_type} aggiunti {time_period}` · `Suona {media_type} aggiunti {time_period}` · `Metti {media_type} aggiunti {time_period}` · `Riproduci novità {media_type} {time_period}` · `Suona novità {media_type} {time_period}` · `Riproduci {media_type} {time_period}` · `Suona {media_type} {time_period}` · `Di riprodurre {media_type} aggiunti {time_period}` · `Di suonare {media_type} aggiunti {time_period}` · `Di riprodurre novità {media_type} {time_period}` |
| Play Mood Music | `Musica {mood}` · `Riproduci musica {mood}` · `Suona musica {mood}` · `Metti musica {mood}` · `Musica mattutina` · `Musica serale` · `Musica per cena` · `Musica per allenamento` · `Musica per concentrarmi` · `Musica per festa` · `Musica rilassante` |
| Play Next | `Suona {song} dopo` · `Suona {song} di {musician} dopo` · `Riproduci {song} dopo` · `Riproduci {song} di {musician} dopo` · `Metti {song} come prossimo` · `Metti {song} di {musician} come prossimo` · `Voglio sentire {song} dopo` |
| Play Playlist | `Riproduci playlist {playlist}` · `Suona playlist {playlist}` · `Metti playlist {playlist}` · `Pleia playlist {playlist}` · `Di riprodurre playlist {playlist}` · `Di suonare playlist {playlist}` · `Di mettere playlist {playlist}` · `Di pleiare playlist {playlist}` · `Riproduci la playlist {playlist}` · `Suona la playlist {playlist}` · `Metti la playlist {playlist}` · `Pleia la playlist {playlist}` · `Metti su la playlist {playlist}` · `Fai partire la playlist {playlist}` · `Comincia la playlist {playlist}` · `Vorrei ascoltare la playlist {playlist}` · `Dammi la playlist {playlist}` · `Ascolta la playlist {playlist}` · `Fai suonare la playlist {playlist}` |
| Play Podcast | `Riproduci il podcast {podcast_name}` · `Suona il podcast {podcast_name}` · `Ascolta il podcast {podcast_name}` · `Metti il podcast {podcast_name}` · `Riproduci podcast {podcast_name}` · `Suona podcast {podcast_name}` · `Ascolta podcast {podcast_name}` · `Ascolta l'ultimo episodio di {podcast_name}` · `Riproduci l'ultimo episodio di {podcast_name}` · `Di riprodurre il podcast {podcast_name}` |
| Play Radio | `Suona la radio` · `Riproduci la radio` · `Attiva la radio` · `Metti musica simile` · `Suona brani simili` · `Inizia una stazione radio` · `Continua con musica simile` · `Suona canzoni come questa` |
| Play Random | `Riproduci {media_type} casuali` · `Riproduci {media_type} a caso` · `Suona {media_type} casuali` · `Suona {media_type} a caso` · `Metti {media_type} casuali` · `Metti {media_type} a caso` · `Pleia {media_type} casuali` · `Pleia {media_type} a caso` · `Metti su {media_type} a caso` · `Fai suonare {media_type} a caso` · `Scegli {media_type} a caso` · `Dammi {media_type} a caso` · `Vorrei {media_type} a caso` · `Mi andrebbe {media_type} a caso` · `Comincia {media_type} casuali` · `Mescola {media_type}` · `Mescola i miei {media_type}` · `Qualsiasi {media_type} va bene` · `Sorprendimi con {media_type}` |
| Play Song | `Suona {song}` · `Metti {song}` · `Ascolta {song}` · `Fai partire {song}` · `Riproduci {song}` · `Pleia {song}` · `Di suonare {song}` · `Di mettere {song}` · `Di ascoltare {song}` · `Di far partire {song}` · `Di riprodurre {song}` · `Di pleiare {song}` · `Riproduci il brano {song}` · `Riproduci la canzone {song}` · `Riproduci il pezzo {song}` · `Riproduci la traccia {song}` · `Suona il brano {song}` · `Suona la canzone {song}` · `Suona il pezzo {song}` · `Suona la traccia {song}` · `Metti il brano {song}` · `Metti la canzone {song}` · `Metti il pezzo {song}` · `Metti la traccia {song}` · `Pleia il brano {song}` · `Pleia la canzone {song}` · `Pleia il pezzo {song}` · `Pleia la traccia {song}` · `Di riprodurre il brano {song}` · `Di riprodurre la canzone {song}` · `Di riprodurre il pezzo {song}` · `Di riprodurre la traccia {song}` · `Di suonare il brano {song}` · `Di suonare la canzone {song}` · `Di suonare il pezzo {song}` · `Di suonare la traccia {song}` · `Di mettere il brano {song}` · `Di mettere la canzone {song}` · `Di mettere il pezzo {song}` · `Di mettere la traccia {song}` · `Di pleiare il brano {song}` · `Di pleiare la canzone {song}` · `Di pleiare il pezzo {song}` · `Di pleiare la traccia {song}` · `Riproduci {song} di {musician}` · `Riproduci {song} dei {musician}` · `Riproduci {song} degli {musician}` · `Riproduci {song} delle {musician}` · `Suona {song} di {musician}` · `Suona {song} dei {musician}` · `Suona {song} degli {musician}` · `Suona {song} delle {musician}` · `Metti {song} di {musician}` · `Metti {song} dei {musician}` · `Metti {song} degli {musician}` · `Metti {song} delle {musician}` · `Pleia {song} di {musician}` · `Pleia {song} dei {musician}` · `Pleia {song} degli {musician}` · `Pleia {song} delle {musician}` · `Di riprodurre {song} di {musician}` · `Di riprodurre {song} dei {musician}` · `Di riprodurre {song} degli {musician}` · `Di riprodurre {song} delle {musician}` · `Di suonare {song} di {musician}` · `Di suonare {song} dei {musician}` · `Di suonare {song} degli {musician}` · `Di suonare {song} delle {musician}` · `Di mettere {song} di {musician}` · `Di mettere {song} dei {musician}` · `Di mettere {song} degli {musician}` · `Di mettere {song} delle {musician}` · `Di pleiare {song} di {musician}` · `Di pleiare {song} dei {musician}` · `Di pleiare {song} degli {musician}` · `Di pleiare {song} delle {musician}` |
| Play Video | `Riproduci {title}` · `Suona {title}` · `Metti {title}` · `Pleia {title}` · `Di riprodurre {title}` · `Di suonare {title}` |
| Query Artist Library | `Quali brani abbiamo di {musician}` · `Quali canzoni abbiamo di {musician}` · `Che brani abbiamo di {musician}` · `Che canzoni abbiamo di {musician}` · `Quali album abbiamo di {musician}` · `Che album abbiamo di {musician}` · `Quali dischi abbiamo di {musician}` · `Che dischi abbiamo di {musician}` · `Cosa abbiamo di {musician}` · `Mostra i brani di {musician}` · `Mostra gli album di {musician}` · `Elenca i brani di {musician}` · `Elenca gli album di {musician}` · `Quali {query_type} abbiamo di {musician}` · `Che {query_type} abbiamo di {musician}` · `Mostra {query_type} di {musician}` · `Quali brani ci sono di {musician}` · `Quali canzoni ci sono di {musician}` · `Che brani ci sono di {musician}` · `Che canzoni ci sono di {musician}` · `Quali album ci sono di {musician}` · `Che album ci sono di {musician}` · `Quali dischi ci sono di {musician}` · `Che dischi ci sono di {musician}` · `Quali {query_type} ci sono di {musician}` · `Che {query_type} ci sono di {musician}` |
| Query Recently Added | `cosa c'è di nuovo` · `cosa è stato aggiunto di recente` · `mostrami le novità` · `ci sono novità` · `quali sono gli ultimi aggiunti` · `cosa c'è di nuovo nella mia libreria` · `elenca gli ultimi contenuti aggiunti` · `quali sono le novità` |
| Recommend | `Consiglia {media_type}` · `Raccomanda {media_type}` · `Suggerisci {media_type}` · `Di consigliare {media_type}` · `Di raccomandare {media_type}` · `Di suggerire {media_type}` · `Consiglia un brano` · `Consiglia una canzone` · `Consiglia un film` · `Suggerisci un brano` · `Suggerisci un film` · `Raccomanda un brano` · `Raccomanda una canzone` |
| Repeat Single On | `Ripeti la canzone` · `Ripeti la traccia` · `Ripeti il brano` · `Ripeti il video` · `di ripeter la canzone` · `di ripeter la traccia` · `di ripeter il brano` · `di ripeter il video` · `Ripetilo` · `di ripeterlo` · `riplei` · `replei` · `Suonalo ancora` · `di suonarlo ancora` · `Suonala ancora` · `di suonarla ancora` |
| Search Media | `Cerca {query}` · `Trova {query}` · `Cerco {query}` · `Hai {query}` · `Voglio trovare {query}` · `Puoi trovare {query}` · `Cerca il contenuto {query}` · `Trova il brano {query}` |
| Sleep Timer | `Imposta timer {duration_minutes}` · `Timer per dormire {duration_minutes}` · `Spegimento automatico {duration_minutes}` · `Ferma dopo {duration_minutes}` |
| Turn Radio Off | `Disattiva la modalità radio` · `Spegni la modalità radio` · `Modalità radio disattivata` · `Disattiva la radio` · `Spegni la radio` · `Ferma la modalità radio` |
| Turn Radio On | `Attiva la modalità radio` · `Accendi la modalità radio` · `Modalità radio attiva` · `Attiva la radio` · `Accendi la radio` |
| Unmark Favorite | `Rimuovi dai preferiti` · `Togli dai preferiti` · `Rimuovi questo dai preferiti` · `Togli questo dai preferiti` · `Non mi piace più` · `Non mi piace piu` · `Questo non mi piace più` · `Questo non mi piace piu` |
| Who Am I | `Chi sono` · `Chi sto usando` · `Con che utente sono collegato` · `Dimmi chi sono` · `Che utente sono` |

### <a id="ja-jp"></a>Japanese (ja-JP)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `{song} をキューに追加して` · `{musician} の {song} をキューに追加して` · `{song} をキューに入れて` · `{musician} の {song} をキューに入れて` |
| Browse Library | `{browse_category} をブラウズして` · `{browse_category} を見せて` · `{browse_category} のリスト` |
| Clear Queue | `キューをクリアして` · `キューを空にして` · `キューから全部消して` |
| Continue Watching | `続きを見て` · `続きを聴いて` · `途中から再開して` · `何を見てたっけ` · `続き` |
| Follow Me | `ついてきて` · `再生を続けて` · `続きから再生` · `再生を引き継ぐ` |
| Go To Chapter | `次のチャプター` · `前のチャプター` · `チャプター {chapter_number} へ行って` · `チャプター {chapter_number} へスキップして` · `チャプターをスキップして` |
| In Progress Media List | `何聴いてたっけ` · `何見てたっけ` · `進行中のものは？` · `進捗を見せて` · `何再生してたっけ` · `開始したものは何？` |
| Learn My Voice | `私の声を覚えて` · `私の声を記憶して` · `私を認識して` · `私の声をリンクして` · `これは私の声` · `ボイスプロフィールを設定して` · `私の声を関連付けて` |
| List Queue | `キューには何がある？` · `次は何？` · `キューを見せて` · `次に何が来る？` · `キューのリスト` |
| Loop Song On | `この曲をループして` · `この曲をずっとループして` · `曲をリピートして` · `この曲をリピートして` |
| Mark Favorite | `いいね` · `ビデオいいね` · `曲いいね` · `音楽いいね` · `この曲いいね` · `これいいね` · `ビデオをお気に入りに追加して` · `曲をお気に入りに追加して` · `これをお気に入りに保存して` · `お気に入りに追加して` |
| Media Info | `曲の名前は何？` · `ビデオの名前は何？` · `音楽の名前は何？` · `曲のタイトルは何？` · `今何が再生中？` · `{media_info_type} は何？` · `{media_info_type} を教えて` · `この曲の長さは？` · `誰が歌ってる？` · `ジャンルは何？` · `いつリリースされた？` · `このアーティストについて教えて` · `何のアルバムから？` |
| Play Album | `{album} を再生して` · `アルバム {album} を再生して` · `{musician} の {album} を再生して` · `アルバム {album} {musician} を再生して` · `{album} を聴かせて` · `{musician} の {album} を聴かせて` · `{album} を流して` · `{album} を聞きたい` · `ストリーム {album}` |
| Play Artist Songs | `{musician} の曲を再生して` · `{musician} の音楽を再生して` · `{musician} のトラックを再生して` · `{musician} を聴かせて` · `{musician} の曲を聴かせて` · `{musician} を流して` · `{musician} を聞きたい` · `{musician} をシャッフルして` · `{musician} を再生して` · `{musician} のストリーム` |
| Play By Decade | `{decade} の曲を再生して` · `{decade} の音楽を再生して` · `{decade} のヒットを再生して` · `{genre} の {decade} を再生して` · `{decade} の音楽を聴きたい` · `{decade} の曲を聴かせて` |
| Play By Genre | `{genre} の音楽を再生して` · `{genre} の曲を再生して` · `{genre} を再生して` · `{genre} を聴きたい` · `{genre} の音楽を流して` · `{genre} のストリーム` |
| Play Channel | `チャンネル {channel} を再生して` · `ラジオ {channel} を再生して` |
| Play Episode | `{series_name} のシーズン {season_number} エピソード {episode_number} を再生して` · `シーズン {season_number} エピソード {episode_number} の {series_name} を再生して` · `{series_name} のシーズン {season_number} エピソード {episode_number} を見たい` |
| Play Favorites | `お気に入りの {media_type} を再生して` · `お気に入りを再生して` · `お気に入りの曲を再生して` · `お気に入りの音楽を再生して` · `{username} のお気に入りを再生して` · `{username} のお気に入りの {media_type} を再生して` |
| Play Last Added | `最新の {media_type} を再生して` · `最近追加された {media_type} を再生して` · `新しいメディアを再生して` · `何か新しいものを再生して` · `{time_period} 追加された {media_type} を再生して` · `新着 {media_type} を再生して` · `{media_type} の新着は？` · `最新の {media_type} を見せて` |
| Play Mood Music | `{mood} の音楽を再生して` · `{mood} な音楽を再生して` · `{mood} な曲を再生して` · `{mood} の音楽が聴きたい` · `リラックスできる音楽を再生して` · `朝の音楽を再生して` · `夜の音楽を再生して` · `ワークアウトミュージックを再生して` · `集中できる音楽を再生して` · `パーティーミュージックを再生して` |
| Play Next | `次に {song} を再生して` · `次に {musician} の {song} を再生して` · `{song} を次に聴きたい` · `{song} をこの後に再生して` |
| Play Playlist | `プレイリスト {playlist} を再生して` · `プレイリスト {playlist} を流して` · `プレイリスト {playlist} をスタートして` · `プレイリスト {playlist} を聴かせて` · `プレイリスト {playlist} を聞きたい` |
| Play Podcast | `ポッドキャスト {podcast_name} を再生して` · `ポッドキャスト {podcast_name} を聴かせて` · `{podcast_name} の最新エピソードを再生して` · `ポッドキャスト {podcast_name} をスタートして` · `{podcast_name} ポッドキャストを聴きたい` |
| Play Radio | `ラジオを再生して` · `ラジオモードを再生して` · `ラジオをスタートして` · `似たような音楽を再生して` · `似た曲を再生して` · `ラジオステーションを始めて` |
| Play Random | `ランダムな {media_type} を再生して` · `{media_type} をシャッフルして` · `ランダムに何か再生して` · `ランダムな {genre} {media_type} を再生して` · `ランダムな曲を再生して` · `ランダムな音楽を再生して` · `何かランダムに流して` · `サプライズ {media_type} を再生して` |
| Play Song | `{song} を再生して` · `曲 {song} を再生して` · `{musician} の {song} を再生して` · `{musician} の曲 {song} を再生して` · `{song} を聴かせて` · `{musician} の {song} を聴かせて` · `{song} を流して` · `{song} を聞きたい` · `ストリーム {song}` · `{musician} のストリーム {song}` |
| Play Video | `ビデオ {title} を再生して` · `動画 {title} を流して` · `{title} を再生して` · `{title} を見たい` · `{title} を見せて` · `映画 {title} を再生して` · `ビデオ {title} をスタートして` |
| Query Artist Library | `{musician} のトラックは何がある？` · `{musician} の曲は何がある？` · `{musician} のアルバムは何がある？` · `{musician} には何がある？` · `{musician} のトラックを見せて` · `{musician} のアルバムを見せて` · `{musician} の {query_type} を見せて` · `{musician} の {query_type} は何がある？` |
| Query Recently Added | `新着はある？` · `最近追加されたものは？` · `ライブラリの新着は？` · `最近追加されたものを見せて` · `最近何か新しいものある？` · `最新のアイテムは何？` |
| Recommend | `何かおすすめは？` · `音楽のおすすめは？` · `映画のおすすめは？` · `何か見るものを提案して` · `{media_type} をおすすめして` |
| Search Media | `{query} を検索して` · `{query} を見つけて` · `{query} を探して` · `{query} ある？` · `{query} を見つけたい` · `{query} を探せる？` |
| Sleep Timer | `{duration_minutes} 分後に止めて` · `スリープタイマー {duration_minutes} 分` · `{duration_minutes} 分後におやすみタイマー` · `{duration_minutes} 分でスリープタイマーをセットして` |
| Turn Radio Off | `ラジオモードをオフにして` · `ラジオモードを無効にして` · `ラジオをオフにして` · `ラジオを無効にして` · `ラジオモードを止めて` |
| Turn Radio On | `ラジオモードをオンにして` · `ラジオモードを有効にして` · `ラジオをオンにして` · `ラジオを有効にして` |
| Unmark Favorite | `これ嫌い` · `ビデオ嫌い` · `曲嫌い` · `音楽嫌い` · `ビデオをお気に入りから削除して` · `曲をお気に入りから削除して` |
| Who Am I | `私は誰？` · `どのアカウント？` · `どのアカウントを使ってる？` · `誰が話してる？` · `どのプロフィールがアクティブ？` |

### <a id="nl-nl"></a>Dutch (nl-NL)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `voeg {song} toe aan mijn wachtrij` · `voeg {song} toe aan de wachtrij` · `voeg {song} van {musician} toe aan mijn wachtrij` · `voeg {song} van {musician} toe aan de wachtrij` · `wachtrij {song}` · `wachtrij {song} van {musician}` · `zet {song} in mijn wachtrij` |
| Browse Library | `browse {browse_category}` · `laat {browse_category} zien` · `lijst {browse_category}` · `welke {browse_category} heb ik` |
| Clear Queue | `wis mijn wachtrij` · `wis de wachtrij` · `leeg mijn wachtrij` · `leeg de wachtrij` · `verwijder alles uit mijn wachtrij` |
| Continue Watching | `verder kijken` · `verder luisteren` · `hervat waar ik was gebleven` · `wat was ik aan het kijken` · `doorgaan` · `verder gaan` |
| Follow Me | `volg me` · `verder met afspelen` · `neem het over` · `doorgaan met luisteren` |
| Go To Chapter | `volgend hoofdstuk` · `vorig hoofdstuk` · `ga naar hoofdstuk {chapter_number}` · `spring naar hoofdstuk {chapter_number}` · `een hoofdstuk vooruit` · `een hoofdstuk terug` · `hoofdstuk overslaan` |
| In Progress Media List | `waar was ik naar aan het luisteren` · `waar was ik naar aan het kijken` · `wat is in behandeling` · `laat mijn voortgang zien` · `wat was ik aan het afspelen` · `wat heb ik gestart` |
| Learn My Voice | `leer mijn stem` · `onthoud mijn stem` · `herken mij` · `koppel mijn stem` · `dit is mijn stem` · `stel mijn stemprofiel in` · `koppel mijn stem` |
| List Queue | `wat zit er in mijn wachtrij` · `wat zit er in de wachtrij` · `wat komt er aan` · `wat is het volgende` · `laat mijn wachtrij zien` · `wat wordt hierna afgespeeld` |
| Loop Song On | `loop dit nummer` · `herhaal dit nummer` · `zet dit nummer op repeat` · `blijf dit nummer herhalen` |
| Mark Favorite | `ik vind dit leuk` · `ik vind de video leuk` · `ik vind het nummer leuk` · `ik vind de muziek leuk` · `ik vind dit nummer leuk` · `ik vind dit leuk` · `voeg de video toe aan favorieten` · `voeg het nummer toe aan favorieten` · `sla dit op in favorieten` · `markeer als favoriet` |
| Media Info | `hoe heet dit nummer` · `hoe heet de video` · `hoe heet de muziek` · `wat is de titel van het nummer` · `wat speelt er nu` · `wat is de {media_info_type} hiervan` · `vertel me de {media_info_type}` · `wie zingt dit` · `wie speelt dit` · `wat is het genre` · `wanneer is dit uitgebracht` · `vertel me over deze artiest` · `van welk album is dit` |
| Play Album | `speel {album}` · `speel het album {album}` · `speel album {album}` · `speel {album} van {musician}` · `speel het album {album} van {musician}` · `luister naar {album}` · `luister naar het album {album}` · `luister naar {album} van {musician}` · `zet {album} op` · `ik wil {album} horen` · `kun je {album} afspelen` · `start {album}` · `stream {album}` |
| Play Artist Songs | `speel nummers van {musician}` · `speel muziek van {musician}` · `speel tracks van {musician}` · `speel liedjes van {musician}` · `luister naar {musician}` · `luister naar nummers van {musician}` · `luister naar muziek van {musician}` · `zet {musician} op` · `ik wil {musician} horen` · `ik wil naar {musician} luisteren` · `kun je {musician} afspelen` · `start {musician}` · `shuffle {musician}` · `shuffle nummers van {musician}` · `speel {musician}` · `speel wat {musician}` · `stream {musician}` |
| Play By Decade | `speel nummers uit de {decade}` · `speel hits uit de {decade}` · `speel muziek uit de {decade}` · `speel {decade} hits` · `speel {decade} nummers` · `speel {genre} uit de {decade}` · `ik wil {decade} muziek horen` · `luister naar {decade} muziek` · `geef me {decade} nummers` · `shuffle {decade} muziek` |
| Play By Genre | `speel wat {genre} muziek` · `speel {genre} nummers` · `speel {genre} muziek` · `speel me wat {genre}` · `ik wil naar {genre} luisteren` · `speel {genre}` · `geef me {genre} muziek` · `zet wat {genre} op` · `ik heb zin in {genre} muziek` · `start {genre}` · `stream {genre} muziek` |
| Play Channel | `speel kanaal {channel}` · `speel radio {channel}` |
| Play Episode | `speel seizoen {season_number} aflevering {episode_number} van {series_name}` · `speel {series_name} seizoen {season_number} aflevering {episode_number}` · `kijk seizoen {season_number} aflevering {episode_number} van {series_name}` |
| Play Favorites | `speel mijn favoriete {media_type}` · `speel mijn favorieten` · `speel mijn favoriete nummers` · `speel mijn favoriete muziek` · `speel de favorieten van {username}` · `speel {username} favoriete {media_type}` · `luister naar de favorieten van {username}` |
| Play Last Added | `speel laatst toegevoegde {media_type}` · `speel recent toegevoegde {media_type}` · `speel nieuwe media` · `speel iets nieuws` · `speel {media_type} toegevoegd {time_period}` · `speel recent toegevoegde {media_type} van {time_period}` · `wat is nieuw in {media_type}` · `speel de nieuwste {media_type}` · `laat nieuwe {media_type} zien` |
| Play Mood Music | `speel {mood} muziek` · `speel iets {mood}` · `speel {mood} nummers` · `ik wil {mood} muziek` · `speel mij iets {mood}` · `speel ochtendmuziek` · `speel avondmuziek` · `speel werkmuziek` · `speel focusmuziek` · `speel feestmuziek` · `speel ontspannende muziek` |
| Play Next | `speel {song} hierna` · `speel {song} van {musician} hierna` · `ik wil {song} hierna horen` · `speel {song} na dit` |
| Play Playlist | `speel de afspeellijst {playlist}` · `speel mijn afspeellijst {playlist}` · `zet de afspeellijst {playlist} op` · `start de afspeellijst {playlist}` · `zet mijn afspeellijst {playlist} op` · `luister naar de afspeellijst {playlist}` · `kun je afspeellijst {playlist} afspelen` · `ik wil afspeellijst {playlist} horen` · `geef me afspeellijst {playlist}` · `stream de afspeellijst {playlist}` |
| Play Podcast | `speel de podcast {podcast_name}` · `speel podcast {podcast_name}` · `luister naar de podcast {podcast_name}` · `speel de laatste aflevering van {podcast_name}` · `start de podcast {podcast_name}` · `ik wil naar podcast {podcast_name} luisteren` |
| Play Radio | `speel radio` · `speel radiomodus` · `start radio` · `speel meer zoals dit` · `blijf vergelijkbare muziek afspelen` · `speel vergelijkbare nummers` · `start een radiostation` · `speel nummers zoals deze` |
| Play Random | `speel een willekeurig {media_type}` · `speel willekeurig {media_type}` · `shuffle mijn {media_type}` · `speel iets willekeurigs` · `speel een willekeurig {media_type} uit {genre}` · `speel willekeurige {genre} {media_type}` · `verras me met {media_type}` · `shuffle {genre} {media_type}` · `speel willekeurige nummers` · `speel willekeurige muziek` · `geef me een willekeurig {media_type}` · `iets willekeurigs afspelen` |
| Play Song | `speel {song}` · `speel het nummer {song}` · `speel nummer {song}` · `speel {song} van {musician}` · `speel het nummer {song} van {musician}` · `luister naar {song}` · `luister naar het nummer {song}` · `luister naar {song} van {musician}` · `zet {song} op` · `ik wil {song} horen` · `ik wil {song} van {musician} horen` · `kun je {song} afspelen` · `kun je {song} van {musician} afspelen` · `start {song}` · `speel het liedje {song}` · `speel het liedje {song} van {musician}` · `draai {song}` · `stream {song}` |
| Play Video | `speel de video {title}` · `zet de video {title} op` · `start {title}` · `kijk {title}` · `kun je {title} afspelen` · `ik wil {title} kijken` · `laten we {title} kijken` · `stream {title}` · `speel de film {title}` · `start de video {title}` |
| Query Artist Library | `welke tracks hebben we van {musician}` · `welke nummers hebben we van {musician}` · `welke albums hebben we van {musician}` · `wat hebben we van {musician}` · `laat tracks zien van {musician}` · `laat albums zien van {musician}` · `welke {query_type} hebben we van {musician}` · `laat {query_type} zien van {musician}` |
| Query Recently Added | `wat is er nieuw` · `wat is er recentelijk toegevoegd` · `wat is er nieuw in mijn bibliotheek` · `laat recent toegevoegde zien` · `iets nieuws onlangs` · `laat de nieuwste items zien` · `wat zijn de nieuwste items` |
| Recommend | `beveel iets aan` · `beveel wat muziek aan` · `beveel een film aan` · `stel iets voor om te kijken` · `stel wat muziek voor` · `speel iets wat ik leuk vind` · `beveel {media_type} aan` · `stel {media_type} voor` |
| Search Media | `zoek naar {query}` · `vind {query}` · `zoek {query}` · `zoek {query} op` · `heb je {query}` · `ik wil {query} vinden` · `kun je {query} vinden` · `vind me {query}` |
| Sleep Timer | `stop met afspelen over {duration_minutes} minuten` · `stel een slaaptimer in voor {duration_minutes} minuten` · `slaaptimer {duration_minutes} minuten` · `stop na {duration_minutes} minuten` · `zet uit over {duration_minutes} minuten` |
| Turn Radio Off | `zet radiomodus uit` · `schakel radiomodus uit` · `radiomodus uit` · `zet radio uit` · `schakel radio uit` · `stop radiomodus` |
| Turn Radio On | `zet radiomodus aan` · `schakel radiomodus in` · `radiomodus aan` · `zet radio aan` · `schakel radio in` |
| Unmark Favorite | `ik vind dit niet leuk` · `ik vind de video niet leuk` · `ik vind het nummer niet leuk` · `verwijder de video uit favorieten` · `verwijder het nummer uit favorieten` |
| Who Am I | `wie ben ik` · `welk account is dit` · `welk account gebruik ik` · `wie spreekt er` · `welk profiel is actief` · `ben ik herkend` |

### <a id="pt-br"></a>Portuguese - Brazil (pt-BR)

Invocation name: **"jellyfin player"**

| Intent | Utterances |
|--------|------------|
| Add To Queue | `adicionar {song} à minha fila` · `adicionar {song} à fila` · `adicionar {song} de {musician} à minha fila` · `adicionar {song} de {musician} à fila` · `enfileirar {song}` · `enfileirar {song} de {musician}` · `colocar {song} na minha fila` |
| Browse Library | `navegar {browse_category}` · `mostrar {browse_category}` · `listar {browse_category}` · `quais {browse_category} eu tenho` |
| Clear Queue | `limpar minha fila` · `limpar a fila` · `esvaziar minha fila` · `esvaziar a fila` · `remover tudo da minha fila` · `limpar minha playlist` |
| Continue Watching | `continuar assistindo` · `continuar ouvindo` · `retomar de onde parei` · `o que eu estava assistindo` · `continuar tocando` · `continuar` |
| Follow Me | `me siga` · `continuar tocando` · `retomar a reprodução` · `transferir a música` |
| Go To Chapter | `próximo capítulo` · `capítulo anterior` · `ir para o capítulo {chapter_number}` · `pular para o capítulo {chapter_number}` · `avançar um capítulo` · `voltar um capítulo` · `pular capítulo` |
| In Progress Media List | `o que eu estava ouvindo` · `o que eu estava assistindo` · `o que está em andamento` · `mostrar meu progresso` · `o que eu estava tocando` · `listar mídias em andamento` |
| Learn My Voice | `aprender minha voz` · `lembrar minha voz` · `me reconhecer` · `vincular minha voz` · `essa é minha voz` · `configurar meu perfil de voz` · `associar minha voz` |
| List Queue | `o que tem na minha fila` · `o que tem na fila` · `o que vem aí` · `o que vem depois` · `mostrar minha fila` · `listar minha fila` · `o que toca depois` |
| Loop Song On | `repetir esta música` · `colocar esta música em loop` · `repetir a música` · `repetir essa música para sempre` · `loop nesta música` |
| Mark Favorite | `eu gostei` · `eu gostei do vídeo` · `eu gostei da música` · `adicionar o vídeo aos favoritos` · `adicionar a música aos favoritos` · `salvar nos favoritos` · `marcar como favorito` · `eu curti isso` · `eu curti a música` · `eu curti o vídeo` |
| Media Info | `qual é o nome da música` · `qual é o nome do vídeo` · `qual é o nome da música` · `qual é o título da música` · `o que está tocando` · `qual {media_info_type} é essa` · `me diga a {media_info_type}` · `quem canta isso` · `quem está tocando` · `qual é o gênero` · `quando foi lançado` · `me fale sobre esse artista` · `de qual álbum é essa` |
| Play Album | `tocar {album}` · `tocar o álbum {album}` · `tocar álbum {album}` · `tocar {album} de {musician}` · `tocar o álbum {album} de {musician}` · `ouvir {album}` · `ouvir o álbum {album}` · `ouvir {album} de {musician}` · `colocar {album}` · `quero ouvir {album}` · `pode tocar {album}` · `começar a tocar {album}` · `me dá {album}` · `stream {album}` |
| Play Artist Songs | `tocar músicas de {musician}` · `tocar música de {musician}` · `tocar faixas de {musician}` · `ouvir {musician}` · `ouvir músicas de {musician}` · `ouvir música de {musician}` · `colocar {musician}` · `quero ouvir {musician}` · `vamos ouvir {musician}` · `pode tocar {musician}` · `começar a tocar {musician}` · `embaralhar {musician}` · `embaralhar músicas de {musician}` · `tocar {musician}` · `tocar um pouco de {musician}` · `me dá {musician}` · `stream {musician}` |
| Play By Decade | `tocar músicas dos {decade}` · `tocar faixas dos {decade}` · `tocar hits dos {decade}` · `tocar música dos {decade}` · `tocar {decade} hits` · `tocar {decade} músicas` · `tocar {genre} dos {decade}` · `quero ouvir {decade}` · `ouvir {decade}` · `me dá {decade} músicas` · `embaralhar {decade}` · `tocar {decade}` |
| Play By Genre | `tocar música {genre}` · `tocar músicas {genre}` · `tocar {genre}` · `quero ouvir {genre}` · `me dá música {genre}` · `colocar um {genre}` · `pode tocar {genre}` · `stream {genre}` · `tocar faixas {genre}` · `vamos ouvir {genre}` |
| Play Channel | `tocar canal {channel}` · `tocar rádio {channel}` |
| Play Episode | `tocar temporada {season_number} episódio {episode_number} de {series_name}` · `tocar {series_name} temporada {season_number} episódio {episode_number}` · `assistir temporada {season_number} episódio {episode_number} de {series_name}` · `assistir {series_name} temporada {season_number} episódio {episode_number}` |
| Play Favorites | `tocar meus favoritos` · `tocar {media_type} favoritos` · `tocar minhas músicas favoritas` · `tocar minha música favorita` · `tocar os favoritos de {username}` · `ouvir os favoritos de {username}` · `tocar {media_type} favoritos de {username}` |
| Play Last Added | `tocar últimos {media_type} adicionados` · `tocar {media_type} adicionados recentemente` · `tocar mídias novas` · `tocar algo novo` · `tocar {media_type} adicionados {time_period}` · `tocar {media_type} novos de {time_period}` · `o que há de novo em {media_type}` · `tocar as novidades de {media_type}` · `mostrar {media_type} novos` · `quero ouvir {media_type} novos` |
| Play Mood Music | `tocar música {mood}` · `tocar algo {mood}` · `tocar músicas {mood}` · `quero música {mood}` · `tocar música relaxante` · `tocar música de manhã` · `tocar música de noite` · `tocar música de treino` · `tocar música para focar` · `tocar música de festa` |
| Play Next | `tocar {song} depois` · `tocar {song} de {musician} depois` · `quero ouvir {song} depois` · `tocar {song} a seguir` · `tocar {song} de {musician} a seguir` · `tocar {song} após essa` |
| Play Playlist | `tocar a playlist {playlist}` · `tocar minha playlist {playlist}` · `colocar a playlist {playlist}` · `iniciar a playlist {playlist}` · `ouvir a playlist {playlist}` · `pode tocar a playlist {playlist}` · `quero ouvir a playlist {playlist}` · `me dá a playlist {playlist}` · `stream a playlist {playlist}` |
| Play Podcast | `tocar o podcast {podcast_name}` · `tocar podcast {podcast_name}` · `ouvir o podcast {podcast_name}` · `ouvir podcast {podcast_name}` · `tocar o último episódio de {podcast_name}` · `iniciar o podcast {podcast_name}` · `quero ouvir o podcast {podcast_name}` |
| Play Radio | `tocar rádio` · `tocar modo rádio` · `iniciar rádio` · `tocar mais como esse` · `continuar tocando música similar` · `tocar músicas parecidas` · `iniciar uma estação de rádio` · `tocar músicas como esta` |
| Play Random | `tocar um {media_type} aleatório` · `tocar {media_type} aleatório` · `embaralhar meus {media_type}` · `tocar algo aleatório` · `tocar um {media_type} aleatório de {genre}` · `surpreender-me com {media_type}` · `embaralhar {genre} {media_type}` · `tocar músicas aleatórias` · `tocar música aleatória` · `tocar {genre} aleatório` · `me dá um {media_type} aleatório` · `quero algo aleatório` · `embaralhar {media_type}` · `escolher um {media_type} aleatório` |
| Play Song | `tocar {song}` · `tocar a música {song}` · `tocar música {song}` · `tocar {song} de {musician}` · `tocar a música {song} de {musician}` · `ouvir {song}` · `ouvir a música {song}` · `ouvir {song} de {musician}` · `colocar {song}` · `quero ouvir {song}` · `quero ouvir {song} de {musician}` · `pode tocar {song}` · `pode tocar {song} de {musician}` · `começar a tocar {song}` · `tocar a faixa {song}` · `tocar a faixa {song} de {musician}` · `tocar {song}` · `me dá {song}` · `stream {song}` |
| Play Video | `tocar o vídeo {title}` · `colocar o vídeo {title}` · `começar a tocar {title}` · `assistir {title}` · `pode tocar {title}` · `quero assistir {title}` · `vamos assistir {title}` · `stream {title}` · `me dá o vídeo {title}` · `mostrar {title}` · `tocar o filme {title}` · `pode tocar o vídeo {title}` · `iniciar o vídeo {title}` |
| Query Artist Library | `quais faixas temos de {musician}` · `quais músicas temos de {musician}` · `quais álbuns temos de {musician}` · `o que temos de {musician}` · `mostrar faixas de {musician}` · `mostrar álbuns de {musician}` · `quais {query_type} temos de {musician}` · `mostrar {query_type} de {musician}` |
| Query Recently Added | `o que há de novo` · `o que foi adicionado recentemente` · `o que há de novo na minha biblioteca` · `mostrar adicionados recentemente` · `alguma novidade` · `mostrar os itens mais recentes` · `quais são os itens mais novos` |
| Recommend | `recomendar algo` · `recomendar uma música` · `recomendar um filme` · `sugerir algo para assistir` · `sugerir uma música` · `tocar algo que eu goste` · `recomendar {media_type}` · `sugerir {media_type}` |
| Search Media | `procurar {query}` · `encontrar {query}` · `buscar {query}` · `vocês têm {query}` · `quero encontrar {query}` · `pode encontrar {query}` · `buscar {query}` |
| Sleep Timer | `parar de tocar em {duration_minutes} minutos` · `definir timer de sono para {duration_minutes} minutos` · `timer de sono {duration_minutes} minutos` · `parar após {duration_minutes} minutos` · `desligar em {duration_minutes} minutos` · `definir timer de sono {duration_minutes}` |
| Turn Radio Off | `desativar modo rádio` · `desabilitar modo rádio` · `modo rádio desligado` · `desligar rádio` · `desabilitar rádio` · `parar modo rádio` |
| Turn Radio On | `ativar modo rádio` · `habilitar modo rádio` · `modo rádio ligado` · `ligar rádio` · `habilitar rádio` |
| Unmark Favorite | `eu não gostei disso` · `eu não gostei do vídeo` · `eu não gostei da música` · `remover o vídeo dos favoritos` · `remover a música dos favoritos` · `tirar dos favoritos` |
| Who Am I | `quem sou eu` · `qual conta é essa` · `qual conta estou usando` · `quem está falando` · `qual perfil está ativo` · `estou sendo reconhecido` |

## License

[GPL-3.0](LICENSE)
