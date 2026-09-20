using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Querying;

namespace Jellyfin.Plugin.AlexaSkill.Alexa.Util;

/// <summary>
/// The ONE latest-episode query for a matched podcast container, shared by every
/// entry point that resolves a podcast to its newest episode (JF-599): the
/// PlayPodcast intent handler, the disambiguation "yes" confirm (YesIntentHandler),
/// and the APL carousel tap (AplUserEventHandler). Podcasts live under two storage
/// shapes: a MusicAlbum of Audio tracks (the community plugins, direct children so
/// ParentId) or a Series of Episode items under season folders (the IlPost plugin,
/// the series is an ANCESTOR so AncestorIds). The series shape takes only the newest
/// episode (Limit 1, matching the TvNextUpService newest-episode cores) and skips
/// virtual placeholder episodes; the album shape keeps its pre-JF-599 form.
/// </summary>
public static class PodcastEpisodeResolver
{
    /// <summary>
    /// Gets a value indicating whether the matched podcast container is the Series
    /// storage shape (IlPost) rather than the MusicAlbum shape (community plugins).
    /// </summary>
    /// <param name="podcast">The matched podcast container item.</param>
    /// <returns>True when episodes must be resolved by ancestor, not parent.</returns>
    public static bool IsSeriesShape(BaseItem podcast)
        => podcast is MediaBrowser.Controller.Entities.TV.Series;

    /// <summary>
    /// Builds the newest-episode query for a matched podcast container, newest first.
    /// The caller wraps the <c>GetItemList</c> call in its own retry/timeout policy.
    /// </summary>
    /// <param name="podcast">The matched podcast container (MusicAlbum or Series).</param>
    /// <param name="jellyfinUser">The linked Jellyfin user (null skips user scoping, e.g. the APL tap path).</param>
    /// <returns>The query; pass it to <c>ILibraryManager.GetItemList</c>.</returns>
    public static InternalItemsQuery BuildLatestEpisodeQuery(BaseItem podcast, Jellyfin.Database.Implementations.Entities.User? jellyfinUser)
    {
        bool isSeriesShape = IsSeriesShape(podcast);
        var query = new InternalItemsQuery
        {
            User = jellyfinUser,
            Recursive = true,
            IncludeItemTypes = isSeriesShape
                ? new[] { BaseItemKind.Episode, BaseItemKind.Audio }
                : new[] { BaseItemKind.Audio },
            Limit = isSeriesShape ? 1 : null,
            IsVirtualItem = isSeriesShape ? false : null,
            OrderBy = new[] { (ItemSortBy.DateCreated, SortOrder.Descending) },
            DtoOptions = new DtoOptions(true)
        };
        if (isSeriesShape)
        {
            query.AncestorIds = new[] { podcast.Id };
        }
        else
        {
            query.ParentId = podcast.Id;
        }

        return query;
    }
}
