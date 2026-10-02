#nullable enable

using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;

/// <summary>
/// A deterministic catalog payload-build invariant was violated while constructing
/// a catalog entry (today: the JF-689 structural guard
/// <see cref="CatalogValueFactory.AssertArtistEnrichment"/>). Derives from
/// <see cref="InvalidOperationException"/> because that is the historically pinned
/// throw contract. The catalog sync isolates this exception per TYPE (JF-695): the
/// failing type freezes for the run (no version minted, its catalog id not
/// forwarded to the model injection, so the live model keeps the type's last-good
/// pinned catalog reference) while the sibling types and the locale's model
/// injection continue. Every other failure keeps the whole-leg handling (the 401
/// refresh-retry, transient-fetch exhaustion, timeouts), so nothing but this
/// deterministic-invariant family is contained per type.
/// </summary>
internal sealed class CatalogPayloadInvariantException : InvalidOperationException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CatalogPayloadInvariantException"/> class.
    /// </summary>
    /// <param name="message">The invariant-violation description.</param>
    public CatalogPayloadInvariantException(string message)
        : base(message)
    {
    }
}
