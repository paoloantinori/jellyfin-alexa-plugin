#nullable enable
using System;
using System.Globalization;

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
/// task): the shuffle/loop toggle paths still parse bare GUIDs
/// (ShuffleOn/ShuffleOff/ApplyRepeatModeAsync) and degrade to the NoMediaPlaying tell
/// mid-sleep rather than crashing.
/// Unknown suffixes ("<c>{guid}|other</c>") are treated as unparseable rather than
/// split, so a future suffix owner must extend this codec instead of ad-hoc parsing.
/// </summary>
internal static class StreamTokenCodec
{
    private const string SleepSuffix = "|sleep:";

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
        => FormattableString.Invariant($"{itemId}{SleepSuffix}{deadlineUtcTicks}");

    /// <summary>
    /// Extracts the item ID from a stream token (the part before the sleep suffix, or
    /// the whole token when it carries none).
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

        int suffix = token.IndexOf(SleepSuffix, StringComparison.Ordinal);
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
}
