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
10. [Supported Voice Commands](#supported-voice-commands)
11. [Supported Languages](#supported-languages)
12. [Troubleshooting](#troubleshooting)
13. [Third Party Notices](#third-party-notices)
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
- **Multi-language**: 12 locale variants across 6 languages
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

## Supported Voice Commands

Utterances are shown for English (en-US) and Italian (it-IT). Replace *Jellyfin Player* with your configured invocation name. The YAML templates in `Alexa/InteractionModel/templates/` are the source of truth for Italian utterances.

### Playback

**Play a song**

| Locale | Example |
|--------|---------|
| en-US | "Play the song *Bohemian Rhapsody*" |
| it-IT | "Riproduci il brano *Bohemian Rhapsody*" / "Suona la canzone *Bohemian Rhapsody*" |

**Play a song by artist**

| Locale | Example |
|--------|---------|
| en-US | "Play the song *Bohemian Rhapsody* from *Queen*" |
| it-IT | "Riproduci il brano *Bohemian Rhapsody* di *Queen*" |

**Play an album**

| Locale | Example |
|--------|---------|
| en-US | "Play the album *Discovery*" |
| it-IT | "Riproduci l'album *Discovery*" / "Suona il disco *Discovery*" |

**Play an album by artist**

| Locale | Example |
|--------|---------|
| en-US | "Play the album *Discovery* by *Daft Punk*" |
| it-IT | "Riproduci l'album *Discovery* di *Daft Punk*" |

**Play artist songs**

| Locale | Example |
|--------|---------|
| en-US | "Play songs from *Queen*" |
| it-IT | "Riproduci brani di *Queen*" / "Suona musica degli *Queen*" |

**Play a playlist**

| Locale | Example |
|--------|---------|
| en-US | "Play the playlist *Workout Mix*" |
| it-IT | "Riproduci playlist *Workout Mix*" / "Suona la playlist *Workout Mix*" |

**Play a video**

| Locale | Example |
|--------|---------|
| en-US | "Play the video *Inception*" |
| it-IT | "Riproduci *Inception*" / "Suona *Inception*" |

**Play a channel/radio**

| Locale | Example |
|--------|---------|
| en-US | "Play channel *BBC Radio 1*" |
| it-IT | "Canale *BBC Radio 1*" / "Riproduci radio *BBC Radio 1*" |

**Play recently added**

| Locale | Example |
|--------|---------|
| en-US | "Play recently added music" |
| it-IT | "Riproduci novità brani" / "Suona ultimi brani aggiunti" |

**Play favorites**

| Locale | Example |
|--------|---------|
| en-US | "Play my favorites" |
| it-IT | "Riproduci i miei preferiti" / "Suona i miei preferiti" |

**Play random**

| Locale | Example |
|--------|---------|
| en-US | "Play random songs" |
| it-IT | "Riproduci brani casuali" / "Suona brani a caso" |

**Play by genre**

| Locale | Example |
|--------|---------|
| en-US | "Play *rock*" |
| it-IT | "Riproduci *rock*" / "Suona *rock*" |

**Play mood music**

| Locale | Example |
|--------|---------|
| en-US | "Play relaxing music" |
| it-IT | "Musica rilassante" / "Riproduci musica rilassante" |

**Play a specific episode**

| Locale | Example |
|--------|---------|
| en-US | "Play *Breaking Bad* season 1 episode 1" |
| it-IT | "Riproduci *Breaking Bad* stagione 1 episodio 1" |

**Continue watching**

| Locale | Example |
|--------|---------|
| en-US | "Continue watching" |
| it-IT | "Continua a guardare" / "Riprendi a guardare" |

### Transport Controls (Built-in Alexa Commands)

| Command | What It Does |
|---------|-------------|
| "Pause" / "Stop" | Pauses or stops playback |
| "Resume" / "Play" | Resumes playback |
| "Next" | Skips to next track |
| "Previous" | Goes to previous track |
| "Shuffle on" | Enables shuffle mode |
| "Shuffle off" | Disables shuffle mode |
| "Start over" | Restarts current media from the beginning |

**Repeat / Loop**

| Locale | Example |
|--------|---------|
| en-US | "Loop this song" / "Loop on" / "Loop off" |
| it-IT | "Ripeti la canzone" / "Attiva loop" / "Disattiva loop" |

### Queue Management

| Locale | Example |
|--------|---------|
| en-US | "Add *Song Name* to queue" / "Play *Song Name* next" / "Clear queue" / "What's in my queue?" |
| it-IT | (falls back to default Alexa phrasing) |

### Favorites Management

**Add to favorites**

| Locale | Example |
|--------|---------|
| en-US | "I like this song" / "Add this to my favorites" |
| it-IT | "Aggiungi ai preferiti" / "Mi piace" / "Segna come preferito" |

**Remove from favorites**

| Locale | Example |
|--------|---------|
| en-US | "I don't like this" / "Remove from my favorites" |
| it-IT | "Rimuovi dai preferiti" / "Non mi piace più" / "Togli dai preferiti" |

### Media Information

| Locale | Example |
|--------|---------|
| en-US | "What's playing?" / "What song is this?" |
| it-IT | "Cosa sta suonando" / "Che brano è questo" / "Chi suona adesso" |

### Library Browsing

**Browse by category**

| Locale | Example |
|--------|---------|
| en-US | "Browse artists" / "Show me albums" / "List songs" |
| it-IT | "Sfoglia artisti" / "Mostra album" / "Elenca canzoni" |

**Query by artist**

| Locale | Example |
|--------|---------|
| en-US | "Which tracks do we have by *Queen*?" / "What albums are available from *Daft Punk*?" |
| it-IT | "Quali brani abbiamo di *Queen*?" / "Quali album abbiamo di *Daft Punk*?" / "Cosa abbiamo di *Beatles*?" |

### In-Progress Media

| Locale | Example |
|--------|---------|
| en-US | "What was I watching?" / "Show me in-progress media" |
| it-IT | "Cosa stavo guardando" / "Cosa ho lasciato a metà" |

### Chapter Navigation

| Locale | Example |
|--------|---------|
| en-US | "Go to chapter 3" / "Skip to the next chapter" |
| it-IT | "Vai al capitolo 3" / "Salta al capitolo prossimo" |

### Recommendations

| Locale | Example |
|--------|---------|
| en-US | "Recommend music" / "Suggest a song" |
| it-IT | "Consiglia musica" / "Raccomanda brani" / "Suggerisci canzoni" |

### Sleep Timer

| Locale | Example |
|--------|---------|
| en-US | "Set sleep timer for 30 minutes" |
| it-IT | "Imposta timer 30" / "Timer per dormire 30" / "Ferma dopo 30" |

### Radio Mode

| Locale | Example |
|--------|---------|
| en-US | "Play radio" / "Turn radio on" / "Turn radio off" |
| it-IT | (falls back to default Alexa phrasing) |

### Voice Identification

| Locale | Example |
|--------|---------|
| en-US | "Learn my voice" / "Who am I?" |
| it-IT | "Impara la mia voce" / "Riconosci la mia voce" / "Chi sono" |

## Supported Languages

The skill supports the following locales:

| Language | Locales |
|----------|---------|
| English | en-US, en-GB, en-AU, en-CA, en-IN |
| German | de-DE |
| Spanish | es-ES, es-MX, es-US |
| French | fr-FR, fr-CA |
| Italian | it-IT |

Custom utterances are provided for en-US, de-DE, es-ES, fr-FR, and it-IT. Other locales fall back to default Alexa phrasing.

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

## License

[GPL-3.0](LICENSE)
