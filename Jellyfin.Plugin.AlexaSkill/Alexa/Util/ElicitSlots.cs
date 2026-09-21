using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE C#-side declaration of every elicited intent's FULL dialog slot set
/// (JF-613). Every elicit call site passes <see cref="For"/> as its
/// allSlotNames (Amazon requires the complete set in updatedIntent), and
/// scripts/validate_interaction_models.py Phase 8 compares THIS table against
/// every locale's dialog.intents declaration; declaration against
/// declaration, no call-shape parsing. A call site that builds its own array
/// is flagged by the validator's canonical-shape check, so a new C# idiom
/// (collection expressions, FrozenSet, Concat) can never silently skip parity.
/// Keep this table and the 17 templates' dialog sections in lockstep: the
/// validator fails on any drift in either direction.
/// </summary>
public static class ElicitSlots
{
    private static readonly Dictionary<string, string[]> Table = new(StringComparer.Ordinal)
    {
        [IntentNames.AddSongToPlaylist] = new[] { IntentNames.Slots.SongQuery, IntentNames.Slots.PlaylistTarget },
        [IntentNames.AddToQueue] = new[] { IntentNames.Slots.Song, IntentNames.Slots.Musician },
        [IntentNames.BrowseLibrary] = new[] { "browse_category", "filter" },
        [IntentNames.FindSongIntent] = new[] { IntentNames.Slots.TitleKeywords },
        [IntentNames.FindSongByArtistIntent] = new[] { IntentNames.Slots.Musician },
        [IntentNames.PlayAlbum] = new[] { IntentNames.Slots.Album, IntentNames.Slots.Musician },
        [IntentNames.PlayArtistSongs] = new[] { IntentNames.Slots.Musician },
        [IntentNames.PlayEpisode] = new[] { "series_name", "season_number", "episode_number" },
        [IntentNames.PlayMoodMusic] = new[] { "mood" },
        [IntentNames.PlayNext] = new[] { IntentNames.Slots.Song, IntentNames.Slots.Musician },
        [IntentNames.PlayNextEpisode] = new[] { "series_name", "episode_position" },
        [IntentNames.PlayPlaylist] = new[] { IntentNames.Slots.Playlist },
        [IntentNames.PlayPodcast] = new[] { "podcast_name" },
        [IntentNames.PlayRadio] = new[] { IntentNames.Slots.Station },
        [IntentNames.PlaySong] = new[] { IntentNames.Slots.Song, IntentNames.Slots.Musician },
        [IntentNames.QueryArtistLibrary] = new[] { IntentNames.Slots.Musician, "query_type" },
        [IntentNames.SetReminder] = new[] { "duration_minutes", "reminder_time" },
        [IntentNames.ShufflePlay] = new[] { IntentNames.Slots.Playlist },
        [IntentNames.SleepTimer] = new[] { "duration_minutes" },
    };

    /// <summary>
    /// Gets the full dialog slot set for an elicited intent. Throws for an
    /// intent missing from the table: a new elicit flow must add its entry
    /// here AND to all 17 templates in the same change (the validator enforces
    /// the lockstep).
    /// </summary>
    /// <param name="intentName">The elicited intent's name constant.</param>
    /// <returns>The complete slot-name set for the updatedIntent.</returns>
    public static string[] For(string intentName)
        => Table.TryGetValue(intentName, out string[]? slots)
            ? (string[])slots.Clone()
            : throw new KeyNotFoundException($"ElicitSlots: intent '{intentName}' elicits but has no slot-set entry; add it here and to all 17 templates' dialog sections in the same change.");
}
