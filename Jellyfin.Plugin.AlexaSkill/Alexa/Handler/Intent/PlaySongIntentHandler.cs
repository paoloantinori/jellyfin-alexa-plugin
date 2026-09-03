using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlaySongIntent requests.
/// </summary>
public class PlaySongIntentHandler : BaseHandler
{
    private static readonly string[] SongCarrierPhrases = new[]
    {
        // Phrases that appear right before {song} in utterance templates.
        // Within each locale group, longer phrases come first (e.g. "the song called " before "the song ").
        // English
        "the song called ", "a song called ",
        "that song ", "the song ", "the track ", "a song ", "a track ",
        // Italian
        "la canzone ", "il brano ", "il pezzo ", "la traccia ",
        "una canzone ", "un brano ", "un pezzo ", "una traccia ",
        "canzone ", "brano ", "pezzo ", "traccia ",
        // German
        "das lied ", "das stück ", "den titel ",
        "ein lied ", "ein stück ",
        // Spanish
        "la canción ", "el tema ", "una canción ", "canción ",
        // French
        "la chanson ", "le titre ", "le morceau ", "une chanson ", "chanson ",
        // Dutch
        "het liedje ", "het nummer ", "liedje ", "nummer ",
        // Portuguese
        "a música ", "a faixa ", "música ",
    };

