namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Circuit breaker states.
/// </summary>
public enum CircuitStatus
{
    /// <summary>
    /// Normal operation -- requests flow through.
    /// </summary>
    Closed,

    /// <summary>
    /// Backend confirmed down -- requests are short-circuited.
    /// </summary>
    Open,

    /// <summary>
    /// Testing recovery -- one probe request is allowed.
    /// </summary>
    HalfOpen
}
