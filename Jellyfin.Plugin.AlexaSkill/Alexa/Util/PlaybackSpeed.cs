using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using Alexa.NET.Request;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE playback-speed vocabulary (JF-636): the six 0.25-step rates the
/// atempo endpoint serves, the cycling requests ("faster"/"slower"), the
/// speed-slot resolver, and the stream-to-content position arithmetic. Native
/// rate control is unavailable to custom skills (the same MSAPI-only class as
/// the scrubber), so speed is served by re-launching the item through the
/// server-side atempo HLS endpoint; every rate decision goes through this
/// table so the endpoint, the handler, and the config API agree on the step
/// set. Ids are the shared cross-locale key space (the JF-468 one-key-space
/// rule): per-mille strings ("750".."2000") plus "faster"/"slower" for the
/// cycling requests, identical in every locale's SpeedRate type.
/// </summary>
internal static class PlaybackSpeed
{
    /// <summary>The cycling entity-resolution id ("più veloce" family).</summary>
    public const string FasterId = "faster";

    /// <summary>The cycling entity-resolution id ("più lentamente" family).</summary>
    public const string SlowerId = "slower";

    /// <summary>
    /// The six served rates in per-mille form (0.75x..2.0x in 0.25x steps).
    /// atempo covers the whole range in one filter instance (0.5-2.0), so no
    /// chaining is needed. Ordered ascending; <see cref="Step"/> cycles in
    /// this order.
    /// </summary>
    public static readonly int[] RatesPerMille = [750, 1000, 1250, 1500, 1750, 2000];

    /// <summary>The identity rate (no speed endpoint, no scaling).</summary>
    public const int NormalPerMille = 1000;

    /// <summary>Whether <paramref name="ratePerMille"/> is one of the six served rates.</summary>
    public static bool IsValidPerMille(int ratePerMille)
        => Array.IndexOf(RatesPerMille, ratePerMille) >= 0;

    /// <summary>
    /// The user's standing rate: the per-user override when it holds a valid
    /// rate, else normal. An invalid stored value (an old config shape) reads
    /// as normal rather than minting an endpoint-rejecting URL.
    /// </summary>
    public static int ResolveStandingRate(Entities.User? user)
        => user?.PodcastSpeedPerMille is { } rate && IsValidPerMille(rate) ? rate : NormalPerMille;

    /// <summary>
    /// Cycle one step up (<paramref name="direction"/> +1) or down (-1) from
    /// <paramref name="currentPerMille"/>, clamped at the ends of the six-rate
    /// ladder (a "faster" at 2.0x stays 2.0x).
    /// </summary>
    public static int Step(int currentPerMille, int direction)
    {
        int idx = Array.IndexOf(RatesPerMille, IsValidPerMille(currentPerMille) ? currentPerMille : NormalPerMille);
        int next = Math.Clamp(idx + Math.Sign(direction), 0, RatesPerMille.Length - 1);
        return RatesPerMille[next];
    }

    /// <summary>
    /// Convert a device-reported STREAM-relative duration on an atempo stream
    /// to the CONTENT duration it covers: at rate R one output second carries
    /// R content seconds, so content = stream x R (JF-636's load-bearing
    /// arithmetic; the inverse of the endpoint's input seek). Integer
    /// per-mille arithmetic truncates at the 100ns tick, far below audible
    /// position drift. Rate 1000 is the identity (no scaling, byte-identical
    /// to the pre-JF-636 composition).
    /// </summary>
    public static long StreamTicksToContent(long streamTicks, int ratePerMille)
        => ratePerMille == NormalPerMille || streamTicks == 0
            ? streamTicks
            : streamTicks * ratePerMille / 1000L;

    /// <summary>
    /// The millisecond-space twin of <see cref="StreamTicksToContent"/> (same
    /// truncating integer semantics; the ONE rate-arithmetic definition per unit,
    /// so a resume path composing device milliseconds cannot re-inline the
    /// formula).
    /// </summary>
    /// <param name="streamMs">A device-reported stream-relative offset in milliseconds.</param>
    /// <param name="ratePerMille">The stream's playback rate in per-mille form.</param>
    /// <returns>The content duration that stream offset covers, in milliseconds.</returns>
    public static long StreamMsToContent(long streamMs, int ratePerMille)
        => ratePerMille == NormalPerMille || streamMs == 0
            ? streamMs
            : streamMs * ratePerMille / 1000L;

