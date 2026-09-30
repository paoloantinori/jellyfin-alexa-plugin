#nullable enable
using System;
using System.Globalization;
using System.Threading;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Playback;

/// <summary>
/// Single codec for AudioPlayer stream tokens that carry a suffix (JF-447). The
/// sleep-timer flow embeds a deadline in the token the skill mints
/// ("<c>{guid}|sleep:{utcTicks}</c>"), and every AudioPlayer event then echoes that
/// token back. The format previously had THREE independent owners (mint in
/// SleepTimerIntentHandler, suffix parse in PlaybackNearlyFinishedEventHandler, prefix
/// parse in PlaybackFailedEventHandler) plus raw <c>new Guid(token)</c> calls that
/// threw <see cref="FormatException"/> on the composite form and killed the Started,
/// Finished and Stopped handlers before their keep-alive ack. Every EVENT handler now
/// shares this codec so the format has one definition and composite tokens parse on
/// every event path. Known non-migrated sites (intent decision, flagged in the JF-447
/// task): the shuffle toggle paths still parse bare GUIDs
/// (ShuffleOn/ShuffleOff) and degrade to the NoMediaPlaying tell
/// mid-sleep rather than crashing.
/// Unknown suffixes ("<c>{guid}|other</c>") are treated as unparseable rather than
/// split, so a future suffix owner must extend this codec instead of ad-hoc parsing.
/// </summary>
internal static class StreamTokenCodec
{
    private const string SleepSuffix = "|sleep:";

    /// <summary>
    /// The JF-655 review launch-generation suffix ("<c>|launch:{n}</c>"): a per-launch
    /// nonce that lets the displacement classifier tell two streams of the SAME item
    /// apart (the speed re-launch, the sleep re-issue, repeat-one). Minted ONLY onto
    /// launches that replace an actively-playing same-item stream (see
    /// <c>PlaybackLaunchBuilder.MintStreamToken</c>), so every other directive keeps
    /// the bare item id it always carried. Process-wide monotonic counter: two mints
    /// never collide, and both stores that consume it (the ordering state and the
    /// active-audio flag) are in-memory and start empty on every boot.
    /// </summary>
    private const string LaunchSuffix = "|launch:";

    private static long _nextLaunchGeneration;

    /// <summary>
    /// Mints a sleep-timer stream token for an item. The item ID is passed as the bare
    /// GUID string so callers can canonicalize a token that ALREADY carries a sleep
    /// suffix (re-arming the timer during sleep playback) before minting; minting from
    /// a suffixed id would stack suffixes whose deadline parse then fails.
    /// </summary>
    /// <param name="itemId">The bare item GUID string.</param>
    /// <param name="deadlineUtcTicks">The sleep deadline in UTC ticks.</param>
    /// <returns>The composite stream token.</returns>
    internal static string MintSleepTimerToken(Guid itemId, long deadlineUtcTicks)
        => MintSleepTimerToken(itemId.ToString(), deadlineUtcTicks);

    /// <summary>
    /// The base-token form of the sleep mint: the JF-655 review call sites compose the
    /// launch generation FIRST (<c>{guid}|launch:{n}</c>) and append the sleep deadline
    /// after it, so the deadline parser (everything past <c>|sleep:</c>) and the
    /// generation parser (the segment past <c>|launch:</c> up to the next suffix) each
    /// read their own suffix no matter how the two combine.
    /// </summary>
    /// <param name="baseToken">The bare item GUID, optionally already carrying a launch-generation suffix.</param>
    /// <param name="deadlineUtcTicks">The sleep deadline in UTC ticks.</param>
    /// <returns>The composite stream token.</returns>
    internal static string MintSleepTimerToken(string baseToken, long deadlineUtcTicks)
        => FormattableString.Invariant($"{baseToken}{SleepSuffix}{deadlineUtcTicks}");

    /// <summary>
    /// Appends a FRESH launch generation to a bare item id (JF-655 review). Callers
    /// must pass the bare GUID (canonicalized first when the current token carries
    /// suffixes), and must append the sleep suffix, if any, AFTER this.
    /// </summary>
    /// <param name="bareItemId">The bare item GUID string.</param>
    /// <returns>The generation-carrying stream token.</returns>
    internal static string WithLaunchGeneration(string bareItemId)
        => FormattableString.Invariant($"{bareItemId}{LaunchSuffix}{Interlocked.Increment(ref _nextLaunchGeneration)}");

