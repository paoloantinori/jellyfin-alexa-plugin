using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Handler;

/// <summary>
/// The JF-420.3 fair-comparison core shared by every containment-vs-alternatives
/// arbitration (one home since JF-894, extracted verbatim from
/// PlayArtistSongsIntentHandler where the JF-420 gate introduced it): the
/// symmetric fair score, the genuinely-plausible bar, the redundant-shorter-form
/// skip, and the auto-select margin. Pure functions and constants only; the
/// DECISIONS that consume them stay at their decision points (the
/// fuzzy-recall-vs-judgment-layers rule, JF-408): the JF-420 gate on
/// PlayArtistSongsIntentHandler, the JF-885 rank-1 dominance leg in
/// MultiValueErDisambiguation, and the JF-894 containment-tie arbitration in
/// CrossMediaFallback. A new consumer reuses these pieces; it must not fork them
/// (the JF-420.3 lesson: a private copy of the fair score drifted from the
/// matcher's semantics by 30+ points and manufactured phantom margins).
/// </summary>
internal static class ArtistFairComparison
{
    /// <summary>
    /// JF-420: minimum fair-score margin for auto-selecting the full-name alternative
    /// over a containment match. Below this margin, the caller disambiguates instead.
    /// Derived from the P!nk floyd case: penalized containment 36 vs genuine 90 = 54
    /// margin (auto-select); a tribute band at ~65 vs penalized 37 = 28 margin (also
    /// auto-select); a genuinely close case would be under 20 (disambiguate).
    /// JF-885: internal because the ER gate
    /// (<see cref="MultiValueErDisambiguation"/>) references this SAME bar for its
    /// rank-1 dominance leg instead of re-inventing the constant; the two decisions
    /// share one calibration. JF-894: the CrossMediaFallback containment-tie
    /// arbitration references it for the same reason (one calibration, three
    /// decisions).
    /// </summary>
    internal const double ContainmentVsFullNameMargin = 20.0;

    /// <summary>
    /// JF-420/JF-420.3: the bar for an alternative to be "genuinely plausible",
    /// checked on the RAW score (worth comparing at all: below this the match just
    /// auto-plays) and on the FAIR score (eligible to WIN the comparison: an
    /// exemption-only partial-word hit cannot clear it). One bar, two checkpoints.
    /// </summary>
    internal const int AlternativeFullNameThreshold = 80;

    /// <summary>
    /// Whether every word of <paramref name="shorterName"/> also appears as a word of
    /// <paramref name="longerName"/>.
    /// </summary>
    internal static bool IsWordSubset(string shorterName, string longerName)
    {
        var longerWords = new HashSet<string>(
            longerName.Split(' ', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        return shorterName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(longerWords.Contains);
    }

    /// <summary>
    /// JF-420.3: the alternative is just a shorter form of the containment match
    /// ("Miles" vs "Miles Davis" for "miles davis live") and adds nothing the user
    /// could have meant, so the comparison is skipped entirely. Guarded on the
    /// match's words appearing in the query (the gate's own substring shape) so a
    /// SUPERSTRING tier-1 match (a tribute act matching "the beatles" by
    /// name-contains-query) never skips.
    /// </summary>
    internal static bool IsRedundantShorterForm(string matchName, string alternativeName, string query)
        => IsWordSubset(matchName, query) && IsWordSubset(alternativeName, matchName);

    /// <summary>
    /// JF-420.3 comparison score: the candidate's score scaled by the length-match
    /// fraction in BOTH directions (min(nameLen/queryLen, queryLen/nameLen)).
    /// Deliberately NOT <see cref="FuzzyMatcher.ApplyFairLengthPenalty"/>: the
    /// matcher's 0.5 floor is a RECALL device (keep half-coverage candidates
    /// reachable in general matching), but in this DECISION it manufactured phantom
    /// margins in both directions (review round 2): a containment-exempt
    /// half-query alternative kept 90 ("Floyd" in "p!nk floyd") while a superstring
    /// tribute also kept 90 against a floor-protected match. Decision fairness
    /// wants the honest length fraction on both sides, symmetrically.
    /// </summary>
    internal static double FairComparisonScore(string name, string query, int score)
    {
        double ratio = Math.Min((double)name.Length / query.Length, (double)query.Length / name.Length);
        return score * ratio;
    }
}