    /// <summary>
    /// The kind of speed request the slot carries: a direct rate, a cycling
    /// step, or nothing the resolver recognized.
    /// </summary>
    public enum RequestKind
    {
        /// <summary>Nothing recognized (missing/empty/unmatched slot).</summary>
        None,

        /// <summary>An explicit rate (one of the six steps).</summary>
        DirectRate,

        /// <summary>Cycle one step up from the current rate.</summary>
        Faster,

        /// <summary>Cycle one step down from the current rate.</summary>
        Slower,
    }

    /// <summary>
    /// A resolved speed-slot request. For <see cref="RequestKind.DirectRate"/>
    /// the rate is guaranteed valid (one of the six steps); the cycling kinds
    /// carry no rate (the handler composes them with the current one).
    /// </summary>
    public readonly record struct Request(RequestKind Kind, int PerMille)
    {
        /// <summary>The nothing-recognized request.</summary>
        public static readonly Request None = new(RequestKind.None, NormalPerMille);
    }

    /// <summary>
    /// Resolve the speed slot (entity resolution first, localized raw words
    /// and spoken decimals second, the JF-583 EpisodePosition shape). The
    /// entity branch reads the shared per-mille/faster/slower ids; the raw
    /// branch covers an older deployed model or ASR drift, including the
    /// spoken digit forms every locale's ASR can deliver ("1,5", "1.5").
    /// </summary>
    /// <param name="slot">The speed slot (null when absent).</param>
    /// <param name="locale">The request locale, for the raw word table.</param>
    /// <returns>The resolved request, or <see cref="Request.None"/>.</returns>
    public static Request Resolve(Slot? slot, string locale)
    {
        if (slot is null || string.IsNullOrWhiteSpace(slot.Value))
        {
            return Request.None;
        }

        if (slot.Resolution?.Authorities is { Length: > 0 } authorities)
        {
            foreach (var authority in authorities)
            {
                if (authority.Status?.Code != "ER_SUCCESS_MATCH" || authority.Values is not { Length: > 0 })
                {
                    continue;
                }

                ResolutionValue resolved = authority.Values[0].Value;
                if (resolved != null && TryResolveId(resolved.Id, out Request byId))
                {
                    return byId;
                }
            }
        }

        string normalized = Normalize(slot.Value);
        if (RawWords.TryGetValue(LocalePrefix.Of(locale), out FrozenDictionary<string, Request>? words)
            && words.TryGetValue(normalized, out Request byWord))
        {
            return byWord;
        }

        return TryResolveSpokenDecimal(normalized);
    }

