namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Categorizes errors for structured logging and user-facing responses.
/// </summary>
public enum ErrorCategory
{
    /// <summary>Transient backend issue (timeout, 503, network error).</summary>
    TransientBackend,

    /// <summary>Permanent backend issue (404, auth failure).</summary>
    PermanentBackend,

    /// <summary>User input error (empty slots, invalid input).</summary>
    UserError,

    /// <summary>Unhandled skill exception.</summary>
    SkillError,

    /// <summary>Alexa 8-second timeout exceeded.</summary>
    Timeout
}
