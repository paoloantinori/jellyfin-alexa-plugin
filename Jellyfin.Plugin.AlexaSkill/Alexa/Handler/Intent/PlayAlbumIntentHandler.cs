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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayAlbumIntent requests.
/// </summary>
public class PlayAlbumIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly DeviceQueueManager? _queueManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// JF-443: cap on the albums whose track counts are queried on the indefinite
    /// album-by-artist path (one COUNT query each, inside the Alexa response window).
    /// The cap takes the candidates in the existing deterministic order (newest
    /// ProductionYear, then Name, then Id), so the 100+-album tail costs nothing; all
    /// counted candidates still outrank every uncounted one under the same order.
    /// </summary>
    private const int MaxRankedAlbumCandidates = 12;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayAlbumIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    public PlayAlbumIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        DeviceQueueManager? queueManager = null,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _queueManager = queueManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayAlbum, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Play a specific album by name, optionally filtered by artist.
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

        string? album = intentRequest.Intent.Slots?.TryGetValue("album", out var albumSlot) == true ? albumSlot.Value : null;
        string? musician = intentRequest.Intent.Slots?.TryGetValue("musician", out var musicianSlot) == true ? musicianSlot.Value : null;

        Logger.LogDebug("PlayAlbum: entered, locale={Locale}", locale);

        // Escape hatch from the elicitation trap (shared CancelWords helpers): while OUR
        // album Dialog.ElicitSlot is open, a stop/cancel word gets captured into
        // a slot (dialogState IN_PROGRESS) instead of routing to AMAZON.Stop/Cancel. ANY
        // slot counts (shared AnySlotIsCancelWord), not just album/musician. Gated on
        // IN_PROGRESS deliberately (JF-445): unlike FindSong, this elicit persists NO
        // session state ("the dialog lives Amazon-side"), so there is no open-flow marker
        // to distinguish a STARTED sibling misroute from a fresh legitimate search, and no
        // force-route delivers sibling requests here (the FindSongSessionData override is
        // the only one). A first-invocation search for an album actually titled "Stop", or
        // for the artist "Basta", must still run. Runs BEFORE the warming gate so an open
        // flow still cancels during the cold-start window (JF-419.2 contract, review
        // round 2).
        if (Util.CancelWords.IsDialogInProgress(intentRequest)
            && Util.CancelWords.AnySlotIsCancelWord(intentRequest, locale))
        {
            Logger.LogInformation("PlayAlbum: captured cancel word during open elicit (album='{Album}'), ending flow", album);
            return ResponseBuilder.Tell(ResponseStrings.Get("FindSongCancelled", locale));
        }

        // JF-419 cold-start: the album queries hit the same cold database the artist
        // index loading proxies (deliberately coarse: album paths have no in-memory
        // index of their own to gate on).
        Util.IndexWarmingGate.EnsureReady(_artistIndex);

        // Elicit via Dialog.ElicitSlot so the session stays in the PlayAlbumIntent
        // dialog (the user's next utterance fills the slot; already-filled slots
        // survive the round-trip). Branching is slot-presence driven (JF-422):
        // - both empty: ask WHICH ALBUM. The common answer to a bare "riproduci un
        //   album" is a title, and the answer feeds the album-title search below.
        //   The previous artist-first order captured a title answer ("the dark side
        //   of the moon") into the musician slot and dead-ended in
        //   NotFoundAlbumByArtist for an album that exists. The 2026-08-28
        //   on-device case that motivated artist-first ("un disco dei Koop" arrived
        //   as "un disco dei", ASR swallowed the name) degrades gracefully since
        //   JF-446: an artist answer in the album slot reaches the SHARED cross-media
        //   gate below, which tokenizes before the word-count guard (articles like
        //   "di"/"dei" no longer count) and accepts via the phonetic artist search,
        //   so "di pink floyd" and ASR-drifted names play the artist too.
        // - musician filled, whether on the first shot or as an answer mid-dialog:
        //   fall through to the JF-411 album-by-artist resolution below, which
        //   plays an album without ever needing a title. The old IN_PROGRESS
        //   re-elicit of the title asked the "any album by X" user a question they
        //   cannot answer.
        if (string.IsNullOrWhiteSpace(album) && string.IsNullOrWhiteSpace(musician))
        {
            return BuildAlbumElicitResponse(locale);
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        List<Guid> artistsIds = new List<Guid>();
        string? matchedArtistName = null;

        // JF-411/JF-427: album resolved from the artist filter when no title was given.
        BaseItem? resolvedAlbum = null;
        if (!string.IsNullOrWhiteSpace(musician))
        {
            Logger.LogDebug("PlayAlbum: searching for artist filter='{Musician}'", musician);
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                musician, user, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtists", ct),
                locale, cancellationToken).ConfigureAwait(false);

            Logger.LogDebug("PlayAlbum: artist search returned {Count} results for '{Musician}'", artists.Count, musician);

            if (artists.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByArtist", locale, musician));
            }

            matchedArtistName = artists[0].Name;
            foreach (BaseItem artist in artists)
            {
                artistsIds.Add(artist.Id);
            }
        }

        // JF-411: "un disco dei X" (indefinite album-by-artist, e.g. "un disco dei Koop") fills
        // only the musician slot. Rather than discarding the artist behind an album-name
        // reprompt (which loops when the user repeats the phrase), resolve one of the artist's
        // albums and play it.
        if (string.IsNullOrWhiteSpace(album) && artistsIds.Count > 0)
        {
            InternalItemsQuery artistAlbumQuery = BuildAlbumQuery(_libraryManager, jellyfinUser, user, searchTerm: null, artistIds: artistsIds.ToArray(), albumArtistsOnly: true);

            // JF-427: explicit deterministic order; the query previously had NO OrderBy, so the
            // pick was an arbitrary database row that could change after an unrelated rescan. The
            // authoritative most-tracks ranking happens in memory (PickMostTracksRelease); these
            // keys give the fetch a stable shape and mirror its tie-breaks (AC#1).
            artistAlbumQuery.OrderBy = new[] { (ItemSortBy.ProductionYear, SortOrder.Descending), (ItemSortBy.SortName, SortOrder.Ascending) };

            IReadOnlyList<BaseItem> artistAlbums = await RetryAsync(
                () => _libraryManager.GetItemList(artistAlbumQuery),
                "GetArtistAlbums",
                cancellationToken).ConfigureAwait(false);

            if (artistAlbums.Count == 0)
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByArtist", locale, matchedArtistName!));
            }

            // JF-427 selection policy: prefer the release with the MOST tracks (a full studio
            // release over a single/EP/live sampler), so "un disco di X" plays a defensible
            // album instead of an arbitrary row. JF-443: counts come from COUNT-only
            // queries over the top-K candidates in the deterministic order (was: one query
            // materializing every Audio row of the artist's ENTIRE catalog, 1,533 rows for
            // a 107-album artist on the live library, all deserialized inside the Alexa
            // window under RetryAsync).
            IReadOnlyList<BaseItem> rankedCandidates = RankByDeterministicOrder(artistAlbums)
                .Take(MaxRankedAlbumCandidates)
                .ToList();
            IReadOnlyDictionary<Guid, int> trackCounts = await GetAlbumTrackCountsAsync(jellyfinUser, rankedCandidates, cancellationToken).ConfigureAwait(false);
            resolvedAlbum = PickMostTracksRelease(artistAlbums, trackCounts);

            album = resolvedAlbum.Name;
            Logger.LogInformation(
                "PlayAlbum: no album title given, picked '{Album}' by artist '{Artist}' out of {CandidateCount} releases (most-tracks policy; indefinite album-by-artist, JF-411/JF-427)",
                album, matchedArtistName, artistAlbums.Count);
        }

        // Flow guard: past this point an album title is guaranteed (either the user said one
        // or the JF-411 block above resolved it from the artist filter).
        if (string.IsNullOrWhiteSpace(album))
        {
            return BuildAlbumElicitResponse(locale);
        }

        // JF-427: carry the album resolved from the artist filter instead of re-querying by
        // its name. The re-query went through SearchTerm, which can miss on accent/index
        // normalization, and a miss cascaded into the artist-less full-catalog query plus
        // the library-wide fuzzy match below, all inside the 6s retry budget. The BaseItem
        // is already in hand.
        IReadOnlyList<BaseItem> albums;
        if (resolvedAlbum != null)
        {
            Logger.LogDebug("PlayAlbum: using the album resolved from the artist filter, skipping the re-query by name");
            albums = new List<BaseItem> { resolvedAlbum };
        }
        else
        {
            var albumSearchQuery = BuildAlbumQuery(_libraryManager, jellyfinUser, user, album, artistsIds.ToArray());

            Logger.LogDebug("PlayAlbum: querying Jellyfin with searchTerm='{Album}', artistIds={ArtistIdsCount}, types=MusicAlbum", album, artistsIds.Count);
            albums = await RetryAsync(
                () => _libraryManager.GetItemList(albumSearchQuery),
                "GetAlbums",
                cancellationToken).ConfigureAwait(false);
            Logger.LogDebug("PlayAlbum: Jellyfin returned {ResultCount} albums", albums.Count);
        }

        if (albums.Count == 0 && !string.IsNullOrWhiteSpace(musician))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByNameAndArtist", locale, album, matchedArtistName!));
        }

        string? fuzzyAlbumAnnouncement = null;
        if (albums.Count == 0 && album.Length >= MinFuzzyAlbumQueryLength)
        {
            // Fuzzy fallback: ASR may transcribe the album name with accents or
            // Italian-vs-English spelling that Jellyfin's search index doesn't normalize
            // (e.g. "caffè" vs "Cafe"). Match against the user's albums via FuzzyMatcher
            // partial-ratio (Levenshtein, NOT Double Metaphone — true phonetic matching
            // would need a precomputed album index, cf. ArtistIndexService). JF-336.
            // Min-length guard: very short queries produce too many substring false
            // positives across a full-catalog scan (e.g. "red", "aria") — skip them.
            Logger.LogDebug("PlayAlbum: exact search miss, trying fuzzy fallback for '{Query}'", album);
            var phoneticAlbumQuery = BuildAlbumQuery(_libraryManager, jellyfinUser, user, searchTerm: null, artistIds: null);
            // JF-446 (finding 5): the full-catalog scan reads only a.Name downstream;
            // use the cheap DTO shape instead of materializing images/userdata/
            // current-program for every album in the library.
            phoneticAlbumQuery.DtoOptions = CheapDtoOptions();
            IReadOnlyList<BaseItem> allAlbums = await RetryAsync(
                () => _libraryManager.GetItemList(phoneticAlbumQuery),
                "GetAlbumsPhonetic",
                cancellationToken).ConfigureAwait(false);

            // FindBestMatchWithScore (single best). NOTE: a multi-match here currently
            // auto-plays the best via HandleFuzzyMiss (which re-scores + auto-accepts at
            // >= GetDefaultThreshold); real disambiguation for different-name collisions
            // (e.g. several "Greatest Hits" by different artists) needs a HandleFuzzyMiss
            // bypass — tracked in JF-341. RankMatches was tried (b12cf5c) but is inert
            // here for that reason.
            var fuzzyMatch = FuzzyMatcher.FindBestMatchWithScore(album, allAlbums, a => a.Name);
            if (fuzzyMatch.HasValue && fuzzyMatch.Value.Score >= FuzzyMatcher.GetDefaultThreshold(user))
            {
                if (Util.ArtistSearch.IsInteriorContainment(album, fuzzyMatch.Value.Item.Name))
                {
                    // JF-408: the match exists only inside other words of the query (live
                    // incident: album "O" scored ContainmentScore against "walls for cup",
                    // ASR for "Waltz for Koop", and auto-played on-device). The recall layer
                    // must keep returning such candidates; the auto-play decision must not
                    // act on them.
                    Logger.LogInformation(
                        "PlayAlbum: fuzzy fallback match '{Name}' score={Score} for query='{Query}' is interior containment, not auto-playing (JF-408)",
                        fuzzyMatch.Value.Item.Name, fuzzyMatch.Value.Score, album);
                }
                else
                {
                    Logger.LogInformation(
                        "PlayAlbum: fuzzy fallback matched album '{Name}' score={Score} for query='{Query}'",
                        fuzzyMatch.Value.Item.Name, fuzzyMatch.Value.Score, album);
                    albums = new List<BaseItem> { fuzzyMatch.Value.Item };
                    // The exact search missed, so the matched album name may differ from
                    // what the user said (accents, spelling). Announce it so voice-only
                    // devices know which album is playing (JF-339).
                    fuzzyAlbumAnnouncement = ResponseStrings.Get("FoundAlbumInstead", locale, fuzzyMatch.Value.Item.Name);
                }
            }
        }

        if (albums.Count == 0)
        {
            // Cross-media artist fallback (JF-446): the NLU may have routed an artist name
            // to PlayAlbumIntent, and an artist ANSWER to the both-empty album elicit
            // (JF-422) lands in this slot. The shared gate (TryEntityFallbackAsync)
            // tokenizes before the word-count guard (so "di pink floyd" no longer
            // dead-ends at 3 raw words) and accepts through the phonetic artist-search
            // thresholds, with the JF-363 Confirm/AutoServe band for sub-strict matches.
            // Search order is unchanged (deliberate, JF-446 finding 3): the album-title
            // search above runs first, so a self-titled album still preempts the artist
            // fallback; the artist answer only plays on a title miss.
            SkillResponse? artistFallback = await TryEntityFallbackAsync(
                album, jellyfinUser!, user, session, context, locale,
                _libraryManager, _userDataManager, _queueManager, _artistIndex,
                "PlayAlbum", cancellationToken,
                notFoundMediaType: DisambiguationHelper.MediaTypeAlbum).ConfigureAwait(false);
            if (artistFallback != null)
            {
                return artistFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundAlbumByName", locale, album));
        }

        if (albums.Count > 1)
        {
            // Disambiguate distinct-name collisions only for Confirm users (AutoPlay users opted
            // out of prompts). Same-name duplicates always auto-play (a "X or X?" prompt is useless).
            // Divergence (JF-427): this path deliberately keeps the NAME ordering as its pick
            // policy and does NOT apply PickMostTracksRelease. The name sort also orders the
            // disambiguation prompt list, and the track-count policy would add a tracks query
            // to this multi-match hot path; the user named an album here, so alphabetical is
            // already a defensible pick. The two paths diverge on purpose.
            albums = albums.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            bool distinctNames = albums.Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (distinctNames && user.FuzzyMatchBehavior != FuzzyMatchBehavior.AutoPlay)
            {
                Logger.LogDebug("PlayAlbum: {Count} distinct-name albums, prompting disambiguation", albums.Count);
                var matches = albums.Take(3).Select(a => (a.Id, a.Name, (string?)GetImageUrl(a.Id.ToString("N"), user))).ToList();
                return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypeAlbum, locale, context);
            }

            Logger.LogDebug("PlayAlbum: {Count} albums, auto-playing the first", albums.Count);
            albums = new List<BaseItem> { albums[0] };
        }

        // First track page for fast time-to-audio; remaining tracks are fetched on
        // demand by PlaybackNearlyFinished. JF-345: the play flow lives in
        // BaseHandler (BuildAlbumPlayResponseAsync) so the song-to-album cascade
        // plays albums with the SAME queue semantics as a direct album request.
        return await BuildAlbumPlayResponseAsync(
            albums[0],
            jellyfinUser!,
            user,
            session,
            context,
            locale,
            _libraryManager,
            _userDataManager,
            _queueManager,
            "PlayAlbum",
            // If the album came from the fuzzy fallback (exact search missed), speak the
            // matched name so the user knows what's playing (same mechanism as the
            // artist fallback's announcement in BuildArtistSongsResponseAsync, JF-339).
            announcement: fuzzyAlbumAnnouncement,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the album-slot elicitation as a Dialog.ElicitSlot response (not a plain Ask)
    /// so the session stays in the PlayAlbumIntent dialog: the user's next utterance is
    /// captured as the album slot and already-filled slots survive the round-trip. A plain
    /// Ask let follow-ups fall through to general NLU and lose the thread (on-device
    /// 2026-08-28 20:23: "quali ci sono" after the elicit surfaced unrelated
    /// recently-added content). The shared builder declares BOTH intent slots in
    /// updatedIntent (Amazon rejects the directive otherwise, live INVALID_RESPONSE
    /// 2026-08-28 21:17: "All slots must be defined when sending updated intent...
    /// Missing: album"). Requires PlayAlbumIntent in dialog.intents with
    /// elicitationRequired=false (manual dialog control, CLAUDE.md anti-pattern #9).
    /// JF-398 write-time mutual exclusion: the elicit owns no session state of its own
    /// (the dialog lives Amazon-side), so no OTHER flow's stale state may ride along.
    /// </summary>
    /// <param name="locale">The request locale, for the prompt string.</param>
    /// <returns>The elicitation response.</returns>
    private static SkillResponse BuildAlbumElicitResponse(string locale)
        => BuildElicitSlotResponse(
            IntentNames.PlayAlbum,
            IntentNames.Slots.Album,
            new[] { IntentNames.Slots.Album, IntentNames.Slots.Musician },
            ResponseStrings.Get("ElicitAlbumName", locale));

    /// <summary>
    /// JF-427/JF-443: track counts per album ID, from COUNT-only queries. Each query reads
    /// TotalRecordCount with Limit=0 so no row is materialized or deserialized (the pattern
    /// Jellyfin's own Folder.GetChildCount uses; on the NRE that SafeGetItemsResult catches,
    /// its GetItemList fallback materializes Take(0) rows, so the count degrades to 0 and
    /// the album falls to the deterministic tie-break instead of throwing). TWO query
    /// shapes, one per server-side linking mechanism (Jellyfin BaseItemRepository, verified
    /// at v10.11.8 and v10.11.11):
    /// - ParentId (primary) is the ENTITY link (e.ParentId == album.Id, the folder
    ///   hierarchy): it counts exactly the album's child tracks however their raw Album
    ///   tag is spelled. This removes the tag-matching edge of the old sweep (counts
    ///   grouped by the track's raw tag and looked up by MusicAlbum.Name), which zeroed
    ///   well-formed albums whose tags carry "Name (Disc 1)", accent or trailing-space
    ///   variants.
    /// - AlbumIds (fallback, fired only when the ParentId count is 0) is a raw-tag NAME
    ///   match, NOT an entity link: the server resolves the album entities by ID and
    ///   compares each track's raw Album tag string against the album's Name
    ///   (f.Name == e.Album). Some malformed/split albums have broken ParentId linkage
    ///   while the raw tag still links their tracks (the JF-338 "Jazz Cafe" shape; the
    ///   play path above keeps an AlbumIds retry for the same reason), so this second
    ///   query preserves their counts. Malformed albums are rare, so the extra query is
    ///   an uncommon cost.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="albums">The candidate albums (already capped and ordered by the caller).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Album ID to track count; ids absent from the map count as 0.</returns>
    private async Task<IReadOnlyDictionary<Guid, int>> GetAlbumTrackCountsAsync(
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        IReadOnlyList<BaseItem> albums,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<Guid, int>();

        // Single-album elision (kept from the pre-JF-443 sweep): with <= 1 candidate the
        // count cannot change PickMostTracksRelease's pick; skip the query. On the live
        // library this covers 486 of 674 artists (single-album).
        if (albums.Count <= 1)
        {
            return counts;
        }

        foreach (BaseItem album in albums)
        {
            // Primary: the ParentId entity-link count (tag spelling irrelevant).
            QueryResult<BaseItem> result = await RetryAsync(
                () => SafeGetItemsResult(_libraryManager, BuildTrackCountQuery(jellyfinUser, album.Id, byParentId: true)),
                "GetAlbumTrackCountByParentId",
                cancellationToken).ConfigureAwait(false);

            if (result.TotalRecordCount == 0)
            {
                // Fallback: the AlbumIds raw-tag name count, for malformed albums whose
                // ParentId linkage is broken but whose tracks still carry the album's
                // name tag (JF-338).
                result = await RetryAsync(
                    () => SafeGetItemsResult(_libraryManager, BuildTrackCountQuery(jellyfinUser, album.Id, byParentId: false)),
                    "GetAlbumTrackCountByAlbumIds",
                    cancellationToken).ConfigureAwait(false);
            }

            counts[album.Id] = result.TotalRecordCount;
        }

        return counts;
    }

    /// <summary>
    /// Builds a COUNT-only track query for <see cref="GetAlbumTrackCountsAsync"/>: Limit=0
    /// keeps TotalRecordCount (the COUNT) while Take(0) skips item materialization
    /// entirely (verified against Jellyfin 10.11.8 BaseItemRepository.ApplyQueryPaging,
    /// which applies Take(Limit) in both the count and list paths).
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="albumId">The album to count the tracks of.</param>
    /// <param name="byParentId">True for the ParentId entity-link count; false for the AlbumIds raw-tag name count.</param>
    /// <returns>The COUNT-only query.</returns>
    private static InternalItemsQuery BuildTrackCountQuery(Jellyfin.Database.Implementations.Entities.User? jellyfinUser, Guid albumId, bool byParentId)
    {
        var q = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Audio },
            Limit = 0,
            DtoOptions = CheapDtoOptions()
        };

        if (byParentId)
        {
            q.ParentId = albumId;
        }
        else
        {
            q.AlbumIds = new[] { albumId };
        }

        return q;
    }

    /// <summary>
    /// JF-443: the deterministic candidate order for the COUNT cap: newest
    /// ProductionYear, then Name, then Id. This mirrors the fetch query's OrderBy
    /// (ProductionYear/SortName, JF-427) and PickMostTracksRelease's tie-breaks, but is
    /// computed in memory so the order stays TOTAL (Id breaks every remaining tie)
    /// regardless of database row order.
    /// </summary>
    /// <param name="albums">The artist's candidate albums.</param>
    /// <returns>The albums in deterministic order.</returns>
    private static IOrderedEnumerable<BaseItem> RankByDeterministicOrder(IReadOnlyList<BaseItem> albums)
    {
        return albums
            .OrderByDescending(a => a.ProductionYear ?? int.MinValue)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Id);
    }

    /// <summary>
    /// JF-427 selection policy for the indefinite album-by-artist path ("un disco di X"):
    /// prefer the release with the MOST tracks (a full studio release over a single, EP or
    /// live sampler), tie-broken by <see cref="RankByDeterministicOrder"/> (newest
    /// ProductionYear, then Name, then Id). Track count is not an ItemSortBy member in
    /// the Jellyfin 10.11 SDK, so the ranking runs in memory over the artist's albums;
    /// the tie-breaks make the order TOTAL and independent of database row order, so a
    /// library rescan cannot change which album plays.
    /// </summary>
    /// <param name="albums">The artist's candidate albums.</param>
    /// <param name="trackCountsByAlbumId">Track counts keyed by album ID, from <see cref="GetAlbumTrackCountsAsync"/>; uncounted candidates rank as 0 tracks.</param>
    /// <returns>The album to play.</returns>
    private static BaseItem PickMostTracksRelease(IReadOnlyList<BaseItem> albums, IReadOnlyDictionary<Guid, int> trackCountsByAlbumId)
    {
        // OrderByDescending is a stable sort in LINQ-to-Objects, so applying the count
        // key over RankByDeterministicOrder keeps that order as the tie-break; the count
        // cap and the tie-break therefore share ONE order definition by construction.
        return RankByDeterministicOrder(albums)
            .OrderByDescending(a => trackCountsByAlbumId.TryGetValue(a.Id, out int trackCount) ? trackCount : 0)
            .First();
    }
}