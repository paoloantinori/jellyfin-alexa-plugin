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
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The JF-315 album/playlist-play collaborator (census cluster G, extracted from
/// BaseHandler batch 8): the ONE album query shape (BuildAlbumQuery, the JF-345
/// contract shared by PlayAlbum's own search and the song-to-album cascade), the
/// JF-469 calling-word strip, the JF-345 song-to-album cascade
/// (TryAlbumFallbackAsync), the ONE album play flow (BuildAlbumPlayResponseAsync,
/// the JF-338 AlbumIds retry + resume index + crash-recovery queue +
/// progressive continuation), and the shared playlist play flow
/// (BuildPlaylistPlayResponseAsync, the JF-455 visibility filter + shuffle arm).
/// NAMESPACE PLACEMENT (the CrossMediaFallback precedent, not the Util home of
/// SearchService/PlaybackLaunchBuilder): BuildPlaylistPlayResponseAsync resolves
/// its disambiguation through DisambiguationHelper (the MediaTypePlaylist
/// vocabulary and the AskFirstMatch prompt), which lives in THIS namespace; no
/// Util file references the Handler namespace and this extraction keeps it that way.
/// JF-408 DISCIPLINE (fuzzy-recall-vs-judgment-layers): the auto-play DECISION
/// block (HandleFuzzyMiss and FuzzyMissOutcome) deliberately STAYED on
/// BaseHandler (the batch-6 stay; rationale in the SearchService class doc).
/// The playlist flow reaches it through the composition-time delegate below, so
/// the moved body is exactly one more caller of the decision block, and the
/// decision stays at its decision point; the site-level JF-526 pre-check moved
/// WITH its call site as verbatim code. This class must never grow a NEW
/// judgment shape (that is a JF-408-class decision, not cleanup).
/// STATELESS by construction (readonly config + logger + the three composed
/// collaborators + the fuzzy-miss delegate), so the singleton-handlers
/// constraint BaseHandler documents is preserved.
/// COMPOSITION DECISION (the SearchService/PlaybackLaunchBuilder/CrossMediaFallback
/// precedent): BaseHandler constructs one instance per handler in its own ctor and
/// exposes it as the protected-internal readonly <c>AlbumPlay</c> property;
/// handlers consume it through that inherited get-only property, so the 61
/// handler ctors stay untouched.
/// </summary>
public sealed class AlbumPlayService
{
    /// <summary>
    /// Delegate seam to BaseHandler's own <see cref="BaseHandler.HandleFuzzyMiss"/>
    /// for <see cref="BaseItem"/> candidates (the one shape the playlist flow
    /// calls), wired at composition time. Non-generic on purpose: the single
    /// moved call site instantiates T = <see cref="BaseItem"/>, and a
    /// method-group conversion from the generic method would have to close the
    /// type argument anyway.
    /// </summary>
    /// <param name="query">The original search query.</param>
    /// <param name="candidates">The full list of candidate items.</param>
    /// <param name="selector">Function to extract the display name from an item.</param>
    /// <param name="matchExtractor">Function to create disambiguation match list from the best candidate.</param>
    /// <param name="mediaType">The media type for disambiguation state.</param>
    /// <param name="locale">The locale for localized responses.</param>
    /// <param name="autoPlayFunc">Optional async function to play the suggested item in AutoPlay mode.</param>
    /// <param name="user">The plugin user (threshold override).</param>
    public delegate Task<(BaseHandler.FuzzyMissOutcome Outcome, SkillResponse? Response)> FuzzyMissHandler(
        string query,
        IReadOnlyList<BaseItem> candidates,
        Func<BaseItem, string> selector,
        Func<BaseItem, List<(Guid Id, string Name)>> matchExtractor,
        string mediaType,
        string locale,
        Func<BaseItem, Task<SkillResponse>>? autoPlayFunc,
        Entities.User? user);

    /// <summary>
    /// Minimum album-query length to attempt the bounded fuzzy album tier. Shorter
    /// queries (e.g. "red", "aria") produce too many substring false positives. Shared
    /// by PlayAlbum's own fuzzy fallback and the JF-345 song-to-album cascade.
    /// </summary>
    public const int MinFuzzyAlbumQueryLength = 4;

    /// <summary>
    /// JF-345: minimum fuzzy-match score for the song-to-album cascade (a bare
    /// "play abbey road" in the free-text locales routes to PlaySong, misses,
    /// and used to dead-end in a song not-found). Containment-grade (equal to
    /// <see cref="FuzzyMatcher.ContainmentScore"/>), deliberately STRICTER than the
    /// artist cascade's <see cref="CrossMediaFallback.CrossMediaArtistThreshold"/> because song/album
    /// name overlap is far more common than artist/mood overlap: only a near-exact
    /// album name substitutes. Apply via
    /// <c>FuzzyMatcher.GetEffectiveThreshold(user, CrossMediaAlbumThreshold)</c>
    /// so a user who raised FuzzyMatchThreshold is still respected.
    /// Moved here with its only consumer, TryAlbumFallbackAsync (JF-315 batch 8).
    /// </summary>
    public const int CrossMediaAlbumThreshold = 90;

    /// <summary>
    /// JF-469: calling-word prefixes that can bleed INTO a title slot value, keyed by
    /// the full locale string (the CancelWords vocabulary precedent). The it-IT NLU
    /// fills the album slot with the literal calling word for shapes the model layer
    /// cannot anchor (profile-nlu evidence, it-IT model rebuilt 2026-09-04, skill
    /// 33dfacd5, deterministic across double probes): 'cerca un album chiamato dark
    /// side of the moon' -> album "chiamato dark side of the moon"; "c'e un album
    /// chiamato thriller" -> album "chiamato thriller"; 'un album che si chiama
    /// surfer rosa' -> album "che si chiama surfer rosa". The JF-441 sample additions
    /// did not stop the fill, so the handler-side strip is the sanctioned fix.
    /// Entries carry a trailing space so a strip can never cut a word fragment (an
    /// album titled "Chiamatole" does not match "chiamato ").
    /// </summary>
    private static readonly Dictionary<string, string[]> AlbumCallingWordPrefixes = new(StringComparer.Ordinal)
    {
        // 'chiamato'/'chiamata' are the evidenced bleed and its natural gender twin;
        // 'che si chiama' is the second evidenced fill shape; 'di nome' is the same
        // natural family (today Amazon absorbs that span into the musician slot
        // instead, so it is listed for the fill shape, not a routed one).
        // Scope is it-IT ONLY by evidence: the other 16 locales carry calling-word
        // samples on SONG paths alone (called/llamada/chamada/appelee), never on the
        // album path, and their fills are clean (en-US probe 2026-09-04: 'find a
        // song called breathe' -> titleKeywords "breathe"). Add a locale key only
        // with a probe showing the same bleed in that locale.
        ["it-IT"] = new[] { "chiamato ", "chiamata ", "che si chiama ", "di nome " }
    };

    /// <summary>
    /// JF-469: strips a leading locale calling word from a raw slot value, for the
    /// one-bounded-retry pattern. CONTRACT: the strip is a FALLBACK, never a
    /// preemptive rewrite. The caller MUST first run its query with the RAW value and
    /// only retry with the stripped value on a confirmed miss (the JF-383
    /// ArtistIds-retry shape, one extra query), so an album actually titled
    /// "Chiamato qualcosa" stays findable through the raw query. The raw value also
    /// stays the value for every log line and the not-found speech (the user said
    /// "chiamato X"; the not-found names what they said).
    /// SANCTIONED DEVIATION (JF-489): the musician-slot caller skips the raw-first
    /// leg for its ARTIST query only, because "chiamato X" as an artist name is
    /// guaranteed garbage (no artist is named with a leading calling word in any
    /// locale vocabulary): it retries the stripped value as an ALBUM title first
    /// (raw-first still applies there), and the artist search runs on the stripped
    /// value. Raw-artist reachability is preserved through the JF-381 containment
    /// band (a calling-word-named artist like "Chiamato Moe" matches via
    /// containment when the user's query is the bare name).
    /// </summary>
    /// <param name="slotValue">The raw slot value as Alexa delivered it.</param>
    /// <param name="locale">The request locale; locales without an entry never strip.</param>
    /// <param name="stripped">The calling-word-stripped value when the method returns true.</param>
    /// <returns>True when a calling word was stripped and a non-empty title remains.</returns>
    public static bool TryStripLeadingAlbumCallingWord(string? slotValue, string locale, out string stripped)
    {
        stripped = string.Empty;
        if (string.IsNullOrWhiteSpace(slotValue) || !AlbumCallingWordPrefixes.TryGetValue(locale, out string[]? prefixes))
        {
            return false;
        }

        // The ONE single-cut primitive (JF-610): space-baked entries mean a bare
        // calling word ("chiamata" alone) never cuts - the raw value stays.
        string value = slotValue;
        if (Util.CarrierPhrase.TryStripLeading(ref value, prefixes))
        {
            stripped = value;
            return true;
        }

        return false;
    }

    /// <summary>
    /// The cheap DTO shape for queries that read only names/ids (JF-443/JF-446): no
    /// images, no userdata, no current program. Fresh instance per call (DtoOptions is
    /// mutable; a shared instance could be mutated through one query and leak into
    /// another). Shared by PlayAlbum's fuzzy fallback and the JF-345 song-to-album
    /// cascade.
    /// </summary>
    /// <returns>A minimal DtoOptions.</returns>
    public static DtoOptions CheapDtoOptions() => new DtoOptions(false) { EnableImages = false, EnableUserData = false, AddCurrentProgram = false };

    private readonly PluginConfiguration _config;
    private readonly ILogger _logger;
    private readonly PlaybackLaunchBuilder _launch;
    private readonly SearchService _search;
    private readonly CrossMediaFallback _crossMedia;
    private readonly int _requestTimeoutMs;
    private readonly FuzzyMissHandler _handleFuzzyMiss;

    /// <summary>
    /// Initializes a new instance of the <see cref="AlbumPlayService"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration (announce toggle, the music-flag fallback).</param>
    /// <param name="logger">The logger (BaseHandler passes its own instance so moved log statements keep their pre-extraction category).</param>
    /// <param name="launch">The playback-launch collaborator (stream URLs and the AudioPlayer.Play response chokepoint both play flows build through).</param>
    /// <param name="search">The search collaborator (SafeGetItemsResult, the playlist fuzzy fallback, the playlist FuzzyMatch pre-check).</param>
    /// <param name="crossMedia">The cross-media-fallback collaborator (the word guard and the JF-412 embedded-winner walk the album cascade gates on).</param>
    /// <param name="requestTimeoutMs">The Alexa request timeout budget in milliseconds (BaseHandler passes <see cref="RetryHelper.AlexaRequestTimeoutMs"/>, single-sourced on RetryHelper since JF-572: it is the same 6s the controller's request cancellation enforces). Passed at composition, the SearchService precedent, so this class holds no BaseHandler reference.</param>
    /// <param name="handleFuzzyMiss">The handler's own <c>HandleFuzzyMiss</c> for BaseItem candidates, wired as a DELEGATE at composition time (the PlaybackLaunchBuilder SendProgressiveResponse seam precedent). The decision block STAYS on BaseHandler (JF-408, the batch-6 stay); the delegate's target is the handler instance itself, so it captures nothing request-scoped; handlers are singleton-lifetime, so the capture pins nothing the handler does not already own.</param>
    public AlbumPlayService(
        PluginConfiguration config,
        ILogger logger,
        PlaybackLaunchBuilder launch,
        SearchService search,
        CrossMediaFallback crossMedia,
        int requestTimeoutMs,
        FuzzyMissHandler handleFuzzyMiss)
    {
        _config = config;
        _logger = logger;
        _launch = launch;
        _search = search;
        _crossMedia = crossMedia;
        _requestTimeoutMs = requestTimeoutMs;
        _handleFuzzyMiss = handleFuzzyMiss;
    }

    /// <summary>
    /// Builds a MusicAlbum query scoped to the user's libraries (with library
    /// filtering). Pass a search term for the exact indexed lookup, or null for the
    /// broad fuzzy-fallback scan. THE ONE album query shape (JF-345): PlayAlbum's own
    /// search and the song-to-album cascade build the same query so the cascade can
    /// never widen into a differently-shaped scan.
    /// </summary>
    /// <param name="libraryManager">Library manager for the library-scope filter.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user whose library filter applies.</param>
    /// <param name="searchTerm">The exact-lookup search term, or null for the fuzzy scan.</param>
    /// <param name="artistIds">Optional artist scoping (AlbumArtistIds when <paramref name="albumArtistsOnly"/> is set).</param>
    /// <param name="albumArtistsOnly">True to match albums BY the artist (AlbumArtistIds) instead of also compilations containing them (ArtistIds).</param>
    /// <returns>The album query.</returns>
    public InternalItemsQuery BuildAlbumQuery(
        ILibraryManager libraryManager,
        JellyfinUser? jellyfinUser,
        Entities.User user,
        string? searchTerm,
        Guid[]? artistIds,
        bool albumArtistsOnly = false)
    {
        var q = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
            DtoOptions = new DtoOptions(true)
        };
        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            q.SearchTerm = searchTerm;
        }

        if (artistIds is { Length: > 0 })
        {
            if (albumArtistsOnly)
            {
                // AlbumArtistIds matches albums BY the artist; ArtistIds would also match
                // compilations merely CONTAINING a track by them (live finding: "un disco
                // dei Koop" resolved to a compilation featuring Koop, not Koop's album).
                q.AlbumArtistIds = artistIds;
            }
            else
            {
                q.ArtistIds = artistIds;
            }
        }

        Util.LibraryFilter.ApplyLibraryFilter(q, user, libraryManager);
        return q;
    }

    /// <summary>
    /// JF-345: song-to-album cascade. In the 16 free-text locales PlayAlbum's album slot
    /// is an <c>AMAZON.MusicRecording</c>-style free-text type (only it-IT has the
    /// catalog-backed AlbumName type), so a bare "play abbey road" routes away from
    /// PlayAlbum, misses, and dead-ends in a song not-found (deterministic since the
    /// bare album carriers were trimmed: PR #15 for the five English locales, JF-459
    /// for the other 11; before those trims the 11 were a routing coin flip. Caveat:
    /// "away from PlayAlbum" lands on PlaySong in 13 of the 16; in the es locales the
    /// bare Reproduce/Pon forms are captured by PlayByGenreIntent's bare genre carriers
    /// instead, so this cascade never fires for those shapes; JF-463). This gate recovers
    /// that recall: on a confirmed song miss (the
    /// caller only reaches it after its own song search AND the artist cascade both
    /// missed) it runs a bounded album search and plays a strong match with a
    /// FoundAlbumInstead announcement.
    ///
    /// PRECEDENCE (deliberate): song first (caller contract), then the ARTIST cascade,
    /// then this album tier. The album tier only sees queries where no artist matched at
    /// all. Album-before-artist was considered and rejected: self-titled albums are
    /// ubiquitous ("play metallica" would flip from today's correct artist playback to
    /// the single album named Metallica), and the artist gate is itself strict
    /// (>= max(normal, 85), phonetic floor 91), so when it fires it is not a weaker
    /// signal than an exact album name. Consequence: when a sub-strict artist match
    /// lands in the JF-363 Confirm band the artist offer wins and the album is never
    /// consulted; the offer is announced and declinable, so that overlap is acceptable.
    ///
    /// Gating (stricter than the artist cascade, per the task contract): the shared
    /// 2-content-word tokenized guard (<see cref="CrossMediaFallback.CrossMediaArtistMaxWords"/>), then
    /// <c>Math.Max(normal, CrossMediaAlbumThreshold=90)</c> (containment-grade; song and
    /// album names overlap far more than artists and moods), then the JF-408
    /// interior-containment rejection. Non-phonetic and unpinned by design: no album
    /// phonetic index exists and PlayAlbum's own fuzzy fallback is likewise non-phonetic
    /// (the album-path precedent). Bounded queries only (the f5c701c lesson): the
    /// indexed SearchTerm tier, then at most ONE cheap-DTO album-catalog scan (the same
    /// bounded shape PlayAlbum's own fuzzy fallback ships), never an Audio-catalog scan.
    /// </summary>
    /// <param name="slotText">The raw slot text that missed (e.g. the song slot value).</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries.</param>
    /// <param name="user">The plugin user (threshold override, library filter).</param>
    /// <param name="session">The Jellyfin session receiving the queue.</param>
    /// <param name="context">The Alexa context (device id for crash-recovery persistence).</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for queries.</param>
    /// <param name="userDataManager">User data manager for the resume-track lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The album play response with the announcement, or null when no album clears the bar (caller falls through to its own not-found).</returns>
    public async Task<SkillResponse?> TryAlbumFallbackAsync(
        string slotText,
        JellyfinUser jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string logLabel,
        CancellationToken cancellationToken)
    {
        // JF-464: same music-disabled gate as the artist fallback. This cascade's
        // whole payoff is playing an album of music, and its queries skip
        // FilterByContentAccess, so the global flag must gate it here (null is the
        // shared no-match contract; the caller falls through to its own not-found).
        // JF-467: reads the LIVE Plugin.Instance configuration (IsMusicEnabled).
        if (!IsMusicEnabled)
        {
            _logger.LogInformation(
                "{Label}: album fallback skipped, music is disabled via configuration, query='{Query}'",
                logLabel, slotText);
            return null;
        }

        if (!_crossMedia.PassesCrossMediaWordGuard(slotText, locale, fallbackNoun: "album", logLabel, out _))
        {
            return null;
        }

        // The query is the RAW trimmed slot text, deliberately NOT the stop-word-
        // stripped token join the artist gate uses: album titles are rarely article-
        // prefixed, and both the SearchTerm index and full-name fuzzy favor raw text.
        string query = slotText.Trim();

        // Tier 1 (indexed): exact SearchTerm over MusicAlbum, the same query PlayAlbum's
        // primary search runs.
        IReadOnlyList<BaseItem> candidates = await RetryAsync(
            () => libraryManager.GetItemList(BuildAlbumQuery(libraryManager, jellyfinUser, user, query, artistIds: null)),
            logLabel + ":GetAlbumsFallbackExact",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Tier 2 (bounded fuzzy): only on an exact miss, one cheap-DTO scan of the album
        // catalog (hundreds of rows, not the Audio catalog's thousands; JF-446 shape).
        if (candidates.Count == 0 && query.Length >= MinFuzzyAlbumQueryLength)
        {
            var fuzzyQuery = BuildAlbumQuery(libraryManager, jellyfinUser, user, searchTerm: null, artistIds: null);
            fuzzyQuery.DtoOptions = CheapDtoOptions();
            candidates = await RetryAsync(
                () => libraryManager.GetItemList(fuzzyQuery),
                logLabel + ":GetAlbumsFallbackFuzzy",
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        // Single best with the JF-408/478 embedded-containment guard. JF-412: the guard
        // blocks only an embedded WINNER, never the whole tier; a substitution below the
        // 90 containment-grade bar stays refused by design (the incident's 61 for "Waltz
        // for Koop" does not substitute a song query; the DIRECT album path plays it).
        int threshold = FuzzyMatcher.GetEffectiveThreshold(user, CrossMediaAlbumThreshold);
        var eligible = _crossMedia.FindBestNonEmbeddedMatch(query, candidates, a => a.Name!, threshold);
        if (eligible is not { } match)
        {
            _logger.LogDebug(
                "{Label}: no album above threshold={Threshold} (or all embedded) for query='{Query}', not substituting",
                logLabel, threshold, query);
            return null;
        }
        _logger.LogInformation(
            "{Label}: album fallback found '{AlbumName}' score={Score} for query='{Query}' (threshold={Threshold})",
            logLabel, match.Item.Name, match.Score, query, threshold);

        return await BuildAlbumPlayResponseAsync(
            match.Item,
            jellyfinUser,
            user,
            session,
            context,
            locale,
            libraryManager,
            userDataManager,
            queueManager,
            logLabel,
            // JF-345: the substitution announcement is opt-out; the album still plays
            // when the flag is off, it just starts silently.
            announcement: _config.AnnounceCrossMediaSubstitution
                ? ResponseStrings.Get("FoundAlbumInstead", locale, match.Item.Name)
                : null,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// JF-345: the ONE album play flow (was inline in PlayAlbumIntentHandler; extracted
    /// so the song-to-album cascade plays albums with the SAME queue semantics as a
    /// direct album request). Fetches the first track page by ParentId with the JF-338
    /// AlbumIds retry for malformed/split albums, applies the resume-track index,
    /// persists the session queue and the crash-recovery queue, stores the progressive
    /// continuation for the remaining tracks, and lets the optional announcement
    /// override the speech.
    /// </summary>
    /// <param name="album">The album to play.</param>
    /// <param name="jellyfinUser">The Jellyfin user for queries and resume data.</param>
    /// <param name="user">The plugin user (stream URL token).</param>
    /// <param name="session">The Jellyfin session receiving the queue.</param>
    /// <param name="context">The Alexa context (device id for crash-recovery persistence).</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="libraryManager">Library manager for track queries.</param>
    /// <param name="userDataManager">User data manager for the resume-track lookup.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    /// <param name="logLabel">Label for log messages.</param>
    /// <param name="announcement">Optional spoken announcement replacing the default speech.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An AudioPlayer response for the album's first (resume-aware) track, or a localized tell when the album has no playable tracks.</returns>
    public async Task<SkillResponse> BuildAlbumPlayResponseAsync(
        BaseItem album,
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
        Request? request = null,
        CancellationToken cancellationToken = default)
    {
        // Get the first page of album tracks for fast time-to-audio.
        // Remaining tracks will be fetched on demand by PlaybackNearlyFinished.
        _logger.LogDebug("{Label}: querying tracks for album='{AlbumName}' (id={AlbumId})", logLabel, album.Name, album.Id);
        QueryResult<BaseItem> albumResult = await RetryAsync(
            () => _search.SafeGetItemsResult(libraryManager, new InternalItemsQuery()
            {
                User = jellyfinUser,
                Recursive = true,
                ParentId = album.Id,
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                DtoOptions = new DtoOptions(true),
                OrderBy = QueueContinuationFetcher.AlbumTrackOrder,
                Limit = ProgressiveQueueConstants.GetInitialFetchSize()
            }),
            logLabel + ":GetAlbumTracks",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("{Label}: Jellyfin returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, albumResult.TotalRecordCount);
        if (albumResult.TotalRecordCount == 0)
        {
            // Tolerant fallback: for split / multi-disc / malformed-folder albums, the
            // folder-based ParentId query can return 0 even when the tracks exist (the
            // track's Album metadata still links them). Query by album membership, which
            // ignores folder structure. Verified on the malformed "Jazz Cafe" album:
            // ParentId+Recursive returns 0, AlbumIds returns all tracks. JF-338.
            _logger.LogDebug("{Label}: folder-based track query returned 0, retrying by AlbumIds for '{Name}'", logLabel, album.Name);
            albumResult = await RetryAsync(
                () => _search.SafeGetItemsResult(libraryManager, new InternalItemsQuery()
                {
                    User = jellyfinUser,
                    Recursive = true,
                    AlbumIds = new[] { album.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Audio },
                    DtoOptions = new DtoOptions(true),
                    OrderBy = QueueContinuationFetcher.AlbumTrackOrder,
                    Limit = ProgressiveQueueConstants.GetInitialFetchSize()
                }),
                logLabel + ":GetAlbumTracksByAlbumIds",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Label}: AlbumIds fallback returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, albumResult.TotalRecordCount);
        }

        if (albumResult.TotalRecordCount == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        IReadOnlyList<BaseItem> albumItems = albumResult.Items;

        // Check for existing queue position from server-side progress
        (int startIndex, _) = ResumeMath.FindResumeTrackIndex(
            albumItems, jellyfinUser, userDataManager, resumePosition: false);

        if (startIndex > 0)
        {
            _logger.LogInformation(
                "{Label}: resuming queue from track {Index} ({Name})",
                logLabel, startIndex, albumItems[startIndex].Name);
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = startIndex; i < albumItems.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = albumItems[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = albumItems[startIndex];

        // Persist queue to device storage for crash recovery
        queueManager?.SetQueue(
            context.System.Device.DeviceID,
            albumItems.Skip(startIndex).Select(i => i.Id.ToString()).ToList(),
            0);

        // Store continuation info so PlaybackNearlyFinished can fetch the rest.
        // StartIndex uses the original page size because the database offset is
        // independent of the resume slice.
        if (albumResult.TotalRecordCount > albumResult.Items.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Album",
                    ParentId = album.Id,
                    StartIndex = albumResult.Items.Count,
                    TotalCount = albumResult.TotalRecordCount,
                    UserId = jellyfinUser.Id
                });
        }

        string item_id = albumItems[startIndex].Id.ToString();

        _logger.LogDebug(
            "{Label}: returning AudioPlayer, itemId={ItemId}, album='{AlbumName}', startIndex={StartIndex}, queueSize={QueueSize}",
            logLabel, item_id, album.Name, startIndex, queueItems.Count);
        // JF-625 queue-as-concat: in seek mode the album launches as ONE video-audio
        // concat stream keyed by the album GUID; the seek bar spans the whole album.
        // Album-level resume offset = the summed runtime of the tracks BEFORE the
        // resume track (the sliced-playlist ?start= mechanism; the in-track partial
        // position is not carried in this first cut - playback resumes at the resume
        // track's beginning, matching the AudioPlayer queue behavior of starting the
        // queue at startIndex).
        long albumStartTicks = albumItems.Take(startIndex).Sum(i => i.RunTimeTicks ?? 0);

        SkillResponse albumResponse = _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, _launch.GetStreamUrl(item_id, user), item_id, albumItems[startIndex], user, context, announceLocale: locale, collectionParentId: album.Id, collectionStartTicks: albumStartTicks);

        // JF-625 (live 2026-09-24): when the response actually took the VideoApp route
        // (observed on the directive, not re-derived from the builder's gates) and the
        // announce attached (the speech's presence IS the toggle), the announce swaps
        // onto the progressive-response vehicle: the fast-start VideoApp player steals
        // the audio channel before a FINAL-response announce finishes ("In
        // riproduzione" and then cut, the JF-501 observation), while a progressive
        // speech completes BEFORE the launch response reaches the device. The vehicle
        // speech names the ALBUM (what an album play announces); on success it returns
        // null, clearing the track-name speech the builder attached, and on vehicle
        // failure (screenless degrade path, send error) it returns the speech to ride
        // the final response.
        if (request != null
            && albumResponse.Response.OutputSpeech is not null
            && albumResponse.Response.Directives.Any(d => d is Directive.VideoAppLaunchDirective))
        {
            IOutputSpeech? albumAnnounce = SpeechBuilder.BuildNowPlayingSpeech(album.Name, locale, announceOn: true);
            albumResponse.Response.OutputSpeech = await _launch.SpeakVideoLaunchAnnounceAsync(context, request, albumAnnounce).ConfigureAwait(false);
        }

        // The caller may pass an announcement (fuzzy name correction in PlayAlbum,
        // cross-media substitution in the JF-345 cascade) so the user knows what is
        // playing instead of what was asked (JF-339).
        CrossMediaFallback.ApplyAnnouncement(albumResponse, announcement);

        return albumResponse;
    }

    /// <summary>
    /// Shared playlist-play flow used by <c>PlayPlaylistIntentHandler</c>
    /// (shuffle=false) and the shuffle-play handler (shuffle=true). Resolves the playlist,
    /// builds the initial queue, optionally shuffles it via
    /// <see cref="Playback.DeviceQueueManager.SetShuffledQueue"/>, persists the queue for
    /// crash recovery, stores progressive-continuation state, and returns an
    /// <c>AudioPlayer.Play</c> response for the first track.
    /// </summary>
    /// <param name="libraryManager">Library manager for querying playlists and items.</param>
    /// <param name="userManager">User manager for resolving the Jellyfin user.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery and shuffle.</param>
    /// <param name="playlistName">The playlist name to search for. MUST be non-empty: the callers own the empty-name elicit (JF-550) because it must name the invoking intent, and this builder serves both PlayPlaylistIntent and ShufflePlayIntent.</param>
    /// <param name="context">The Alexa context.</param>
    /// <param name="user">The plugin user.</param>
    /// <param name="session">The Jellyfin session.</param>
    /// <param name="locale">The locale for response strings.</param>
    /// <param name="shuffle">When true and <paramref name="queueManager"/> is non-null, shuffles the queue via <see cref="Playback.DeviceQueueManager.SetShuffledQueue"/>.</param>
    /// <param name="rng">Optional injectable random source for deterministic shuffle (tests); null uses <see cref="Random.Shared"/>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A skill response with an AudioPlayer directive, or a localized error tell.</returns>
    public async Task<SkillResponse> BuildPlaylistPlayResponseAsync(
        ILibraryManager libraryManager,
        IUserManager userManager,
        Playback.DeviceQueueManager? queueManager,
        string playlistName,
        Context context,
        Entities.User user,
        SessionInfo session,
        string locale,
        bool shuffle,
        Random? rng,
        CancellationToken cancellationToken)
    {
        // Shared by PlayPlaylist (shuffle=false) and ShufflePlay (shuffle=true); the
        // shuffle flag distinguishes the calling path (follow-on logs keep the
        // "PlayPlaylist:" prefix as the shared method body is identical).
        _logger.LogDebug("BuildPlaylistPlayResponseAsync: entered, locale={Locale}, shuffle={Shuffle}", locale, shuffle);

        _logger.LogDebug("Play playlist: {0}", playlistName);

        var (jellyfinUser, userError) = BaseHandler.ResolveJellyfinUser(userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        InternalItemsQuery query = new InternalItemsQuery()
        {
            User = jellyfinUser,
            SearchTerm = playlistName,
            IncludeItemTypes = new[] { BaseItemKind.Playlist },
            DtoOptions = new DtoOptions(true),
        };

        // Playlists are user-scoped, not library-scoped: native Jellyfin playlists
        // live outside any media library (the PlaylistsFolder), so the kind-aware
        // ApplyLibraryFilter skips the TopParentIds filter for the all-Playlist kind
        // set (JF-455; single decision point in LibraryFilter since JF-456). Trade-off:
        // .m3u playlists stored inside an excluded library surface too. Visibility is
        // NOT guaranteed by query.User on this path: GetItemsResult goes straight to
        // the repository without the IsVisible post-filter GetItemList applies, so
        // other users' private playlists can come back and must be filtered here
        // (code-review P1, JF-455). The track resolver separately filters tracks per user.
        Util.LibraryFilter.ApplyLibraryFilter(query, user, libraryManager, _logger);
        _logger.LogDebug("PlayPlaylist: querying Jellyfin with searchTerm='{PlaylistName}', types=Playlist", playlistName);
        QueryResult<BaseItem> playlists = await RetryAsync(() => _search.SafeGetItemsResult(libraryManager, query), "GetPlaylists", cancellationToken).ConfigureAwait(false);
        var visiblePlaylists = playlists.Items.Where(p => p.IsVisible(jellyfinUser)).ToList();
        if (visiblePlaylists.Count != playlists.Items.Count)
        {
            _logger.LogDebug("PlayPlaylist: filtered {HiddenCount} playlist(s) not visible to the user", playlists.Items.Count - visiblePlaylists.Count);
        }

        // One meaning for TotalRecordCount: the count of visible playlists in Items.
        // Rebuilding unconditionally keeps the filtered and unfiltered shapes identical
        // (JF-456; the conditional rebuild left TotalRecordCount meaning the raw count
        // whenever nothing was hidden).
        playlists = new QueryResult<BaseItem> { Items = visiblePlaylists, TotalRecordCount = visiblePlaylists.Count };

        _logger.LogDebug("PlayPlaylist: Jellyfin returned {ResultCount} playlists", playlists.TotalRecordCount);

        if (playlists.TotalRecordCount == 0)
        {
            var fuzzy = await _search.SearchItemsFuzzyAsync(playlistName, jellyfinUser, user, libraryManager, new[] { BaseItemKind.Playlist }, cancellationToken, "PlayPlaylistFuzzyFallback", locale: locale).ConfigureAwait(false);
            if (fuzzy != null)
            {
                playlists = new QueryResult<BaseItem> { Items = new List<BaseItem> { fuzzy.Value.Item }, TotalRecordCount = 1 };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
            }
        }

        BaseItem? playlistMatch = null;
        if (playlists.TotalRecordCount > 1)
        {
            _logger.LogDebug("PlayPlaylist: {Count} playlists matched, running disambiguation", playlists.TotalRecordCount);
            BaseItem? topMatch = _search.FuzzyMatch(playlistName, playlists.Items, p => p.Name, user);
            // JF-526 (JF-508 sibling): this site-level pre-check returns before
            // HandleFuzzyMiss, so the short-query full-coverage gate must be applied
            // here too; a gated miss falls into HandleFuzzyMiss below, whose Confirm
            // mode asks the yes/no "did you mean" prompt.
            if (topMatch != null && KeywordMatcher.HasFullKeywordCoverage(KeywordMatcher.Tokenize(playlistName, locale), topMatch.Name, locale))
            {
                playlistMatch = topMatch;
            }
            else
            {
                var (missOutcome, missResponse) = await _handleFuzzyMiss(
                    playlistName,
                    playlists.Items,
                    p => p.Name,
                    best => new List<(Guid, string)> { (best.Id, best.Name) },
                    DisambiguationHelper.MediaTypePlaylist,
                    locale,
                    best =>
                    {
                        playlistMatch = best;
                        return Task.FromResult<SkillResponse>(null!);
                    },
                    user: user).ConfigureAwait(false);

                if (missOutcome != BaseHandler.FuzzyMissOutcome.NotFound)
                {
                    if (missResponse != null)
                    {
                        return missResponse;
                    }
                }
                else
                {
                    var matches = playlists.Items.Take(3).Select(p => (p.Id, p.Name, (string?)_launch.GetImageUrl(p.Id.ToString("N"), user))).ToList();
                    return DisambiguationHelper.AskFirstMatch(matches, DisambiguationHelper.MediaTypePlaylist, locale, context);
                }
            }
        }
        else
        {
            playlistMatch = playlists.Items[0];
        }

        BaseItem playlist = playlistMatch!;
        _logger.LogDebug("PlayPlaylist: matched playlist='{PlaylistName}' (id={PlaylistId})", playlist.Name, playlist.Id);

        // Playlist members are linked children in the Playlists join table, NOT ParentId-owned
        // rows; querying ILibraryManager with ParentId=playlist.Id always returns 0 (issue #10).
        // Use Playlist.GetManageableItems(), the same API the Jellyfin web UI uses.
        _logger.LogDebug("PlayPlaylist: resolving tracks for playlist='{PlaylistName}'", playlist.Name);
        IReadOnlyList<BaseItem> allTracks = PlaylistTrackResolver.GetAudioTracks(playlist as Playlist, jellyfinUser);
        _logger.LogDebug("PlayPlaylist: resolved {TrackCount} audio tracks for playlist='{PlaylistName}'", allTracks.Count, playlist.Name);

        if (allTracks.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("PlaylistEmpty", locale));
        }

        int totalCount = allTracks.Count;
        List<BaseItem> playlistItems = allTracks.Take(ProgressiveQueueConstants.GetInitialFetchSize()).ToList();

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < playlistItems.Count; i++)
        {
            BaseItem item = playlistItems[i];
            queueItems.Add(new QueueItem
            {
                Id = item.Id,
                PlaylistItemId = playlist.Id.ToString(),
            });
        }

        session.NowPlayingQueue = queueItems;  // ordered, so MirrorQueueToSession can read track metadata

        string deviceId = context.System.Device.DeviceID;
        List<string> idList = playlistItems.Select(i => i.Id.ToString()).ToList();
        BaseItem? firstItem;

        if (shuffle && queueManager != null)
        {
            queueManager.SetShuffledQueue(deviceId, idList, rng);
            // Mirror the shuffled DeviceQueue order back into the session queue (metadata preserved).
            Playback.DeviceQueue deviceQueue = queueManager.GetOrCreateQueue(deviceId);
            ProgressReporter.MirrorQueueToSession(deviceQueue, session);
            firstItem = libraryManager.GetItemById(Guid.Parse(deviceQueue.ItemIds[0]));
        }
        else
        {
            firstItem = libraryManager.GetItemById(queueItems[0].Id);
            if (firstItem != null)
            {
                queueManager?.SetQueue(deviceId, idList, 0);
            }
        }

        if (firstItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        session.FullNowPlayingItem = firstItem;

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        if (totalCount > playlistItems.Count)
        {
            QueueContinuationStore.Set(
                session.UserId,
                context.System.Device.DeviceID,
                new QueueContinuation
                {
                    SourceType = "Playlist",
                    ParentId = playlist.Id,
                    PlaylistId = playlist.Id,
                    StartIndex = playlistItems.Count,
                    TotalCount = totalCount,
                    UserId = jellyfinUser!.Id,
                    // Cache the resolved tracks so continuation batches slice this list
                    // instead of re-resolving every linked child on each PlaybackNearlyFinished.
                    CachedTracks = allTracks
                });
        }

        string item_id = firstItem.Id.ToString();

        _logger.LogDebug(
            "PlayPlaylist: returning AudioPlayer, itemId={ItemId}, playlist='{PlaylistName}', queueSize={QueueSize}",
            item_id, playlist.Name, queueItems.Count);
        return _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, _launch.GetStreamUrl(item_id, user), item_id, firstItem, user, context);
    }

    /// <summary>
    /// Live read of the global music flag (global-only: no per-user override exists).
    /// Moved here with the album cascade, its only caller (JF-315 batch 8 deleted
    /// the BaseHandler original the same turn): the expression is identical to the
    /// CrossMediaFallback twin, Plugin.Instance.Configuration FIRST so a
    /// standard-API configuration replacement takes effect without a restart
    /// (JF-467), falling back to the injected configuration only when the plugin
    /// instance is absent (off-host unit tests). BaseHandler's IfMediaTypeDisabled
    /// and FilterByContentAccess share the same live-read source but never called
    /// the property even before the move.
    /// </summary>
    private bool IsMusicEnabled => (Plugin.Instance?.Configuration ?? _config).MusicEnabled;

    /// <summary>Delegates to RetryHelper.ExecuteWithRequestBudgetAsync with
    /// the composition-injected request budget and this class's logger; kept as a thin alias so the moved
    /// members keep calling <c>RetryAsync</c> by name (see the entry point doc).</summary>
    private Task<T> RetryAsync<T>(Func<T> operation, string operationName, CancellationToken cancellationToken = default)
        => RetryHelper.ExecuteWithRequestBudgetAsync(operation, _logger, operationName, timeoutMs: _requestTimeoutMs, cancellationToken: cancellationToken);
}
