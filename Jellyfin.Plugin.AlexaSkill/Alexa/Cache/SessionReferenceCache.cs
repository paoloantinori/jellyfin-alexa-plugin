#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using MediaBrowser.Controller.Session;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Cache;

/// <summary>
/// Cache of live Jellyfin <see cref="SessionInfo"/> references keyed by
/// (JellyfinToken, deviceId), JF-477. <c>GetSessionByAuthenticationToken</c> runs a
/// device/auth-database query plus session re-registration on EVERY skill request; during
/// a transient IO hiccup it hung for the full retry budget and ate Alexa's ~8s window
/// before the handler could run (live incident 2026-09-03 corr=40edec8a). Jellyfin's
/// SessionManager returns the LIVE per-session object it holds
/// (<c>_activeConnections.GetOrAdd</c>, Jellyfin 10.11 Emby.Server.Implementations
/// SessionManager.GetSessionInfo), so caching the REFERENCE keeps every mutable property
/// (PlayState, NowPlayingQueue, NowPlayingItem) current without re-querying; only the
/// lookup itself is skipped. The TTL is hygiene (bounded staleness and dictionary size),
/// not correctness: a removed session surfaces as ResourceNotFoundException on the next
/// play-path report, and the report failure paths call <see cref="InvalidateDevice"/> so
/// the NEXT request refetches instead of reusing the corpse; a RE-STAMPED session (another
/// household profile resolved it) is caught by the expected-UserId check in
/// <see cref="TryGet"/>.
/// </summary>
internal static class SessionReferenceCache
{
    /// <summary>
    /// Separator for the composite key. A control character that appears in neither
    /// Jellyfin access tokens nor Alexa device IDs, so (token, device) pairs can never
    /// collide by concatenation.
    /// </summary>
    private const char KeySeparator = '\n';

    private static readonly TimeSpan EntryTtl = TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, CachedSession> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Attempts to retrieve the cached live session for a (token, device) pair. An entry
    /// past its TTL is treated as a miss and reclaimed (lazy expiry: there is no sweep,
    /// so an idle device's entry stays resident until its next read, which is bounded by
    /// the one-entry-per-pair dictionary shape).
    /// A fresh entry is STILL a miss when the live session's UserId no longer equals the
    /// <see cref="CachedSession.ExpectedUserId"/> captured at Store time: Jellyfin keeps
    /// ONE session object per (app, device) and LogSessionActivity re-stamps its UserId
    /// with the LAST resolver, so when another household profile spoke since this entry
    /// was stored, the shared object now names THEM and serving it would attribute this
    /// request to the wrong Jellyfin user (handlers resolve the library from
    /// session.UserId). The mismatch entry is dropped and the caller's fresh lookup
    /// re-stamps. Comparing the stored expectation against the LIVE stamp (not against
    /// any plugin-side user id) keeps the check inside the ONE id space the session
    /// actually uses; the session just resolved with THIS token carried the right stamp
    /// by construction.
    /// </summary>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="session">The cached live session, if a fresh entry exists.</param>
    /// <returns>True when a fresh entry was found.</returns>
    public static bool TryGet(string? token, string? deviceId, out SessionInfo? session)
    {
        session = null;
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId))
        {
            return false;
        }

        string key = BuildKey(token, deviceId);
        if (!Cache.TryGetValue(key, out CachedSession? entry))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - entry.StoredAt > EntryTtl)
        {
            Cache.TryRemove(new KeyValuePair<string, CachedSession>(key, entry));
            return false;
        }

        if (entry.Session.UserId != entry.ExpectedUserId)
        {
            // Another profile re-stamped the shared session since this entry was stored.
            // Drop the stale entry (value-conditional: never a concurrent Store's
            // replacement) so the caller's lookup re-stamps and re-stores.
            Cache.TryRemove(new KeyValuePair<string, CachedSession>(key, entry));
            return false;
        }

        session = entry.Session;
        return true;
    }

    /// <summary>
    /// Stores the live session reference for a (token, device) pair, replacing any
    /// previous entry (one live entry per pair keeps the cache bounded), and captures the
    /// session's CURRENT UserId stamp as the entry's expected value (see
    /// <see cref="TryGet"/>). A null session is never stored: null means "no session",
    /// and caching it would turn the next request into a fake hit that skips the retry.
    /// </summary>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="session">The live session reference returned by the lookup.</param>
    public static void Store(string? token, string? deviceId, SessionInfo? session)
    {
        if (session == null || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        Cache[BuildKey(token, deviceId)] = new CachedSession(session, session.UserId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Removes every cached entry for a device, regardless of token. Jellyfin keeps ONE
    /// session object per (app, device), so a session that died for one user of a shared
    /// Echo is dead for all of them; the dead-session signal (ResourceNotFoundException
    /// from a playback report) carries the device, not the token.
    /// </summary>
    /// <param name="deviceId">The Alexa device ID whose entries must be dropped.</param>
    public static void InvalidateDevice(string? deviceId)
    {
        if (string.IsNullOrEmpty(deviceId))
        {
            return;
        }

        string suffix = KeySeparator + deviceId;
        foreach (KeyValuePair<string, CachedSession> kvp in Cache)
        {
            if (kvp.Key.EndsWith(suffix, StringComparison.Ordinal))
            {
                // Value-conditional remove: takes only this exact entry, never a
                // concurrent Store's replacement (same discipline as the expiry path
                // and NextTrackPrecomputeCache, JF-424).
                Cache.TryRemove(kvp);
            }
        }
    }

    /// <summary>
    /// Clears all entries and resets test-stored timestamps. For test isolation only:
    /// the cache is process-wide static state shared by every handler.
    /// </summary>
    internal static void Reset() => Cache.Clear();

    /// <summary>
    /// Stores an entry with a caller-supplied store timestamp so tests can create an
    /// already-expired entry without waiting out the TTL. The expected UserId is still
    /// derived from the session's current stamp (the real Store behavior). For tests only.
    /// </summary>
    /// <param name="token">The user's Jellyfin access token.</param>
    /// <param name="deviceId">The Alexa device ID.</param>
    /// <param name="session">The live session reference.</param>
    /// <param name="storedAt">The (possibly past) store timestamp to pin.</param>
    internal static void StoreAtForTests(string token, string deviceId, SessionInfo session, DateTimeOffset storedAt)
        => Cache[BuildKey(token, deviceId)] = new CachedSession(session, session.UserId, storedAt);

    private static string BuildKey(string? token, string? deviceId) => string.Concat(token, KeySeparator, deviceId);

    /// <summary>
    /// A cached live session reference: the session, the UserId stamp it carried when
    /// stored (the read-side expectation), and the store timestamp for TTL evaluation.
    /// </summary>
    private sealed record CachedSession(SessionInfo Session, Guid ExpectedUserId, DateTimeOffset StoredAt);
}
