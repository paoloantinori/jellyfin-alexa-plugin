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
    /// Song ends, a brief announcement plays, then genre-related music starts automatically.
    /// </summary>
    AutoPlay = 1,

    /// <summary>
    /// Song ends and the user is asked whether they want to hear more music.
    /// </summary>
    Ask = 2
}
