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
        var intentRequest = (IntentRequest)request;
        Slot? stationSlot = null;
        intentRequest.Intent.Slots?.TryGetValue(IntentNames.Slots.Station, out stationSlot);
        string? station = stationSlot?.Value;

        // Escape hatch from the elicitation trap (shared CancelWords helpers, same shape
        // as PlaySong/PlayAlbum): while the station Dialog.ElicitSlot below is open, a
        // stop/cancel word gets captured into the station slot (dialogState
        // IN_PROGRESS) instead of routing to AMAZON.Stop/CancelIntent, and answering it
        // with the nothing-playing Tell recreates the out-of-context reply JF-472 fixes.
        // Gated on IN_PROGRESS deliberately (JF-445): this elicit persists no session
        // state, so a fresh full-utterance request whose slot happens to be a cancel
        // word keeps today's behavior.
        if (Util.CancelWords.IsDialogInProgress(intentRequest)
            && Util.CancelWords.AnySlotIsCancelWord(intentRequest, locale))
        {
            Logger.LogInformation("PlayRadio: captured cancel word during open station elicit (station='{Station}'), ending flow", station);
            return ResponseBuilder.Tell(ResponseStrings.Get("FindSongCancelled", locale));
        }

        if (session.FullNowPlayingItem == null)
        {
            // JF-472: Amazon's statistical NLU steals bare genre forms ("suona jazz")
            // for PlayRadioIntent even though every sample is noun-carrying, so the
            // intent arrives slot-less while nothing plays. Radio mode cannot start
            // without a seed track, and the nothing-playing Tell sounds out of context
            // for those utterances: elicit the station instead (session open, the
            // follow-up fills the station slot because the intent is dialog-registered,
            // anti-pattern #9). The elicit is deliberately conditional on NOTHING
            // playing: with a current track the context seeds the radio and every
            // slot-less sample ("riproduci radio") must keep starting radio mode
            // directly. The captured station is not yet actionable (no station playback
            // feature), so a filled slot still falls through to the Tell. The intent
            // declares exactly one slot (station) in every locale; if a second slot is
            // ever added, allSlotNames below must list it too (Amazon rejects a partial
            // updatedIntent).
            if (string.IsNullOrWhiteSpace(station))
            {
                return BuildElicitSlotResponse(
                    IntentNames.PlayRadio,
                    IntentNames.Slots.Station,
                    new[] { IntentNames.Slots.Station },
                    ResponseStrings.Get("RadioAskStation", locale));
            }

            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNothingPlaying", locale));
        }

        var currentAudio = session.FullNowPlayingItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNotAudio", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

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

        string? nowPlayingSsml = GetSsml("NowPlayingSsml", locale, EscapeXml(currentAudio.Name));
        string radioMsg = ResponseStrings.Get("RadioStarted", locale, (queue.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));

        var response = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(currentAudio.Id.ToString(), user), currentAudio.Id.ToString(), currentAudio, user, context);
        if (GetAnnounceNowPlaying(user))
        {
            response.Response.OutputSpeech = nowPlayingSsml != null
                ? (IOutputSpeech)new SsmlOutputSpeech { Ssml = $"<speak>{nowPlayingSsml}. {EscapeXml(radioMsg)}</speak>" }
                : new PlainTextOutputSpeech($"{ResponseStrings.Get("NowPlaying", locale, currentAudio.Name)}. {radioMsg}");
        }

        return response;
    }
}
