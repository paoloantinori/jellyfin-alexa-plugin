using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for QueryArtistLibraryIntent requests.
/// Lets users ask about their library content by artist, e.g.
/// "Which tracks do we have by Artist X?" or "What albums of Artist X are available?".
/// </summary>
public class QueryArtistLibraryIntentHandler : BaseHandler
{
    private const int VoicePageSize = 5;
    private static int MaxDisplayItems => Plugin.Instance?.Configuration?.MaxListDisplayItems ?? 15;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IArtistIndex? _artistIndex;
    private readonly ISongNgramIndex? _songNgramIndex;

    public QueryArtistLibraryIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null,
        ISongNgramIndex? songNgramIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _userDataManager = userDataManager;
        _artistIndex = artistIndex;
        _songNgramIndex = songNgramIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.QueryArtistLibrary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Query the library for content by a specific artist and return a spoken list.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the async operation.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;

        string? musician = null;
        string? queryType = null;

        if (intentRequest.Intent.Slots != null)
        {
            if (intentRequest.Intent.Slots.TryGetValue("musician", out Slot? musicianSlot))
            {
                musician = musicianSlot.Value;
                // JF-426: strip a leading Italian article Amazon failed to strip
                // (see PlayArtistSongs).
                if (musician != null)
                {
                    musician = Util.ArtistSearch.StripLeadingArticle(musician, locale);
                }
            }

            if (intentRequest.Intent.Slots.TryGetValue("query_type", out Slot? queryTypeSlot))
            {
                queryType = queryTypeSlot.Value;
            }
        }

        // JF-659: the ER canonical feeds the artist search and the song fallback;
        // `musician` keeps driving the not-found speech (the JF-642 F-1 lesson;
        // SlotValueHelper owns the full contract).
        string? canonicalMusician = Util.SlotValueHelper.GetCanonicalValue(intentRequest, "musician");

        Logger.LogDebug("QueryArtistLibrary: entered, locale={Locale}, musician={Musician}, queryType={QueryType}", locale, musician, queryType);

        // JF-550 (dead-mic sweep; JF-549 class).
        if (BuildCancelDuringOpenElicit(intentRequest, locale, "QueryArtistLibrary") is { } elicitCancel)
        {
            return elicitCancel;
        }

        if (string.IsNullOrWhiteSpace(musician))
        {
            Logger.LogDebug("QueryArtistLibrary: missing musician slot, eliciting");
            return BuildDialogElicitResponse("DidNotCatchArtistName", locale, "musician", IntentNames.QueryArtistLibrary, Util.ElicitSlots.For(IntentNames.QueryArtistLibrary));
        }

        // Layer-1 gate (GuardIndexReady): before the "searching" progressive response.
        GuardIndexReady(_artistIndex);

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        string musicianSearch = canonicalMusician ?? musician;

