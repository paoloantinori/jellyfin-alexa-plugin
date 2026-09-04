using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
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
    /// <summary>How many live-TV radio channel names the elicit reprompt offers (JF-474 UX a).</summary>
    private const int SuggestChannelCount = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILiveTvStreamResolver _streamResolver;

    public PlayRadioIntentHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILiveTvStreamResolver streamResolver,
        ILoggerFactory loggerFactory) : base(sessionManager, config, loggerFactory)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _streamResolver = streamResolver;
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

        // JF-480: PAUSED is a third state the JF-472 model missed. The plugin's pause
        // emits AudioPlayer.Stop, so the platform answers with a PlaybackStopped event,
        // and Jellyfin's OnPlaybackStopped clears FullNowPlayingItem only on the session
        // of the device that SENT that event. In a multi-room group the voice request
        // comes from the coordinator while playback events come from a member device,
        // so the requester's session keeps the play path's optimistic
        // FullNowPlayingItem indefinitely (live: corr=2c2d8676, paused 43s earlier,
        // still seeded radio mode from the paused track). Item presence therefore
        // cannot distinguish playing from paused; the request context can: after a
        // pause every customer-initiated request carries playerActivity STOPPED
        // ("stream was interrupted"), while active playback reports PLAYING.
        // BUFFER_UNDERRUN counts as playing (transient mid-playback rebuffering, the
        // same treatment as PlaybackFinishedEventHandler's hasQueuedNext); both states
        // live in the shared BaseHandler.IsActivelyPlaying helper (JF-481).
        bool activelyPlaying = IsActivelyPlaying(context);

        // JF-481 item 2a, PROVISIONAL pending the device observation listed in the
        // task: FINISHED is natural queue exhaustion, and with the requester's session
        // still holding the item (the multi-room shape of JF-480: playback events come
        // from a member device, so the coordinator's FullNowPlayingItem survives) the
        // radio seed is the PostPlay-adjacent continuation. A user asking for radio
        // right after the queue ended wants more music, not a station question. This
        // branch is unit-testable only for the mechanism, not the rightness; if the
        // device ever shows FINISHED arriving with a stale item it does not apply to,
        // tighten here.
        bool queueJustFinished = string.Equals(context.AudioPlayer?.PlayerActivity, "FINISHED", StringComparison.Ordinal);
        bool maySeedRadio = activelyPlaying || queueJustFinished;

        if (!maySeedRadio || session.FullNowPlayingItem == null)
        {
            // JF-472: Amazon's statistical NLU steals bare genre forms ("suona jazz")
            // for PlayRadioIntent even though every sample is noun-carrying, so the
            // intent arrives slot-less while nothing plays. Radio mode cannot start
            // without a seed track, and the nothing-playing Tell sounds out of context
            // for those utterances: elicit the station instead (session open, the
            // follow-up fills the station slot because the intent is dialog-registered,
            // anti-pattern #9). The elicit is deliberately conditional on nothing
            // ACTIVELY playing (JF-480: a paused device still holds a now-playing item
            // but is not actively playing, so it elicits too): with a current track
            // actively playing, or the queue just finished (JF-481 item 2a), the
            // context seeds the radio and every slot-less sample
            // ("riproduci radio") must keep starting radio mode directly. The intent
            // declares exactly one slot (station) in every locale; if a second slot is
            // ever added, allSlotNames below must list it too (Amazon rejects a partial
            // updatedIntent).
            if (string.IsNullOrWhiteSpace(station))
            {
                // JF-474 review P3-1: the reprompt's channel-list query is DB work before
                // the ask; send the progressive response first so a slow query cannot
                // blow the response window on the question itself.
                RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));
                List<string> channelNames = await GetRadioChannelNamesAsync(session, user, locale, cancellationToken).ConfigureAwait(false);
                return BuildStationElicit(ResponseStrings.Get("RadioAskStation", locale), locale, channelNames);
            }

            // JF-474 UX b: a question-shaped answer ("quali ci sono?", "what are my
            // options?") is free text in the station slot, not a station. Answer it
            // with the available list (channels + genre examples) and RE-ASK with the
            // elicit (session stays open so the next utterance still fills the slot),
            // never the nothing-playing Tell.
            if (QuestionWords.IsQuestion(station, locale))
            {
                Logger.LogInformation("PlayRadio: question-shaped station answer '{Station}', listing options and re-asking", station);
                RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));
                List<string> channelNames = await GetRadioChannelNamesAsync(session, user, locale, cancellationToken).ConfigureAwait(false);
                return BuildStationHelpResponse(locale, channelNames);
            }

            // JF-474: a captured station is actionable now, in tiers: (i) a live-TV
            // radio channel match plays that channel; (ii) a genre word seeds radio
            // mode with that genre's tracks; (iii) anything else gets a truthful
            // not-found naming the word, never the out-of-context nothing-playing Tell.
            RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return userError;
            }

            BaseItem? channel = await FindRadioChannelAsync(station!, jellyfinUser!, user, cancellationToken).ConfigureAwait(false);
            if (channel != null)
            {
                return await LaunchChannelAsync(channel, context, user, session, locale, cancellationToken).ConfigureAwait(false);
            }

            // JF-474 review P3-4: natural answers carry carrier nouns and articles
            // ("musica jazz", "il jazz", "jazz music") and the Genres filter is exact
            // CleanValue equality, so strip locale stop-words before the genre query
            // (the same Tokenize discipline TryEntityFallbackAsync uses for artists).
            string[] contentWords = Util.KeywordMatcher.Tokenize(station!, locale);
            string genreQuery = contentWords.Length > 0 ? string.Join(" ", contentWords) : station!;
            IReadOnlyList<BaseItem> genreTracks = await FindRadioTracksByGenreAsync(
                new[] { genreQuery }, jellyfinUser!, user, _libraryManager, cancellationToken).ConfigureAwait(false);
            if (genreTracks.Count > 0)
            {
                return StartGenreRadio(genreQuery, genreTracks, session, user, context, locale);
            }

            Logger.LogInformation("PlayRadio: station '{Station}' matched no radio channel and no genre", station);
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioStationNotFound", locale, station));
        }

        var currentAudio = session.FullNowPlayingItem as MediaBrowser.Controller.Entities.Audio.Audio;
        if (currentAudio == null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("RadioNotAudio", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (seedUser, seedError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (seedError != null)
        {
            return seedError;
        }

        IReadOnlyList<BaseItem> similarTracks = await FindRadioTracksAsync(currentAudio, seedUser!, user, _libraryManager, cancellationToken).ConfigureAwait(false);

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

        Logger.LogInformation("Radio mode enabled with {Count} similar tracks for {SongName}", queue.Count - 1, currentAudio.Name);

        return StartRadioPlayback(currentAudio, queue, queue.Count - 1, session, user, context, locale);
    }

    /// <summary>
    /// The live-TV RADIO channel names for the elicit reprompt and the help answer
    /// (JF-474 UX a): a bounded query (Limit 3, sort-name order), radio channels only.
    /// A LiveTvChannel persists MediaType Audio exactly when its ChannelType is Radio
    /// (Jellyfin 10.11 LiveTvChannel.MediaType override + the entity persist path), so
    /// MediaTypes=Audio scopes the query to radio stations. Fail-soft by design: this
    /// only ENRICHES the elicit, so a failed user resolution or a throwing query falls
    /// back to the genre-word reprompt instead of blocking the question.
    /// </summary>
    /// <param name="session">The Jellyfin session (user id for the query).</param>
    /// <param name="user">The plugin user (library access filtering).</param>
    /// <param name="locale">The request locale, for user-resolution errors.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to <see cref="SuggestChannelCount"/> channel names; empty on any failure.</returns>
    private async Task<List<string>> GetRadioChannelNamesAsync(SessionInfo session, Entities.User user, string locale, CancellationToken cancellationToken)
    {
        try
        {
            var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
            if (userError != null)
            {
                return new List<string>();
            }

            IReadOnlyList<BaseItem>? channels = await QueryRadioChannelsAsync(
                jellyfinUser!, user, null, SuggestChannelCount, cancellationToken).ConfigureAwait(false);
            return channels?
                .Select(c => c.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Take(SuggestChannelCount)
                .ToList() ?? new List<string>();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "PlayRadio: radio-channel name lookup failed, elicit falls back to the genre-word reprompt");
            return new List<string>();
        }
    }

    /// <summary>
    /// The shared live-TV radio channel query (tier i and the name list): the
    /// PlayChannelIntentHandler machinery (SearchTerm + LiveTvChannel + bounded
    /// RetryAsync, ApplyLibraryFilter), read-only reused per the JF-474 scope, scoped
    /// to RADIO channels via MediaTypes=Audio. A null searchTerm lists channels
    /// (for suggestions) instead of searching them.
    /// </summary>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user (library access filtering).</param>
    /// <param name="searchTerm">The station word to search for, or null to list.</param>
    /// <param name="limit">Row cap for the query.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matched/listed channels, possibly empty or null from the mocked manager in tests.</returns>
    private async Task<IReadOnlyList<BaseItem>?> QueryRadioChannelsAsync(
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        string? searchTerm,
        int limit,
        CancellationToken cancellationToken)
    {
        var channelQuery = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = searchTerm,
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel },
            MediaTypes = new[] { MediaType.Audio },
            Limit = limit,
            DtoOptions = new DtoOptions(true)
        };
        if (searchTerm == null)
        {
            // Listing for suggestions: deterministic name order. The SearchTerm search
            // stays unordered, exactly like the PlayChannel query it mirrors.
            channelQuery.OrderBy = new[] { (ItemSortBy.SortName, SortOrder.Ascending) };
        }

        ApplyLibraryFilter(channelQuery, user, _libraryManager);

        return await RetryAsync(
            () => _libraryManager.GetItemList(channelQuery),
            searchTerm == null ? "GetRadioChannelNames" : "GetRadioStationChannels",
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Tier (i): resolve the captured station against the live-TV radio channels with
    /// the PlayChannel resolution rules: an exact SearchTerm query first, then the
    /// shared fuzzy fallback (SearchItemsFuzzyAsync, accent/typo drift) on a miss.
    /// </summary>
    /// <param name="station">The captured station word.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="user">The plugin user (library access filtering).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The matched channel, or null when no channel matches.</returns>
    private async Task<BaseItem?> FindRadioChannelAsync(
        string station,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<BaseItem>? channels = await QueryRadioChannelsAsync(jellyfinUser, user, station, 1, cancellationToken).ConfigureAwait(false);
        if (channels is { Count: > 0 })
        {
            return channels[0];
        }

        var fuzzy = await SearchItemsFuzzyAsync(
            station, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.LiveTvChannel },
            cancellationToken, "PlayRadioStationFuzzyFallback", mediaTypes: new[] { MediaType.Audio }).ConfigureAwait(false);
        return fuzzy?.Item;
    }

    /// <summary>
    /// Tier (i) payoff: launch the matched live-TV radio channel. Mirrors
    /// PlayChannelIntentHandler's playback block (read-only for JF-474, consolidation
    /// candidate): live sources must launch via VideoApp.Launch with the resolver's
    /// PlaybackInfo URL (the static Audio stream endpoint 500s for a live source),
    /// ShouldEndSession stays null, and the device queue records the launch.
    /// </summary>
    /// <param name="channel">The matched LiveTvChannel item.</param>
    /// <param name="context">The Alexa context (device id for the queue record).</param>
    /// <param name="user">The plugin user (stream resolution).</param>
    /// <param name="session">The Jellyfin session (queue + now-playing).</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The VideoApp.Launch response, or the not-available Tell when the stream cannot be resolved.</returns>
    private async Task<SkillResponse> LaunchChannelAsync(
        BaseItem channel, Context context, Entities.User user, SessionInfo session, string locale, CancellationToken cancellationToken)
    {
        Logger.LogInformation("PlayRadio: station '{ChannelName}' matched live-TV radio channel {ChannelId}", channel.Name, channel.Id);

        session.NowPlayingQueue = new List<QueueItem> { new() { Id = channel.Id } };
        session.FullNowPlayingItem = channel;

        LiveTvStream? stream = await _streamResolver.ResolveAsync(channel, user, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale));
        }

        string? deviceId = context?.System?.Device?.DeviceID;
        if (!string.IsNullOrEmpty(deviceId))
        {
            Plugin.Instance?.DeviceQueueManager?.RecordLastPlayed(deviceId, channel.Id.ToString());
        }

        return new SkillResponse
        {
            Version = "1.0",
            Response = new ResponseBody
            {
                // VideoApp.Launch must NOT include shouldEndSession - Alexa rejects it.
                ShouldEndSession = null,
                OutputSpeech = BuildNowPlayingSpeech(channel.Name, locale, GetAnnounceNowPlaying(user)),
                Directives = new List<IDirective>
                {
                    new VideoAppLaunchDirective
                    {
                        VideoItem = new Directive.VideoItem
                        {
                            Source = stream.Url,
                            Metadata = new Directive.VideoItemMetadata
                            {
                                Title = channel.Name
                            }
                        }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Tier (ii) payoff: seed radio mode from the genre word's tracks. Same shape as
    /// the context-seeded path (shuffle, 20-track cap, RadioModeState on, the
    /// RadioStarted announcement), with the first genre track in the now-playing slot
    /// and PlaybackNearlyFinished continuing the genre radio via AutoPopulateRadioTracks.
    /// </summary>
    /// <param name="genre">The matched genre word (logging only).</param>
    /// <param name="genreTracks">The genre's tracks (from FindRadioTracksByGenreAsync).</param>
    /// <param name="session">The Jellyfin session (queue + now-playing).</param>
    /// <param name="user">The plugin user (stream URLs).</param>
    /// <param name="context">The Alexa context (device id for radio-mode state).</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The AudioPlayer.Play response with the radio announcement.</returns>
    private SkillResponse StartGenreRadio(
        string genre, IReadOnlyList<BaseItem> genreTracks, SessionInfo session, Entities.User user, Context context, string locale)
    {
        Logger.LogInformation("PlayRadio: station '{Genre}' resolved as a genre, seeding radio mode with {Count} tracks", genre, genreTracks.Count);

        List<BaseItem> shuffled = genreTracks.ToList();
        Shuffle(shuffled);
        if (shuffled.Count > 20)
        {
            shuffled.RemoveRange(20, shuffled.Count - 20);
        }

        BaseItem first = shuffled[0];
        session.FullNowPlayingItem = first;
        return StartRadioPlayback(first, shuffled.Select(t => new QueueItem { Id = t.Id }).ToList(), shuffled.Count, session, user, context, locale);
    }

    /// <summary>
    /// The shared radio-mode start (both the context-seeded path and the genre tier):
    /// queue assignment, RadioModeState on, the RadioStarted announcement over the
    /// now-playing announce, and the AudioPlayer.Play response for the first track.
    /// </summary>
    /// <param name="first">The track that starts playing now.</param>
    /// <param name="queue">The full radio queue (first track included).</param>
    /// <param name="announcedCount">The track count spoken in the announcement (the seed path counts only the similar tracks).</param>
    /// <param name="session">The Jellyfin session (queue + radio-mode state).</param>
    /// <param name="user">The plugin user (stream URLs + announce toggles).</param>
    /// <param name="context">The Alexa context (device id for radio-mode state).</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The AudioPlayer.Play response with the radio announcement.</returns>
    private SkillResponse StartRadioPlayback(
        BaseItem first, List<QueueItem> queue, int announcedCount, SessionInfo session, Entities.User user, Context context, string locale)
    {
        session.NowPlayingQueue = queue;
        RadioModeState.Enable(session.UserId, context.System.Device.DeviceID);

        string? nowPlayingSsml = GetSsml("NowPlayingSsml", locale, EscapeXml(first.Name));
        string radioMsg = ResponseStrings.Get("RadioStarted", locale, announcedCount.ToString(CultureInfo.InvariantCulture));

        var response = BuildAudioPlayerResponse(PlayBehavior.ReplaceAll, GetStreamUrl(first.Id.ToString(), user), first.Id.ToString(), first, user, context);
        if (GetAnnounceNowPlaying(user))
        {
            response.Response.OutputSpeech = nowPlayingSsml != null
                ? (IOutputSpeech)new SsmlOutputSpeech { Ssml = $"<speak>{nowPlayingSsml}. {EscapeXml(radioMsg)}</speak>" }
                : new PlainTextOutputSpeech($"{ResponseStrings.Get("NowPlaying", locale, first.Name)}. {radioMsg}");
        }

        return response;
    }

    /// <summary>
    /// The help answer (JF-474 UX b): the available list (channels + genre examples)
    /// followed by the same re-ask, as ANOTHER elicit so the follow-up still fills the
    /// station slot instead of escaping to general NLU.
    /// </summary>
    /// <param name="locale">The request locale.</param>
    /// <param name="channelNames">The suggested channel names (possibly empty).</param>
    /// <returns>The list + re-ask elicitation response.</returns>
    private SkillResponse BuildStationHelpResponse(string locale, List<string> channelNames)
    {
        string list = channelNames.Count > 0
            ? ResponseStrings.Get("RadioStationHelpList", locale, string.Join(", ", channelNames))
            : ResponseStrings.Get("RadioStationHelpNoChannels", locale);
        return BuildStationElicit($"{list} {ResponseStrings.Get("RadioAskStation", locale)}", locale, channelNames);
    }

    /// <summary>
    /// The one station elicit construction: every station ask (the short first ask, the
    /// help re-ask) targets the same dialog slot and carries the choices reprompt
    /// (JF-474 UX a: the first ask stays short, the reprompt names the real options).
    /// </summary>
    /// <param name="prompt">The spoken ask (short first ask, or list + re-ask after a help question).</param>
    /// <param name="locale">The request locale.</param>
    /// <param name="channelNames">The suggested channel names (possibly empty).</param>
    /// <returns>The elicitation response.</returns>
    private SkillResponse BuildStationElicit(string prompt, string locale, List<string> channelNames)
        => BuildElicitSlotResponse(
            IntentNames.PlayRadio,
            IntentNames.Slots.Station,
            new[] { IntentNames.Slots.Station },
            prompt,
            BuildStationChoicesReprompt(locale, channelNames));

    /// <summary>
    /// The choices reprompt: the channel names when the library has radio channels,
    /// the genre-word suggestion when it has none.
    /// </summary>
    /// <param name="locale">The request locale.</param>
    /// <param name="channelNames">The suggested channel names (possibly empty).</param>
    /// <returns>The reprompt text.</returns>
    private static string BuildStationChoicesReprompt(string locale, List<string> channelNames)
        => channelNames.Count > 0
            ? ResponseStrings.Get("RadioStationChoicesReprompt", locale, string.Join(", ", channelNames))
            : ResponseStrings.Get("RadioStationGenreReprompt", locale);
}
