---
id: JF-337
title: >-
  Phonetic/fuzzy fallback missing across 9 name-searching handlers
  (album/song/video/playlist/episode/book/channel/podcast/browse) — shared
  BaseHandler helper
status: To Do
assignee: []
created_date: '2026-07-13 05:55'
updated_date: '2026-07-22 21:15'
labels:
  - search
  - phonetic
  - handler
  - asr
  - i18n
  - tech-debt
dependencies: []
modified_files:
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/BaseHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayVideoIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlaySongIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/SearchMediaIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPlaylistIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayEpisodeIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayBookIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayChannelIntentHandler.cs
  - Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/PlayPodcastIntentHandler.cs
  - >-
    Jellyfin.Plugin.AlexaSkill/Alexa/Handler/Intent/BrowseLibraryIntentHandler.cs
priority: medium
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Umbrella for the systematic gap found by a handler sweep (2026-07-13). JF-336 (PlayAlbum) is the first concrete instance; this task covers the OTHER 9 name-searching handlers with the same gap + the shared fix.

Verified by handler survey: only 2 of ~12 name-searching handlers have a phonetic/fuzzy fallback — PlayArtistSongsIntentHandler (4-tier ArtistSearch with Double Metaphone) and FindSongIntentHandler (SongNgramIndexService: n-gram + phonetic + DB). The other 10 do an EXACT Jellyfin searchTerm query for the user-spoken name and have no phonetic fallback, so ASR transcription/accent/spelling variants fail (the JF-336 "caffè" vs "Cafe" pattern):

GAP handlers (exact searchTerm, no phonetic fallback):
- PlayAlbumIntentHandler  → JF-336 (first instance)
- PlaySongIntentHandler (uses SearchWithAsrFallbackAsync — only compound-word variants, still exact search)
- PlayVideoIntentHandler (exact title search → NotFoundVideo)
- SearchMediaIntentHandler (SearchWithAsrFallbackAsync, unified content types → MediaNotFound)
- PlayPlaylistIntentHandler (exact, FuzzyMatch only for disambiguation → NotFoundPlaylist)
- PlayEpisodeIntentHandler (exact series name → NotFoundSeries)
- PlayBookIntentHandler (exact, FuzzyMatch only for disambiguation → NotFoundBook)
- PlayChannelIntentHandler (exact channel name → NotFoundChannel)
- PlayPodcastIntentHandler (exact podcast name → NotFoundPodcast)
- BrowseLibraryIntentHandler (exact filter when provided → NoBrowseResults)

Right-altitude fix: a shared BaseHandler phonetic-fallback helper (reuse FuzzyMatcher / Double Metaphone — same primitive ArtistSearch and SongNgramIndex already use), adopted by each handler. Avoids 10 ad-hoc reimplementations.

