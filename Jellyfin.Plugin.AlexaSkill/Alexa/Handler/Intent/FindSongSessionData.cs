#nullable enable
using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;

/// <summary>
/// Session data persisted across FindSongIntent turns via Alexa session attributes.
/// Serialized as JSON using JsonConvert.
/// </summary>
public class FindSongSessionData
{
    public FindSongState State { get; set; }

    public Guid? ArtistId { get; set; }

    public string? ArtistName { get; set; }

    public string? Keywords { get; set; }

    public List<FindSongCandidate>? Candidates { get; set; }
}

/// <summary>
/// A scored song candidate presented during disambiguation.
/// </summary>
public record FindSongCandidate(Guid ItemId, string Name, string? ArtistName, double Score);

/// <summary>
/// State machine states for the FindSongIntent multi-turn dialogue.
/// </summary>
public enum FindSongState
{
    /// <summary>Waiting for the user to provide an artist name.</summary>
    AwaitingArtist,

    /// <summary>Waiting for the user to provide song title keywords.</summary>
    AwaitingKeywords,

    /// <summary>Presenting candidates and waiting for the user to pick one.</summary>
    Disambiguating
}
