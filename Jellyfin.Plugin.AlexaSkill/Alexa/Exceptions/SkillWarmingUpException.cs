using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;

/// <summary>
/// Thrown when a required in-memory index is present but still loading (cold start);
/// the request pipeline's single <see cref="SkillRefusalException"/> catch translates
/// it (JF-708) into the session-ending SkillWarmingUp Tell, or the speechless
/// keep-alive on Alexa event requests. Enrichment-only callers may catch it and
/// degrade gracefully.
/// </summary>
public sealed class SkillWarmingUpException : SkillRefusalException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SkillWarmingUpException"/> class.
    /// </summary>
    /// <param name="indexName">Which index is cold (e.g. "artist", "song n-gram"); it names the index in the refusal message the pipeline logs and the tests assert on.</param>
    public SkillWarmingUpException(string indexName)
        : base($"{indexName} index is present but still loading (cold start)", "SkillWarmingUp", LogLevel.Information)
    {
    }
}