        // JF-448/JF-715/JF-742 (the artist composite owns the pin, the gate, and
        // the fall-through search): the choke-preserving UNGUARDED pin reads ONE
        // publish for the gate and the search, and the gate's pool seeds the
        // search (redundant with this handler's own entry gate on warming, kept
        // uniform with the other artist sites). PlayArtistSongs' shape: this
        // intent's only content input IS the musician slot, so no constraint
        // needs preserving and the gate runs unrestricted. A REAL ambiguity asks
        // which artist (the confirm leg plays the artist's songs instead of
        // listing them, the accepted JF-690 contract shift) and a stale-catalog
        // collapse lists the proven survivor instead of letting the stale rank-#1
        // canonical drive the not-found.
        var gate = await MultiValueErDisambiguation.TryArbitrateOrSearchArtistsAsync(
            intentRequest, user, _artistIndex, _libraryManager, Logger, locale, musicianSearch,
            arbitrate: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (gate.Ask != null)
        {
            return gate.Ask;
        }

        IReadOnlyList<BaseItem> artists = gate.Artists;

        if (artists.Count == 0)
        {
            Logger.LogDebug("QueryArtistLibrary: artist '{Musician}' not found", musician);

            // JF-440 sibling coverage: the same NLU coin flip that feeds
            // PlayArtistSongs feeds this intent's musician slot ('cosa abbiamo di
            // sugar free jazz'); serve the song instead of a dead-end not-found.
            // JF-659 (gate review, finding 2): skipped when the slot is
            // ER-resolved: an ER match is artist evidence, so guessing the
            // resolved name as a song TITLE could play an unrelated song titled
            // like the artist.
            if (canonicalMusician == null)
            {
                // JF-654: the song-side kana bar applies; this call passes the RAW
                // slot (this handler never romanizes its own copy), so
                // TrySongFallback self-computes the kana-origin flag pre-romanization.
                SkillResponse? songFallback = CrossMedia.TrySongFallback(
                    musicianSearch, user, session, context, locale, _songNgramIndex, _libraryManager, "QueryArtistLibrary", cancellationToken);
                if (songFallback != null)
                {
                    return songFallback;
                }
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundArtist", locale, musician));
        }

        Guid artistId = artists[0].Id;
        string artistName = artists[0].Name;
        Logger.LogDebug("QueryArtistLibrary: matched artist '{ArtistName}' ({ArtistId}), isAlbumQuery={IsAlbum}", artistName, artistId, IsAlbumQuery(queryType));

        if (IsAlbumQuery(queryType))
        {
            return await ListItemsByArtistAsync(
                artistId,
                artistName,
                jellyfinUser!,
                locale,
                new[] { BaseItemKind.MusicAlbum },
                "NoAlbumsByArtist",
                "AlbumsByArtistList",
                "AlbumsByArtistPartial",
                "GetArtistAlbums",
                context,
                user,
                cancellationToken).ConfigureAwait(false);
        }

        return await ListItemsByArtistAsync(
            artistId,
            artistName,
            jellyfinUser!,
            locale,
            // JF-358: MediaTypes does not constrain an ArtistIds query; filter via item type.
            new[] { BaseItemKind.Audio },
            "NoSongsForArtist",
            "TracksByArtistList",
            "TracksByArtistPartial",
            "GetArtistTracks",
            context,
            user,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsAlbumQuery(string? queryType)
    {
        if (string.IsNullOrWhiteSpace(queryType))
        {
            return false;
        }

        return SlotMappings.LibraryQueryTypeIsAlbum.TryGetValue(queryType.ToLowerInvariant().Trim(), out bool isAlbum) && isAlbum;
    }

    private async Task<SkillResponse> ListItemsByArtistAsync(
        Guid artistId,
        string artistName,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        string locale,
        BaseItemKind[] includeItemTypes,
        string emptyKey,
        string listKey,
        string partialKey,
        string operationName,
        Context? context,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            ArtistIds = new[] { artistId },
            IncludeItemTypes = includeItemTypes,
            OrderBy = CrossMediaFallback.PopularitySort,
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(query, user, _libraryManager);

        IReadOnlyList<BaseItem> items = await RetryAsync(() => _libraryManager.GetItemList(query), operationName, cancellationToken).ConfigureAwait(false);

        items = ResumeMath.FavoritesAndRatingsFirst(items, jellyfinUser, _userDataManager);

        if (items.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get(emptyKey, locale, artistName));
        }

        int total = items.Count;
        int displayCount = Math.Min(total, MaxDisplayItems);
        bool isTruncated = total > VoicePageSize;
        SkillResponse response;

        if (total <= VoicePageSize)
        {
            string list = string.Join(", ", items.Select(i => i.Name));
            response = ResponseBuilder.Ask(
                ResponseStrings.Get(listKey, locale, artistName, total, list),
                new Reprompt(ResponseStrings.Get("CarouselReprompt", locale)));
        }
        else
        {
            string partialList = string.Join(", ", items.Take(VoicePageSize).Select(i => i.Name));
            string speech = ResponseStrings.Get(partialKey, locale, artistName, total, VoicePageSize, partialList);
            speech += " " + ResponseStrings.Get("ShowMorePrompt", locale);
            response = ResponseBuilder.Ask(speech, new Reprompt(ResponseStrings.Get("CarouselReprompt", locale)));

            // Store pagination state for ShowMoreIntent
            response.SessionAttributes = new Dictionary<string, object>();
            ListPaginationHelper.WriteState(
                response.SessionAttributes,
                ListPaginationHelper.ListType.ArtistLibrary,
                items.Take(displayCount).Select(i => i.Id.ToString()).ToArray(),
                VoicePageSize,
                VoicePageSize);
        }

        var aplItems = items.Take(displayCount).Select(i =>
            new Apl.ListDisplayItem(i.Name, i.Id.ToString("N"), artistName, Launch.GetImageUrl(i.Id.ToString("N"), user))).ToList();
        AplDirectiveAttacher.TryAttachCarouselDirective(Logger, response, context, artistName, aplItems, "queryArtist", locale: locale);

        return response;
    }
}
