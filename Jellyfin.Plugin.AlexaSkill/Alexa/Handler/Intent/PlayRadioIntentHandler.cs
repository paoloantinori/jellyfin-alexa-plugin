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
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayRadioIntent requests.
/// Starts radio mode by finding similar tracks to the current or last played item
/// and queuing them for continuous playback.
/// </summary>
public class PlayRadioIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlayRadioIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayRadio, StringComparison.Ordinal);
    }

    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.RadioModeEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);

        if (session.FullNowPlayingItem == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNothingPlaying", locale));
        }

        var currentAudio = session.FullNowPlayingItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNotAudio", locale));
        }

        await SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)).ConfigureAwait(false);

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        IReadOnlyList<BaseItem> similarTracks = await FindRadioTracksAsync(currentAudio, jellyfinUser!, user, _libraryManager, cancellationToken).ConfigureAwait(false);

        if (similarTracks.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNoSimilar", locale));
        }

        List<BaseItem> shuffled = similarTracks.ToList();
        Shuffle(shuffled);
        if (shuffled.Count > 20)
        {
            shuffled.RemoveRange(20, shuffled.Count - 20);
        }

        var queue = new List<QueueItem> { new() { Id = currentAudio.Id } };
        foreach (BaseItem track in shuffled)
        {
            if (track.Id != currentAudio.Id)
            {
                queue.Add(new QueueItem { Id = track.Id });
            }
        }

        session.NowPlayingQueue = queue;
        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        Logger.LogInformation("Radio mode enabled with {Count} similar tracks for {SongName}", queue.Count - 1, currentAudio.Name);

        string? nowPlayingSsml = GetSsml("NowPlayingSsml", locale, currentAudio.Name);
        string radioMsg = ResponseStrings.Get("RadioStarted", locale, (queue.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var response = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(currentAudio.Id.ToString(), user), currentAudio.Id.ToString(), currentAudio, user, context);
        response.Response.OutputSpeech = nowPlayingSsml != null
            ? (IOutputSpeech)new SsmlOutputSpeech { Ssml = $"<speak>{nowPlayingSsml}. {radioMsg}</speak>" }
            : new PlainTextOutputSpeech($"{ResponseStrings.Get("NowPlaying", locale, currentAudio.Name)}. {radioMsg}");

        return response;
    }
}
