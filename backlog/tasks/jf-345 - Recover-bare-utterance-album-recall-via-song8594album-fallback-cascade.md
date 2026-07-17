---
id: JF-345
title: Recover bare-utterance album recall via song-to-album fallback cascade
status: To Do
assignee: []
created_date: '2026-07-16 16:38'
updated_date: '2026-07-16 16:39'
labels: []
dependencies: []
references:
  - 'PR #15'
  - commit cd2bbdc (JF-344 cross-media artist fallback)
  - commit cdc872b (language-agnostic artist fallback)
  - commit f5c701c (PlaySong fuzzy fallback revert — 8s timeout lesson)
  - JF-332 (album slot type / catalog architecture)
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After PR #15 trimmed PlayAlbum's bare-carrier samples ("play {album}", "listen to {album}", etc.) in the 16 free-text locales, a bare "play abbey road" no longer finds an album — it routes to PlaySong, misses, and returns not-found. Recover that recall with a **song&#8594;album** cascade inside `PlaySongIntentHandler`, mirroring the existing song&#8594;artist cascade (`PlaySongIntentHandler.cs:219-262`) and the shared `BaseHandler.TryEntityFallbackAsync` used by PlayMoodMusic/FindSong. On a confirmed song miss (and per the chosen precedence, also no artist match), run a bounded album search and, on a strong match, play the album via a `FoundAlbumInstead` announcement.

**Why this scope (16 locales, not just English):** Verified per-locale — 16 of 17 locales use free-text `AMAZON.MusicRecording` for `PlayAlbumIntent.album` (all 5 English + ar-SA, de-DE, es-ES/MX/US, fr-CA/FR, hi-IN, ja-JP, nl-NL, pt-BR). ONLY it-IT uses the catalog-backed `AlbumName` type with phonetic synonyms for foreign titles, which already gives it one-shot album routing. So it-IT is exempt; the other 16 all share the collision PR #15 fixed and all benefit from this cascade. PR #15's trim is the prerequisite: it makes PlaySong the single owner of bare "play X".

**Hard constraints:**
- **8-second Alexa response deadline.** A prior PlaySong fallback (`SearchItemsFuzzyAsync`) scanned the entire Audio catalog &#8594; 11s &#8594; `InvalidResponse` and was reverted (commit f5c701c). The cascade MUST reuse `PlayAlbumIntentHandler`'s bounded `GetItemList` album query (exact&#8594;fuzzy, small catalog), never a full-catalog linear scan. The lesson: every cascade tier must be indexed or bounded.
- **Tighter gating than the artist cascade.** Song/album name overlap is far more common than artist/mood overlap, so the threshold must be stricter than `Math.Max(FuzzyMatcher.GetDefaultThreshold(user), CrossMediaArtistThreshold)` + the existing 2-word guard, or "play thriller" substitutes the album when the user meant the song. Decide album-before-artist vs album-after-artist precedence explicitly.
- **Optional announce-back.** Gate the "playing the album X instead" spoken confirmation behind a new `PluginConfiguration` bool (e.g. `AnnounceCrossMediaSubstitution`, default true), following the existing feature-flag pattern (~20 `public bool ...Enabled` toggles, e.g. `PhoneticSongSearchEnabled`). Users who find cross-media announcements chatty can silence them. Consider applying the same toggle to the existing `FoundArtistInstead` announcement for consistency.

Likely files: `PlaySongIntentHandler.cs`, `BaseHandler.cs` (shared cascade helper if refactored), `Configuration/PluginConfiguration.cs`, `Configuration/config.html` (toggle UI), tests under `Jellyfin.Plugin.AlexaSkill.Tests/`.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Bare 'play <album>' with no matching song routes to and plays the album in a free-text locale (verify on-device en-US + one non-English free-text locale, e.g. de-DE)
- [ ] #2 A bare utterance that matches a song does NOT substitute the album (song-match precedence verified)
- [ ] #3 The full PlaySong handler path (song search &#8594; album cascade) completes within Alexa's 8s deadline on a large library — regression test or timing log proving it stays well under 8s (guards against repeating f5c701c)
- [ ] #4 A new PluginConfiguration bool gates the cross-media substitution announcement; when off, the album still plays but no 'FoundAlbumInstead' speech is emitted
- [ ] #5 Unit tests cover: song miss &#8594; album hit (play + announcement), song hit &#8594; no album substitution, below-threshold album &#8594; no substitution, announcement config-off &#8594; silent substitution
- [ ] #6 it-IT album routing is unchanged (still one-shots via AlbumName catalog type)
<!-- AC:END -->

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
- [ ] #10 /code-review high passed (no blocking findings remaining, or findings applied/tracked)
<!-- DOD:END -->
