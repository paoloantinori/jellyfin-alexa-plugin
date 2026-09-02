using System.Collections.Generic;
using System.Linq;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// Single source of the skill request routing selection semantics: the FindSong
/// multi-turn force-route, then the first handler in registration order whose
/// CanHandle claims the request. The skill controller and the routing test harness
/// both dispatch through Select, so a controller-side routing edit cannot drift
/// past the test suite (JF-452).
/// </summary>
internal static class HandlerSelector
{
    /// <summary>
    /// Selects the handler to execute for an incoming skill request.
    /// </summary>
    /// <param name="handlers">The registered handlers in dispatch registration order.</param>
    /// <param name="request">The incoming skill request.</param>
    /// <returns>The selection, with a null handler when nothing claims the request.</returns>
#pragma warning disable CA1851 // Two enumeration passes by design: the force-route lookup must find FindSongIntentHandler even when an earlier handler's CanHandle would match, and buffering would force-resolve every handler singleton per request
    internal static HandlerSelection Select(IEnumerable<BaseHandler> handlers, SkillRequest request)
    {
        // The controller answers ResponseBuilder.Empty() for a null Request before
        // selection; callers that reach selection anyway get "no selection".
        if (request.Request == null)
        {
            return default;
        }

        // Session-aware routing: if a FindSong multi-turn dialog is active,
        // always route to FindSongIntentHandler regardless of what intent Alexa's
        // NLU assigned. Short replies like "family" often get misrouted by NLU
        // (e.g. to ShowMoreIntent or BrowseLibraryIntent) when the user is in
        // a multi-turn FindSong conversation. IntentRequests only: a
        // SessionEndedRequest arriving with FindSong attributes must fall through
        // to SessionEndedRequestHandler (FindSongIntentHandler casts to
        // IntentRequest and crashed with InvalidCastException, live 2026-08-21).
        if (request.Request is IntentRequest
            && request.Session?.Attributes != null
            && request.Session.Attributes.ContainsKey(FindSongIntentHandler.SessionDataKey))
        {
            FindSongIntentHandler? findSongHandler = handlers.OfType<FindSongIntentHandler>().FirstOrDefault();
            if (findSongHandler != null)
            {
                return new HandlerSelection(findSongHandler, ForceRouted: true);
            }
        }

        // First CanHandle match in registration order wins.
        foreach (BaseHandler handler in handlers)
        {
            if (handler.CanHandle(request.Request))
            {
                return new HandlerSelection(handler, ForceRouted: false);
            }
        }

        return default;
    }
#pragma warning restore CA1851
}

/// <summary>
/// Result of a routing selection: the handler to execute (null when no handler claims
/// the request) and whether selection came from the FindSong session force-route.
/// </summary>
/// <param name="Handler">The selected handler, or null when no handler claims the request.</param>
/// <param name="ForceRouted">True when the selection was via the FindSongSessionData force-route.</param>
internal readonly record struct HandlerSelection(BaseHandler? Handler, bool ForceRouted);