    /// <summary>
    /// Extracts the launch generation from a stream token's suffix, when present.
    /// </summary>
    /// <param name="token">The raw stream token from the event or directive.</param>
    /// <param name="generation">The launch generation when parsing succeeds.</param>
    /// <returns>True when the token carries a parseable launch generation.</returns>
    internal static bool TryGetLaunchGeneration(string? token, out long generation)
    {
        generation = 0;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        int suffix = token.IndexOf(LaunchSuffix, StringComparison.Ordinal);
        if (suffix < 0)
        {
            return false;
        }

        ReadOnlySpan<char> valuePart = token.AsSpan(suffix + LaunchSuffix.Length);
        int nextSuffix = valuePart.IndexOf('|');
        if (nextSuffix >= 0)
        {
            valuePart = valuePart[..nextSuffix];
        }

        return long.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out generation);
    }

    /// <summary>
    /// Extracts the item ID from a stream token (the part before the first KNOWN
    /// suffix, or the whole token when it carries none). Known suffixes: the sleep
    /// deadline and the launch generation; an unknown suffix still makes the token
    /// unparseable (the guard the class doc owns).
    /// </summary>
    /// <param name="token">The raw stream token from the event or directive.</param>
    /// <param name="itemId">The embedded item ID when parsing succeeds.</param>
    /// <returns>True when the token carries a parseable item ID.</returns>
    internal static bool TryGetItemId(string? token, out Guid itemId)
    {
        if (token is null)
        {
            itemId = Guid.Empty;
            return false;
        }

        int sleep = token.IndexOf(SleepSuffix, StringComparison.Ordinal);
        int launch = token.IndexOf(LaunchSuffix, StringComparison.Ordinal);
        int suffix = sleep >= 0 ? (launch >= 0 ? Math.Min(sleep, launch) : sleep) : launch;
        ReadOnlySpan<char> idPart = suffix >= 0 ? token.AsSpan(0, suffix) : token.AsSpan();
        return Guid.TryParse(idPart, out itemId);
    }

    /// <summary>
    /// Extracts the sleep deadline from a stream token's suffix, when present.
    /// </summary>
    /// <param name="token">The raw stream token from the event or directive.</param>
    /// <param name="deadlineUtcTicks">The deadline in UTC ticks when a suffix exists and parses.</param>
    /// <returns>True when the token carries a parseable sleep deadline.</returns>
    internal static bool TryGetSleepDeadlineUtcTicks(string? token, out long deadlineUtcTicks)
    {
        deadlineUtcTicks = 0;
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        int suffix = token.IndexOf(SleepSuffix, StringComparison.Ordinal);
        return suffix >= 0
            && long.TryParse(
                token.AsSpan(suffix + SleepSuffix.Length),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out deadlineUtcTicks);
    }

    /// <summary>
    /// Whether a stream token's sleep timer has EXPIRED at the given instant: the ONE
    /// predicate both playback-event gates read (JF-683 review F5). NearlyFinished
    /// stops enqueueing at an expired deadline (playback ends after the current
    /// stream, nothing follows); PlaybackFinished's keep-alive arm must decline on
    /// exactly the same shape, or the session would outlive the sleep timer's
    /// stop-and-dismiss intent. The two gates must stay in exact parity, hence one
    /// definition instead of a copied comparison.
    /// </summary>
    /// <param name="token">The raw stream token (a token without a sleep suffix never expires).</param>
    /// <param name="now">The evaluation instant (the call sites pass DateTimeOffset.UtcNow).</param>
    /// <returns>True when the token carries a sleep deadline that has passed.</returns>
    internal static bool IsSleepExpiredUtc(string? token, DateTimeOffset now)
        => TryGetSleepDeadlineUtcTicks(token, out long deadlineTicks)
            && now.UtcTicks >= deadlineTicks;

    /// <summary>
    /// Whether a stream token NAMES the given item (the JF-655 round-3 sweep's ONE
    /// comparison helper): both sides parse through <see cref="TryGetItemId"/>, so a
    /// generation- or sleep-suffixed token compares equal to the bare store id it
    /// was minted from. Every token-vs-id comparison goes through this instead of
    /// raw string equality, which a suffixed token silently fails.
    /// </summary>
    /// <param name="token">The raw stream token from the event or directive.</param>
    /// <param name="itemId">The bare item id from a store (ledger, queue pointer, session).</param>
    /// <returns>True when both parse and name the same item.</returns>
    internal static bool NamesItem(string? token, string? itemId)
        => TryGetItemId(token, out Guid tokenItem)
            && TryGetItemId(itemId, out Guid namedItem)
            && tokenItem == namedItem;
}
