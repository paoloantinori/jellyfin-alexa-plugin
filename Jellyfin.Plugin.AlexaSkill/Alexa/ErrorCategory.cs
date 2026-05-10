using Microsoft.Extensions.Logging;

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

/// <summary>
/// Maps <see cref="ErrorCategory"/> values to locale string keys and log levels.
/// </summary>
public static class ErrorCategoryInfo
{
    /// <summary>Gets the locale string key for the given category.</summary>
    public static string LocaleKey(ErrorCategory category) => category switch
    {
        ErrorCategory.TransientBackend => "ServerUnavailable",
        ErrorCategory.PermanentBackend => "ErrorPermanentBackend",
        ErrorCategory.UserError => "ErrorUserError",
        ErrorCategory.SkillError => "ErrorSkillError",
        ErrorCategory.Timeout => "ErrorTimeout",
        _ => "ErrorSkillError"
    };

    /// <summary>Gets the appropriate log level for the given category.</summary>
    public static LogLevel LogLevel(ErrorCategory category) => category switch
    {
        ErrorCategory.TransientBackend => Microsoft.Extensions.Logging.LogLevel.Warning,
        ErrorCategory.PermanentBackend => Microsoft.Extensions.Logging.LogLevel.Error,
        ErrorCategory.UserError => Microsoft.Extensions.Logging.LogLevel.Information,
        ErrorCategory.SkillError => Microsoft.Extensions.Logging.LogLevel.Critical,
        ErrorCategory.Timeout => Microsoft.Extensions.Logging.LogLevel.Warning,
        _ => Microsoft.Extensions.Logging.LogLevel.Error
    };
}
