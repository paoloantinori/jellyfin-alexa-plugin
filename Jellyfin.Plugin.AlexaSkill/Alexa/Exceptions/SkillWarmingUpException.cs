using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;

/// <summary>
/// Thrown when a required in-memory index is present but still loading (cold start);
/// the request pipeline translates it into the session-ending SkillWarmingUp Tell.
/// Enrichment-only callers may catch it and degrade gracefully.
/// </summary>
public sealed class SkillWarmingUpException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SkillWarmingUpException"/> class.
    /// </summary>
    public SkillWarmingUpException()
        : base("A required in-memory index is present but still loading (cold start)")
    {
    }
}
