using System.Collections.Generic;
using Alexa.NET.Request;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Attaches APL render directives to skill responses (JF-315 batch 3, cluster I):
/// the visuals/device gate, the directive attach, and the interactive-APL session
/// contract (a carousel on a session-ending response flips the session open and
/// injects the CarouselReprompt so SendEvent taps are not dropped). Static by
/// design: no handler instance state, the logger arrives as a parameter.
/// Directive template building itself lives in <see cref="Apl.AplHelper"/>.
/// Members moved verbatim from BaseHandler; call sites migrated mechanically.
/// TryAttachNowPlayingDirective stayed in BaseHandler (reads config via
/// GetImageUrl; see its doc comment there for the full rationale).
/// </summary>
internal static class AplDirectiveAttacher
{
    /// <summary>
    /// Conditionally attach an APL list directive to a response if the device supports APL.
    /// </summary>
    /// <param name="logger">The handler logger for gate/build diagnostics.</param>
    /// <param name="response">The skill response to attach the directive to.</param>
    /// <param name="context">The Alexa context for APL device detection.</param>
    /// <param name="title">The title for the APL list.</param>
    /// <param name="items">The items to display in the list.</param>
    /// <param name="token">A token identifying the APL directive.</param>
    /// <param name="action">The action for the APL list items.</param>
    internal static void TryAttachListDirective(
        ILogger logger,
        SkillResponse response,
        Context? context,
        string title,
        List<Apl.ListDisplayItem> items,
        string token,
        string action = "selectItem",
        bool hasMore = false)
    {
        if (!Apl.AplHelper.VisualsEnabled)
        {
            logger.LogDebug("APL list skipped for '{Token}': visuals disabled in config", token);
            return;
        }

        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            var keys = context?.System?.Device?.SupportedInterfaces?.Keys;
            logger.LogDebug("APL list skipped for '{Token}': device does not support APL. Interfaces: {Interfaces}", token, keys != null ? string.Join(", ", keys) : "null");
            return;
        }

        var directive = Apl.AplHelper.BuildListDirective(title, items, token, action, context, hasMore);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);
        }
        else
        {
            logger.LogWarning("APL BuildListDirective returned null for '{Token}' with {Count} items", token, items.Count);
        }
    }

    /// <summary>
    /// Attach an APL image carousel directive to a response when the device supports APL.
    /// No-op on non-APL devices or when visuals are disabled.
    /// </summary>
    internal static void TryAttachCarouselDirective(
        ILogger logger,
        SkillResponse response,
        Context? context,
        string title,
        List<Apl.ListDisplayItem> items,
        string token = "carousel",
        string locale = "en-US")
    {
        if (!Apl.AplHelper.VisualsEnabled)
        {
            logger.LogDebug("APL carousel skipped for '{Token}': visuals disabled in config", token);
            return;
        }

        if (!Apl.AplHelper.DeviceSupportsApl(context))
        {
            var keys = context?.System?.Device?.SupportedInterfaces?.Keys;
            logger.LogDebug("APL carousel skipped for '{Token}': device does not support APL. Interfaces: {Interfaces}", token, keys != null ? string.Join(", ", keys) : "null");
            return;
        }

        var directive = Apl.AplHelper.BuildCarouselDirective(title, items, token, context);
        if (directive != null)
        {
            response.Response.Directives.Add(directive);

            // Interactive APL directives require an open session to receive SendEvent callbacks.
            if (response.Response.ShouldEndSession == true)
            {
                response.Response.ShouldEndSession = false;
                string repromptText = ResponseStrings.Get("CarouselReprompt", locale);
                if (response.Response.Reprompt == null && !string.IsNullOrEmpty(repromptText))
                {
                    response.Response.Reprompt = new Reprompt(repromptText);
                }
            }
        }
        else
        {
            logger.LogWarning("APL BuildCarouselDirective returned null for '{Token}' with {Count} items", token, items.Count);
        }
    }
}
