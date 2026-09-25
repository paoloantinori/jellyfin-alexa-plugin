namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE entity-kind ladder (JF-629, the parallel-ladder doctrine): maps a resolved
/// BaseItem onto its content kind by TYPE PATTERN, never GetBaseItemKind() (that helper
/// parses the CLR type NAME into the enum and throws for derived types: test subclasses,
/// future entity shapes). AudioBook precedes Audio because a book IS an Audio subclass in
/// Jellyfin and the book kind must win wherever music-content gating reads the answer.
/// Returns null for shapes outside the four content kinds; callers own what null means
/// (ResumeIntent skips the candidate; MediaInfo projects it as the video-name-only shape).
/// </summary>
internal static class ItemKindLadder
{
    /// <summary>The content kind for the item, or null when it is none of the four.</summary>
    public static Jellyfin.Data.Enums.BaseItemKind? TryKind(MediaBrowser.Controller.Entities.BaseItem? item)
        => item switch
        {
            MediaBrowser.Controller.Entities.AudioBook => Jellyfin.Data.Enums.BaseItemKind.AudioBook,
            MediaBrowser.Controller.Entities.Audio.Audio => Jellyfin.Data.Enums.BaseItemKind.Audio,
            MediaBrowser.Controller.Entities.Movies.Movie => Jellyfin.Data.Enums.BaseItemKind.Movie,
            MediaBrowser.Controller.Entities.TV.Episode => Jellyfin.Data.Enums.BaseItemKind.Episode,
            _ => null,
        };
}
