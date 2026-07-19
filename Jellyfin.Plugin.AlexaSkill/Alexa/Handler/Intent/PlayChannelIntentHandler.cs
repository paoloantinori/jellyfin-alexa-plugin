using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handler for PlayChannelIntent — searches for live TV channels by name
/// and launches playback via the Alexa VideoApp interface (VideoApp.Launch),
/// like movies and episodes, so channels actually play on Echo Show.
/// </summary>
public class PlayChannelIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILiveTvStreamResolver _streamResolver;

    public PlayChannelIntentHandler(
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

    /// <inheritdoc/>
    public override bool CanHandle(Request request)
    {
        IntentRequest? intentRequest = request as IntentRequest;
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayChannel, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        if (IfFeatureDisabled(c => c.LiveTvEnabled, request) is { } disabled)
        {
            return disabled;
        }

        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? channelQuery = intentRequest.Intent.Slots?.TryGetValue("channel", out var slot) == true ? slot.Value : null;

        if (string.IsNullOrWhiteSpace(channelQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchChannelName", locale));
        }

        RunFireAndForget(SendProgressiveResponse(context, request, ResponseStrings.Get("SearchingMedia", locale)));

        var (jellyfinUser, userError) = ResolveJellyfinUser(_userManager, session.UserId, locale);
        if (userError != null)
        {
            return userError;
        }

        var channelSearchQuery = new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = channelQuery,
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel },
            DtoOptions = new DtoOptions(true)
        };
        ApplyLibraryFilter(channelSearchQuery, user, _libraryManager);

        IReadOnlyList<BaseItem> channels = await RetryAsync(
            () => _libraryManager.GetItemList(channelSearchQuery),
            "GetChannels",
            cancellationToken).ConfigureAwait(false);

        if (channels.Count == 0)
        {
            var fuzzy = await SearchItemsFuzzyAsync(channelQuery, jellyfinUser, user, _libraryManager, new[] { BaseItemKind.LiveTvChannel }, cancellationToken, "PlayChannelFuzzyFallback").ConfigureAwait(false);
            if (fuzzy != null)
            {
                channels = new List<BaseItem> { fuzzy.Value.Item };
            }
            else
            {
                return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundChannel", locale, channelQuery));
            }
        }

        BaseItem channel = channels[0];

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = channel.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = channel;

        // Live TV must launch via VideoApp.Launch (like movies/episodes) so it plays on
        // Echo Show. The AudioPlayer static stream URL used previously 500s for a live
        // source. The resolver picks the correct URL via Jellyfin's PlaybackInfo.
        LiveTvStream? stream = await _streamResolver.ResolveAsync(channel, user, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("MediaTypeNotAvailable", locale));
        }

        // Record the last-played channel for this device (the resume / continue-watching signal).
        // Mirrors the chokepoint in BaseHandler.BuildAudioPlayerResponse; needed here because the
        // direct-remote stream URL has no /Videos/ segment for LastPlayedResponseInterceptor to parse.
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
                // VideoApp.Launch must NOT include shouldEndSession — Alexa rejects it.
                ShouldEndSession = null,
                // Announce the channel on launch for consistency with other video launches (JF-349).
                OutputSpeech = BuildOutputSpeech("NowPlayingSsml", "NowPlaying", locale, channel.Name),
                Directives = new List<IDirective>
                {
                    new VideoAppLaunchDirective
                    {
                        VideoItem = new VideoItem
                        {
                            Source = stream.Url,
                            Metadata = new VideoItemMetadata
                            {
                                Title = channel.Name
                            }
                        }
                    }
                }
            }
        };
    }
}
