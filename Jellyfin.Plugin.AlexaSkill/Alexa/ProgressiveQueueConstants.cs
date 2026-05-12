namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Constants for progressive queue building configuration.
/// </summary>
internal static class ProgressiveQueueConstants
{
    /// <summary>
    /// Number of items to fetch in the initial bulk-play handler response.
    /// Kept small for fast time-to-first-audio.
    /// </summary>
    public const int InitialFetchSize = 5;

    /// <summary>
    /// Number of items to fetch per continuation batch.
    /// Larger than initial fetch to reduce fetch frequency.
    /// </summary>
    public const int ContinuationBatchSize = 10;

    /// <summary>
    /// Number of items before queue end that triggers a continuation fetch.
    /// E.g., if threshold is 2, fetch more when current position is within 2 of the end.
    /// </summary>
    public const int PrefetchThreshold = 2;
}
