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
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The JF-315 cross-media-fallback collaborator (census cluster F, extracted from
/// BaseHandler batch 7): the shared entity-fallback gates for greedy intents that
/// misroute an artist/song query (TryEntityFallbackAsync with its JF-363
/// suggestion band and the JF-382 coincidental-containment downgrade), the inverse
/// song fallback (TrySongFallback), the JF-471 decision-point acceptance
/// predicate, the JF-412 embedded-winner walk (FindBestNonEmbeddedMatch), the
/// JF-446/JF-479 word-count guard, the JF-363 offer ask, and the play-shape sinks
/// those fallbacks resolve through (BuildArtistSongsResponseAsync with the
/// progressive artist queue, BuildSingleSongResponse, and the JF-345
/// ApplyAnnouncement override).
/// JF-408 DISCIPLINE (fuzzy-recall-vs-judgment-layers, two reverted relocations):
/// the auto-play DECISION predicates moved here WITH their call sites as verbatim
/// code (the JF-382 end-gates inside TryEntityFallbackAsync, the JF-363 band
/// precedence, and PassesArtistMatchAcceptance keep their exact pre-extraction
/// semantics); this class must never grow a NEW judgment shape (that is a
/// JF-408-class decision, not cleanup).
/// NAMESPACE PLACEMENT, deliberate (divergence from the Util home of
/// SearchService/PlaybackLaunchBuilder): BuildCrossMediaArtistOfferAsk is wired
/// into DisambiguationHelper's session-attribute vocabulary, which lives in THIS
/// namespace; no Util file references the Handler namespace today and this
/// extraction keeps it that way. The collaborator still sits BELOW BaseHandler:
/// it receives the PlaybackLaunchBuilder at composition and never calls back into
/// the handler.
/// STATELESS by construction (readonly config + logger + launch builder), so the
/// singleton-handlers constraint BaseHandler documents is preserved.
/// COMPOSITION DECISION (the SearchService/PlaybackLaunchBuilder precedent):
/// BaseHandler constructs one instance per handler in its own ctor and exposes it
/// as the protected-internal readonly <c>CrossMedia</c> property; handlers consume
/// it through that inherited get-only property, so the 61 handler ctors stay
/// untouched.
/// </summary>
public sealed class CrossMediaFallback
{
    /// <summary>
    /// Minimum fuzzy-match score required for a cross-media-type artist fallback
    /// (album/song not found → play an artist instead). Higher than the normal
    /// default threshold because a wrong-artist false positive is worse than a
    /// clean "not found". The observed false positives ("la ballata del genesio"
    /// → "Lamb", "disco jazz caffè" → "Uazz") both scored 75. Apply via
    /// <c>FuzzyMatcher.GetEffectiveThreshold(user, CrossMediaArtistThreshold)</c>
    /// so a user who raised FuzzyMatchThreshold is still respected. Shared by the
    /// PlayAlbum and PlaySong cross-media fallbacks (JF-339).
    /// </summary>
    public const int CrossMediaArtistThreshold = 85;

    /// <summary>
    /// Maximum word count for a cross-media artist fallback query. A long query is a
    /// poor artist query and a wrong-artist false positive is worse than a clean
    /// "not found" (observed: "la ballata del genesio" → "Lamb"). Shared by the PlaySong
    /// cross-media fallback and the greedy-intent TryEntityFallbackAsync.
    /// </summary>
    public const int CrossMediaArtistMaxWords = 2;

    /// <summary>
    /// JF-439: minimum KeywordMatcher score for the inverse cross-media song
    /// fallback to auto-play (the pre-extraction BaseHandler home since JF-440,
    /// moved here JF-315 batch 7). Live calibration
    /// (minix, 12766 songs): the WRONG half-coverage phonetic hit ('rolling
    /// stones' -> 'Like a Rolling Stone') scores ~34; the RIGHT near-full phonetic
    /// match ('screenwriters blues' -> 'Screenwriter's Blues') scores ~72; exact
    /// full coverage scores ~105. The bar at 65 keeps 31 points of rejection margin
    /// over the wrong-substitution class and 7 over the legitimate phonetic class.
    /// The artist-side mirror gates fuzzy scores at 85 (different scale, not shared).
    /// </summary>
    public const double CrossMediaSongThreshold = 65.0;

    /// <summary>
    /// Reorder candidates so the most-played/liked items come first: favorite or
    /// liked items, then play count, then community rating, then name. The fetch
    /// order of the artist-songs play paths (BuildArtistSongsResponseAsync here,
    /// PlayArtistSongsIntentHandler's own query) and the persisted
    /// QueueContinuation.SortOrder contract (PlaybackNearlyFinished replays it).
    /// </summary>
    public static readonly (ItemSortBy SortBy, SortOrder Order)[] PopularitySort =
    {
        (ItemSortBy.IsFavoriteOrLiked, SortOrder.Descending),
        (ItemSortBy.PlayCount, SortOrder.Descending),
        (ItemSortBy.CommunityRating, SortOrder.Descending),
        (ItemSortBy.SortName, SortOrder.Ascending)
    };

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly PlaybackLaunchBuilder _launch;
    private readonly int _requestTimeoutMs;

    /// <summary>
    /// Initializes a new instance of the <see cref="CrossMediaFallback"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration (thresholds, shuffle and announce toggles).</param>
    /// <param name="logger">The logger (BaseHandler passes its own instance so moved log statements keep their pre-extraction category).</param>
    /// <param name="launch">The playback-launch collaborator (stream URLs and the AudioPlayer.Play response chokepoint the play-shape sinks build through).</param>
    /// <param name="requestTimeoutMs">The Alexa request timeout budget in milliseconds (BaseHandler passes its own const, single-sourced there: it matches the controller's 6-second cancellation). Passed at composition, the SearchService precedent, so this class holds no BaseHandler reference.</param>
    public CrossMediaFallback(PluginConfiguration config, ILogger logger, PlaybackLaunchBuilder launch, int requestTimeoutMs)
    {
        _config = config;
        _logger = logger;
        _launch = launch;
        _requestTimeoutMs = requestTimeoutMs;
    }

    /// <summary>
    /// JF-412: best fuzzy match that is NOT an embedded-containment winner. The single
    /// match shape (find best, refuse if embedded) aborted the whole tier on a degenerate
    /// winner: live shape "walls for cup" over the real catalog ranks album "O" first at 90
    /// (the JF-408/478 incident class, correctly refused) with "Waltz for Koop" next at 61
    /// (plain partial-ratio; the 3-arg FindBestMatchWithScore carries no phonetic boost),
    /// above threshold, and the old code answered not-found. The guard must block only the
    /// embedded candidate: walk the best-match results, skipping every embedded winner, and
    /// take the first eligible one. The loop is load-bearing: FindBestMatchWithScore keeps
    /// the maxLenDiff length-band filter and its first-crossing-90 early exit, which
    /// RankMatches would change. Same-name ties pick arbitrarily (JF-341). Bounded: each
    /// iteration removes one candidate, and degenerate embedded winners are rare.
    /// </summary>
    public (BaseItem Item, int Score)? FindBestNonEmbeddedMatch(
        string query, IReadOnlyList<BaseItem> candidates, Func<BaseItem, string> selector, int threshold)
    {
        var remaining = new List<BaseItem>(candidates);
        while (remaining.Count > 0)
        {
            var best = FuzzyMatcher.FindBestMatchWithScore(query, remaining, selector);
            if (best is not { } match || match.Score < threshold)
            {
                return null;
            }

            if (Util.ArtistSearch.IsEmbeddedContainment(query, selector(match.Item)))
            {
                // Information, not Debug: the JF-408/478 device correlations (corr=80bb4642)
                // were read from exactly these refusal lines; keep them visible.
                _logger.LogInformation(
                    "Embedded-containment match '{Name}' score={Score} for query='{Query}' skipped (JF-408/478/JF-412), walking down the ranking",
                    selector(match.Item), match.Score, query);
                remaining.Remove(match.Item);
                continue;
            }

            return match;
        }

        return null;
    }

    /// <summary>
    /// Shared word-count guard for BOTH cross-media fallback gates (artist and album):
    /// tokenizes the slot text with the locale stop-word set and rejects queries whose
    /// content words exceed <see cref="CrossMediaArtistMaxWords"/> (a long query is a
    /// poor artist AND a poor album guess; JF-295's original rationale, JF-446 consolidation,
    /// JF-345 extension to the album gate). The guard counts spoken WORDS that carry
    /// content, not the tokenizer's alphanumeric fragments: a stylized name with
    /// intra-word punctuation ("P!nk") splits into two tokens but is ONE spoken word,
    /// and counting fragments rejected the JF-479 device shape "dei P!nk floyd"
    /// (article stripped + one spoken name + surname = 2 content words) as three.
    /// The <paramref name="tokens"/> out param keeps the fragment-level tokenize output
    /// (the join the artist gate searches); only the COUNT is word-level.
    /// </summary>
    /// <param name="slotText">The raw slot text.</param>
    /// <param name="locale">The request locale (stop-word set selection).</param>
    /// <param name="fallbackNoun">The fallback noun for the skip log ("artist"/"album").</param>
    /// <param name="logLabel">Log label.</param>
    /// <param name="tokens">The content-word tokens when the guard passes.</param>
    /// <returns>True when the query is short enough to attempt the fallback.</returns>
    public bool PassesCrossMediaWordGuard(string slotText, string locale, string fallbackNoun, string logLabel, out string[] tokens)
    {
        tokens = Util.KeywordMatcher.Tokenize(slotText, locale);
        int contentWords = CountContentWords(slotText, locale);
        if (contentWords == 0 || contentWords > CrossMediaArtistMaxWords)
        {
            _logger.LogDebug(
                "{Label}: skipping {Noun} fallback, {Count} content words in '{Query}' (guard {Max})",
                logLabel, fallbackNoun, contentWords, slotText, CrossMediaArtistMaxWords);
            return false;
        }

        return true;
    }

    /// <summary>
    /// How many whitespace-delimited words of the slot text carry content (their
    /// tokenized form is non-empty, i.e. they are not pure stop words). This is the
    /// word-count the cross-media guard judges on: punctuation inside a spoken word
    /// ("P!nk", "AC/DC") must not split one name into two words against the cap.
    /// JF-479.
    /// </summary>
    /// <param name="slotText">The raw slot text.</param>
    /// <param name="locale">The request locale (stop-word set selection).</param>
    /// <returns>The number of content-carrying spoken words.</returns>
    private static int CountContentWords(string slotText, string locale)
        => slotText
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => Util.KeywordMatcher.Tokenize(w, locale).Length > 0);

    /// <summary>
    /// Gets the effective cross-media artist suggestion behavior for a user, falling back to
    /// the global default. Per-user setting (when explicitly set, i.e. non-null) takes
    /// precedence. Controls whether a sub-strict-threshold artist match (found when a
    /// song/album wasn't) is offered for confirmation, auto-served, or ignored.
    /// </summary>
    public CrossMediaArtistSuggestion GetCrossMediaArtistSuggestion(Entities.User? user)
    {
        if (user?.CrossMediaArtistSuggestion is { } userMode)
        {
            _logger.LogDebug("CrossMediaArtistSuggestion: user={UserId} mode={Mode} source=PerUser", user.Id, userMode);
            return userMode;
        }

        _logger.LogDebug("CrossMediaArtistSuggestion: user={UserId} mode={Mode} source=GlobalDefault", user?.Id, _config.DefaultCrossMediaArtistSuggestion);
        return _config.DefaultCrossMediaArtistSuggestion;
    }

    /// <summary>
    /// Builds the cross-media artist OFFER response (Ask): "I didn't find a song/album
    /// '{query}'. Did you mean the artist {artist}?". Keeps the session open carrying the
    /// single best artist in the standard disambiguation session state, so YesIntentHandler
    /// routes a "yes" to PlayArtist unchanged. Also stashes the original not-found query +
    /// media type so NoIntentHandler can produce the correct clean not-found when the user
    /// declines (otherwise "no" would say "no more matches", which is wrong here). Single
    /// candidate only (per JF-363 design).
    /// </summary>
    /// <param name="query">The not-found song/album query (the raw slot value).</param>
    /// <param name="artist">The single best artist match to offer.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="notFoundMediaType">The media type the user originally asked for:
    /// exactly <see cref="DisambiguationHelper.MediaTypeSong"/> or
    /// <see cref="DisambiguationHelper.MediaTypeAlbum"/> (the wire value NoIntentHandler
    /// switches on for the decline response).</param>
    public SkillResponse BuildCrossMediaArtistOfferAsk(string query, BaseItem artist, string locale, string notFoundMediaType)
    {
        // Const guard (JF-446 simplify): the attribute written below is compared verbatim
        // by NoIntentHandler's decline path, so only the two MediaType consts are valid.
        if (notFoundMediaType != DisambiguationHelper.MediaTypeSong
            && notFoundMediaType != DisambiguationHelper.MediaTypeAlbum)
        {
            _logger.LogWarning(
                "CrossMediaArtistSuggestion: unexpected notFoundMediaType '{MediaType}' (must be MediaTypeSong or MediaTypeAlbum); the decline path will speak the song not-found",
                notFoundMediaType);
        }

        SkillResponse response = SpeechBuilder.AskLocalized(
            "CrossMediaArtistOfferSsml", "CrossMediaArtistOffer", "FuzzySuggestionReprompt", locale, query, artist.Name);

        var matchInfos = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = artist.Id.ToString(), Name = artist.Name }
        };

        response.SessionAttributes = DisambiguationHelper.BuildAttributes(
            matchInfos,
            0,
            DisambiguationHelper.MediaTypeArtist,
            // JF-363: carry the original not-found request so NoIntentHandler can decline to
            // the right "song/album not found" instead of the generic "no more matches".
            (DisambiguationHelper.AttrCrossmediaQuery, query),
            (DisambiguationHelper.AttrCrossmediaType, notFoundMediaType));

        // JF-398: activating the cross-media artist offer (a disambiguation flavor)
        // supersedes any other flow's state.
        ConversationalFlows.MarkOthersInactive(response, ConversationalFlows.DisambiguationKeys);

        _logger.LogDebug(
            "CrossMediaArtistSuggestion: offering artist '{Artist}' for not-found query='{Query}' (type={Type})",
            artist.Name, query, notFoundMediaType);
        return response;
    }

    /// <summary>
    /// Build an AudioPlayer response that plays an artist's songs, sorted by popularity
    /// with optional shuffle and progressive queue continuation.
    /// Shared by PlaySongIntentHandler, PlayAlbumIntentHandler, and others that fall back
    /// to artist playback when the primary media-type search finds nothing.
    /// </summary>
    /// <param name="artistId">The artist's Jellyfin ID.</param>
    /// <param name="artistName">The artist's display name (for messages and logging).</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries.</param>
    /// <param name="user">The Alexa user.</param>
    /// <param name="session">The current session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for querying items.</param>
    /// <param name="userDataManager">User data manager for resume-position lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages (e.g. "PlaySong fallback").</param>
    /// <param name="announcement">Optional speech to announce before playback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A skill response with AudioPlayer directive, or a "no songs" tell.</returns>
    public async Task<SkillResponse> BuildArtistSongsResponseAsync(
        Guid artistId,
        string artistName,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string logLabel,
        string? announcement = null,
        CancellationToken cancellationToken = default)
    {
        var artistSongsQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            // JF-358: IncludeItemTypes=Audio, not MediaTypes=Audio (see PlayArtistSongsIntentHandler).
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            OrderBy = PopularitySort,
            DtoOptions = new DtoOptions(true),
            ArtistIds = new[] { artistId },
            Limit = ProgressiveQueueConstants.GetInitialFetchSize()
        };
        LibraryFilter.ApplyLibraryFilter(artistSongsQuery, user, libraryManager);

        IReadOnlyList<BaseItem> artistItems = await RetryAsync(
            () => libraryManager.GetItemList(artistSongsQuery),
            logLabel + ":GetArtistSongs",
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("{Label}: fetched {Count} songs for artist='{Artist}'", logLabel, artistItems.Count, artistName);

        if (artistItems.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsForArtist", locale, artistName));
        }

        var (sortedItems, startIndex, _) = ResumeMath.SortAndFindResumeIndex(
            artistItems, jellyfinUser, userDataManager, resumePosition: false);

        if (_config.ShuffleArtistSongs)
        {
            var shuffled = sortedItems.ToList();
            Shuffle(shuffled);
            sortedItems = shuffled;
            startIndex = 0;
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < sortedItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = sortedItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = sortedItems[startIndex];

        // Persist queue to device storage for crash recovery
        queueManager?.SetQueue(
            context.System.Device.DeviceID,
            sortedItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        if (artistItems.Count >= ProgressiveQueueConstants.GetInitialFetchSize())
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Artist",
                    ArtistId = artistId,
                    StartIndex = artistItems.Count,
                    TotalCount = int.MaxValue,
                    UserId = jellyfinUser.Id,
                    SortOrder = PopularitySort,
                    Shuffle = _config.ShuffleArtistSongs
                });
        }

        string itemId = sortedItems[startIndex].Id.ToString();
        _logger.LogDebug(
            "{Label}: returning AudioPlayer, itemId={ItemId}, startIndex={StartIndex}, queueSize={QueueSize}",
            logLabel, itemId, startIndex, queueItems.Count);
        SkillResponse response = _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, _launch.GetStreamUrl(itemId, user), itemId, sortedItems[startIndex], user, context, announceLocale: locale);

        ApplyAnnouncement(response, announcement);

        return response;
    }

    /// <summary>
    /// Overrides a play response's speech with the given announcement (JF-345: the
    /// ONE override site; was a triplicated 3-liner across the artist/album/song play
    /// builders). No-op for a null/whitespace announcement.
    /// </summary>
    /// <param name="response">The play response to speak over.</param>
    /// <param name="announcement">The announcement text, or null to keep the default speech.</param>
    internal static void ApplyAnnouncement(SkillResponse response, string? announcement)
    {
        if (!string.IsNullOrWhiteSpace(announcement))
        {
            response.Response.OutputSpeech = new PlainTextOutputSpeech { Text = announcement };
        }
    }

    /// <summary>
    /// JF-440: the ONE single-song play shape (was the 4th/5th inline copy across
    /// handlers): one-song session queue, full-item bookkeeping, stale progressive
    /// continuation cleared (a one-song queue replacing an artist's progressive queue
    /// must not let the OLD artist resume after the song), AudioPlayer response with
    /// the optional announcement overriding the speech. Crash-recovery SetQueue is
    /// deliberately NOT persisted: every single-song site historically skips it and a
    /// one-song queue is trivially re-requestable (the divergence is tracked in JF-440's
    /// notes; normalize rather than silently change crash-recovery behavior).
    /// </summary>
    /// <param name="song">The audio item to play.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Alexa session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="announcement">Optional spoken announcement replacing the default speech.</param>
    /// <returns>The AudioPlayer play response.</returns>
    public SkillResponse BuildSingleSongResponse(
        BaseItem song,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        string? announcement = null)
    {
        session.NowPlayingQueue = new List<QueueItem> { new() { Id = song.Id } };
        session.FullNowPlayingItem = song;
        QueueContinuationStore.Remove(session.UserId, context.System.Device.DeviceID);

        string itemId = song.Id.ToString();
        SkillResponse response = _launch.BuildAudioPlayerResponse(
            PlayBehavior.ReplaceAll, _launch.GetStreamUrl(itemId, user), itemId, song, user, context, announceLocale: locale);
        ApplyAnnouncement(response, announcement);

        return response;
    }

    /// <summary>
    /// JF-439/JF-440 inverse cross-media fallback: no artist matched, so try the song
    /// index with the musician value (the NLU coin-flips musician-shaped song titles
    /// into artist intents). Serves the best song above
    /// <see cref="CrossMediaSongThreshold"/> with a FoundSongInstead announcement;
    /// returns null (caller falls through to its clean not-found) when the index is
    /// absent/warming (an opportunistic fallback must never worsen the not-found
    /// path) or nothing clears the bar. NO word-count guard by design: a spaceless
    /// CJK title tokenizes to one token (JF-439 review).
    /// </summary>
    /// <param name="musician">The raw musician slot value.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Alexa session.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="songIndex">The song n-gram index (null in minimal setups).</param>
    /// <param name="libraryManager">Library manager for the library-filter walk.</param>
    /// <param name="cancellationToken">Shutdown token.</param>
    /// <returns>The play response, or null to fall through to the caller's not-found.</returns>
    public SkillResponse? TrySongFallback(
        string musician,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ISongNgramIndex? songIndex,
        ILibraryManager libraryManager,
        string logLabel,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (songIndex == null)
        {
            return null;
        }

        var keywordTokens = KeywordMatcher.Tokenize(musician, locale);
        if (keywordTokens.Length == 0)
        {
            return null;
        }

        // The index's topParentMap holds PHYSICAL library folder ids; ResolveForUser
        // emits the union of both id spaces, so the membership test matches the index
        // maps (JF-439/JF-455) and returns null when the user is unrestricted.
        Guid[]? topParentIds = LibraryFilter.ResolveForUser(user, libraryManager, _logger);

        List<(BaseItem Item, double Score)> scored;
        try
        {
            scored = songIndex.SearchWithPhoneticFallback(keywordTokens, locale, topParentIds, _config.PhoneticSongSearchEnabled);
        }
        catch (Exceptions.SkillWarmingUpException)
        {
            // The warming gate's refusal answers the ORIGINAL request; this
            // opportunistic fallback must not convert a not-found into a warming Tell.
            _logger.LogDebug("{Label}: song fallback skipped, song index warming", logLabel);
            return null;
        }

        if (scored.Count == 0 || scored[0].Score < CrossMediaSongThreshold)
        {
            _logger.LogDebug(
                "{Label}: song fallback rejected for query='{Query}' (best score {Score:F0} over {Count} candidates, bar={Bar})",
                logLabel, musician, scored.Count > 0 ? scored[0].Score : 0, scored.Count, CrossMediaSongThreshold);
            return null;
        }

        BaseItem song = scored[0].Item;
        _logger.LogInformation(
            "Song fallback found '{SongName}' itemId={ItemId} (score={Score:F0}) for query='{Query}'",
            song.Name, song.Id, scored[0].Score, musician);

        return BuildSingleSongResponse(
            song, user, session, context, locale,
            // JF-345: the third cross-media substitution announcement joins the flag
            // (an artist-intent request served a song is the same substitution class).
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundSongInstead", locale, song.Name)
                : null);
    }

    /// <summary>
    /// JF-471: decision-point acceptance for an artist handed back by
    /// <see cref="Util.ArtistSearch.SearchAsync"/> on a path that AUTO-PLAYS an album
    /// (PlayAlbum's album-by-artist resolution). The fuzzy tiers police their own
    /// results (tier-1 containment scores at least ContainmentScore by construction;
    /// tiers 2-4 accept only through <see cref="FuzzyMatcher"/> with the user's
    /// threshold, phonetic bonus, and the JF-381 collision floor), but the JF-437
    /// word-coverage tier (1.5) is a free pass: it returns any artist whose name
    /// word-set is a subset of the query's content words with NO score requirement.
    /// A judgment-less caller then silently plays an unrelated artist's album (live
    /// 2026-09-03 corr=38498471: the album span of "riproduci album dark side of the
    /// moon" was stolen into the musician slot by Amazon entity resolution (JF-469),
    /// tier 1.5 matched the band "Dark Dark Dark" (name word "dark" inside the
    /// query's {dark, side, moon}) at honest score 42 with NO phonetic collision,
    /// and the skill played that band's album with no announcement). PlayArtistSongs
    /// judges tier results downstream (JF-377 downgrade, JF-420 gate); this helper
    /// gives the album path the equivalent decision-point predicate.
    /// <para>
    /// The check IS the matcher, never a handler-side re-implementation of its
    /// semantics (the JF-420.3 lesson: private copies of matcher scoring drifted by
    /// 30+ points). Same overload, same threshold, same phonetic lookup the tiers
    /// use, so everything the fuzzy tiers themselves accepted still passes:
    /// containment/exact ("pink floyd"), ASR truncation ("crash" ->
    /// "Crash Test Dummies"), qualifier queries ("miles davis live"), and the JF-381
    /// phonetic accent-drift class ("cup" -> "Koop", length-banded collision floored
    /// above ContainmentScore).
    /// </para>
    /// <para>
    /// Two documented corners (JF-471 review): (1) the bar is the USER's threshold,
    /// and tier-1 containment matches were never threshold-gated before, so a user
    /// who sets FuzzyMatchThreshold above ContainmentScore (90) now sees containment
    /// matches refused HERE while PlayArtistSongs still auto-plays them (its bars
    /// are fixed constants); that is the user's own bar being respected, JF-363
    /// semantics. (2) In the disabled-index corner (IsReady false after
    /// MaxLoadAttempts) the chain takes the database branch whose tiers score with
    /// the PLAIN overload while this gate scores with the phonetic one; the
    /// divergence is fail-open only (the phonetic overload computes the identical
    /// PartialRatio plus an optional boost), so the gate can never refuse a match
    /// the plain tiers accepted.
    /// </para>
    /// </summary>
    /// <param name="artist">The chain's representative match (the first result).</param>
    /// <param name="query">The raw query the chain searched (the musician slot value).</param>
    /// <param name="user">The skill user (fuzzy threshold override).</param>
    /// <param name="artistIndex">The PINNED index view the search ran on, so the
    /// phonetic codes come from the SAME snapshot (JF-448); null degrades to the
    /// plain text overload, mirroring the database search path.</param>
    /// <returns>True when the match independently survives the fuzzy/phonetic
    /// acceptance the tiers themselves enforce. <paramref name="score"/> carries the
    /// acceptance score (0 when the matcher's length band excluded the candidate) for
    /// decision-point triage logs.</returns>
    public bool PassesArtistMatchAcceptance(
        BaseItem artist,
        string query,
        Entities.User? user,
        IArtistIndex? artistIndex,
        out int score)
    {
        int threshold = FuzzyMatcher.GetDefaultThreshold(user);

        if (artistIndex != null)
        {
            var pinned = artistIndex;
            var scored = FuzzyMatcher.FindBestMatchWithScore(
                query,
                new[] { artist },
                a => a.Name!,
                a => a.Id,
                id => pinned.TryGetPhoneticCode(id, out var codes) ? codes : null);
            score = scored.HasValue ? scored.Value.Score : 0;
            return score >= threshold;
        }

        var plainScored = FuzzyMatcher.FindBestMatchWithScore(query, new[] { artist }, a => a.Name!);
        score = plainScored.HasValue ? plainScored.Value.Score : 0;
        return score >= threshold;
    }

    /// <summary>
    /// Entity fallback for greedy <c>AMAZON.SearchQuery</c> intents that misroute an
    /// artist query (e.g. it-IT "di miles davis" captured as a mood). Strips locale
    /// stop-words via <see cref="KeywordMatcher.Tokenize"/> (all 11 language prefixes of
    /// the 17 locales since JF-389; English stop words are stripped under every locale
    /// since JF-384), reuses
    /// the phonetic artist search pipeline, and respects the cross-media word-count guard
    /// (<see cref="CrossMediaArtistMaxWords"/>) and threshold. Returns null when no
    /// confident match is found so the caller falls through to its own not-found response.
    /// JF-464: returns null immediately when the global music flag
    /// (<see cref="PluginConfiguration.MusicEnabled"/>) is off: the fallback plays artist
    /// songs, and every caller must inherit that gate here rather than re-wire it.
    /// JF-446: the ONE cross-media artist gate. PlaySong and PlayAlbum route their
    /// no-results fallback here instead of inline copies (the copies counted RAW words,
    /// so an article-carrying elicit answer like "di pink floyd" dead-ended at the guard,
    /// and accepted via non-phonetic scoring, so ASR drift in [60,85) like "cup" for
    /// "Koop" never played). Acceptance scores through the SAME phonetic matcher the
    /// musician-slot path uses (FuzzyMatcher's Double Metaphone overload): a
    /// length-banded code collision floors at PhoneticFloorScore = 91 (ContainmentScore
    /// 90 + 1), above the strict bar of max(normal, 85) while the user's threshold
    /// stays at or below 91; a user threshold above 91 drops the floored collision
    /// into the sub-strict band instead, so accent drift plays while non-phonetic
    /// plausible matches stay in the JF-363
    /// Confirm/AutoServe band. The band is opt-in via <paramref name="notFoundMediaType"/>
    /// because its decline path must speak a media-type not-found: FindSong re-prompts
    /// for the title instead (not a terminal song not-found) and PlayMoodMusic declines
    /// to NotFoundMood, so neither can reuse the band's decline contract.
    /// </summary>
    /// <param name="notFoundMediaType">When non-null (<see cref="DisambiguationHelper.MediaTypeSong"/>
    /// or <see cref="DisambiguationHelper.MediaTypeAlbum"/>), enables the JF-363
    /// sub-strict band: a single best artist scoring in [normalThreshold, strict) is
    /// offered for confirmation (or auto-served per config), and the offer's decline
    /// speaks the media-type not-found. Null keeps the pre-band behavior (clean miss).</param>
    public async Task<SkillResponse?> TryEntityFallbackAsync(
        string slotText,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        IArtistIndex? artistIndex,
        string logLabel,
        CancellationToken cancellationToken,
        string? notFoundMediaType = null)
    {
        // Restored from the deleted PlaySong/PlayAlbum inline copies (JF-446 review):
        // the gate's entry point must name the query it is about to interpret.
        _logger.LogDebug(
            "{Label}: no results found, trying artist fallback with query='{Query}'",
            logLabel, slotText);

        // JF-464: the fallback's whole payoff is playing music (artist songs), and its
        // artist queries skip FilterByContentAccess, so the global music flag must gate
        // it HERE at the shared entry rather than in any caller's wiring. JF-467: the
        // read is the LIVE Plugin.Instance configuration (IsMusicEnabled), so a
        // standard-API configuration replacement takes effect without a restart.
        if (!IsMusicEnabled)
        {
            _logger.LogInformation(
                "{Label}: artist fallback skipped, music is disabled via configuration, query='{Query}'",
                logLabel, slotText);
            return null;
        }

        if (!PassesCrossMediaWordGuard(slotText, locale, fallbackNoun: "artist", logLabel, out var tokens))
        {
            return null;
        }

        string cleaned = string.Join(' ', tokens);

        // JF-448 (review F2): pin the artist index once for this fallback so the search
        // chain below and the phonetic confirm after it read the SAME publish (a
        // mid-fallback refresh could otherwise serve the artist list of one snapshot
        // against another's phonetic codes). SearchAsync's own capture is idempotent on
        // this view, so no extra hop is added; Pin degrades to live reads when the
        // implementation cannot pin.
        IArtistIndex? pinnedArtistIndex = artistIndex.Pin();

        IReadOnlyList<BaseItem> artists = await ArtistSearch.SearchAsync(
            cleaned, user, libraryManager, pinnedArtistIndex, _logger,
            (q, ct) => RetryAsync(() => libraryManager.GetItemList(q), logLabel + ":GetArtistsFallback", ct),
            locale, cancellationToken).ConfigureAwait(false);

        if (artists.Count == 0)
        {
            return null;
        }

        // JF-446 finding 2: accept through the PHONETIC matcher when the artist index is
        // available (the same lookup Search.FuzzyMatchPhonetic and ArtistSearch's own tiers use).
        // Threshold rationale: the strict bar stays Math.Max(normal, CrossMediaArtistThreshold)
        // because this is still a cross-media GUESS (the user asked for another media
        // type), so only near-exact or phonetically-colliding matches auto-play. The
        // phonetic overload is what makes the shared thresholds meaningful: ASR drift
        // ("cup" for "Koop", both code KP) floors at PhoneticFloorScore and plays, while
        // the plain overload scored it below every bar and dead-ended (the defect the
        // inline copies carried).
        var best = pinnedArtistIndex != null
            ? FuzzyMatcher.FindBestMatchWithScore(
                cleaned,
                artists,
                a => a.Name,
                a => a.Id,
                id => pinnedArtistIndex.TryGetPhoneticCode(id, out var codes) ? codes : null)
            : FuzzyMatcher.FindBestMatchWithScore(cleaned, artists, a => a.Name);
        int normalThreshold = FuzzyMatcher.GetDefaultThreshold(user);
        int threshold = FuzzyMatcher.GetEffectiveThreshold(user, CrossMediaArtistThreshold);
        BaseItem? bestItem = best.HasValue ? best.Value.Item : null;
        int bestScore = best.HasValue ? best.Value.Score : 0;

        if (bestItem != null && bestScore >= threshold)
        {
            // Strict (or phonetic-floor) match: fall through to playback below.
            // Restored from the deleted PlaySong/PlayAlbum inline copies (JF-446
            // review): the acceptance must name the artist and its score.
            _logger.LogInformation(
                "{Label}: artist fallback found '{ArtistName}' with score={Score} for query='{Query}' (threshold={Threshold})",
                logLabel, bestItem.Name, bestScore, cleaned, threshold);
        }
        else if (bestItem != null && bestScore >= normalThreshold && notFoundMediaType != null)
        {
            // JF-363 sub-strict band [normalThreshold, threshold): offer or auto-serve
            // the single best artist instead of a dead-end miss. Confirm/AutoServe are
            // safe (no silent wrong substitution: Confirm asks first; AutoServe is
            // opt-in). The offer carries the RAW slot text so the decline speaks the
            // user's own words. Single candidate only (same design as the copies this
            // replaced: disambiguating among cross-media guesses is wrong UX).
            // PRECEDENCE (review finding, pinned by CrossMediaTypeFallbackTests
            // .JF363_BandWinsOverWordCoverageValve_Confirm): the band is checked
            // BEFORE the word-coverage valve so it wins for the callers that enabled
            // it (PlaySong/PlayAlbum). The valve auto-plays silently; letting it win
            // in the overlap would break the JF-363 contract of no silent
            // substitution in [normalThreshold, threshold).
            var suggestionMode = GetCrossMediaArtistSuggestion(user);
            if (suggestionMode == CrossMediaArtistSuggestion.Confirm)
            {
                return BuildCrossMediaArtistOfferAsk(slotText, bestItem, locale, notFoundMediaType);
            }

            if (suggestionMode == CrossMediaArtistSuggestion.Off)
            {
                _logger.LogDebug(
                    "{Label}: entity fallback artist '{Artist}' score={Score} in suggestion band but suggestion is Off, query='{Query}'",
                    logLabel, bestItem.Name, bestScore, cleaned);
                return null;
            }

            _logger.LogInformation(
                "{Label}: entity fallback artist suggestion AutoServe '{Artist}' score={Score} for query='{Query}'",
                logLabel, bestItem.Name, bestScore, cleaned);
        }
        else if (bestItem != null && bestScore >= normalThreshold
            && Util.ArtistSearch.WordCoverageCandidates(cleaned, new[] { bestItem }, locale).Count > 0)
        {
            // JF-440 (F4): a word-coverage tier match scores LOW on the fuzzy scale
            // ('The Beatles' vs 'beatles live' = 27, below every cross-media gate),
            // so the fuzzy bar alone rejects exactly the qualifier-query class the
            // tier exists to serve. Accept the match when the artist is a
            // word-subset of the cleaned query (same predicate as the search tier).
            // Review round: the word-coverage acceptance also needs the NORMAL fuzzy
            // floor. Without it, a one-word subset of a 2-word mood slot ('soft rock'
            // -> artist 'Soft') auto-substitutes at any score; with it, the gate is a
            // safety valve for genuinely-artist-shaped queries that just miss the
            // strict cross-media bar, never a bypass of every bar (the JF-437 search
            // tier, with its full selection + downstream gates, owns the main path).
            // Scoped to callers WITHOUT the JF-363 band (the band branch above wins
            // the overlap first): FindSong and PlayMoodMusic keep the valve behavior
            // the shared gate always had.
            _logger.LogInformation(
                "{Label}: entity fallback artist '{Artist}' below fuzzy threshold ({Score}<{Threshold}) but accepted as a word-coverage match for query='{Query}'",
                logLabel, bestItem.Name, bestScore, threshold, cleaned);
        }
        else
        {
            _logger.LogDebug(
                "{Label}: entity fallback artist score={Score} below threshold={Threshold}, query='{Query}'",
                logLabel, bestScore, threshold, cleaned);
            return null;
        }

        // JF-382 surface 3: a coincidental containment (a short common-word artist name
        // riding the containment/phonetic-floor score exemption inside the slot text)
        // must not auto-play silently. The downgrade reuses the JF-363 yes/no offer ask
        // (its decline speaks the media-type not-found) whenever the caller wired that
        // machinery (notFoundMediaType) and suggestions are not Off; callers without the
        // band (FindSong re-prompts for the title, PlayMoodMusic speaks NotFoundMood)
        // have no artist yes/no contract, so they get the clean miss (null) and fall
        // through to their own not-found. DEVIATION, deliberate: an AutoServe user (who
        // opted into silent auto-play for GENUINE offers) also gets the ask here - the
        // prompt is still a no-silent-substitution outcome and "yes" plays; and an Off
        // user with a STRICT-bar (>=85) coincidental match now gets the clean miss
        // instead of the old silent auto-play (pinned by tests).
        if (Util.ArtistSearch.IsCoincidentalContainmentMatch(cleaned, bestItem!.Name, locale))
        {
            if (notFoundMediaType != null && GetCrossMediaArtistSuggestion(user) != CrossMediaArtistSuggestion.Off)
            {
                _logger.LogInformation(
                    "{Label}: artist fallback match '{ArtistName}' for query='{Query}' is coincidental-containment, downgrading to the offer ask (JF-382)",
                    logLabel, bestItem.Name, cleaned);
                return BuildCrossMediaArtistOfferAsk(slotText, bestItem, locale, notFoundMediaType);
            }

            _logger.LogInformation(
                "{Label}: artist fallback match '{ArtistName}' for query='{Query}' is coincidental-containment with no offer machinery, treating as a miss (JF-382)",
                logLabel, bestItem.Name, cleaned);
            return null;
        }

        return await BuildArtistSongsResponseAsync(
            bestItem!.Id,
            bestItem.Name,
            jellyfinUser,
            user,
            session,
            context,
            locale,
            libraryManager,
            userDataManager,
            queueManager,
            logLabel,
            // JF-345: the cross-media substitution announcement (which artist is
            // playing instead of what was asked) is opt-out; the substitution itself
            // always plays. Same flag as the song-to-album cascade's announcement.
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundArtistInstead", locale, bestItem.Name)
                : null,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Live read of the global music flag (global-only: no per-user override exists).
    /// Copied, not moved (JF-315 batch 7): BaseHandler's own IsMusicEnabled was
    /// later deleted with its last caller, the album cascade, when that cascade
    /// moved to AlbumPlayService carrying its own twin (JF-315 batch 8; IfMediaTypeDisabled
    /// and FilterByContentAccess share the same live-read source but do not call the
    /// property), and the expression is identical: Plugin.Instance.Configuration
    /// FIRST so a standard-API configuration replacement takes effect without a
    /// restart (JF-467), falling back to the injected configuration only when the
    /// plugin instance is absent (off-host unit tests).
    /// </summary>
    private bool IsMusicEnabled => (Plugin.Instance?.Configuration ?? _config).MusicEnabled;

    /// <summary>
    /// Execute a synchronous Jellyfin API call with retry logic and exponential backoff.
    /// Copied, not moved (JF-315 batch 7, the SearchService twin's sibling): BaseHandler
    /// retains its own RetryAsync for its remaining callers, and the moved members call
    /// this one by their original name so their bodies keep the pre-extraction call
    /// shape (modulo the batch's declared receiver swaps). The budget is the
    /// composition-passed <c>_requestTimeoutMs</c> field (BaseHandler's const,
    /// single-sourced there as the 6s controller cancellation the whole request path
    /// shares). Consolidation of the RetryAsync twins is tracked as JF-572.
    /// </summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
    {
        return RetryHelper.ExecuteWithRetryAsync(operation, _logger, operationName, cancellationToken: cancellationToken, timeoutMs: _requestTimeoutMs);
    }

    /// <summary>
    /// Shuffle a list in place using Fisher-Yates algorithm.
    /// Copied, not moved (JF-315 batch 7): the Shuffle family stays on BaseHandler
    /// with its cluster-H consumers (ShuffleCopy/ShuffleAndCap, PlayRandom, the radio
    /// queues); BuildArtistSongsResponseAsync needed it verbatim, so this private twin
    /// keeps the moved body's call shape. Consolidation belongs to the cluster-H
    /// extraction (the JF-572 twin-consolidation family).
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="list">The list to shuffle.</param>
#pragma warning disable CA1859 // IList parameter kept verbatim from the BaseHandler original; the twin's single call site passing List<T> is exactly what cluster-H consolidation will resolve
    private static void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
#pragma warning restore CA1859
}
