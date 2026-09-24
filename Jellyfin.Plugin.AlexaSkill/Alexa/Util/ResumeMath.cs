using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// Pure resume/position math (JF-315 batch 2): queue resume-index selection, the
/// audiobook tracker key and start-ticks resolution, position formatting, and
/// (batch 10) the favorites-first rating ordering that shares the sort machinery.
/// Static by design: no handler instance state, every explicit dependency arrives as a
/// parameter. Members moved verbatim from BaseHandler; call sites migrated
/// mechanically. Two members are pure modulo an ambient read, kept here
/// deliberately per the batch-2 gate scoping: GetAudiobookStartTicks reads the
/// Plugin.Instance audiobook tracker singleton, and the full FindResumeTrackIndex
/// overload calls GetOrCreateQueue (creates-on-read) on the passed queue manager;
/// both should be parameterized when the stateful collaborators (JF-315 clusters
/// H/C) land. The manager-querying resume members stayed in BaseHandler until
/// batch 10: FindLastPlayedItemWithProgress remains there (I/O, cluster K/L
/// residue), while ComposeItemAbsolutePosition/ComposeEventPositionTicks and
/// TryGetRuntimeTicksForGuard moved to the Handler-namespace ProgressReporter
/// (queue-state-coupled writers, their cluster-H home).
/// </summary>
public static class ResumeMath
{
    /// <summary>
    /// JF-618: parses an AMAZON.DURATION slot value (the shared home, so the
    /// SetReminder migration will not have to copy this). ISO 8601 through
    /// XmlConvert, with three live-probed accommodations: week forms (P1W) are valid
    /// ISO 8601 but NOT valid XSD duration, so they map to days first («ferma dopo
    /// una settimana» delivered P1W and dead-ended in the elicit loop); the catch
    /// covers OverflowException too (a P100000000D probe escapes as overflow, not
    /// format); and a bare whole number means minutes (AMAZON.DURATION's
    /// raw-value passthrough can carry unresolved spoken text where a bare number
    /// keeps the pre-JF-618 semantic). Null when unparseable.
    /// </summary>
    /// <param name="raw">The raw slot value.</param>
    /// <returns>The parsed duration, or null.</returns>
    public static TimeSpan? ParseAlexaDuration(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string value = raw.Trim();

        // PnW week form: XmlConvert rejects it, ISO 8601 allows it. Bounded so an
        // absurd week count cannot overflow FromDays (anything past ~29,000 years
        // overflows TimeSpan; 520,000 weeks is 10,000 years, far past any real ask).
        if (value.Length >= 3
            && (value[0] == 'P' || value[0] == 'p')
            && (value[^1] == 'W' || value[^1] == 'w')
            && int.TryParse(value[1..^1], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int weeks))
        {
            return weeks >= 1 && weeks <= 520_000 ? TimeSpan.FromDays(7d * weeks) : null;
        }

        try
        {
            return System.Xml.XmlConvert.ToTimeSpan(value);
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            // Not an XSD duration: fall through to the bare-number shape.
        }

        return int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int minutes)
            ? TimeSpan.FromMinutes(minutes)
            : null;
    }

    /// <summary>
    /// The spoken-duration phrase shared by the sleep-timer (JF-618) and reminder (JF-622) confirmations:, largest whole unit
    /// with singular/plural (SleepTimerUnit* keys; the plural-seconds arm reuses
    /// SecondsOnly). Boundary snap (review finding): a PT59.5S timer speaks "one
    /// minute", not "60 seconds" (the arms hand off where rounding would cross 60).
    /// Distinct from <see cref="FormatTimeSpan"/> on purpose: that formatter emits
    /// two-unit composites ("1 hours and 30 minutes") for progress announcements,
    /// this one a single natural phrase for a just-set timer.
    /// </summary>
    /// <param name="duration">The parsed duration (must be positive).</param>
    /// <param name="locale">The request locale.</param>
    /// <returns>The localized phrase, e.g. "trenta secondi" / "un minuto" / "2 ore".</returns>
    public static string FormatSpokenLargestUnit(TimeSpan duration, string locale)
    {
        // Seconds arm first: sub-minute asks ("trenta secondi") speak seconds; the
        // plural form reuses SecondsOnly.
        if (duration.TotalSeconds < 59.5)
        {
            int seconds = (int)Math.Round(duration.TotalSeconds, MidpointRounding.AwayFromZero);
            return Locale.ResponseStrings.Get(
                seconds == 1 ? "SleepTimerUnitSecond" : "SecondsOnly", locale,
                seconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        double totalMinutes = duration.TotalMinutes;

        // Whole-hour asks ("un'ora", "due ore") speak hours even though the ISO
        // value is exact minutes; +-3 minutes tolerance keeps 57-63 minutes in the
        // hours phrase while 45 or 90 minutes stay minutes.
        if (totalMinutes >= 57)
        {
            double hoursRounded = Math.Round(duration.TotalHours, MidpointRounding.AwayFromZero);
            if (Math.Abs(totalMinutes - (hoursRounded * 60)) <= 3)
            {
                int hours = (int)hoursRounded;
                return Locale.ResponseStrings.Get(
                    hours == 1 ? "SleepTimerUnitHour" : "SleepTimerUnitHours", locale,
                    hours.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (totalMinutes < 600)
        {
            int minutes = (int)Math.Round(totalMinutes, MidpointRounding.AwayFromZero);
            return Locale.ResponseStrings.Get(
                minutes == 1 ? "SleepTimerUnitMinute" : "SleepTimerUnitMinutes", locale,
                minutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        int fallbackHours = (int)Math.Round(duration.TotalHours, MidpointRounding.AwayFromZero);
        return Locale.ResponseStrings.Get(
            fallbackHours == 1 ? "SleepTimerUnitHour" : "SleepTimerUnitHours", locale,
            fallbackHours.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Ticks to milliseconds, clamped to int.MaxValue like the LaunchRequestHandler
    /// idiom (review consistency finding 2026-09-22: three unclamped inline casts in
    /// ResumeIntentHandler could produce an unspecified cast value on a corrupt or
    /// buggy device report beyond ~24.86 days of ticks).
    /// </summary>
    /// <param name="ticks">The position in .NET ticks.</param>
    /// <returns>The position in whole milliseconds, clamped to int range.</returns>
    public static int TicksToMs(long ticks)
        => (int)Math.Min(TimeSpan.FromTicks(ticks).TotalMilliseconds, int.MaxValue);

    /// <summary>
    /// Combined sort-by-rating + resume-index detection in a single pass over user data.
    /// Eliminates duplicate GetUserData calls when both operations are needed.
    /// </summary>
    public static (IReadOnlyList<BaseItem> SortedItems, int ResumeIndex, long ResumeTicks) SortAndFindResumeIndex(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager,
        bool resumePosition)
    {
        if (items.Count <= 1)
        {
            if (items.Count == 1)
            {
                UserItemData? data = userDataManager.GetUserData(user, items[0]);
                long ticks = resumePosition && data?.PlaybackPositionTicks > 0 && data.Played == false
                    ? data.PlaybackPositionTicks : 0;
                return (items, 0, ticks);
            }

            return (items, 0, 0);
        }

        var favorites = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>();
        var rest = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>(items.Count);
        bool anyRating = false;
        int lastPlayedIndex = -1;
        int inProgressIndex = -1;
        long inProgressTicks = 0;

        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            UserItemData? data = userDataManager.GetUserData(user, item);
            double? rating = data?.Rating;
            if (rating.HasValue)
            {
                anyRating = true;
            }

            bool isFavorite = data?.IsFavorite == true;

            // Track resume position (first in-progress track wins)
            if (inProgressIndex < 0 && data?.PlaybackPositionTicks > 0 && data.Played == false)
            {
                inProgressIndex = i;
                inProgressTicks = resumePosition ? data.PlaybackPositionTicks : 0;
            }

            if (data?.Played == true && lastPlayedIndex < i)
            {
                lastPlayedIndex = i;
            }

            var entry = (i, item, rating, data);
            if (isFavorite)
            {
                favorites.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        // Sort
        IReadOnlyList<BaseItem> sorted;
        if (!anyRating)
        {
            sorted = items;
        }
        else
        {
            List<BaseItem> result = new List<BaseItem>(items.Count);
            result.AddRange(SortByRating(favorites));
            result.AddRange(SortByRating(rest));
            sorted = result;
        }

        // Determine resume index
        if (inProgressIndex >= 0)
        {
            // Map original index to sorted position
            BaseItem inProgressItem = items[inProgressIndex];
            int sortedIndex = FindItemIndex(sorted, inProgressItem);
            return (sorted, sortedIndex, inProgressTicks);
        }

        if (lastPlayedIndex >= 0 && lastPlayedIndex + 1 < items.Count)
        {
            BaseItem nextItem = items[lastPlayedIndex + 1];
            int sortedIndex = FindItemIndex(sorted, nextItem);
            return (sorted, sortedIndex, 0);
        }

        return (sorted, 0, 0);
    }

    private static int FindItemIndex(IReadOnlyList<BaseItem> items, BaseItem target)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i], target))
            {
                return i;
            }
        }

        return 0;
    }

    private static IEnumerable<BaseItem> SortByRating(List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)> items)
    {
        return items.OrderByDescending(i => i.Rating ?? double.MinValue)
                    .ThenBy(i => i.Index)
                    .Select(i => i.Item);
    }

    /// <summary>
    /// Reorder items so favorites appear first, then by personal rating descending
    /// within each group (favorites, non-favorites). Items without a rating keep
    /// their original relative order (stable sort). Moved from BaseHandler (JF-315
    /// batch 10): it is the ordering half of <see cref="SortAndFindResumeIndex"/>
    /// and now shares its ONE SortByRating (JF-570 closed this batch: the former
    /// 3-tuple BaseHandler twin and this 4-tuple were token-identical chains, so
    /// the caller now builds the 4-tuple it already had the user data for and the
    /// 3-tuple copy is deleted).
    /// </summary>
    /// <param name="items">Items to reorder.</param>
    /// <param name="user">Jellyfin user for favorite and rating lookup.</param>
    /// <param name="userDataManager">User data manager for favorite/rating status.</param>
    /// <returns>Items sorted with favorites first and highest-rated within each group.</returns>
    public static IReadOnlyList<BaseItem> FavoritesAndRatingsFirst(
        IReadOnlyList<BaseItem> items,
        Jellyfin.Database.Implementations.Entities.User user,
        IUserDataManager userDataManager)
    {
        if (items.Count <= 1)
        {
            return items;
        }

        var favorites = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>();
        var rest = new List<(int Index, BaseItem Item, double? Rating, UserItemData? Data)>(items.Count);
        bool anyRating = false;

        for (int i = 0; i < items.Count; i++)
        {
            BaseItem item = items[i];
            UserItemData? data = userDataManager.GetUserData(user, item);
            double? rating = data?.Rating;
            if (rating.HasValue)
            {
                anyRating = true;
            }

            bool isFavorite = data?.IsFavorite == true;

            var entry = (i, item, rating, data);
            if (isFavorite)
            {
                favorites.Add(entry);
            }
            else
            {
                rest.Add(entry);
            }
        }

        if (!anyRating)
        {
            return items;
        }

        List<BaseItem> result = new List<BaseItem>(items.Count);
        result.AddRange(SortByRating(favorites));
        result.AddRange(SortByRating(rest));
        return result;
    }

    /// <summary>
    /// Find the resume track index with only UserData (no ItemPositionState).
    /// Delegates to the full overload with null queueManager.
    /// </summary>
    public static (int Index, long PositionTicks) FindResumeTrackIndex(
        IReadOnlyList<BaseItem> tracks,
        JellyfinUser jellyfinUser,
        IUserDataManager userDataManager,
        bool resumePosition,
        ILogger? logger = null)
        => FindResumeTrackIndex(tracks, jellyfinUser, userDataManager, null, null, resumePosition, logger);

    /// <summary>
    /// Find the resume track index, checking ItemPositionState first
    /// (bypasses Jellyfin's MinAudiobookResume threshold), then UserData.
    /// When queueManager/deviceId are null, skips the ItemPositionState check.
    /// </summary>
    public static (int Index, long PositionTicks) FindResumeTrackIndex(
        IReadOnlyList<BaseItem> tracks,
        JellyfinUser jellyfinUser,
        IUserDataManager userDataManager,
        Playback.DeviceQueueManager? queueManager,
        string? deviceId,
        bool resumePosition,
        ILogger? logger = null)
    {
        Playback.DeviceQueue? queue = queueManager != null && deviceId != null
            ? queueManager.GetOrCreateQueue(deviceId)
            : null;
        int lastPlayedIndex = -1;

        for (int i = 0; i < tracks.Count; i++)
        {
            // Check ItemPositionState first (bypasses MinAudiobookResume threshold)
            if (queue != null)
            {
                string itemIdStr = tracks[i].Id.ToString("N");
                if (queue.ItemPositionState.TryGetValue(itemIdStr, out long cachedTicks) && cachedTicks > 0)
                {
                    logger?.LogDebug(
                        "FindResumeTrackIndex: found ItemPositionState for track[{Idx}] '{Name}' — ticks={Ticks}",
                        i, tracks[i].Name, cachedTicks);
                    return (i, resumePosition ? cachedTicks : 0);
                }
            }

            // Fall back to Jellyfin UserData
            UserItemData? data = userDataManager.GetUserData(jellyfinUser, tracks[i]);
            if (data == null)
            {
                continue;
            }

            if (data.PlaybackPositionTicks > 0 && !data.Played)
            {
                logger?.LogDebug(
                    "FindResumeTrackIndex: found UserData for track[{Idx}] '{Name}' — ticks={Ticks}",
                    i, tracks[i].Name, data.PlaybackPositionTicks);
                return (i, resumePosition ? data.PlaybackPositionTicks : 0);
            }

            if (data.Played && lastPlayedIndex < i)
            {
                lastPlayedIndex = i;
            }
        }

        if (lastPlayedIndex >= 0 && lastPlayedIndex + 1 < tracks.Count)
        {
            logger?.LogDebug(
                "FindResumeTrackIndex: no in-progress track, resuming after last played[{Idx}] '{Name}'",
                lastPlayedIndex, tracks[lastPlayedIndex].Name);
            return (lastPlayedIndex + 1, 0);
        }

        logger?.LogDebug("FindResumeTrackIndex: no resume position found, starting from beginning");
        return (0, 0);
    }

    /// <summary>
    /// The audiobook position-tracker key for a book item: its parent book folder (the
    /// key the HLS segment requests record under), falling back to the item itself when
    /// it has no parent. The canonical definition (JF-563): the record path (segment
    /// URLs keyed by the parentId the playlists embed) and every resume read site must
    /// agree on the shape, or the lookup silently misses and resume falls to position 0.
    /// The JF-567 migration completed: the only remaining variants are this canonical
    /// one and the deliberately dashed-Guid URL form in PlaybackLaunchBuilder.
    /// </summary>
    /// <param name="bookItem">An audiobook item (chapter or single-file book).</param>
    /// <returns>The tracker key (GUID "N" format).</returns>
    public static string GetAudiobookBookKey(BaseItem bookItem)
        => (bookItem.ParentId != Guid.Empty ? bookItem.ParentId : bookItem.Id).ToString("N");

    /// <summary>
    /// The resume start position for a book: the tracker position first (the HLS concat
    /// timeline the tracker records), the caller's fallback ticks when the tracker is
    /// cold. JF-563: shared so every resume site resolves the position in PlayBook's
    /// order (tracker beats server-side progress).
    /// </summary>
    /// <param name="bookKey">The tracker key (<see cref="GetAudiobookBookKey"/>).</param>
    /// <param name="fallbackTicks">The position to use when the tracker holds none.</param>
    /// <returns>The resume start ticks (0 when neither source has a position).</returns>
    public static long GetAudiobookStartTicks(string bookKey, long fallbackTicks)
        => Plugin.Instance?.AudiobookPositionTracker?.GetPositionTicks(bookKey) is long tracked && tracked > 0
            ? tracked
            : fallbackTicks;

    /// <summary>
    /// Formats a tick-based playback position into a human-readable string.
    /// </summary>
    /// <param name="ticks">The playback position in ticks.</param>
    /// <returns>A formatted position string (e.g. "1h 30m", "45m 12s", "30s").</returns>
    public static string FormatPosition(long ticks)
    {
        var ts = TimeSpan.FromTicks(ticks);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        return ts.TotalMinutes >= 1 ? $"{(int)ts.TotalMinutes}m {ts.Seconds}s" : $"{ts.Seconds}s";
    }

    /// <summary>
    /// Format a TimeSpan into a locale-aware voice-friendly string (e.g. "1 hours and 30 minutes").
    /// Uses ResponseStrings for localized templates.
    /// </summary>
    public static string FormatTimeSpan(TimeSpan span, string locale)
    {
        if (span.TotalHours >= 1)
        {
            return ResponseStrings.Get("HoursAndMinutes", locale, (int)span.TotalHours, span.Minutes);
        }

        if (span.TotalMinutes >= 1)
        {
            return ResponseStrings.Get("MinutesAndSeconds", locale, (int)span.TotalMinutes, span.Seconds);
        }

        return ResponseStrings.Get("SecondsOnly", locale, span.Seconds);
    }

    /// <summary>
    /// Build a locale-aware position string from session state.
    /// Returns "X of Y" when runtime is known, just the position otherwise, or empty when position is 0/unavailable.
    /// </summary>
    public static string BuildPositionDisplay(SessionInfo session, string locale)
    {
        if (session.PlayState?.PositionTicks == null || session.PlayState.PositionTicks.Value <= 0)
        {
            return string.Empty;
        }

        var position = TimeSpan.FromTicks(session.PlayState.PositionTicks.Value);
        string positionStr = FormatTimeSpan(position, locale);

        long? runtimeTicks = session.NowPlayingItem?.RunTimeTicks;
        if (runtimeTicks.HasValue && runtimeTicks.Value > 0)
        {
            var runtime = TimeSpan.FromTicks(runtimeTicks.Value);
            return ResponseStrings.Get("PositionOfTotal", locale, positionStr, FormatTimeSpan(runtime, locale));
        }

        return positionStr;
    }
}
