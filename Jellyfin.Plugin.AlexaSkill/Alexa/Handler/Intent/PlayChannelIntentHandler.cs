using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
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
/// and starts playback via the Alexa AudioPlayer interface.
/// </summary>
public class PlayChannelIntentHandler : BaseHandler
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    public PlayChannelIntentHandler(
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
        return intentRequest != null && string.Equals(intentRequest.Intent.Name, IntentNames.PlayChannel, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public override async Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string locale = GetLocale(request);
        IntentRequest intentRequest = (IntentRequest)request;
        string? channelQuery = intentRequest.Intent.Slots?.TryGetValue("channel", out var slot) == true ? slot.Value : null;

        if (string.IsNullOrWhiteSpace(channelQuery))
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("DidNotCatchChannelName", locale));
        }

        Jellyfin.Database.Implementations.Entities.User jellyfinUser = _userManager.GetUserById(session.UserId);

        IReadOnlyList<BaseItem> channels = _libraryManager.GetItemList(new InternalItemsQuery()
        {
            User = jellyfinUser,
            Recursive = true,
            SearchTerm = channelQuery,
            IncludeItemTypes = new[] { BaseItemKind.LiveTvChannel },
            DtoOptions = new DtoOptions(true)
        });

        if (channels.Count == 0)
        {
            return ResponseBuilder.Tell(ResponseStrings.Get("NotFoundChannel", locale, channelQuery));
        }

        BaseItem channel = channels[0];
        string itemId = channel.Id.ToString();

        List<QueueItem> queueItems = new List<QueueItem>
        {
            new QueueItem { Id = channel.Id }
        };
        session.NowPlayingQueue = queueItems;
        session.FullNowPlayingItem = channel;

        return ResponseBuilder.AudioPlayerPlay(PlayBehavior.ReplaceAll, GetStreamUrl(itemId, user), itemId);
    }
}
