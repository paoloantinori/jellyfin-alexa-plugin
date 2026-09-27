using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayByGenreIntent requests. Plays media filtered by genre.
/// </summary>
public class PlayByGenreIntentHandler : BaseHandler
{
    private const int MaxQueryResults = 500;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly DeviceQueueManager? _queueManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlayByGenreIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    /// <param name="queueManager">Optional per-device queue manager for crash recovery.</param>
    public PlayByGenreIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        DeviceQueueManager? queueManager = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _queueManager = queueManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayByGenre, StringComparison.Ordinal);
    }

    /// <summary>
    /// Play songs from a specific genre.
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

        string? genreSlot = null;
        if (intentRequest.Intent.Slots != null && intentRequest.Intent.Slots.TryGetValue("genre", out Slot? genreSlotObj))
        {
            genreSlot = genreSlotObj.Value;
        }

        if (string.IsNullOrWhiteSpace(genreSlot))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchGenre", locale));
        }

        // JF-466 (JF-467 convention): the payoff is genre MUSIC through the audio
        // stream URL, so the global music flag gates the whole entry. Without the
        // gate, FilterByContentAccess would hand the genre query an EMPTY
        // IncludeItemTypes, which Jellyfin reads as "all kinds" (verified at
        // 10.11.11): a genre shared with movies (e.g. "Action") would queue and
        // play non-audio items. Placed after the slot prompt, before the warming
        // gate and the searching announcement, so a disabled request issues no
        // library query at all.
        SkillResponse? genreMusicDisabled = IfMediaTypeDisabled(c => c.MusicEnabled, request);
        if (genreMusicDisabled != null)
        {
            return genreMusicDisabled;
        }

        // Layer-1 gate (GuardIndexReady): the genre queries hit the same cold database
        // the artist index loading proxies (JF-463 wiring); gate before the
        // announcement, same placement as PlayMoodMusic.
        GuardIndexReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // JF-643: the Genres filter is exact-match against the library's Latin genre
        // tags; a ja-JP slot value arrives as katakana ('ジャズ' vs 'Jazz'), so the
        // QUERY is romanized. The not-found speech below keeps the raw slot value
        // (the user's own words).
        string genreQuery = Util.KatakanaRomanizer.Romanize(genreSlot);

        Task<IReadOnlyList<BaseItem>> GetGenreItemsAsync(string genre)
        {
            var query = new InternalItemsQuery
            {
                User = jellyfinUser,
                Recursive = true,
                Limit = MaxQueryResults,
                Genres = new[] { genre },
                IncludeItemTypes = FilterByContentAccess(new[] { BaseItemKind.Audio }),
                OrderBy = new[] { (ItemSortBy.Random, SortOrder.Ascending) },
                DtoOptions = new DtoOptions(true)
            };
            ApplyLibraryFilter(query, user, _libraryManager);

            return RetryAsync(() => _libraryManager.GetItemList(query), "GetGenreItems", cancellationToken);
        }

        IReadOnlyList<BaseItem> items = await GetGenreItemsAsync(genreQuery).ConfigureAwait(false);

        if (items.Count == 0 && Util.KatakanaRomanizer.ContainsKana(genreSlot))
        {
            // JF-643: romanization puts the query in Latin script, but the server-side
            // Genres filter is exact CleanValue equality, so 'jazu' still misses the tag
            // 'Jazz'. This resolution tier fires ONLY for kana slots (a Latin query keeps
            // its exact-match behavior byte-for-byte): match the romanized value against
            // the library's genre vocabulary through the shared phonetic matcher (the
            // same Double Metaphone bridge the artist path uses via its pre-computed
            // index), then re-query with the canonical tag. No match falls through to the
            // existing artist fallback and not-found unchanged.
            string? resolvedGenre = await ResolveGenreTagAsync(genreQuery, jellyfinUser!, user, cancellationToken).ConfigureAwait(false);
            if (resolvedGenre != null)
            {
                Logger.LogInformation(
                    "PlayByGenre: kana genre '{Query}' resolved to library tag '{Genre}' (JF-643)",
                    genreQuery, resolvedGenre);
                items = await GetGenreItemsAsync(resolvedGenre).ConfigureAwait(false);
            }
        }

        if (items.Count == 0)
        {
            // JF-463: the genre slot is free-text (AMAZON.Genre in 16 locales,
            // AMAZON.SearchQuery in it-IT), so bare verb+title utterances
            // ("Reproduce abbey road", es) can land here with a title captured as the
            // genre. Mirror the PlayMoodMusic recovery: try the shared cross-media
            // artist fallback (word-count guard + threshold inside); on a miss fall
            // through to the genre not-found unchanged.
            SkillResponse? artistFallback = await CrossMedia.TryEntityFallbackAsync(
                genreSlot, jellyfinUser!, user, session, context, locale,
                _libraryManager, _userDataManager, _queueManager, _artistIndex,
                "PlayByGenre artist fallback", cancellationToken).ConfigureAwait(false);
            if (artistFallback != null)
            {
                return artistFallback;
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundGenre", locale, genreSlot));
        }

        List<QueueItem> queueItems = new List<QueueItem>();
        for (int i = 0; i < items.Count; i++)
        {
            queueItems.Add(new QueueItem { Id = items[i].Id });
        }

        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = items[0];

        string itemId = items[0].Id.ToString();

        return Launch.BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, Launch.GetStreamUrl(itemId, user), itemId, items[0], user, context);
    }

    /// <summary>
    /// Bound on the genre-vocabulary query of the JF-643 kana resolution tier:
    /// distinct genre tags are low-cardinality (dozens to low hundreds), so 500
    /// rows covers every realistic library; a pathological auto-tagging library
    /// beyond the cap resolves to not-found, the tier's no-match outcome.
    /// </summary>
    private const int MaxGenreVocabulary = 500;

    /// <summary>
    /// JF-643: resolves a romanized katakana genre value ('jazu') to the library's
    /// canonical Latin tag ('Jazz') through the shared phonetic fuzzy matcher. Genre
    /// items carry no pre-computed phonetic codes (only the artist index has those),
    /// so the codes are computed here once per vocabulary scan; candidates are
    /// deduplicated by NAME because Genre and MusicGenre items can both exist for the
    /// same tag. Returns null on no vocabulary or no match above threshold.
    /// </summary>
    /// <param name="romanizedGenre">The romanized genre query.</param>
    /// <param name="jellyfinUser">The Jellyfin user (query scoping).</param>
    /// <param name="user">The plugin user (library filter).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The canonical library genre tag, or null.</returns>
    private async Task<string?> ResolveGenreTagAsync(
        string romanizedGenre,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        var vocabularyQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Genre, BaseItemKind.MusicGenre },
            Limit = MaxGenreVocabulary,
            DtoOptions = new DtoOptions(false) { EnableImages = false, EnableUserData = false }
        };
        ApplyLibraryFilter(vocabularyQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> genres = await RetryAsync(
            () => _libraryManager.GetItemList(vocabularyQuery),
            "GetGenreVocabulary",
            cancellationToken).ConfigureAwait(false);
        if (genres.Count == 0)
        {
            return null;
        }

        // Deduplicate by name (Genre + MusicGenre twins), computing one phonetic code set per tag.
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)>(genres.Count);
        var candidates = new List<BaseItem>(genres.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (BaseItem genre in genres)
        {
            if (string.IsNullOrEmpty(genre.Name) || !seen.Add(genre.Name))
            {
                continue;
            }

            codes[genre.Id] = DoubleMetaphone.Encode(genre.Name);
            candidates.Add(genre);
        }

        BaseItem? best = FuzzyMatcher.FindBestMatch(
            romanizedGenre,
            candidates,
            g => g.Name!,
            g => g.Id,
            id => codes.TryGetValue(id, out var code) ? code : null,
            FuzzyMatcher.DefaultThreshold);

        return best?.Name;
    }
}
