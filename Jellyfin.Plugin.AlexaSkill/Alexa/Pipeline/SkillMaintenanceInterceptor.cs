using System;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;

/// <summary>
/// JF-846: maintenance-window transparency. While a maintenance window is open
/// (<see cref="SkillMaintenanceScope"/>: a catalog sync or a custom-model
/// rebuild in flight), prepends the SHORT localized SkillUpdatingPrefix to
/// responses that already carry speech, so users invoking the skill mid-window
/// hear the skill is updating instead of suspecting breakage. Responses WITHOUT
/// an OutputSpeech are never given one: VideoApp launches and AudioPlayer plays
/// are directive-only (the VideoApp rules forbid extra fields there and the
/// progressive announce owns that path), and the silent stop shape
/// (AudioPlayer.Stop, ShouldEndSession=true) is docs-mandated to stay silent.
/// Event requests (JF-507) are skipped defensively: their responses may not
/// carry outputSpeech at all. Registered LAST among response interceptors, so
/// it runs FIRST in the pipeline's REVERSE execution order, before
/// ResponseBodyLoggingInterceptor's snapshot: the logged body must show the
/// prefix the user actually heard (the triage correlation JF-846 asks for).
/// </summary>
public class SkillMaintenanceInterceptor : IResponseInterceptor
{
    /// <summary>The ResponseStrings key for the localized prefix.</summary>
    internal const string PrefixKey = "SkillUpdatingPrefix";

    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SkillMaintenanceInterceptor"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    public SkillMaintenanceInterceptor(ILogger<SkillMaintenanceInterceptor> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ProcessAsync(RequestContext context, CancellationToken cancellationToken)
    {
        if (!SkillMaintenanceScope.IsOpen
            || context.Response?.Response?.OutputSpeech is not IOutputSpeech speech
            || BaseHandler.IsEventRequest(context.SkillRequest))
        {
            return Task.CompletedTask;
        }

        string prefix = ResponseStrings.Get(PrefixKey, BaseHandler.GetLocalePublic(context.SkillRequest));
        switch (speech)
        {
            case PlainTextOutputSpeech plain:
                plain.Text = string.IsNullOrWhiteSpace(plain.Text) ? prefix : $"{prefix} {plain.Text}";
                break;
            case SsmlOutputSpeech ssml:
                ssml.Ssml = PrefixIntoSsml(ssml.Ssml, SpeechBuilder.EscapeXml(prefix));
                break;
        }

        _logger.LogDebug("Maintenance window open, prefixed response speech corr={CorrelationId}", context.CorrelationId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Inserts the (already XML-escaped) prefix at the start of an SSML speech
    /// string, inside the &lt;speak&gt; wrapper: prepending to the raw string
    /// would emit text before the root element, which is invalid SSML. A
    /// string with no leading &lt;speak&gt; tag (no builder in this repo emits
    /// one) is defensively wrapped rather than left malformed. The insert arm
    /// keys on the LEADING tag only: requiring a matching tail would push a
    /// "&lt;speak&gt;...&lt;/speak&gt;\n" (trailing whitespace) into the wrap
    /// arm and NEST a second speak root, which Amazon rejects.
    /// </summary>
    /// <param name="ssml">The existing SSML speech (may be null/empty).</param>
    /// <param name="escapedPrefix">The prefix, XML-escaped for SSML.</param>
    /// <returns>The prefixed SSML speech.</returns>
    internal static string PrefixIntoSsml(string? ssml, string escapedPrefix)
    {
        const string OpenTag = "<speak>";
        string value = ssml ?? string.Empty;
        if (value.StartsWith(OpenTag, StringComparison.OrdinalIgnoreCase))
        {
            return value.Insert(OpenTag.Length, $"{escapedPrefix} ");
        }

        return $"{OpenTag}{escapedPrefix} {value}</speak>";
    }
}
