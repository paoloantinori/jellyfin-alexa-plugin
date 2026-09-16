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
    /// Pre-existing inline copies of this chain (PlayBook, YesIntent, LaunchRequestHandler)
    /// are tracked for migration in JF-567.
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
