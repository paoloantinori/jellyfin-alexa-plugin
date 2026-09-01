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
    /// <param name="indexName">Which index is cold (e.g. "artist", "song n-gram"), for logs.</param>
    public SkillWarmingUpException(string indexName)
        : base($"{indexName} index is present but still loading (cold start)")
    {
        IndexName = indexName;
    }

    /// <summary>Which index is still loading (used by the pipeline log line).</summary>
    public string IndexName { get; }
}
