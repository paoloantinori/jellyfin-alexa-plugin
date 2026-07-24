namespace Jellyfin.Plugin.AlexaSkill.Configuration;

/// <summary>
/// Controls the cross-media artist suggestion: when a PlaySong/PlayAlbum request finds no
/// exact match but the artist fallback finds a plausible artist (score in the [normal, strict)
/// band), what happens. Scores at or above the strict cross-media threshold always auto-play
/// regardless of this setting; scores below the normal threshold always not-found.
/// </summary>
public enum CrossMediaArtistSuggestion
{
    /// <summary>
    /// Disabled: a sub-threshold cross-media artist match reports a clean not-found, as
    /// before this feature. No offer, no auto-serve.
    /// </summary>
    Off = 0,

    /// <summary>
    /// Offer the artist for confirmation: speak a prompt and keep the session open; the
    /// user says "yes" to play the artist, or "no" to get the clean not-found. Nothing plays
    /// without confirmation, so there is no wrong-substitution risk. (Default.)
    /// </summary>
    Confirm = 1,

    /// <summary>
    /// Auto-serve: play the artist directly with a FoundArtistInstead announcement, no
    /// confirmation. Equivalent to lowering the cross-media gate to the normal threshold,
    /// but only for users who opted in.
    /// </summary>
    AutoServe = 2
}
