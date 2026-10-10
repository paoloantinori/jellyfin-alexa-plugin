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
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
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
/// Handler for ShowPhotoAlbumIntent (JF-329): renders a photo album (or, when no
/// album is named, the library's most recent photos) as an APL slideshow on
/// screen devices. Photos have no audio-only degradation, so a device without
/// APL gets the honest screen-required Tell instead of a broken or silent
/// response. The feature rides the shared <see cref="PluginConfiguration.AplVisualsEnabled"/>
/// flag (no dedicated flag minted: the display IS the whole payoff, so the admin
/// visuals switch is the coherent off switch).
/// </summary>
public class ShowPhotoAlbumIntentHandler : BaseHandler
{
    /// <summary>
    /// Cap on images per slideshow: bounds the datasource payload (each entry is
    /// one image URL) and the pager's inflated page count.
    /// </summary>
    internal const int MaxSlideshowImages = 30;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShowPhotoAlbumIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public ShowPhotoAlbumIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(
            intentRequest.Intent.Name, IntentNames.ShowPhotoAlbum, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(
        Request request,
        Context context,
        Entities.User user,
        SessionInfo session,
        CancellationToken cancellationToken)
    {
        var disabled = IfFeatureDisabled(c => c.AplVisualsEnabled, request);
        if (disabled != null)
        {
            return disabled;
        }

        string locale = GetLocale(request);

        // Capability fork before any library work: photos are visual-only, so an
        // audio-only device has nothing this skill can deliver (AC #3).
        if (!AplHelper.DeviceSupportsApl(context))
        {
            Logger.LogDebug("ShowPhotoAlbum: device does not support APL, speaking the screen-required tell");
            return ResponseBuilder.Tell(ResponseStrings.Get("PhotosRequireScreen", locale));
        }

        IntentRequest intentRequest = (IntentRequest)request;
        // Optional slot (the JF-403 boundary): empty means the whole-photo-library
        // default (most recent photos), not an error to elicit.
        string? albumName = GetSlotValue(intentRequest, "photoAlbum");

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        // JF-643: the album name feeds the SearchTerm query and the fuzzy cascade,
        // both against Latin-script library names; romanize once (the fuzzy
        // fallback romanizes internally too).
        albumName = albumName == null ? null : Util.KatakanaRomanizer.Romanize(albumName);

        BaseItem? album = null;
        if (albumName != null)
        {
            album = await ResolveAlbumAsync(albumName, jellyfinUser, user, locale, cancellationToken).ConfigureAwait(false);
            if (album == null)
            {
                Logger.LogDebug("ShowPhotoAlbum: no album matched query={Query}", albumName);
                return ResponseBuilder.Tell(ResponseStrings.Get("PhotoAlbumNotFound", locale, albumName));
            }
        }

        IReadOnlyList<BaseItem> photos = await GetPhotosAsync(album, jellyfinUser, user, cancellationToken).ConfigureAwait(false);
        if (photos.Count == 0)
        {
            return ResponseBuilder.Tell(album != null
                ? ResponseStrings.Get("PhotoAlbumEmpty", locale, album.Name)
                : ResponseStrings.Get("NoPhotosFound", locale));
        }

        Logger.LogDebug(
            "ShowPhotoAlbum: album={Album} ({AlbumId}), photoCount={PhotoCount}, firstPhotoId={FirstPhotoId}, lastPhotoId={LastPhotoId}",
            album?.Name ?? "(recent)",
            album?.Id,
            photos.Count,
            photos[0].Id,
            photos[^1].Id);

        List<string> imageUrls = photos.Select(p => Launch.GetImageUrl(p.Id.ToString("N"), user)).ToList();
        string title = album?.Name ?? ResponseStrings.Get("RecentPhotosTitle", locale);
        AplRenderDocumentDirective? directive = AplHelper.BuildSlideshowDirective(title, imageUrls, context);

        // Terminal display response: the pager advances on-device via its tick
        // handler, so the session ends (a Tell) and the document stays up.
        // The directive is non-null by construction here (photos.Count > 0
        // means imageUrls.Count > 0; the builder's null is its empty contract).
        string speech = album != null
            ? ResponseStrings.Get("ShowingPhotoAlbum", locale, photos.Count, album.Name)
            : ResponseStrings.Get("ShowingRecentPhotos", locale, photos.Count);
        SkillResponse response = ResponseBuilder.Tell(speech);
        response.Response.Directives.Add(directive!);

        return response;
    }

    /// <summary>
    /// Resolve the spoken album name to a Jellyfin <c>PhotoAlbum</c> folder on the
    /// shared named-item resolve chain (<see cref="SearchService.ResolveNamedItemAsync"/>,
    /// the JF-862 hoist of this handler's inline chain): the SearchTerm tier first,
    /// the <see cref="NamedItemMultiMatchPolicy.ArbitrateThenTopRanked"/> policy on a
    /// multi-match (fuzzy arbitration through the JF-776 ScoringName reading, and the
    /// below-bar fallback to the top-ranked SearchTerm match, announced by name), and
    /// the shared bounded fuzzy scan on a SearchTerm miss. All tiers run under the
    /// per-user library filter.
    /// </summary>
    /// <param name="albumName">The romanized spoken album name.</param>
    /// <param name="jellyfinUser">The resolved Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access filtering.</param>
    /// <param name="locale">The request locale, for the fuzzy scan's tokenizer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The best-matching photo album, or null when nothing clears the bar.</returns>
    private async Task<BaseItem?> ResolveAlbumAsync(
        string albumName,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        string locale,
        CancellationToken cancellationToken)
    {
        NamedItemResolution resolution = await Search.ResolveNamedItemAsync(
            albumName,
            jellyfinUser,
            user,
            _libraryManager,
            new[] { BaseItemKind.PhotoAlbum },
            NamedItemMultiMatchPolicy.ArbitrateThenTopRanked,
            "GetPhotoAlbums",
            "ShowPhotoAlbumFuzzyFallback",
            locale,
            cancellationToken).ConfigureAwait(false);

        return resolution.Item;
    }

    /// <summary>
    /// Fetch the slideshow's photos: an album's children (DateCreated ascending,
    /// the chronological walk through the album) or the library's most recent
    /// photos (DateCreated descending) when no album was named. Both shapes run
    /// under the per-user library filter (AC #2).
    /// </summary>
    /// <param name="album">The resolved photo album, or null for the recent-photos default.</param>
    /// <param name="jellyfinUser">The resolved Jellyfin user for query context.</param>
    /// <param name="user">The plugin user for library access filtering.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The photos to display, capped at <see cref="MaxSlideshowImages"/>.</returns>
    private async Task<IReadOnlyList<BaseItem>> GetPhotosAsync(
        BaseItem? album,
        Jellyfin.Database.Implementations.Entities.User? jellyfinUser,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        var photosQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Photo },
            Limit = MaxSlideshowImages,
            OrderBy = new[] { (ItemSortBy.DateCreated, album != null ? SortOrder.Ascending : SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        if (album != null)
        {
            photosQuery.ParentId = album.Id;
        }

        ApplyLibraryFilter(photosQuery, user, _libraryManager);

        return await RetryAsync(
            () => _libraryManager.GetItemList(photosQuery),
            "GetPhotos",
            cancellationToken).ConfigureAwait(false);
    }
}
