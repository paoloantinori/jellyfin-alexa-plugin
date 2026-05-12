using Jellyfin.Plugin.AlexaSkill.Configuration;

namespace Jellyfin.Plugin.AlexaSkill.Alexa;

/// <summary>
/// Constants for progressive queue building configuration.
/// Methods read from live plugin config when available, falling back to defaults.
/// </summary>
internal static class ProgressiveQueueConstants
{
    public const int DefaultInitialFetchSize = 5;
    public const int DefaultContinuationBatchSize = 10;
    public const int DefaultPrefetchThreshold = 2;

    /// <summary>
    /// Number of items to fetch in the initial bulk-play handler response.
    /// </summary>
    public static int GetInitialFetchSize() =>
        Plugin.Instance?.Configuration?.InitialFetchSize ?? DefaultInitialFetchSize;

    /// <summary>
    /// Number of items to fetch per continuation batch.
    /// </summary>
    public static int GetContinuationBatchSize() =>
        Plugin.Instance?.Configuration?.ContinuationBatchSize ?? DefaultContinuationBatchSize;

    /// <summary>
    /// Number of items before queue end that triggers a continuation fetch.
    /// </summary>
    public static int GetPrefetchThreshold() =>
        Plugin.Instance?.Configuration?.PrefetchThreshold ?? DefaultPrefetchThreshold;
}
