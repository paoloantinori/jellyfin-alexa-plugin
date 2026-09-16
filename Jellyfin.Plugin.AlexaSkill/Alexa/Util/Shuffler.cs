using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The Fisher-Yates shuffle family (JF-315 batch 10, census cluster H): the
/// in-place <see cref="Shuffle"/>, the copy-on-shuffle <see cref="ShuffleCopy"/>,
/// and the capped radio-queue variant <see cref="ShuffleAndCap"/>, moved verbatim
/// from BaseHandler. Static by design: pure list utilities with no instance state.
/// This file is the ONE home of the FORMER BaseHandler shuffle family; the private
/// twin CrossMediaFallback carried since batch 7 was deleted when this home landed
/// (the consolidation tracked in JF-572). DeviceQueueManager keeps its own two
/// Fisher-Yates copies deliberately: FisherYates(List&lt;string&gt;, Random) is
/// rng-injectable for the deterministic shuffle tests (seeded through
/// SetShuffledQueue's rng parameter), and ShuffleRemaining's inline tail shuffle
/// deliberately pins Random.Shared; neither was ever a BaseHandler member, so both
/// are outside this family.
/// </summary>
public static class Shuffler
{
    /// <summary>
    /// Shuffle a list in place using Fisher-Yates algorithm.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="list">The list to shuffle.</param>
    public static void Shuffle<T>(IList<T> list)
    {
        int n = list.Count;
        for (int i = n - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Create a shuffled copy of a read-only list.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="source">The list to shuffle.</param>
    /// <returns>A shuffled copy of the list.</returns>
    public static List<T> ShuffleCopy<T>(IReadOnlyList<T> source)
    {
        var copy = source.ToList();
        Shuffle(copy);
        return copy;
    }

    /// <summary>
    /// Create a shuffled copy of a read-only list truncated to at most
    /// <paramref name="cap"/> entries. Shuffle happens before the cap, so the kept
    /// subset is random (the radio queues: a 20-track radio start, a 15-track
    /// continuation). Caps differ per caller, so the cap is a parameter.
    /// </summary>
    /// <typeparam name="T">The element type of the list.</typeparam>
    /// <param name="source">The list to shuffle and cap.</param>
    /// <param name="cap">The maximum number of entries to keep.</param>
    /// <returns>A shuffled list of at most <paramref name="cap"/> entries.</returns>
    public static List<T> ShuffleAndCap<T>(IReadOnlyList<T> source, int cap)
    {
        List<T> copy = ShuffleCopy(source);
        if (copy.Count > cap)
        {
            copy.RemoveRange(cap, copy.Count - cap);
        }

        return copy;
    }
}
