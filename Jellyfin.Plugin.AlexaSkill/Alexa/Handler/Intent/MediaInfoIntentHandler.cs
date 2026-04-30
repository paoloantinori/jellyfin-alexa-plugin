using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for MediaInfoIntent requests.
/// </summary>
public class MediaInfoIntentHandler : BaseHandler
{
    private static class ItemType
    {
        public const string Audio = "Audio";
        public const string Episode = "Episode";
        public const string Movie = "Movie";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaInfoIntentHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public MediaInfoIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "MediaInfoIntent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Report information about the currently playing media.
    /// </summary>
    /// <param name="request">The skill request which should be handled.</param>
    /// <param name="context">The context of the skill intent request.</param>
    /// <param name="user">The user instance.</param>
    /// <param name="session">The session instance.</param>
    /// <returns>A skill response with media information or an error message.</returns>
    public override SkillResponse Handle(Request request, Context context, Entities.User user, SessionInfo session)
    {
        string locale = GetLocale(request);
        BaseItemDto? item = session.NowPlayingItem;
        if (item == null)
        {
            Logger.LogInformation("MediaInfoIntent: no media currently playing");
            return ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale));
        }

        string description = BuildMediaDescription(item, locale);
        string position = BuildPositionInfo(session, locale);

        string response = string.IsNullOrEmpty(position)
            ? ResponseStrings.Get("NowPlaying", locale, description)
            : ResponseStrings.Get("NowPlayingWithPosition", locale, description, position);

        Logger.LogInformation("MediaInfoIntent: reporting {ItemName}", item.Name);
        return ResponseBuilder.Tell(response);
    }

    private static string BuildMediaDescription(BaseItemDto item, string locale)
    {
        string type = item.Type.ToString() ?? string.Empty;

        if (string.Equals(type, ItemType.Audio, StringComparison.OrdinalIgnoreCase))
        {
            string artist = item.AlbumArtist ?? string.Empty;
            string album = item.Album ?? string.Empty;

            if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(album))
            {
                return ResponseStrings.Get("TrackByArtistFromAlbum", locale, item.Name, artist, album);
            }
            else if (!string.IsNullOrEmpty(artist))
            {
                return ResponseStrings.Get("TrackByArtist", locale, item.Name, artist);
            }

            return item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        }

        if (string.Equals(type, ItemType.Episode, StringComparison.OrdinalIgnoreCase))
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

        if (string.Equals(type, ItemType.Movie, StringComparison.OrdinalIgnoreCase))
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

        long? positionTicks = session.PlayState.PositionTicks;
        long? runtimeTicks = session.NowPlayingItem?.RunTimeTicks;

        if (positionTicks.HasValue && positionTicks.Value > 0)
        {
            var position = TimeSpan.FromTicks(positionTicks.Value);
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
