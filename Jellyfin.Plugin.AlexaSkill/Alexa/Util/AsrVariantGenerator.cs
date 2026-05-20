using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Generates ASR (Automatic Speech Recognition) variant strings by joining
/// adjacent word pairs, simulating how Alexa may concatenate spoken words.
/// </summary>
public static class AsrVariantGenerator
{
    /// <summary>
    /// Generates variants of the input query by joining each adjacent pair of words,
    /// plus a fully-collapsed variant with all spaces removed.
    /// </summary>
    /// <param name="query">The input query string.</param>
    /// <returns>A deduplicated list of ASR variant strings. Empty if input is null, empty, whitespace, or single-word.</returns>
    public static IReadOnlyList<string> GenerateAsrVariants(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<string>();
        }

        var words = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (words.Length <= 1)
        {
            return Array.Empty<string>();
        }

        var variants = new List<string>();
        var seen = new HashSet<string>();

        for (int i = 0; i < words.Length - 1; i++)
        {
            var variantWords = new string[words.Length];
            Array.Copy(words, variantWords, words.Length);
            variantWords[i] = words[i] + words[i + 1];
            variantWords[i + 1] = string.Empty;

            var variant = string.Join(" ", variantWords, 0, words.Length);
            // Re-split and re-join to collapse double spaces from the empty slot
            variant = string.Join(" ", variant.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

            if (seen.Add(variant))
            {
                variants.Add(variant);
            }
        }

        var collapsed = string.Concat(words);
        if (seen.Add(collapsed))
        {
            variants.Add(collapsed);
        }

        return variants;
    }
}
