using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;

/// <summary>
/// Base of every refusal the request pipeline translates ONCE into a session-safe
/// response (JF-708). The pipeline's single catch decides the policy for all
/// subtypes: log at the type's <see cref="Severity"/> (the refusal's own message
/// carries the specifics), set SkipColdLibraryWork so no library query rides a
/// refusal answer, and respond through
/// <see cref="Handler.BaseHandler"/>.DegradeForEventRequest with the localized
/// <see cref="ResponseKey"/> Tell in the request's locale, or the speechless
/// keep-alive when the request is an Alexa EVENT request (Amazon rejects
/// outputSpeech on event responses with INVALID_RESPONSE, the JF-507 lesson).
/// A new refusal is therefore a new sealed subtype carrying its key and severity,
/// never a new catch block; event-awareness can no longer be re-decided per
/// refusal. Concrete members: <see cref="SkillWarmingUpException"/> (Information,
/// a routine cold-start state) and <see cref="StreamTokenNotConfiguredException"/>
/// (Error, admin action required). Warming inherited event-awareness in JF-708:
/// unreachable on event requests today (the warming gates and index choke points
/// sit on intent paths), but the next event-path index call would otherwise
/// reproduce exactly the latent invalid Tell the token refusal already guards
/// against. DELIBERATE CLASSIFICATION (the JF-699 gate-marker note, restated where
/// the one decision now lives): the event test
/// (<see cref="Handler.BaseHandler.IsEventRequest"/>) does NOT classify
/// PlaybackController CommandIssued taps, so a refused tap-routed launch answers a
/// speech Tell on that request class, the same response shape the happy path
/// already produces there (no recorded incident); revisit only if Amazon documents
/// the no-outputSpeech rule for it.
/// </summary>
public abstract class SkillRefusalException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SkillRefusalException"/> class.
    /// </summary>
    /// <param name="message">The refusal's log narrative (names the condition and its specifics).</param>
    /// <param name="responseKey">The <see cref="Locale.ResponseStrings"/> key for the non-event Tell.</param>
    /// <param name="severity">The pipeline log level for the refusal.</param>
    protected SkillRefusalException(string message, string responseKey, LogLevel severity)
        : base(message)
    {
        ResponseKey = responseKey;
        Severity = severity;
    }

    /// <summary>
    /// The <see cref="Locale.ResponseStrings"/> key the pipeline speaks when the
    /// refusal answers a Tell (event requests get the keep-alive instead).
    /// </summary>
    public string ResponseKey { get; }

    /// <summary>
    /// The severity the pipeline logs the refusal at: warming is Information (a
    /// routine post-restart state that self-heals), a configuration error such as
    /// the empty stream-token secret is Error (an admin must act).
    /// </summary>
    public LogLevel Severity { get; }
}