Cross-language: handlers are locale-agnostic (one C# path serves all 17 locales), so the fix applies to every language automatically. Double Metaphone is acceptable for Romance locales (it-IT/es/fr/pt/de per the survey) but weak for non-Romance (ja-JP/ar-SA/hi-IN); locale-aware phonetics (cf. PhoneticSynonymGenerator) is a possible follow-up. The fix does NOT depend on the catalog-backed slot (JF-335) — it operates on the free-text slot value.

Related: JF-336 (PlayAlbum, first instance + artist-fallback-threshold sub-issue), JF-335 (catalog sync multilingual — complementary, improves slot-fill confidence).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Add a shared helper in BaseHandler (e.g. SearchItemsPhoneticAsync(query, itemTypes, user)) that, when the exact Jellyfin searchTerm query returns 0, fetches candidate items and phonetic/fuzzy-matches names via the existing FuzzyMatcher (Double Metaphone) — reusing the same primitive PlayArtistSongs (ArtistSearch) and FindSong (SongNgramIndexService) already use. Keep the miss path cheap (cold path only).
- [ ] #2 Adopt the helper in the 9 name-searching handlers that currently do exact-only search: PlaySong, PlayVideo, SearchMedia, PlayPlaylist, PlayEpisode, PlayBook, PlayChannel, PlayPodcast, BrowseLibrary. (PlayAlbum is JF-336 — do it first as the reference adoption, then generalize.)
- [ ] #3 Each adoption: on exact-search 0 results, try the phonetic fallback BEFORE the current 'not found' / wrong cross-media fallback. Verify one representative repro per media type via the Jellyfin simulator + logs (e.g. accented/transcribed title resolves to the library item).
- [ ] #4 Locale dimension: the helper is locale-agnostic (one C# path, all 17 locales). Verify Double Metaphone quality is acceptable for Romance locales (it-IT/es-ES/fr-FR/pt-BR/de-DE); document the known weakness for non-Romance locales (ja-JP/ar-SA/hi-IN) and whether locale-aware phonetics (cf. PhoneticSynonymGenerator) are needed as a follow-up.
- [ ] #5 No regression: exact-name playback unchanged for all 9 handlers; PlayArtistSongs/FindSong untouched; run the existing NLU/E2E fixtures.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
LIVE REPRO 2026-07-22 (web console + on-device):
- 'chiedi a mia collezione di riprodurre il cantante sol coffin' → routed to PlaySongIntent (song='sol coffin'), NOT PlayArtistSongsIntent despite the 'cantante' carrier word.
- Handler artist fallback DID fire (tier_reached=4) but found 0 results for 'sol coffin' → 'Soul Coughing'. Double Metaphone encodes both 'sol' and 'soul' identically, so the phonetic layer is not the problem — the Jellyfin search query construction is.
- Same utterance with correct spelling ('soul coughing') routes correctly to PlayArtistSongsIntent via the catalog-backed JellyfinArtist slot and plays.

TWO DISTINCT BUGS:
1. NLU routing: 'il cantante X' should force PlayArtistSongsIntent, but the model routes to PlaySongIntent even with the carrier word. The 'cantante' samples exist in the it-IT model but aren't winning the NLU competition.
2. Search miss: handler artist fallback can't match 'sol coffin'→'Soul Coughing' across all 4 search tiers (SearchTerm, NameStartsWith×2, NameContains). The query construction against Jellyfin's search index needs investigation.

VERIFIED ROOT CAUSE 2026-07-22 (throwaway diagnostic against the real FuzzyMatcher/DoubleMetaphone code):

- The prior note's assumption was WRONG. Double Metaphone does NOT encode 'sol coffin' and 'Soul Coughing' identically. Whole-string codes diverge: 'sol coffin'->SLKF, 'Soul Coughing'->SLKJ.

- Per-word: 'sol'->SL == 'soul'->SL (match); 'coffin'->KFN vs 'coughing'->KJNK (diverge). So whole-string PhoneticCodesMatch returns false -> the +15 phonetic bonus is withheld -> raw Levenshtein PartialRatio score = 50 < threshold 60 -> tier-4 fuzzy-all matched=False -> 0 artists -> NotFound. The phonetic layer IS the root cause.

- Logs confirmed: ArtistSearch tier_reached=4 results=0 for 'sol coffin'; the same search returns tier=1 score=100 for correctly-spelled 'soul coughing'.

- Two-gate finding: PlayArtistSongs (intended artist route) uses the normal 60 threshold on the phonetic overload, so fixing the phonetic scoring resolves the intended 'il cantante X' / 'la band X' utterances directly. PlaySong's cross-media fallback re-scores NON-phonetically at threshold 85 (CrossMediaArtistThreshold) — a deliberately conservative path; a 1/2-token, Levenshtein-50 match being rejected there is correct, not a bug. Left intact.

FIX APPLIED (FuzzyMatcher.cs, phonetic FindBestMatchWithScore overload):

- Added token-level phonetic matching as a fallback after the whole-string PhoneticCodesMatch check fails. Tokenize query + candidate into words, Double-Metaphone-encode each; if >= PhoneticTokenMatchFraction (0.5) of query-word codes match any candidate-word code, apply the same +15 bonus.

- Query token codes computed once and cached across the search; candidate words encoded lazily per-candidate ONLY when whole-string phonetic failed AND score < ContainmentScore AND score >= PhoneticTokenMinBaseScore (40). Bounds cost on the cold tier-4 path.

- Result for the repro: 'sol coffin' base score 50 + 15 bonus = 65 >= 60 -> match. Acceptance test locks this at unit level.

OUT OF SCOPE (separate follow-ups, NOT addressed here):

- NLU carrier routing ('il cantante X' -> PlaySongIntent). The it-IT model already has 'il cantante {musician}' samples; on-device ASR bleeds the article and PlaySong's 80 samples win (documented profile-nlu vs on-device divergence). Model-side fixes have diminishing returns; the handler phonetic recovery absorbs the misroute. If a future session wants to harden the carrier, that is an NLU-layer task.

- The PlaySong cross-media 85-threshold gate (correct conservative behavior).

- Locale-aware phonetics (PhoneticSynonymGenerator) for non-Romance locales — still a possible follow-up per AC #4.

ATTEMPT REVERTED 2026-07-22 (token-level phonetic in FuzzyMatcher — REJECTED by /code-review high):

- Implemented token-level Double Metaphone matching in the phonetic FindBestMatchWithScore overload (fraction-gated >=0.5, min-base-score 40). Made 'sol coffin'->'Soul Coughing' match (50 + 15 bonus = 65 >= 60). 2586 tests passed, clean Release build.

- /code-review high (opus) found a BLOCKING correctness regression, independently verified against the real code: 'sol coffin' ALSO matches 'Soul Train' (score 65) when Soul Coughing is absent — a false positive the existing whole-string path correctly rejected.

- ROOT REASON the token-fraction approach is flawed: at this scoring granularity, 'sol coffin'->'Soul Coughing' (intended) and 'sol coffin'->'Soul Train' (false positive) are INDISTINGUISHABLE. Both score 50 on PartialRatio (verified by replication). Both are 1/2 phonetic-token matches (sol==SL==soul matches; coffin=KFN matches neither coughing=KJNK nor train=LRN). The discriminator would have to be per-word Levenshtein on the non-matching token (coffin/coughing closer than coffin/train), which PartialRatio's coarse sliding window does not reflect.

- Decision (user, 2026-07-22): REVERT. No regression shipped. The code change and its tests were discarded (git checkout HEAD). Suite back to 2584 green.

REDIRECTED FIX — catalog phonetic-synonym layer (the CORRECT altitude for ASR pronunciation distortion):

- The mechanism designed for exactly this problem is PhoneticSynonymGenerator (used by LibrarySyncService/JF-335 to populate the JellyfinArtist slot with it-IT phonetic synonyms for English names). A proper Italian generator should produce a synonym for 'Soul Coughing' that matches how an Italian speaker says it ('sol coffin'/'soul caffin'). Then the NLU slot resolves the utterance directly — no handler fuzzy recovery needed.

- The repro ('la band sol coffin' routed to PlayArtistSongs with raw slot text 'sol coffin', still not found) indicates either (a) catalog sync hasn't populated 'Soul Coughing' with a matching synonym, or (b) PhoneticSynonymGenerator for it-IT doesn't emit a 'sol'-style variant. Investigate PhoneticSynonymGenerator output for 'Soul Coughing' and whether catalog sync ran for this artist.

- This is a separate task from this umbrella. The handler fuzzy layer should remain conservative (reject 1/2-token phonetic matches) — a wrong-artist substitution is worse than a clean not-found.

NLU carrier routing ('il cantante X' -> PlaySongIntent): still a separate, lower-priority NLU-layer follow-up (on-device ASR divergence; model already has the samples). Not addressed.

CATALOG-LAYER INVESTIGATION COMPLETE 2026-07-22 -> spawned JF-362:

- Verified against the real ItalianPhoneticSynonyms.Generate: 'Soul Coughing' produces synonyms ['Soul Cofing', 'i Soul Cofing'] — neither matches the spoken 'sol coffin'.

- Two missing Italian-pronunciation rules identified: soul->sol (silent-l vocalized), coughing->coffin (ough->off + ing->in + f-doubling). The current ough->of transform yields 'Cofing' (keeps 'ing').

- Recommended narrow fix = explicit whole-word override map (NOT broad ing->in regex, which would pollute countless names). See JF-362 for the full spec + verification caveats (confirm slot-resolution mechanism + that catalog sync ran for the artist).

- This confirms the handler fuzzy layer is the wrong altitude (reverted) and the catalog synonym generator is the right one.

RECONCILIATION 2026-07-22 (user flagged the task description was stale; verified against git history):

- This umbrella is SUBSTANTIALLY COMPLETE at the handler layer. The shared helper shipped as BaseHandler.SearchItemsFuzzyAsync (BaseHandler.cs:1304) — originally named SearchItemsPhoneticAsync in commit c14475d, renamed since. It is adopted in 8 handlers: PlayVideo, PlayEpisode, PlayBook, PlayChannel, PlayPlaylist, SearchMedia, PlayPodcast, BrowseLibrary (MORE than c14475d's commit message claimed — that listed SearchMedia/PlayPodcast/BrowseLibrary as 'remaining', but they now use it).

- PlaySong deliberately does NOT use SearchItemsFuzzyAsync (Audio catalog too large; see PlaySongIntentHandler.cs:207 NOTE) — it uses the ArtistSearch cross-media fallback instead.

- SearchItemsFuzzyAsync uses the NON-phonetic FuzzyMatcher.FindBestMatchWithScore (3-arg, pure Levenshtein, threshold GetDefaultThreshold=60). Double Metaphone is NOT in this path. DM only runs in ArtistSearch (artist-specific) via the phonetic overload.

- The 'sol coffin'->'Soul Coughing' miss is NOT fixable at the handler layer: (a) PlaySong's ArtistSearch tier-4 DM path returns 0 (verified); (b) even SearchItemsFuzzyAsync Levenshtein gives 'sol coffin'/'Soul Coughing' = 50 < 60; (c) any change to make it match also matches 'Soul Train' (verified false positive, see reverted attempt above). Confirms the fix belongs at the catalog-slot layer = JF-362.

- AC #1/#2/#3 are effectively DONE (the helper exists + 8 adoptions). The remaining real gap is AC #4 (locale phonetic quality) which folds into JF-362. This task can be closed as 'handler-layer work complete; catalog-synonym gap tracked in JF-362'.
<!-- SECTION:NOTES:END -->

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
