using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Maps <see cref="ErrorCategory"/> values to the log levels the structured
/// error logging uses.
/// </summary>
public static class ErrorCategoryInfo
{
    /// <summary>
    /// Gets the appropriate log level for the given category.
    /// </summary>
    /// <param name="category">The error category.</param>
    /// <returns>The log level.</returns>
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
