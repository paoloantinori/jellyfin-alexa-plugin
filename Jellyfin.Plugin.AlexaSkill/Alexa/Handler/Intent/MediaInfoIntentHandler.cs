using System;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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
        BaseItemDto? item = session.NowPlayingItem;
        if (item == null)
        {
            Logger.LogInformation("MediaInfoIntent: no media currently playing");
            return ResponseBuilder.Tell("Nothing is currently playing.");
        }

        string description = BuildMediaDescription(item);
        string position = BuildPositionInfo(session);

        string response = string.IsNullOrEmpty(position)
            ? $"Now playing: {description}"
            : $"Now playing: {description}. Position: {position}";

        Logger.LogInformation("MediaInfoIntent: reporting {ItemName}", item.Name);
        return ResponseBuilder.Tell(response);
    }

    private static string BuildMediaDescription(BaseItemDto item)
    {
        string type = item.Type ?? string.Empty;

        if (string.Equals(type, ItemType.Audio, StringComparison.OrdinalIgnoreCase))
        {
            string artist = item.AlbumArtist ?? string.Empty;
            string album = item.Album ?? string.Empty;

            if (!string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(album))
            {
                return $"{item.Name} by {artist}, from {album}";
            }
            else if (!string.IsNullOrEmpty(artist))
            {
                return $"{item.Name} by {artist}";
            }

            return item.Name ?? "Unknown media";
        }

        if (string.Equals(type, ItemType.Episode, StringComparison.OrdinalIgnoreCase))
        {
            string series = item.SeriesName ?? string.Empty;
            int? season = item.ParentIndexNumber;
            int? episode = item.IndexNumber;

            if (!string.IsNullOrEmpty(series) && season.HasValue && episode.HasValue)
            {
                return $"{series}, season {season.Value}, episode {episode.Value}: {item.Name}";
            }
            else if (!string.IsNullOrEmpty(series))
            {
                return $"{series}: {item.Name}";
            }

            return item.Name ?? "Unknown media";
        }

        if (string.Equals(type, ItemType.Movie, StringComparison.OrdinalIgnoreCase))
        {
            int? year = item.ProductionYear;
            return year.HasValue
                ? $"{item.Name} ({year.Value})"
                : item.Name ?? "Unknown media";
        }

        return item.Name ?? "Unknown media";
    }

    private static string BuildPositionInfo(SessionInfo session)
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
            string positionStr = FormatTimeSpan(position);

            if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
            {
                var runtime = TimeSpan.FromTicks(runtimeTicks.Value);
                return $"{positionStr} of {FormatTimeSpan(runtime)}";
            }

            return positionStr;
        }

        return string.Empty;
    }

    private static string FormatTimeSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours} hours and {span.Minutes} minutes";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{(int)span.TotalMinutes} minutes and {span.Seconds} seconds";
        }

        return $"{span.Seconds} seconds";
    }
}
