#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;

/// <summary>
/// The JF-960 media census: when <see cref="Configuration.PluginConfiguration.MediaProfiledModels"/>
/// is on, counts per media family across the libraries the plugin's LINKED USERS can
/// access, so the rebuild can prune intents whose family is absent on the instance.
/// SCOPE (the JF-960 design answer): the model is per-skill while users differ, so the
/// census is the per-instance UNION of the linked users' scopes - a family is present
/// when ANY linked user can reach one of its kinds.
/// BOUNDED: one Limit-1 existence query per family per user (counts, not enumerations).
/// FAIL-OPEN: every abnormal path (no linked users, an unresolvable Jellyfin user id, a
/// throwing query) returns "all families present", because a degraded census must never
/// prune intents (over-pruning breaks users; under-pruning is safe).
/// </summary>
public class MediaCensus
{
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediaCensus"/> class.
    /// </summary>
    /// <param name="libraryManager">Jellyfin library manager (the queries mirror the serving handlers' shape).</param>
    /// <param name="userManager">Jellyfin user manager (resolves linked plugin users).</param>
    /// <param name="logger">Logger.</param>
    public MediaCensus(ILibraryManager libraryManager, IUserManager userManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>
    /// The fail-open answer: every family present, so the caller prunes nothing.
    /// Derived from <see cref="IntentMediaFamilies.FamilyKinds"/> (the ONE family
    /// definition), so a family added there appears here automatically instead of a
    /// stale hand-enumerated row keying a silent mismatch.
    /// </summary>
    public static IReadOnlyDictionary<MediaFamily, int> AllFamiliesPresent()
    {
        return IntentMediaFamilies.FamilyKinds.ToDictionary(pair => pair.Key, _ => 1);
    }

    /// <summary>
    /// Counts the media families over the union of the linked users' library scopes.
    /// See the class doc for the scope, boundedness, and fail-open contracts.
    /// </summary>
    /// <param name="pluginUsers">The plugin's linked users (configuration.Users snapshot).</param>
    /// <returns>Family name to presence count (0 = absent on the instance, 1 = present).</returns>
    public IReadOnlyDictionary<MediaFamily, int> CountFamilies(IReadOnlyList<User> pluginUsers)
    {
        try
        {
            if (pluginUsers.Count == 0)
            {
                _logger.LogWarning(
                    "MediaCensus: no linked users configured; failing open with all families present (JF-960)");
                return AllFamiliesPresent();
            }

            // Resolve every plugin user to its Jellyfin user up front. A single
            // unresolvable id (a stale config row for a deleted user) degrades the
            // union, so the whole census fails open instead of silently dropping that
            // user's libraries out of the profile (the warning names the row to clean).
            var resolved = new List<(Entities.User PluginUser, Jellyfin.Database.Implementations.Entities.User JellyfinUser)>();
            foreach (User pluginUser in pluginUsers)
            {
                var jellyfinUser = _userManager.GetUserById(pluginUser.Id);
                if (jellyfinUser == null)
                {
                    _logger.LogWarning(
                        "MediaCensus: linked user id {UserId} does not resolve to a Jellyfin user (stale config row?); failing open with all families present (JF-960)",
                        pluginUser.Id);
                    return AllFamiliesPresent();
                }

                resolved.Add((pluginUser, jellyfinUser));
            }

            // Derived from FamilyKinds (the ONE family definition), never hand-enumerated:
            // a family added to the table joins the census automatically instead of a
            // missing zeroed row KeyNotFoundException-ing into the fail-open catch.
            Dictionary<MediaFamily, int> counts =
                IntentMediaFamilies.FamilyKinds.ToDictionary(pair => pair.Key, _ => 0);

            foreach (var (pluginUser, jellyfinUser) in resolved)
            {
                // The per-user scope resolution mirrors the serving handlers exactly
                // (LibraryFilter.ResolveForUser: CollectionFolder ids resolved to
                // physical top parents; null = unrestricted user).
                Guid[]? scope = LibraryFilter.ResolveForUser(pluginUser, _libraryManager, _logger);

                foreach (MediaFamily family in IntentMediaFamilies.FamilyKinds.Keys)
                {
                    // Union semantics: a family stays present once ANY user proved it.
                    if (counts[family] > 0)
                    {
                        continue;
                    }

                    // Per-family failure isolation: one throwing probe (a transient
                    // SQLite busy at startup) degrades ONLY its own family, which then
                    // reports PRESENT so its intents are kept; the other families keep
                    // profiling. A family-wide failure here is the fail-open direction.
                    try
                    {
                        if (IsFamilyPresent(family, jellyfinUser, scope))
                        {
                            counts[family] = 1;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "MediaCensus: {Family} probe failed; reporting the family present so its intents are kept, other families keep profiling (JF-960)",
                            IntentMediaFamilies.DisplayName(family));
                        counts[family] = 1;
                    }
                }
            }

            _logger.LogInformation(
                "MediaCensus (JF-960): users={UserCount}, {Counts}",
                resolved.Count,
                string.Join(" ", counts.OrderBy(pair => pair.Key)
                    .Select(pair => $"{IntentMediaFamilies.DisplayName(pair.Key)}={pair.Value}")));

            return counts;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MediaCensus failed; failing open with all families present so no intent is pruned (JF-960)");
            return AllFamiliesPresent();
        }
    }

    /// <summary>
    /// One bounded existence query for one family, shaped exactly like the serving
    /// handlers' queries (Recursive, User, IncludeItemTypes, DtoOptions, the shared
    /// LibraryFilter scope application with its out-of-library exemption for
    /// LiveTvChannel). Limit 1: the query answers presence, never enumerates.
    /// </summary>
    /// <param name="family">The family to probe.</param>
    /// <param name="jellyfinUser">The Jellyfin user for the query.</param>
    /// <param name="scope">The user's resolved top-parent scope, or null when unrestricted.</param>
    /// <returns>True when at least one item of the family's kinds is reachable.</returns>
    private bool IsFamilyPresent(MediaFamily family, Jellyfin.Database.Implementations.Entities.User jellyfinUser, Guid[]? scope)
    {
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = IntentMediaFamilies.FamilyKinds[family],
            Limit = 1,
            DtoOptions = new DtoOptions(true)
        };

        // The pre-resolved overload mirrors ApplyLibraryFilter's semantics: a null scope
        // (unrestricted user) leaves the query unfiltered, and the all-out-of-library
        // exemption (LiveTvChannel) skips the scope entirely, exactly like the
        // PlayChannel/PlayRadio serving queries.
        LibraryFilter.ApplyLibraryFilter(query, scope);

        IReadOnlyList<BaseItem> rows = _libraryManager.GetItemList(query);
        return rows.Count > 0;
    }
}
