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
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for MediaInfoIntent requests.
/// </summary>
public class MediaInfoIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly IArtistIndex? _artistIndex;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaInfoIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    /// <param name="artistIndex">Optional in-memory artist index for fast search.</param>
    public MediaInfoIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory,
        IArtistIndex? artistIndex = null) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _artistIndex = artistIndex;
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.MediaInfo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Report information about the currently playing media, including extended artist metadata when available.
    /// Supports targeted follow-up queries via the media_info_type slot
    /// (title, album, artist, year, duration, genre, biography, season, episode, series, director, cast, author, narrator, rating).
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <param name="cancellationToken">Cancellation token for request timeout.</param>
    /// <returns>A skill response with media information or an error message.</returns>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        BaseItemDto? item = session.NowPlayingItem;
        if (item == null)
        {
            Logger.LogInformation("MediaInfoIntent: no media currently playing");
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
        }

        // Check for specific info type slot (follow-up queries like "what album is this?")
        IntentRequest? intentRequest = request as IntentRequest;
        string? infoType = GetInfoType(intentRequest);

        if (!string.IsNullOrEmpty(infoType))
        {
            Logger.LogInformation("MediaInfoIntent: specific query '{InfoType}' for {ItemName}", infoType, item.Name);
            return await HandleSpecificInfoType(infoType, item, session, locale, context, user, cancellationToken).ConfigureAwait(false);
        }

        // Default: return full now-playing info
        Logger.LogInformation("MediaInfoIntent: reporting {ItemName}", item.Name);
        return await BuildNowPlayingResponse(item, session, locale, context, user, cancellationToken).ConfigureAwait(false);
    }

    private static string? GetInfoType(IntentRequest? intentRequest)
    {
        if (intentRequest?.Intent.Slots == null
            || !intentRequest.Intent.Slots.TryGetValue("media_info_type", out Slot? slot)
            || string.IsNullOrEmpty(slot.Value))
        {
            return null;
        }

        return slot.Value.ToLowerInvariant();
    }

    private async Task<SkillResponse> HandleSpecificInfoType(
        string infoType, BaseItemDto item, SessionInfo session, string locale, Context context, Entities.User user, CancellationToken cancellationToken)
    {
        SkillResponse response = infoType switch
        {
            "title" => HandleTitleQuery(item, locale),
            "album" => HandleAlbumQuery(item, locale),
            "artist" => HandleArtistQuery(item, locale),
            "year" => HandleYearQuery(item, locale),
            "duration" => HandleDurationQuery(item, locale),
            "genre" => HandleGenreQuery(item, locale),
            "biography" => await HandleBiographyQuery(item, session, locale, cancellationToken).ConfigureAwait(false),
            "season" => HandleSeasonQuery(item, locale),
            "episode" => HandleEpisodeQuery(item, locale),
            "series" => HandleSeriesQuery(item, locale),
            "director" => await HandleDirectorQuery(item, locale, cancellationToken).ConfigureAwait(false),
            "cast" => await HandleCastQuery(item, locale, cancellationToken).ConfigureAwait(false),
            "author" => await HandleAuthorQuery(item, locale, cancellationToken).ConfigureAwait(false),
            "narrator" => await HandleNarratorQuery(item, locale, cancellationToken).ConfigureAwait(false),
            "rating" => HandleRatingQuery(item, locale),
            _ => await BuildNowPlayingResponse(item, session, locale, context, user, cancellationToken).ConfigureAwait(false)
        };

        TryAttachNowPlayingCard(response, item, session, locale, context, user);
        return response;
    }

    private static SkillResponse HandleTitleQuery(BaseItemDto item, string locale)
    {
        string name = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        string artist = item.AlbumArtist ?? string.Empty;
        string response = !string.IsNullOrEmpty(artist)
            ? ResponseStrings.Get("MediaInfoTitle", locale, name, artist)
            : ResponseStrings.Get("MediaInfoTitleNoArtist", locale, name);

        return ResponseBuilder.Tell(response);
    }

    private static SkillResponse HandleAlbumQuery(BaseItemDto item, string locale)
    {
        if (string.IsNullOrEmpty(item.Album))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoAlbumUnavailable", locale));
        }

        string response = ResponseStrings.Get("MediaInfoAlbum", locale, item.Album);
        return ResponseBuilder.Tell(response);
    }

    private static SkillResponse HandleArtistQuery(BaseItemDto item, string locale)
    {
        if (string.IsNullOrEmpty(item.AlbumArtist))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoArtistUnavailable", locale));
        }

        string response = ResponseStrings.Get("MediaInfoArtist", locale, item.AlbumArtist);
        return ResponseBuilder.Tell(response);
    }

    private static SkillResponse HandleYearQuery(BaseItemDto item, string locale)
    {
        if (!item.ProductionYear.HasValue)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoYearUnavailable", locale));
        }

        string response = ResponseStrings.Get("MediaInfoYear", locale, item.ProductionYear.Value);
        return ResponseBuilder.Tell(response);
    }

    private static SkillResponse HandleDurationQuery(BaseItemDto item, string locale)
    {
        if (!item.RunTimeTicks.HasValue || item.RunTimeTicks.Value <= 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoDurationUnavailable", locale));
        }

        var duration = TimeSpan.FromTicks(item.RunTimeTicks.Value);
        string durationStr = FormatTimeSpan(duration, locale);
        string response = ResponseStrings.Get("MediaInfoDuration", locale, durationStr);
        return ResponseBuilder.Tell(response);
    }

    private static SkillResponse HandleGenreQuery(BaseItemDto item, string locale)
    {
        if (item.Genres == null || item.Genres.Length == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoGenreUnavailable", locale));
        }

        string genreList = string.Join(", ", item.Genres.Take(3));
        string response = ResponseStrings.Get("MediaInfoGenre", locale, genreList);
        return ResponseBuilder.Tell(response);
    }

    private async Task<SkillResponse> HandleBiographyQuery(BaseItemDto item, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        string? artistName = item.AlbumArtist;
        if (string.IsNullOrEmpty(artistName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoArtistUnavailable", locale));
        }

        string? artistInfo = await GetArtistInfoText(artistName, session, locale, cancellationToken).ConfigureAwait(false);
        if (artistInfo == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoBiographyUnavailable", locale, artistName));
        }

        return ResponseBuilder.Tell(artistInfo);
    }

    private static SkillResponse HandleSeasonQuery(BaseItemDto item, string locale)
    {
        if (!item.ParentIndexNumber.HasValue)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoSeasonUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoSeason", locale, item.ParentIndexNumber.Value));
    }

    private static SkillResponse HandleEpisodeQuery(BaseItemDto item, string locale)
    {
        if (!item.IndexNumber.HasValue)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoEpisodeUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoEpisode", locale, item.IndexNumber.Value));
    }

    private static SkillResponse HandleSeriesQuery(BaseItemDto item, string locale)
    {
        if (string.IsNullOrEmpty(item.SeriesName))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoSeriesUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoSeries", locale, item.SeriesName));
    }

    private static SkillResponse HandleRatingQuery(BaseItemDto item, string locale)
    {
        if (!item.CommunityRating.HasValue)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoRatingUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoRating", locale, item.CommunityRating.Value));
    }

    private async Task<SkillResponse> HandleAuthorQuery(BaseItemDto item, string locale, CancellationToken cancellationToken)
    {
        string? author = item.AlbumArtist;

        if (string.IsNullOrEmpty(author))
        {
            var people = await GetPeopleForItem(item, cancellationToken).ConfigureAwait(false);
            var authorPerson = people.FirstOrDefault(p => p.Type == PersonKind.Author);
            author = authorPerson?.Name;
        }

        if (string.IsNullOrEmpty(author))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoAuthorUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoAuthor", locale, author));
    }

    private async Task<SkillResponse> HandleDirectorQuery(BaseItemDto item, string locale, CancellationToken cancellationToken)
    {
        var people = await GetPeopleForItem(item, cancellationToken).ConfigureAwait(false);
        var director = people.FirstOrDefault(p => p.Type == PersonKind.Director);

        if (director == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoDirectorUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoDirector", locale, director.Name));
    }

    private async Task<SkillResponse> HandleCastQuery(BaseItemDto item, string locale, CancellationToken cancellationToken)
    {
        var people = await GetPeopleForItem(item, cancellationToken).ConfigureAwait(false);
        var actors = people.Where(p => p.Type == PersonKind.Actor).Take(3).ToList();

        if (actors.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoCastUnavailable", locale));
        }

        string castList = string.Join(", ", actors.Select(a => a.Name));
        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoCast", locale, castList));
    }

    private async Task<SkillResponse> HandleNarratorQuery(BaseItemDto item, string locale, CancellationToken cancellationToken)
    {
        var people = await GetPeopleForItem(item, cancellationToken).ConfigureAwait(false);
        var narrator = people.FirstOrDefault(p =>
            string.Equals(p.Role, "Narrator", StringComparison.OrdinalIgnoreCase));

        if (narrator == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoNarratorUnavailable", locale));
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("MediaInfoNarrator", locale, narrator.Name));
    }

    private async Task<IReadOnlyList<PersonInfo>> GetPeopleForItem(BaseItemDto item, CancellationToken cancellationToken)
    {
        if (item.People is { Length: > 0 })
        {
            return item.People.Select(p => new PersonInfo { Name = p.Name, Type = p.Type, Role = p.Role }).ToList();
        }

        try
        {
            BaseItem? fullItem = await RetryAsync(
                () => _libraryManager.GetItemById(item.Id),
                "GetItemForPeople",
                cancellationToken).ConfigureAwait(false);

            if (fullItem != null)
            {
                return _libraryManager.GetPeople(fullItem);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MediaInfoIntent: failed to fetch people for {ItemId}", item.Id);
        }

        return (IReadOnlyList<PersonInfo>)Array.Empty<PersonInfo>();
    }

    private async Task<SkillResponse> BuildNowPlayingResponse(BaseItemDto item, SessionInfo session, string locale, Context context, Entities.User user, CancellationToken cancellationToken)
    {
        string description;
        string? descriptionSsml;

        if (item.Type == BaseItemKind.Audio)
        {
            (description, descriptionSsml) = await BuildAudioDescriptionWithArtistInfo(item, session, locale, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            description = BuildMediaDescription(item, locale, out descriptionSsml);
        }

        string position = BuildPositionDisplay(session, locale);
        var response = string.IsNullOrEmpty(position)
            ? GetSsml("NowPlayingSsml", locale, descriptionSsml ?? description) is { } ssml
                ? TellSsml(ssml)
                : ResponseBuilder.Tell(ResponseStrings.Get("NowPlaying", locale, description))
            : GetSsml("NowPlayingWithPositionSsml", locale, descriptionSsml ?? description, position) is { } ssmlFull
                ? TellSsml(ssmlFull)
                : ResponseBuilder.Tell(ResponseStrings.Get("NowPlayingWithPosition", locale, description, position));

        TryAttachNowPlayingCard(response, item, session, locale, context, user);
        return response;
    }

    private async Task<(string Description, string? Ssml)> BuildAudioDescriptionWithArtistInfo(
        BaseItemDto item, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        (string trackDescription, string? trackSsml) = BuildTrackDescription(item, locale);

        if (string.IsNullOrEmpty(item.AlbumArtist))
        {
            return (trackDescription, trackSsml);
        }

        string? artistInfo = await GetArtistInfoText(item.AlbumArtist, session, locale, cancellationToken).ConfigureAwait(false);
        if (artistInfo == null)
        {
            return (trackDescription, trackSsml);
        }

        string enriched = trackDescription + ". " + artistInfo;
        string? enrichedSsml = trackSsml != null
            ? trackSsml + "<break time=\"300ms\"/>" + artistInfo
            : null;

        return (enriched, enrichedSsml);
    }

    private async Task<string?> GetArtistInfoText(string artistName, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<BaseItem> artists = await Util.ArtistSearch.SearchAsync(
                artistName, null, _libraryManager, _artistIndex, Logger,
                (q, ct) => RetryAsync(() => _libraryManager.GetItemList(q), "GetArtistInfo", ct),
                cancellationToken).ConfigureAwait(false);

            if (artists.Count == 0)
            {
                return null;
            }

            BaseItem artistItem = artists[0];
            string? bio = TruncateBio(artistItem.Overview);
            string[] genres = artistItem.Genres ?? Array.Empty<string>();

            if (bio == null && genres.Length == 0 && session.UserId == Guid.Empty)
            {
                return null;
            }

            int albumCount = 0;
            if (session.UserId != Guid.Empty)
            {
                var jellyfinUser = _userManager.GetUserById(session.UserId);
                if (jellyfinUser != null)
                {
                    IReadOnlyList<BaseItem> albums = await RetryAsync(
                        () => _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            User = jellyfinUser,
                            Recursive = true,
                            ArtistIds = new[] { artistItem.Id },
                            IncludeItemTypes = new[] { BaseItemKind.MusicAlbum },
                            OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) },
                            DtoOptions = new DtoOptions(false)
                        }),
                        "GetArtistAlbumCount",
                        cancellationToken).ConfigureAwait(false);

                    albumCount = albums.Count;
                }
            }

            return BuildArtistInfoResponse(artistName, bio, genres, albumCount, locale);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "MediaInfoIntent: failed to fetch artist info for {Artist}", artistName);
            return null;
        }
    }

    private static string? TruncateBio(string? overview)
    {
        if (string.IsNullOrWhiteSpace(overview))
        {
            return null;
        }

        // Take up to 2 sentences for voice response
        string clean = overview.Replace("\n", " ", StringComparison.Ordinal).Trim();
        int dotCount = 0;
        int cutIndex = clean.Length;

        for (int i = 0; i < clean.Length; i++)
        {
            if (clean[i] == '.' || clean[i] == '!' || clean[i] == '?')
            {
                dotCount++;
                if (dotCount == 2)
                {
                    cutIndex = i + 1;
                    break;
                }
            }
        }

        string truncated = clean[..cutIndex].Trim();
        return string.IsNullOrWhiteSpace(truncated) ? null : truncated;
    }

    private static string? BuildArtistInfoResponse(string artistName, string? bio, string[] genres, int albumCount, string locale)
    {
        bool hasBio = !string.IsNullOrWhiteSpace(bio);
        bool hasGenres = genres.Length > 0;
        bool hasAlbums = albumCount > 0;

        string genreList = hasGenres ? string.Join(", ", genres.Take(3)) : string.Empty;

        if (hasBio && hasGenres && hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoBioGenreAlbums", locale, artistName, genreList, albumCount, bio!);
        }

        if (hasBio && hasGenres)
        {
            return ResponseStrings.Get("ArtistInfoBioGenre", locale, artistName, genreList, bio!);
        }

        if (hasBio && hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoBioAlbums", locale, artistName, albumCount, bio!);
        }

        if (hasGenres && hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoGenreAlbums", locale, artistName, genreList, albumCount);
        }

        if (hasBio)
        {
            return ResponseStrings.Get("ArtistInfoBioOnly", locale, artistName, bio!);
        }

        if (hasGenres)
        {
            return ResponseStrings.Get("ArtistInfoGenreOnly", locale, artistName, genreList);
        }

        if (hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoAlbumsOnly", locale, artistName, albumCount);
        }

        return null;
    }

    private static (string Description, string? Ssml) BuildTrackDescription(BaseItemDto item, string locale)
    {
        string artist = item.AlbumArtist ?? string.Empty;
        string album = item.Album ?? string.Empty;

        if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(album))
        {
            return (
                ResponseStrings.Get("TrackByArtistFromAlbum", locale, item.Name, artist, album),
                GetSsml("TrackByArtistFromAlbumSsml", locale, item.Name, artist, album));
        }

        if (!string.IsNullOrEmpty(artist))
        {
            return (
                ResponseStrings.Get("TrackByArtist", locale, item.Name, artist),
                GetSsml("TrackByArtistSsml", locale, item.Name, artist));
        }

        return (item.Name ?? ResponseStrings.Get("UnknownMedia", locale), null);
    }

    private static string BuildMediaDescription(BaseItemDto item, string locale, out string? ssmlDescription)
    {
        ssmlDescription = null;

        if (item.Type == BaseItemKind.Audio)
        {
            (string desc, string? ssml) = BuildTrackDescription(item, locale);
            ssmlDescription = ssml;
            return desc;
        }

        if (item.Type == BaseItemKind.Episode)
        {
            string series = item.SeriesName ?? string.Empty;
            int? season = item.ParentIndexNumber;
            int? episode = item.IndexNumber;

            if (!string.IsNullOrEmpty(series) && season.HasValue && episode.HasValue)
            {
                return ResponseStrings.Get("SeasonEpisode", locale, series, season.Value, episode.Value, item.Name);
            }
            else if (!string.IsNullOrEmpty(series))
            {
                return ResponseStrings.Get("SeriesTitle", locale, series, item.Name);
            }

            return item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        }

        if (item.Type == BaseItemKind.Movie)
        {
            int? year = item.ProductionYear;
            return year.HasValue
                ? ResponseStrings.Get("TitleWithYear", locale, item.Name, year.Value)
                : item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        }

        return item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
    }

    private void TryAttachNowPlayingCard(SkillResponse response, BaseItemDto item, SessionInfo session, string locale, Context context, Entities.User user)
    {
        if (item.Id == Guid.Empty)
        {
            return;
        }

        string imageUrl = GetImageUrl(item.Id.ToString("N"), user);
        var audio = new MediaBrowser.Controller.Entities.Audio.Audio { Name = item.Name ?? string.Empty };
        audio.Artists = !string.IsNullOrEmpty(item.AlbumArtist) ? new List<string> { item.AlbumArtist } : new List<string>();
        audio.Album = item.Album;

        bool seekEnabled = Plugin.Instance?.Configuration?.SeekEnabled == true;
        long progressMs = 0, durationMs = 0;
        long progressTicks = 0, durationTicks = 0;

        if (seekEnabled)
        {
            if (session.PlayState?.PositionTicks != null)
            {
                progressTicks = session.PlayState.PositionTicks.Value;
                progressMs = (long)TimeSpan.FromTicks(progressTicks).TotalMilliseconds;
            }

            if (item.RunTimeTicks.HasValue && item.RunTimeTicks.Value > 0)
            {
                durationTicks = item.RunTimeTicks.Value;
                durationMs = (long)TimeSpan.FromTicks(durationTicks).TotalMilliseconds;
            }
        }

        // APL card — only on APL-capable devices
        if (AplHelper.DeviceSupportsApl(context) && AplHelper.VisualsEnabled)
        {
            var directive = seekEnabled
                ? AplHelper.BuildNowPlayingDirective(audio, imageUrl, imageUrl, context, progressMs, durationMs)
                : AplHelper.BuildNowPlayingDirective(audio, imageUrl, imageUrl, context);

            if (directive != null)
            {
                response.Response.Directives.Add(directive);
            }
        }

        // Standard Card — always sent when SeekEnabled, visible in Alexa app
        if (seekEnabled && response.Response != null)
        {
            string title = ResponseStrings.Get("NowPlayingCardTitle", locale);
            string body = item.Name ?? string.Empty;

            if (progressTicks > 0 && durationTicks > 0)
            {
                body += $"\n{FormatPosition(progressTicks)} / {FormatPosition(durationTicks)}";
            }

            response.Response.Card = new StandardCard
            {
                Title = title,
                Content = body
            };
        }
    }
}