    /// <summary>Resolve a SpeedRate entity id ("750".."2000", faster, slower).</summary>
    internal static bool TryResolveId(string? id, out Request request)
    {
        request = Request.None;
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int perMille)
            && IsValidPerMille(perMille))
        {
            request = new Request(RequestKind.DirectRate, perMille);
            return true;
        }

        if (string.Equals(id, FasterId, StringComparison.OrdinalIgnoreCase))
        {
            request = new Request(RequestKind.Faster, NormalPerMille);
            return true;
        }

        if (string.Equals(id, SlowerId, StringComparison.OrdinalIgnoreCase))
        {
            request = new Request(RequestKind.Slower, NormalPerMille);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Parse a spoken decimal ("1,5", "1.5", "0.75") into the nearest served
    /// rate. ASR delivers digits as text in every locale; the comma/dot split
    /// is normalized because it-IT/es/de speak commas while the slot value can
    /// arrive either way. Out-of-range or non-quarter values read as None (the
    /// elicit prompt), never a rounded guess.
    /// </summary>
    private static Request TryResolveSpokenDecimal(string value)
    {
        string decimalForm = value.Replace(',', '.').Trim().TrimEnd('.');
        if (!double.TryParse(decimalForm, NumberStyles.Float, CultureInfo.InvariantCulture, out double spoken)
            || spoken < 0.5 || spoken > 2.0)
        {
            return Request.None;
        }

        int perMille = (int)Math.Round(spoken * 1000.0, MidpointRounding.AwayFromZero);
        // Snap to the quarter-step grid only when the spoken value IS a quarter
        // step (within 2 per-mille); "1.3" is not a served rate and asks.
        foreach (int candidate in RatesPerMille)
        {
            if (Math.Abs(candidate - perMille) <= 2)
            {
                return new Request(RequestKind.DirectRate, candidate);
            }
        }

        return Request.None;
    }

    private static string Normalize(string word)
        => word.Trim().ToLowerInvariant().Replace('’', '\'').Replace('‘', '\'');

    /// <summary>
    /// The localized raw-value fallback table (language-prefix keyed, the
    /// EpisodePosition precedent). Only the canonical value + top synonyms of
    /// each concept: entity resolution is the primary resolver and carries the
    /// full synonym sets per locale's SpeedRate type.
    /// </summary>
    private static readonly FrozenDictionary<string, FrozenDictionary<string, Request>> RawWords =
        new Dictionary<string, IReadOnlyDictionary<string, Request>>
        {
            ["it"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["più veloce"] = new(RequestKind.Faster, NormalPerMille),
                ["accelera"] = new(RequestKind.Faster, NormalPerMille),
                ["più lentamente"] = new(RequestKind.Slower, NormalPerMille),
                ["più lento"] = new(RequestKind.Slower, NormalPerMille),
                ["rallenta"] = new(RequestKind.Slower, NormalPerMille),
                ["uno e mezzo"] = new(RequestKind.DirectRate, 1500),
                ["doppia velocità"] = new(RequestKind.DirectRate, 2000),
                ["il doppio"] = new(RequestKind.DirectRate, 2000),
                ["velocità normale"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normale"] = new(RequestKind.DirectRate, NormalPerMille),
                ["tre quarti"] = new(RequestKind.DirectRate, 750),
                ["uno e un quarto"] = new(RequestKind.DirectRate, 1250),
                ["uno e tre quarti"] = new(RequestKind.DirectRate, 1750),
            },
            ["en"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["faster"] = new(RequestKind.Faster, NormalPerMille),
                ["speed up"] = new(RequestKind.Faster, NormalPerMille),
                ["slower"] = new(RequestKind.Slower, NormalPerMille),
                ["slow down"] = new(RequestKind.Slower, NormalPerMille),
                ["one and a half"] = new(RequestKind.DirectRate, 1500),
                ["double speed"] = new(RequestKind.DirectRate, 2000),
                ["twice the speed"] = new(RequestKind.DirectRate, 2000),
                ["normal speed"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["three quarters"] = new(RequestKind.DirectRate, 750),
                ["three quarters speed"] = new(RequestKind.DirectRate, 750),
                ["one and a quarter"] = new(RequestKind.DirectRate, 1250),
                ["one and three quarters"] = new(RequestKind.DirectRate, 1750),
            },
            ["de"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["schneller"] = new(RequestKind.Faster, NormalPerMille),
                ["langsamer"] = new(RequestKind.Slower, NormalPerMille),
                ["eineinhalb"] = new(RequestKind.DirectRate, 1500),
                ["doppelte geschwindigkeit"] = new(RequestKind.DirectRate, 2000),
                ["doppelt so schnell"] = new(RequestKind.DirectRate, 2000),
                ["normale geschwindigkeit"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["dreiviertel"] = new(RequestKind.DirectRate, 750),
                ["eine und ein viertel"] = new(RequestKind.DirectRate, 1250),
                ["eine und drei viertel"] = new(RequestKind.DirectRate, 1750),
            },
            ["es"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["más rápido"] = new(RequestKind.Faster, NormalPerMille),
                ["mas rapido"] = new(RequestKind.Faster, NormalPerMille),
                ["más lento"] = new(RequestKind.Slower, NormalPerMille),
                ["más despacio"] = new(RequestKind.Slower, NormalPerMille),
                ["uno y medio"] = new(RequestKind.DirectRate, 1500),
                ["velocidad doble"] = new(RequestKind.DirectRate, 2000),
                ["el doble"] = new(RequestKind.DirectRate, 2000),
                ["velocidad normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["tres cuartos"] = new(RequestKind.DirectRate, 750),
                ["uno y cuarto"] = new(RequestKind.DirectRate, 1250),
                ["uno y tres cuartos"] = new(RequestKind.DirectRate, 1750),
            },
            ["fr"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["plus vite"] = new(RequestKind.Faster, NormalPerMille),
                ["plus lentement"] = new(RequestKind.Slower, NormalPerMille),
                ["plus lent"] = new(RequestKind.Slower, NormalPerMille),
                ["un et demi"] = new(RequestKind.DirectRate, 1500),
                ["vitesse double"] = new(RequestKind.DirectRate, 2000),
                ["le double"] = new(RequestKind.DirectRate, 2000),
                ["vitesse normale"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["trois quarts"] = new(RequestKind.DirectRate, 750),
                ["un et quart"] = new(RequestKind.DirectRate, 1250),
                ["un et trois quarts"] = new(RequestKind.DirectRate, 1750),
            },
            ["pt"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["mais rápido"] = new(RequestKind.Faster, NormalPerMille),
                ["mais rapido"] = new(RequestKind.Faster, NormalPerMille),
                ["mais devagar"] = new(RequestKind.Slower, NormalPerMille),
                ["mais lento"] = new(RequestKind.Slower, NormalPerMille),
                ["um e meio"] = new(RequestKind.DirectRate, 1500),
                ["velocidade dupla"] = new(RequestKind.DirectRate, 2000),
                ["o dobro"] = new(RequestKind.DirectRate, 2000),
                ["velocidade normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["três quartos"] = new(RequestKind.DirectRate, 750),
                ["tres quartos"] = new(RequestKind.DirectRate, 750),
                ["um e um quarto"] = new(RequestKind.DirectRate, 1250),
                ["um e três quartos"] = new(RequestKind.DirectRate, 1750),
            },
            ["nl"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["sneller"] = new(RequestKind.Faster, NormalPerMille),
                ["langzamer"] = new(RequestKind.Slower, NormalPerMille),
                ["anderhalf"] = new(RequestKind.DirectRate, 1500),
                ["dubbele snelheid"] = new(RequestKind.DirectRate, 2000),
                ["twee keer zo snel"] = new(RequestKind.DirectRate, 2000),
                ["normale snelheid"] = new(RequestKind.DirectRate, NormalPerMille),
                ["normaal"] = new(RequestKind.DirectRate, NormalPerMille),
                ["drie kwart"] = new(RequestKind.DirectRate, 750),
                ["een en een kwart"] = new(RequestKind.DirectRate, 1250),
                ["een en drie kwart"] = new(RequestKind.DirectRate, 1750),
            },
            ["hi"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["तेज़"] = new(RequestKind.Faster, NormalPerMille),
                ["तेज"] = new(RequestKind.Faster, NormalPerMille),
                ["धीमा"] = new(RequestKind.Slower, NormalPerMille),
                ["धीरे"] = new(RequestKind.Slower, NormalPerMille),
                ["डेढ़"] = new(RequestKind.DirectRate, 1500),
                ["दोगुनी गति"] = new(RequestKind.DirectRate, 2000),
                ["सामान्य गति"] = new(RequestKind.DirectRate, NormalPerMille),
                ["सामान्य"] = new(RequestKind.DirectRate, NormalPerMille),
                ["तीन चौथाई"] = new(RequestKind.DirectRate, 750),
                ["एक और एक चौथाई"] = new(RequestKind.DirectRate, 1250),
                ["एक और तीन चौथाई"] = new(RequestKind.DirectRate, 1750),
            },
            ["ja"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["速く"] = new(RequestKind.Faster, NormalPerMille),
                ["遅く"] = new(RequestKind.Slower, NormalPerMille),
                ["1.5倍"] = new(RequestKind.DirectRate, 1500),
                ["2倍"] = new(RequestKind.DirectRate, 2000),
                ["通常速度"] = new(RequestKind.DirectRate, NormalPerMille),
                ["等速"] = new(RequestKind.DirectRate, NormalPerMille),
                ["0.75倍"] = new(RequestKind.DirectRate, 750),
                ["1.25倍"] = new(RequestKind.DirectRate, 1250),
                ["1.75倍"] = new(RequestKind.DirectRate, 1750),
            },
            ["ar"] = new Dictionary<string, Request>(StringComparer.Ordinal)
            {
                ["أسرع"] = new(RequestKind.Faster, NormalPerMille),
                ["أبطأ"] = new(RequestKind.Slower, NormalPerMille),
                ["سرعة واحدة ونصف"] = new(RequestKind.DirectRate, 1500),
                ["سرعة مضاعفة"] = new(RequestKind.DirectRate, 2000),
                ["سرعة عادية"] = new(RequestKind.DirectRate, NormalPerMille),
                ["ثلاثة أرباع"] = new(RequestKind.DirectRate, 750),
                ["واحد وربع"] = new(RequestKind.DirectRate, 1250),
                ["واحد وثلاثة أرباع"] = new(RequestKind.DirectRate, 1750),
            },
        }.ToFrozenDictionary(
            kv => kv.Key,
            kv => ((IReadOnlyDictionary<string, Request>)kv.Value).ToFrozenDictionary(StringComparer.Ordinal));
}