    // Generic words meaning "music/songs" across supported locales.
    // When Alexa captures one of these as the {song} slot alongside a {musician} slot,
    // the user means "play music by <artist>" not "play a song titled 'music'".
    internal static readonly HashSet<string> GenericMusicWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // English
        "music", "songs", "song", "track", "tracks", "tune", "tunes",
        // Italian
        "musica", "canzoni", "canzone", "brani", "brano", "pezzo", "traccia",
        // German
        "musik", "lieder", "lied", "titel", "song",
        // Spanish
        "música", "musica", "canciones", "canción", "cancion", "tema", "temas",
        // French
        "chansons", "chanson", "musique", "morceau", "titre", "titres",
        // Dutch
        "muziek", "liedjes", "liedje", "nummer", "nummers",
        // Portuguese
        "canções", "cancoes", "músicas", "musicas", "faixa", "faixas",
    };

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly DeviceQueueManager? _queueManager;
    private readonly ISongNgramIndex? _songNgramIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaySongIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="songNgramIndex">Optional in-memory song n-gram index for the exact-miss title fallback (JF-383).</param>
    public PlaySongIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        DeviceQueueManager? queueManager = null,
        ISongNgramIndex? songNgramIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _queueManager = queueManager;
        _songNgramIndex = songNgramIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlaySong, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a specific song by name, optionally filtered by artist.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A skill response.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? songQuery = intentRequest.Intent.Slots?.TryGetValue("song", out var songSlot) == true ? songSlot.Value : null;
        string? musicianQuery = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlaySong: entered, locale={Locale}", locale);

        // Escape hatch from the elicitation trap (shared CancelWords helpers): while OUR
        // song-name Dialog.ElicitSlot is open, a stop/cancel word gets captured into a
        // slot (dialogState IN_PROGRESS) instead of routing to AMAZON.Stop/Cancel. ANY
        // slot counts (shared AnySlotIsCancelWord), not just song/musician. Gated on
        // IN_PROGRESS deliberately (JF-445): unlike FindSong, this elicit persists NO
        // session state ("the dialog lives Amazon-side"), so there is no open-flow marker
        // to distinguish a STARTED sibling misroute from a fresh legitimate search, and no
        // force-route delivers sibling requests here (the FindSongSessionData override is
        // the only one). A first-invocation search for a song actually titled "Stop", or
        // for the artist "Basta", must still run. Runs BEFORE the warming gate so an open
        // flow still cancels during the cold-start window (JF-419.2 contract, review
        // round 2).
        if (Util.CancelWords.IsDialogInProgress(intentRequest)
            && Util.CancelWords.AnySlotIsCancelWord(intentRequest, locale))
        {
            Logger.LogInformation("PlaySong: captured cancel word during open elicit (song='{Song}'), ending flow", songQuery);
            return ResponseBuilder.Tell(ResponseStrings.Get("FindSongCancelled", locale));
        }

        // JF-419.3 per-path gates, both BEFORE the "searching" announcement: a
        // musician-scoped request resolves the artist first (targeted, bounded
        // queries) and never touches the song index, so it gates on the ARTIST
        // index like PlayArtistSongs; a title-only request's fast path IS the song
        // n-gram index, whose full-catalog cold window outlasts the artist's.
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            GuardIndexReady(_artistIndex);
        }
        else
        {
            GuardIndexReady(_songNgramIndex);
        }

        if (string.IsNullOrWhiteSpace(songQuery))
        {
            Logger.LogDebug("PlaySong: empty song slot, eliciting via Dialog.ElicitSlot");
            return BuildSongElicitResponse(locale);
        }

        // JF-467: primary-path music gate (shared contract on IfMediaTypeDisabled).
        // Placement here is forced by two constraints: the warming gate must stay
        // before the slot elicitation (enabled cold-start behavior is unchanged) and
        // the music gate must stay after it (empty-slot prompt precedence), so a
        // disabled+valid-slot request during the cold window can surface the warming
        // message once; once warm, the disabled response is immediate.
        SkillResponse? musicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
        if (musicDisabled != null)
        {
            return musicDisabled;
        }

        songQuery = StripSongCarrierPhrase(songQuery);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;
        if (!string.IsNullOrWhiteSpace(musicianQuery))
        {
            Logger.LogDebug("PlaySong: searching for artist filter='{Musician}'", musicianQuery);
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musicianQuery, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                locale, cancellationToken).ConfigureAwait(false);

            Logger.LogDebug("PlaySong: artist search returned {Count} results for '{Musician}'", artists.Count, musicianQuery);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByArtist", locale, musicianQuery));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        // When the song query is a generic word like "musica"/"music" and we have
        // a valid artist, skip the song search and go straight to artist playback.
        // This avoids 1-4 wasted DB queries searching for a literal "music" song.
        if (GenericMusicWords.Contains(songQuery)
            && !string.IsNullOrWhiteSpace(musicianQuery) && artistsIds.Count > 0)
        {
            Logger.LogInformation(
                "PlaySong: song slot '{SongQuery}' is a generic music word, playing artist songs for '{Artist}'",
                songQuery, matchedArtistName);
            return await PlayArtistSongsFallback(
                artistsIds[0], matchedArtistName!, jellyfinUser!, user, session, context, locale, cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<BaseItem> songs = await SearchWithAsrFallbackAsync(songQuery,
            searchTerm =>
            {
                var q = new InternalItemsQuery()
                {
                    User = jellyfinUser,
                    Recursive = true,
                    SearchTerm = searchTerm,
                    ArtistIds = artistsIds.ToArray(),
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true)
                };
                ApplyLibraryFilter(q, user, _libraryManager);
                return RetryAsync(() => _libraryManager.GetItemList(q), "GetSongs", cancellationToken);
            }).ConfigureAwait(false);
        Logger.LogDebug("PlaySong: Jellyfin returned {SongCount} songs for query='{SongQuery}'", songs.Count, songQuery);

        // NOTE: PlaySong does NOT use SearchItemsFuzzyAsync - the Audio catalog is too large
        // (thousands of tracks -> 11s scan -> exceeds Alexa's 8s timeout -> InvalidResponse).
        // Song search is handled by SearchWithAsrFallbackAsync above, the keyword-matcher
        // fallbacks below (JF-383, bounded/O(1)), and the n-gram index via FindSongIntent.
        // The generic fuzzy helper is safe for smaller catalogs (albums, videos, books,
        // channels, playlists) but NOT for Audio.

        // JF-383: the exact SearchTerm query misses abbreviated tagged titles ("decatur
        // street" vs "Decatur St."). Before giving up, try the keyword matchers, which
        // canonicalize abbreviations. Both fallbacks are bounded and respect the NOTE
        // above: with a musician, fetch only that artist's songs (NOT the full catalog)
        // and score them; without one, consult the n-gram index (O(1) lookup).
        if (songs.Count == 0)
        {
            if (artistsIds.Count > 0)
            {
                IReadOnlyList<BaseItem> artistSongs = await GetArtistSongsAsync(
                    jellyfinUser, user, _libraryManager, artistsIds.ToArray(),
                    "GetSongsByArtistTitleFallback", cancellationToken,
                    limit: 500).ConfigureAwait(false);

                var keywordTokens = Util.KeywordMatcher.Tokenize(songQuery, locale);

                // JF-384: the helper's phonetic fallback prevents one accent-drifted
                // keyword from vetoing the match.
                var scoredByKeywords = Util.KeywordMatcher.ScoreWithPhoneticFallback(artistSongs, keywordTokens, locale, _config.PhoneticSongSearchEnabled);
                if (scoredByKeywords.Count > 0)
                {
                    songs = scoredByKeywords.Select(s => s.Item).ToList();
                    Logger.LogDebug("PlaySong: title fallback (artist songs + keyword matcher) matched {Count} songs for query='{Query}'", songs.Count, songQuery);
                }
            }
            else
            {
                // JF-440: the ONE index lookup chain (was a private copy with its own
                // readiness contract). The JF-419.3 entry gate guarantees readiness;
                // null/disabled returns empty and the DB fallback below owns the query.
                // The filter is resolved to physical library folder ids (JF-455), the
                // same id space the index maps hold; null means unrestricted.
                var keywordTokens = Util.KeywordMatcher.Tokenize(songQuery, locale);
                Guid[]? songTopParents = Util.LibraryFilter.ResolveForUser(user, _libraryManager, Logger);
                var scoredByIndex = _songNgramIndex.SearchWithPhoneticFallback(keywordTokens, locale, songTopParents, _config.PhoneticSongSearchEnabled);
                if (scoredByIndex.Count > 0)
                {
                    songs = scoredByIndex.Select(s => s.Item).ToList();
                    Logger.LogDebug("PlaySong: title fallback (n-gram index) matched {Count} songs for query='{Query}'", songs.Count, songQuery);
                }
            }
        }

        if (songs.Count == 0 && !string.IsNullOrWhiteSpace(musicianQuery) && artistsIds.Count > 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByNameAndArtist", locale, songQuery, matchedArtistName!));
        }
        else if (songs.Count == 0)
        {
            // Cross-media-type fallback: no songs found and no musician slot.
            // The NLU may have routed an artist name to PlaySongIntent by mistake
            // (e.g. "mettere gli strokes" → song="strokes" instead of artist="strokes").
            // JF-446: the shared gate (TryEntityFallbackAsync) owns the word-count guard
            // (on TOKENIZED words, so locale articles no longer count against the limit)
            // and the acceptance thresholds (phonetic matcher + the JF-363
            // Confirm/AutoServe band for sub-strict matches).
            SkillResponse? artistFallback = await TryEntityFallbackAsync(
                songQuery, jellyfinUser!, user, session, context, locale,
                _libraryManager, _userDataManager, _queueManager, _artistIndex,
                "PlaySong", cancellationToken,
                notFoundMediaType: DisambiguationHelper.MediaTypeSong).ConfigureAwait(false);
            if (artistFallback != null)
            {
                return artistFallback;
            }

            // JF-345: song-to-album cascade, AFTER the artist cascade (see
            // TryAlbumFallbackAsync for the precedence rationale: a bare "play abbey
            // road" finds no artist, so the album tier recovers it here, while a bare
            // "play metallica" still plays the artist above and never reaches this
            // tier). Bare album titles route to PlaySong in the free-text locales
            // (guaranteed in the five English locales PR #15 trimmed the carriers
            // from; a coin flip in the other 11, which still ship them) and used
            // to dead-end in the song not-found below.
            SkillResponse? albumFallback = await TryAlbumFallbackAsync(
                songQuery, jellyfinUser!, user, session, context, locale,
                _libraryManager, _userDataManager, _queueManager,
                "PlaySong", cancellationToken).ConfigureAwait(false);
            if (albumFallback != null)
            {
                return albumFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundSongByName", locale, songQuery));
        }

        if (songs.Count > 1)
        {
            Logger.LogDebug("PlaySong: {Count} songs matched, running disambiguation", songs.Count);
            var (missOutcome, missResponse) = HandleFuzzyMiss(
                songQuery,
                songs,
                s => s.Name,
                best => new List<(Guid, string)> { (best.Id, best.Name) },
                DisambiguationHelper.MediaTypeSong,
                locale,
                best =>
                {
                    songs = new List<BaseItem> { best };
                    var qi = new List<QueueItem> { new() { Id = best.Id } };
                    session.NowPlayingQueue = qi;
                    session.FullNowPlayingItem = best;
                    string iid = best.Id.ToString();
                    int fuzzOffset = GetItemResumeOffset(best, jellyfinUser!, locale);
                    if (fuzzOffset > 0)
                    {
                        Logger.LogInformation(
                            "PlaySong fuzzy auto-play: resuming '{SongName}' from {OffsetMs}ms",
                            best.Name, fuzzOffset);
                    }

                    return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(iid, user), iid, best, user, context, fuzzOffset, announceLocale: locale);
                },
                user: user);

            if (missOutcome != FuzzyMissOutcome.NotFound)
            {
                return missResponse!;
            }

            var matches = songs.Take(3).Select(s => (s.Id, s.Name, (string?)GetImageUrl(s.Id.ToString("N"), user))).ToList();
            return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeSong, locale, context);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        queueItems.Add(new QueueItem { Id = songs[0].Id });

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = songs[0];

        string item_id = songs[0].Id.ToString();

        int offsetMs = GetItemResumeOffset(songs[0], jellyfinUser!, locale);

        if (offsetMs > 0)
        {
            Logger.LogInformation(
                "PlaySong: resuming '{SongName}' from {OffsetMs}ms (saved position)",
                songs[0].Name, offsetMs);
        }

        Logger.LogDebug(
            "PlaySong: returning AudioPlayer, itemId={ItemId}, song='{SongName}', offsetMs={OffsetMs}",
            item_id, songs[0].Name, offsetMs);
        return BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(item_id, user), item_id, songs[0], user, context, offsetMs, announceLocale: locale);
    }

    /// <summary>
    /// Song-name elicitation via Dialog.ElicitSlot (context-preserving, JF-413): the next
    /// utterance fills the song slot inside the PlaySongIntent dialog instead of falling
    /// through to general NLU, and an already-filled musician slot survives the
    /// round-trip. The shared builder declares BOTH intent slots in updatedIntent
    /// (Amazon rejects partial updatedIntent, live INVALID_RESPONSE 2026-08-28).
    /// PlaySongIntent is registered in dialog.intents in all 17 locales (verified
    /// 2026-08-29). MarkOthersInactive with no active keys (JF-398): the elicit owns no
    /// session state of its own (the dialog lives Amazon-side), so any OTHER flow's
    /// stale state must not ride along.
    /// </summary>
    /// <param name="locale">The request locale, for the prompt string.</param>
    /// <returns>The elicitation response.</returns>
    private static SkillResponse BuildSongElicitResponse(string locale)
        => BuildElicitSlotResponse(
            IntentNames.PlaySong,
            IntentNames.Slots.Song,
            new[] { IntentNames.Slots.Song, IntentNames.Slots.Musician },
            ResponseStrings.Get("ElicitSongName", locale));

    // Alexa's NLU can misalign slot boundaries, causing carrier phrases like
    // "la canzone" to bleed into the slot value. Strip them before searching.
    internal static string StripSongCarrierPhrase(string query)
    {
        string trimmed = query.TrimStart();
        foreach (string phrase in SongCarrierPhrases)
        {
            if (trimmed.StartsWith(phrase, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed.Substring(phrase.Length).TrimStart();
            }
        }

        return query;
    }

    /// <summary>
    /// Check server-side playback progress for a single item and return resume offset in ms.
    /// Returns 0 if the item has no saved position or is marked as fully played.
    /// </summary>
    private int GetItemResumeOffset(BaseItem item, Jellyfin.Database.Implementations.Entities.User jellyfinUser, string locale)
    {
        UserItemData? data = _userDataManager.GetUserData(jellyfinUser, item);
        if (data == null)
        {
            Logger.LogDebug("PlaySong resume check: no UserItemData for '{SongName}'", item.Name);
            return 0;
        }

        Logger.LogDebug(
            "PlaySong resume check: '{SongName}' — PositionTicks={Ticks}, Played={Played}, IsFavorite={Fav}",
            item.Name, data.PlaybackPositionTicks, data.Played, data.IsFavorite);

        if (data.PlaybackPositionTicks > 0 && !data.Played)
        {
            int offset = (int)TimeSpan.FromTicks(data.PlaybackPositionTicks).TotalMilliseconds;
            Logger.LogInformation(
                "PlaySong resume check: '{SongName}' has saved position {Ticks} ticks ({OffsetMs}ms), will resume",
                item.Name, data.PlaybackPositionTicks, offset);
            return offset;
        }

        Logger.LogDebug(
            "PlaySong resume check: '{SongName}' — no resume needed (ticks={Ticks}, played={Played})",
            item.Name, data.PlaybackPositionTicks, data.Played);
        return 0;
    }

    /// <summary>
    /// Fallback when the song slot contains a generic music word (e.g. "musica"/"music")
    /// but the musician slot has a valid artist. Plays the artist's songs instead of
    /// returning "not found". Delegates to <see cref="BaseHandler.BuildArtistSongsResponseAsync"/>.
    /// </summary>
    private Task<SkillResponse> PlayArtistSongsFallback(
        Guid artistId,
        string artistName,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        CancellationToken cancellationToken,
        string? announcement = null)
    {
        return BuildArtistSongsResponseAsync(
            artistId, artistName, jellyfinUser, user, session, context, locale,
            _libraryManager, _userDataManager, _queueManager,
            "PlaySong fallback",
            announcement, cancellationToken);
    }
}
