namespace Jellyfin.Plugin.AlexaSkill.Configuration;

/// <summary>
/// Controls what happens when playback ends and the queue is exhausted.
/// </summary>
public enum PostPlayBehavior
{
    /// <summary>
    /// Song ends and playback stops. No further action.
    /// </summary>
    Stop = 0,

    /// <summary>
    /// Song ends and similar music starts automatically via gapless transition.
    /// </summary>
    AutoPlay = 1
}
