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

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaInfoIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public MediaInfoIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.MediaInfo, StringComparison.Ordinal);
    }

    /// <summary>
    /// Report information about the currently playing media, including extended artist metadata when available.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
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

        string position = BuildPositionInfo(session, locale);

        Logger.LogInformation("MediaInfoIntent: reporting {ItemName}", item.Name);

        if (string.IsNullOrEmpty(position))
        {
            string? ssml = GetSsml("NowPlayingSsml", locale, descriptionSsml ?? description);
            if (ssml != null)
            {
                return TellSsml(ssml);
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("NowPlaying", locale, description));
        }

        string? ssmlFull = GetSsml("NowPlayingWithPositionSsml", locale, descriptionSsml ?? description, position);
        if (ssmlFull != null)
        {
            return TellSsml(ssmlFull);
        }

        return ResponseBuilder.Tell(ResponseStrings.Get("NowPlayingWithPosition", locale, description, position));
    }

    private async Task<(string description, string? ssml)> BuildAudioDescriptionWithArtistInfo(
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
            IReadOnlyList<BaseItem> artists = await RetryAsync(
                () => _libraryManager.GetItemList(new InternalItemsQuery
                {
                    Recursive = true,
                    SearchTerm = artistName,
                    IncludeItemTypes = new[] { BaseItemKind.MusicArtist },
                    Limit = 1,
                    DtoOptions = new DtoOptions(false)
                }), "GetArtistInfo", cancellationToken).ConfigureAwait(false);

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
                            DtoOptions = new DtoOptions(false)
                        }), "GetArtistAlbumCount", cancellationToken).ConfigureAwait(false);

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
            return ResponseStrings.Get("ArtistInfoBioGenreAlbums", locale, artistName, genreList, albumCount, bio);
        }

        if (hasBio && hasGenres)
        {
            return ResponseStrings.Get("ArtistInfoBioGenre", locale, artistName, genreList, bio);
        }

        if (hasBio && hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoBioAlbums", locale, artistName, albumCount, bio);
        }

        if (hasGenres && hasAlbums)
        {
            return ResponseStrings.Get("ArtistInfoGenreAlbums", locale, artistName, genreList, albumCount);
        }

        if (hasBio)
        {
            return ResponseStrings.Get("ArtistInfoBioOnly", locale, artistName, bio);
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

    private static (string description, string? ssml) BuildTrackDescription(BaseItemDto item, string locale)
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

    private static string BuildPositionInfo(SessionInfo session, string locale)
    {
        if (session.PlayState == null || session.PlayState.PositionTicks == null)
        {
            return string.Empty;
        }

        long positionTicks = session.PlayState.PositionTicks.Value;
        long? runtimeTicks = session.NowPlayingItem?.RunTimeTicks;

        if (positionTicks > 0)
        {
            var position = TimeSpan.FromTicks(positionTicks);
            string positionStr = FormatTimeSpan(position, locale);

            if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
            {
                var runtime = TimeSpan.FromTicks(runtimeTicks.Value);
                return ResponseStrings.Get("PositionOfTotal", locale, positionStr, FormatTimeSpan(runtime, locale));
            }

            return positionStr;
        }

        return string.Empty;
    }

    private static string FormatTimeSpan(TimeSpan span, string locale)
    {
        if (span.TotalHours >= 1)
        {
            return ResponseStrings.Get("HoursAndMinutes", locale, (int)span.TotalHours, span.Minutes);
        }

        if (span.TotalMinutes >= 1)
        {
            return ResponseStrings.Get("MinutesAndSeconds", locale, (int)span.TotalMinutes, span.Seconds);
        }

        return ResponseStrings.Get("SecondsOnly", locale, span.Seconds);
    }
}
