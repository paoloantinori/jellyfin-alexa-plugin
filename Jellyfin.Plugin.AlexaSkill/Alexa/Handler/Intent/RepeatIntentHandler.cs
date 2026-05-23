using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Handler for AMAZON.RepeatIntent.
/// Since Amazon only routes built-in intents to custom skills during AudioPlayer playback,
/// this handler returns now-playing information (title, artist, position) instead of
/// actually repeating. This gives users a voice-activated "what's playing" command that
/// works without the invocation name during active playback.
/// </summary>
public class RepeatIntentHandler : BaseHandler
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RepeatIntentHandler"/> class.
    /// </summary>
    public RepeatIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, "AMAZON.RepeatIntent", StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns now-playing information including position when media is active.
    /// </summary>
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);

        Logger.LogDebug("RepeatIntent: entered (acting as now-playing info), locale={Locale}", locale);

        if (session?.NowPlayingItem == null)
        {
            Logger.LogDebug("RepeatIntent: no media playing, returning Tell");
            return Task.FromResult(ResponseBuilder.Tell(ResponseStrings.Get("NoMediaPlaying", locale)));
        }

        var item = session.NowPlayingItem;
        string description = BuildItemDescription(item, locale);
        string position = BuildPositionDisplay(session, locale);

        string responseText;
        if (!string.IsNullOrEmpty(position))
        {
            responseText = ResponseStrings.Get("NowPlayingWithPosition", locale, description, position);
        }
        else
        {
            responseText = ResponseStrings.Get("NowPlaying", locale, description);
        }

        var response = ResponseBuilder.Tell(responseText);
        response.Response.ShouldEndSession = false;
        return Task.FromResult(response);
    }

    private static string BuildItemDescription(MediaBrowser.Model.Dto.BaseItemDto item, string locale)
    {
        string name = item.Name ?? ResponseStrings.Get("UnknownMedia", locale);
        string artist = item.AlbumArtist ?? string.Empty;
        string series = item.SeriesName ?? string.Empty;

        if (!string.IsNullOrEmpty(artist))
        {
            return ResponseStrings.Get("TrackByArtist", locale, name, artist);
        }

        if (!string.IsNullOrEmpty(series))
        {
            int? season = item.ParentIndexNumber;
            int? episode = item.IndexNumber;
            if (season.HasValue && episode.HasValue)
            {
                return ResponseStrings.Get("SeasonEpisode", locale, series, season.Value, episode.Value, name);
            }

            return ResponseStrings.Get("SeriesTitle", locale, series, name);
        }

        return name;
    }
}
