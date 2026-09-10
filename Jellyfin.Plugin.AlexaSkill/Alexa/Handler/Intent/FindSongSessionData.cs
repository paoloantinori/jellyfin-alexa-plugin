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

    /// <summary>
    /// True when an artist RESOLVED and scopes this search. JF-533 named this invariant
    /// because <see cref="ArtistName"/> holds the raw musician input even when resolution
    /// failed (the stored name and the resolved scope are different things): scope/gate
    /// decisions must read this property, announcements read the name. A branch that
    /// dereferences <see cref="ArtistId"/>.Value should keep the direct HasValue check
    /// instead (nullable flow analysis cannot see through this property). Ignored on the
    /// session-attribute JSON (derived; a get-only property would otherwise ride it).
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public bool HasResolvedArtist => ArtistId.HasValue;

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
