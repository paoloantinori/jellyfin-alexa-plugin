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

    /// <summary>
    /// JF-661/JF-662: the kana-origin album bar, the ONE shared definition consumed
    /// by every album auto-play decision point fed by a kana-origin query (the JF-345
    /// song-to-album cascade's 90-bar acceptance and PlayAlbum's own JF-336 fuzzy
    /// arm's 60-bar): a real length-banded Double Metaphone code collision between
    /// the romanized query and the album name (the JF-654 title-collision shape,
    /// shared with the song bar; album names carry the same '(Deluxe Edition)'
    /// parenthetical-metadata suffixes, so the band reads the stripped name). NO
    /// plain-score leg by design: on the FuzzyMatcher scale plain PartialRatio
    /// reaches 90-99 for near-identical strings with no code collision and the
    /// containment floor itself scores exactly 90, so any plain leg would re-open
    /// the wrong-accept class the JF-652 review closed ('ビートルズ' romanized to
    /// 'bitoruzu' containment-matching 'Bitoruzu Deluxe' at the bar with the suffix
    /// widening the name past the band). The score bars (the callers' thresholds)
    /// compose unchanged; this predicate adds only the kana-specific collision
    /// evidence.
    /// </summary>
    /// <param name="romanizedQuery">The romanized (post-KatakanaRomanizer) query string.</param>
    /// <param name="album">The candidate album.</param>
    /// <returns>True when the album name carries a length-banded code collision with the query.</returns>
    internal static bool PassesKanaOriginAlbumAcceptance(string romanizedQuery, BaseItem album)
        => Util.SongIndexSearch.PassesLengthBandedTitleCollision(
            DoubleMetaphone.Encode(romanizedQuery),
            romanizedQuery.Length,
            album.Name ?? string.Empty);

    /// <summary>
    /// JF-663: the kana-origin playlist bar, the playlist-surface sibling of
    /// <see cref="PassesKanaOriginAlbumAcceptance"/> (the song wrapper and the album
    /// predicate are the existing per-surface names over the ONE shared
    /// <see cref="Util.SongIndexSearch.PassesLengthBandedTitleCollision"/> primitive;
    /// no private strip+band+encode copy). Playlist names are the same title-shaped
    /// candidates with the same suffix-widening risk, so the bar is collision-only
    /// for the same containment-floor-scores-90 reason the album bar documents.
    /// </summary>
    /// <param name="romanizedQuery">The romanized (post-KatakanaRomanizer) query string.</param>
    /// <param name="playlist">The candidate playlist.</param>
    /// <returns>True when the playlist name carries a length-banded code collision with the query.</returns>
    internal static bool PassesKanaOriginPlaylistAcceptance(string romanizedQuery, BaseItem playlist)
        => PassesKanaOriginPlaylistAcceptance(
            DoubleMetaphone.Encode(romanizedQuery),
            romanizedQuery.Length,
            playlist);

    /// <summary>
    /// Codes-carried form of <see cref="PassesKanaOriginPlaylistAcceptance(string, BaseItem)"/>
    /// for callers that encode the query once and reuse it across several candidate
    /// checks (the PassesKanaOriginSongAcceptance encode-once shape): the
    /// multi-match narrowing in <see cref="BuildPlaylistPlayResponseAsync"/> loops
    /// this predicate over the server-narrowed candidate list.
    /// </summary>
    /// <param name="queryCodes">The Double Metaphone codes of the romanized query.</param>
    /// <param name="romanizedQueryLength">The romanized query's length (the band input).</param>
    /// <param name="playlist">The candidate playlist.</param>
    /// <returns>True when the playlist name carries a length-banded code collision with the query.</returns>
    internal static bool PassesKanaOriginPlaylistAcceptance(
        (string Primary, string? Alternate) queryCodes,
        int romanizedQueryLength,
        BaseItem playlist)
        => Util.SongIndexSearch.PassesLengthBandedTitleCollision(
            queryCodes,
            romanizedQueryLength,
            playlist.Name ?? string.Empty);

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
                // JF-358: this post-construction ArtistIds assignment is outside the
                // initializer scan's reach; the query (here and in its initializer)
                // must never gain a MediaTypes term: MediaTypes does not constrain
                // an ArtistIds query.
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
    /// interior-containment rejection. JF-661: a kana-origin query adds the album
    /// kana bar on top of the threshold (a real length-banded Double Metaphone
    /// collision, <see cref="PassesKanaOriginAlbumAcceptance"/>); a plain-fuzzy
    /// accept is the honest miss, and the bar composes over BOTH candidate tiers
    /// (the JF-652 precedent: the chain result is judged at the acceptance point
    /// whatever tier produced it, so a tier-1 SearchTerm winner needs the same
    /// collision evidence). The album tiers stay non-phonetic in their SCORING
    /// (no album phonetic index exists and no candidate codes are pre-computed);
    /// the kana bar carries the per-query Double Metaphone collision evidence
    /// instead. Bounded queries only (the f5c701c lesson): the
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
    /// <param name="kanaOrigin">JF-661: the flag captured on the caller's RAW
    /// PRE-romanization slot value (the JF-659 canonical invariant is the caller's
    /// to compute, it holds the slot). Null (the default) self-computes from
    /// <paramref name="slotText"/> BEFORE this method's own romanization; pin a
    /// value from a caller whose input is already romanized (see the JF-660
    /// TryEntityFallbackAsync shape).</param>
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
        global::Alexa.NET.Request.Type.Request? request,
        CancellationToken cancellationToken,
        bool? kanaOrigin = null)
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

        // JF-643: mirror of the artist gate's entry romanization, before the guard
        // to match its ordering (the SearchTerm tier and the fuzzy tier below
        // compare against Latin album names).
        // JF-661: the kana-origin flag is read from the PRE-romanization value (the
        // JF-652/JF-660 shape): the only production caller (PlaySong's cascade)
        // romanizes its song slot at entry and hands this gate the Latin local, so
        // it pins the flag captured on its raw slot; null (the default)
        // self-computes for raw-text callers.
        bool kana = kanaOrigin ?? Util.ArtistSearch.IsKanaOriginQuery(null, slotText);
        slotText = Util.KatakanaRomanizer.Romanize(slotText);

        if (!_crossMedia.PassesCrossMediaWordGuard(slotText, locale, fallbackNoun: "album", logLabel, out _))
        {
            return null;
        }

        // The query is the raw-and-romanized trimmed slot text, deliberately NOT the
        // stop-word-stripped token join the artist gate uses: album titles are rarely
        // article-prefixed, and both the SearchTerm index and full-name fuzzy favor
        // raw text.
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

        // JF-661: the kana-origin album bar. A threshold-clearing match that carries
        // no length-banded Double Metaphone collision is the plain-fuzzy class the
        // romaji query shape false-accepts (the containment floor scores exactly the
        // 90 bar: 'bitoruzu' containment-matched 'Bitoruzu Deluxe' with the suffix
        // widening the name past the collision band), so it is the honest miss here
        // (one shared definition, PassesKanaOriginAlbumAcceptance; Latin queries
        // never reach this check).
        if (kana && !PassesKanaOriginAlbumAcceptance(query, match.Item))
        {
            _logger.LogInformation(
                "{Label}: kana-origin query '{Query}' matched album '{AlbumName}' score={Score} without a length-banded Double Metaphone collision, treating as a miss (JF-661)",
                logLabel, query, match.Item.Name, match.Score);
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
            request: request,
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
        // JF-753: this page and the AlbumIds retry below drive the continuation
        // store gate, so the fallback total must be the sentinel
        // (unknownTotalOnFallback): a page-size total would read as "complete"
        // there and the album would truncate at this page on NRE-class servers
        // (the JF-673 audiobook head shape).
        QueryResult<BaseItem> albumResult = await RetryAsync(
            () => _search.SafeGetItemsResult(libraryManager,
                QueueContinuationFetcher.BuildAlbumTracksQuery(
                    jellyfinUser, album.Id, 0, ProgressiveQueueConstants.GetInitialFetchSize(), byAlbumIds: false),
                unknownTotalOnFallback: true),
            logLabel + ":GetAlbumTracks",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("{Label}: Jellyfin returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, _logger.IsEnabled(LogLevel.Debug) ? QueueContinuationFetcher.RenderTotal(albumResult.TotalRecordCount) : null);
        if (QueueContinuationFetcher.PageHasNoItems(albumResult))
        {
            // Tolerant fallback: for split / multi-disc / malformed-folder albums, the
            // folder-based ParentId query can return 0 even when the tracks exist (the
            // track's Album metadata still links them). Query by album membership, which
            // ignores folder structure. Verified on the malformed "Jazz Cafe" album:
            // ParentId+Recursive returns 0, AlbumIds returns all tracks. JF-338.
            _logger.LogDebug("{Label}: folder-based track query returned 0, retrying by AlbumIds for '{Name}'", logLabel, album.Name);
            albumResult = await RetryAsync(
                () => _search.SafeGetItemsResult(libraryManager,
                    QueueContinuationFetcher.BuildAlbumTracksQuery(
                        jellyfinUser, album.Id, 0, ProgressiveQueueConstants.GetInitialFetchSize(), byAlbumIds: true),
                    unknownTotalOnFallback: true),
                logLabel + ":GetAlbumTracksByAlbumIds",
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("{Label}: AlbumIds fallback returned {TrackCount} tracks (total={TotalCount})", logLabel, albumResult.Items.Count, _logger.IsEnabled(LogLevel.Debug) ? QueueContinuationFetcher.RenderTotal(albumResult.TotalRecordCount) : null);
        }

        if (QueueContinuationFetcher.PageHasNoItems(albumResult))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NoSongsInAlbum", locale, album.Name));
        }

        IReadOnlyList<BaseItem> albumItems = albumResult.Items;

        // Check for existing queue position from server-side progress
        (int startIndex, _) = ResumeMath.FindResumeTrackIndex(
            albumItems, jellyfinUser, userDataManager, resumePosition: false);

        // JF-625 (criterion 3): in seek mode the album's whole-album stream records
        // progress in the AudiobookPositionTracker under the album GUID (segment
        // fetches; VideoApp emits no playback events, so UserData never moves on
        // that route). When the tracker has a position, IT is the resume truth for
        // the video route and overrides the audio-route UserData answer: map the
        // absolute album position onto the track timeline by walking the runtime
        // prefix. The in-track partial rides collectionStartTicks below (exact on a
        // warm cache; the cold-serve guard drops it to the track start).
        long trackedInTrackTicks = 0;
        if (_launch.GetVideoAppForAudio(user)
            && Interface.VideoAppCapabilities.DeviceSupportsVideoApp(context)
            && Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(album.Id.ToString()) is long tracked and > 0)
        {
            long prefix = 0;
            int trackedIndex = 0;
            for (int i = 0; i < albumItems.Count; i++)
            {
                long runtime = albumItems[i].RunTimeTicks ?? 0;
                if (tracked < prefix + runtime)
                {
                    trackedIndex = i;
                    trackedInTrackTicks = tracked - prefix;
                    break;
                }

                prefix += runtime;
                trackedIndex = i + 1;
            }

            if (trackedIndex < albumItems.Count)
            {
                startIndex = trackedIndex;
                _logger.LogInformation(
                    "{Label}: seek-mode album resume from tracker: track {Index} ({Name}), album position {Position}s (in-track {Partial}s)",
                    logLabel, startIndex, albumItems[startIndex].Name, tracked / TimeSpan.TicksPerSecond, trackedInTrackTicks / TimeSpan.TicksPerSecond);
            }
        }

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

        // JF-699 item 5: launch build BEFORE any queue/session/continuation write
        // (the ordering policy lives on EnsureStreamTokenDeliverable; in seek mode
        // this launch delegates to the token-gated album-concat URL, so a refusal
        // throws here and the writes below must not land).
        string item_id = albumItems[startIndex].Id.ToString();

        _logger.LogDebug(
            "{Label}: returning AudioPlayer, itemId={ItemId}, album='{AlbumName}', startIndex={StartIndex}, queueSize={QueueSize}",
            logLabel, item_id, album.Name, startIndex, queueItems.Count);
        // JF-625 queue-as-concat: in seek mode the album launches as ONE video-audio
        // concat stream keyed by the album GUID; the seek bar spans the whole album.
        // Album-level resume offset = the summed runtime of the tracks BEFORE the
        // resume track (the sliced-playlist ?start= mechanism; the in-track partial
        // position is not carried in this first cut: playback resumes at the resume
        // track's beginning, matching the AudioPlayer queue behavior of starting the
        // queue at startIndex).
        long albumStartTicks = albumItems.Take(startIndex).Sum(i => i.RunTimeTicks ?? 0);
        // The seek-mode tracker path's in-track partial (tracked - prefix at the resume
        // track): added so a warm-cache slice lands mid-track where listening stopped.
        albumStartTicks += trackedInTrackTicks;

        SkillResponse albumResponse = _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, _launch.GetStreamUrl(item_id, user), item_id, albumItems[startIndex], user, context, announceLocale: locale, collectionParentId: album.Id, collectionStartTicks: albumStartTicks);

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
        // JF-753: the gate is regime-aware (the ONE decision shared with the
        // audiobook head, see QueueContinuationFetcher.InitialPageHasMore).
        if (QueueContinuationFetcher.InitialPageHasMore(albumResult))
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
                    UserId = jellyfinUser.Id,
                    // JF-674: identity = the queue page just installed (see MintedQueueItemIds).
                    MintedQueueItemIds = QueueContinuation.QueueIdsOf(queueItems)
                });
        }

        // The caller may pass an announcement (fuzzy name correction in PlayAlbum,
        // cross-media substitution in the JF-345 cascade) so the user knows what is
        // playing instead of what was asked (JF-339; JF-699 item 4: the string
        // overload of the gated attach absorbed the ApplyAnnouncement adapter).
        PlaybackLaunchBuilder.AttachAnnounceIfLaunched(albumResponse, announcement);

        // JF-625 (live 2026-09-24 + review): whatever speech the final response now
        // carries (the album announce, or the announcement override above, which is the
        // load-bearing correction) swaps onto the progressive-response vehicle WHEN the
        // response actually took the VideoApp route (observed on the directive, not
        // re-derived from the builder's gates): the fast-start VideoApp player steals
        // the audio channel before a FINAL-response speech finishes, while a progressive
        // speech completes BEFORE the launch response reaches the device. On success the
        // vehicle returns null (final response speechless); on failure (screenless
        // degrade path, send error) it returns the speech to ride the final response.
        if (request != null
            && albumResponse.Response.OutputSpeech is not null
            && PlaybackLaunchBuilder.HasVideoAppLaunchDirective(albumResponse))
        {
            // The vehicle speech: the caller's announcement when one was applied (the
            // load-bearing correction), else the ALBUM name - what an album play
            // announces, not the resume track the builder's attach named.
            IOutputSpeech? carried = announcement != null
                ? albumResponse.Response.OutputSpeech
                : SpeechBuilder.BuildNowPlayingSpeech(album.Name, locale, announceOn: true);
            albumResponse.Response.OutputSpeech = await _launch.SpeakVideoLaunchAnnounceAsync(context, request, carried).ConfigureAwait(false);
        }

        return albumResponse;
    }

    /// <summary>
    /// Shared playlist-play flow used by <c>PlayPlaylistIntentHandler</c>
    /// (shuffle=false) and the shuffle-play handler (shuffle=true). Resolves the playlist,
    /// builds the initial queue, optionally derives a shuffle snapshot and commits it
    /// after the launch build (JF-713, <see cref="Playback.DeviceQueueManager.DeriveShuffledQueue"/>/
    /// <see cref="Playback.DeviceQueueManager.CommitShuffledQueue"/>), persists the
    /// queue for crash recovery, stores progressive-continuation state, and returns an
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
    /// <param name="shuffle">When true and <paramref name="queueManager"/> is non-null, derives the shuffled order via <see cref="Playback.DeviceQueueManager.DeriveShuffledQueue"/> and commits it after the launch build (JF-713).</param>
    /// <param name="rng">Optional injectable random source for deterministic shuffle (tests); null uses <see cref="Random.Shared"/>.</param>
    /// <param name="kanaOrigin">JF-663: the flag captured on the caller's post-strip, PRE-romanization slot value (the JF-652/JF-660/JF-661 threading shape; kana in the stripped NAME is the transliteration evidence, kana in a stripped carrier is not). Both production callers pin it; null self-computes from <paramref name="playlistName"/> before this method's own romanization for raw-text callers.</param>
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
        bool? kanaOrigin,
        CancellationToken cancellationToken)
    {
        // Shared by PlayPlaylist (shuffle=false) and ShufflePlay (shuffle=true); the
        // shuffle flag distinguishes the calling path (follow-on logs keep the
        // "PlayPlaylist:" prefix as the shared method body is identical).
        _logger.LogDebug("BuildPlaylistPlayResponseAsync: entered, locale={Locale}, shuffle={Shuffle}", locale, shuffle);

        _logger.LogDebug("Play playlist: {0}", playlistName);

        // JF-643: the SearchTerm index, the fuzzy fallback, and the coverage gate all
        // compare against Latin-script playlist names; romanize the query once at the
        // shared entry.
        // JF-663: the kana-origin flag is read BEFORE that romanization (the
        // JF-652/JF-660/JF-661 shape: the romanization erases the script evidence
        // the bar keys on); both production callers pin the flag captured on their
        // raw slot, null self-computes for raw-text callers. spokenName keeps the
        // post-strip pre-romanization value past the reassignment so the gate
        // logs below can tell a kana-origin query from one that spoke the romaji
        // verbatim (byte-identical romanized log lines otherwise).
        bool kana = kanaOrigin ?? Util.ArtistSearch.IsKanaOriginQuery(null, playlistName);
        string spokenName = playlistName;
        playlistName = Util.KatakanaRomanizer.Romanize(playlistName);

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
            // JF-663: the kana bar at the fuzzy fallback's single acceptance point.
            // A kana-origin query whose romaji plain-fuzzy scored into the default
            // bar (the containment floor scores exactly 90) would play silently with
            // no real length-banded Double Metaphone collision, the wrong-accept
            // class the JF-652 family bars; the refusal converts the hit into the
            // honest miss below.
            if (fuzzy != null && kana && !PassesKanaOriginPlaylistAcceptance(playlistName, fuzzy.Value.Item))
            {
                _logger.LogInformation(
                    "PlayPlaylist: kana bar armed={Kana} refused the fuzzy fallback hit for spoken='{SpokenName}' romanized='{Query}': playlist '{PlaylistName}' has no length-banded Double Metaphone collision, treating as a miss (JF-663)",
                    kana, spokenName, playlistName, fuzzy.Value.Item.Name);
                fuzzy = null;
            }

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
            // JF-663: the kana bar composes over every acceptance point in this
            // branch (the site-level FuzzyMatch pre-check, the HandleFuzzyMiss
            // auto-play delegate, and the yes/no prompts below): with the flag set
            // the candidate list is narrowed once here to the collision-passing
            // playlists, so no plain-fuzzy acceptance can pick a non-colliding
            // bait the romaji scored into the bar. An emptied set is the honest
            // playlist not-found; a collision-backed candidate still disambiguates
            // and plays normally. The single-server-hit branch below is
            // deliberately NOT barred, on the JF-661 notes' doctrine criterion:
            // an acceptance that speaks a SUBSTITUTION (a name the user did not
            // say, like the cascade's FoundAlbumInstead, whose tier-1 pin bars
            // even a server-narrowed winner) demands collision evidence, while
            // this branch is a direct play and rides the server's index
            // narrowing (contains-class here, not an exact match).
            IReadOnlyList<BaseItem> fuzzyCandidates = playlists.Items;
            if (kana)
            {
                var queryCodes = DoubleMetaphone.Encode(playlistName);
                fuzzyCandidates = playlists.Items.Where(p => PassesKanaOriginPlaylistAcceptance(queryCodes, playlistName.Length, p)).ToList();
                if (fuzzyCandidates.Count == 0)
                {
                    // Name the refused candidates, bounded: the server-narrowed
                    // multi-match set is a handful, and triage needs to see WHICH
                    // baits the bar refused.
                    string refused = playlists.Items.Count <= 5
                        ? string.Join(", ", playlists.Items.Select(p => p.Name))
                        : $"{playlists.Items.Count} playlists";
                    _logger.LogInformation(
                        "PlayPlaylist: kana bar armed={Kana} refused every server candidate for spoken='{SpokenName}' romanized='{Query}': {Refused} matched with no length-banded Double Metaphone collision, treating as a miss (JF-663)",
                        kana, spokenName, playlistName, refused);
                    return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundPlaylist", locale, playlistName));
                }
            }

            BaseItem? topMatch = _search.FuzzyMatch(playlistName, fuzzyCandidates, p => p.Name, user);
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
                    fuzzyCandidates,
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
                    var matches = fuzzyCandidates.Take(3).Select(p => (p.Id, p.Name, (string?)_launch.GetImageUrl(p.Id.ToString("N"), user))).ToList();
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

        // JF-699 item 5 + JF-713: launch build BEFORE the session writes, the
        // crash-recovery SetQueue/CommitShuffledQueue tail, and the continuation store
        // (the ordering policy lives on EnsureStreamTokenDeliverable). The shuffle
        // arm is derive-then-commit like every other arm: the shuffled order is
        // derived ONCE into a PendingShuffledQueue snapshot, the first track (and
        // so the launch) is built FROM the snapshot, and the commit stores the SAME
        // snapshot after a successful build. A refused shuffle start therefore
        // leaves the device queue untouched too (the former JF-699 residual:
        // SetShuffledQueue used to land before the build, so a refused launch
        // replaced the device queue with an order that never played); the old
        // in-code reason for that order (shuffling again at commit would re-shuffle
        // and disagree with the stored order) is answered by REUSING the snapshot,
        // never re-deriving it.
        string deviceId = context.System.Device.DeviceID;
        List<string> idList = playlistItems.Select(i => i.Id.ToString()).ToList();
        BaseItem? firstItem;
        Playback.PendingShuffledQueue? pendingShuffle = null;

        if (shuffle && queueManager != null)
        {
            pendingShuffle = queueManager.DeriveShuffledQueue(idList, rng);
            firstItem = libraryManager.GetItemById(Guid.Parse(pendingShuffle.FirstItemId));
        }
        else
        {
            firstItem = libraryManager.GetItemById(queueItems[0].Id);
        }

        if (firstItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaNotFound", locale));
        }

        string item_id = firstItem.Id.ToString();

        _logger.LogDebug(
            "PlayPlaylist: returning AudioPlayer, itemId={ItemId}, playlist='{PlaylistName}', queueSize={QueueSize}",
            item_id, playlist.Name, queueItems.Count);
        SkillResponse response = _launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, _launch.GetStreamUrl(item_id, user), item_id, firstItem, user, context);

        session.NowPlayingQueue = queueItems;  // ordered, so MirrorQueueToSession can read track metadata
        session.FullNowPlayingItem = firstItem;

        if (pendingShuffle != null)
        {
            // JF-713: commit the SAME snapshot the launch built from (queueManager is
            // non-null on this arm: the snapshot is only derived under its null-check).
            // KNOWN RACE (the JF-712 derive-to-commit precedent): the commit's
            // whole-list replace is unlocked and the derive-to-commit window spans
            // the launch build, so a sibling that mutates the OLD device queue's
            // membership or pointer during the build (a playback event such as
            // PlaybackNearlyFinished, or a queue-editing intent such as AddToQueue)
            // has those writes dropped by the replace, where the pre-JF-713 order
            // (queue written first) let them land on the stored queue. The window
            // is bounded to siblings during one launch build and shares its shape
            // with the JF-699 SetQueue-after-build reorder on the ordered arm; the
            // builder's OWN mid-build writes (last-played ledger, launch scope)
            // are carried by CopySurvivingStores. A lock belongs on
            // DeviceQueueManager.ReplaceQueue (extracted in this same change) if
            // the shape ever bites live. The launch-scope side of this same window
            // is covered by the JF-723 guard in TrimLaunchBaseIfNeeded (the fresh
            // entry is never its own trim's evictee) and by the JF-739 freshness
            // stamps (a sibling record's trim inside the window cannot evict it
            // either).
            Playback.DeviceQueue shuffledQueue = queueManager!.CommitShuffledQueue(deviceId, pendingShuffle);
            // Mirror the shuffled DeviceQueue order back into the session queue (metadata preserved).
            ProgressReporter.MirrorQueueToSession(shuffledQueue, session);
        }
        else
        {
            queueManager?.SetQueue(deviceId, idList, 0);
        }

        // Store continuation info so PlaybackNearlyFinished can fetch the rest
        // (the ONE maybe-more decision; the playlist total is always real, so
        // the two-int known-total form applies)
        if (QueueContinuationFetcher.InitialPageHasMore(playlistItems.Count, totalCount))
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
                    CachedTracks = allTracks,
                    // JF-674: bind the entry to THIS queue page. The shuffle arm
                    // re-mirrors the session queue into the shuffled order right
                    // above; the identity is set-shaped, so the ordered capture
                    // validates against the shuffled install equally.
                    MintedQueueItemIds = QueueContinuation.QueueIdsOf(queueItems)
                });
        }

        return response;
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
