using System;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE single-cut carrier-phrase primitive (JF-610) shared by the
/// slot-carrier strip mechanisms whose semantics coincide: a table of
/// space-BAKED carrier entries ("chiamata ", "the song ", "il film "), one cut
/// per call, true when a cut happened. The POLICY (when to call this) stays at
/// each call site and is documented there:
/// <list type="bullet">
/// <item><description>AlbumPlayService (JF-469): FALLBACK-ONLY, one bounded retry after a confirmed raw miss.</description></item>
/// <item><description>PlaySongIntentHandler.StripSongCarrierPhrase: PREEMPTIVE, single cut before the search.</description></item>
/// <item><description>PlaylistNameNormalizer (JF-600/602): PREEMPTIVE at read time for speech/create, RAW-FIRST inside the edit family's matching.</description></item>
/// <item><description>PlayVideoIntentHandler (JF-509): RAW-FIRST and deliberately NOT on this primitive - its strip is three-state (null = no retry, stripped = retry) with a whole-value-is-the-noun collapse that a bool cut cannot express.</description></item>
/// </list>
/// A future slot-carrier fix copies the primitive, never the policy: pick the
/// policy from this table by slot kind (title-bearing slots are fallback-only).
/// </summary>
public static class CarrierPhrase
{
    /// <summary>
    /// Strips one leading carrier from the value. Entries carry their trailing
    /// separator space baked in, so word-fragment values never match and a value
    /// that IS the bare carrier word does not cut.
    /// </summary>
    /// <param name="value">The value to strip; replaced in place by the remainder.</param>
    /// <param name="carriers">The space-baked leading carrier entries.</param>
    /// <returns>True when a carrier was stripped.</returns>
    public static bool TryStripLeading(ref string value, string[] carriers)
    {
        foreach (string carrier in carriers)
        {
            if (value.Length > carrier.Length
                && value.StartsWith(carrier, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = value[carrier.Length..].Trim();
                if (remainder.Length == 0)
                {
                    // Carrier plus only whitespace ("chiamato  "): nothing
                    // searchable survives the cut, so this is NO cut - the raw
                    // value stays and the caller's own empty-value handling
                    // applies (review round: the pre-JF-610 album strip kept the
                    // raw here; an empty cut would send an empty SearchTerm).
                    continue;
                }

                value = remainder;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Strips one trailing carrier from the value (the ja/hi name-suffix shapes).
    /// Entries carry their leading separator space baked in.
    /// </summary>
    /// <param name="value">The value to strip; replaced in place by the remainder.</param>
    /// <param name="carriers">The space-baked trailing carrier entries.</param>
    /// <returns>True when a carrier was stripped.</returns>
    public static bool TryStripTrailing(ref string value, string[] carriers)
    {
        foreach (string carrier in carriers)
        {
            if (value.Length > carrier.Length
                && value.EndsWith(carrier, StringComparison.OrdinalIgnoreCase))
            {
                string remainder = value[..^carrier.Length].TrimEnd();
                if (remainder.Length == 0)
                {
                    continue;
                }

                value = remainder;
                return true;
            }
        }

        return false;
    }
}
