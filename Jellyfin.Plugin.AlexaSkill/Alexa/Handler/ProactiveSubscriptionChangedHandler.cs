using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Handles the <c>ProactiveSubscriptionChanged</c> callback from Alexa
/// when a user subscribes or unsubscribes to proactive notifications via the Alexa app.
/// </summary>
public class ProactiveSubscriptionChangedHandler : BaseHandler
{
    private const string ProactiveSubscriptionChangedType = "AlexaSkillEvent.ProactiveSubscriptionChanged";

    private static readonly PropertyInfo? _bodyProp = typeof(Request).GetProperty("Body");
    private static readonly PropertyInfo? _subscriptionsProp = _bodyProp?.PropertyType?.GetProperty("Subscriptions");
    private static readonly Type? _elementType = _subscriptionsProp?.PropertyType?.GetElementType()
        ?? _subscriptionsProp?.PropertyType?.GetProperty("Item")?.PropertyType;
    private static readonly PropertyInfo? _eventNameProp = _elementType?.GetProperty("EventName");

    /// <summary>
    /// Initializes a new instance of the <see cref="ProactiveSubscriptionChangedHandler"/> class.
    /// </summary>
    /// <param name="sessionManager">Session manager instance.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="loggerFactory">Logger factory instance.</param>
    public ProactiveSubscriptionChangedHandler(
        ISessionManager sessionManager,
        PluginConfiguration config,
        ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    /// <inheritdoc />
    public override bool CanHandle(Request request)
    {
        return request.Type == ProactiveSubscriptionChangedType;
    }

    /// <inheritdoc />
    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
    {
        string? systemUserId = context?.System?.User?.UserId;
        if (string.IsNullOrEmpty(systemUserId))
        {
            Logger.LogWarning("ProactiveSubscriptionChanged received without system user ID");
            return Task.FromResult(ResponseBuilder.Empty());
        }

        var config = Plugin.Instance?.Configuration;
        if (config == null)
        {
            return Task.FromResult(ResponseBuilder.Empty());
        }

        Entities.User? matchedUser = null;
        foreach (var u in config.Users)
        {
            if (!string.IsNullOrEmpty(u.AlexaPersonId) && string.Equals(u.AlexaPersonId, systemUserId, StringComparison.Ordinal))
            {
                matchedUser = u;
                break;
            }
        }

        if (matchedUser == null)
        {
            Logger.LogDebug("ProactiveSubscriptionChanged: no matching user for Alexa ID {AlexaId}", systemUserId);
            return Task.FromResult(ResponseBuilder.Empty());
        }

        bool subscribed = IsSubscribedToMediaContent(request);

        matchedUser.ProactiveEventsEnabled = subscribed;
        Plugin.Instance!.SaveConfiguration();

        Logger.LogInformation("User {Username} proactive events: {Status}", matchedUser.Username, subscribed ? "enabled" : "disabled");

        return Task.FromResult(ResponseBuilder.Empty());
    }

    private bool IsSubscribedToMediaContent(Request request)
    {
        try
        {
            if (_bodyProp?.GetValue(request) is not { } body)
            {
                return false;
            }

            if (_subscriptionsProp?.GetValue(body) is not System.Collections.IEnumerable subs)
            {
                return false;
            }

            foreach (var sub in subs)
            {
                if (_eventNameProp != null &&
                    string.Equals(_eventNameProp.GetValue(sub)?.ToString(), "AMAZON.MediaContent.Available", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "Failed to parse ProactiveSubscriptionChanged body");
        }

        return false;
    }
}
