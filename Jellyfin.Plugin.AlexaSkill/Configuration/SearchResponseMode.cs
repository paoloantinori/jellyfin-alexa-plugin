namespace Jellyfin.Plugin.AlexaSkill.Configuration;

/// <summary>
/// Controls the trade-off between search speed and recall quality.
/// </summary>
public enum SearchResponseMode
{
    /// <summary>
    /// Full 4-tier fallback chain with disambiguation. Best recall, slower response.
    /// </summary>
    Thorough = 0,

    /// <summary>
    /// Single query or reduced tiers with auto-play. Fastest response, may miss obscure matches.
    /// </summary>
    Fast = 1
}
