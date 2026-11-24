#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.ModelDeployment;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using AudioItem = MediaBrowser.Controller.Entities.Audio.Audio;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-960 media census tests with mocked Jellyfin services: the union scope across
/// linked users, the bounded Limit-1 query shape, the per-user library scoping with
/// the out-of-library exemption (LiveTvChannel), and every fail-open path.
/// </summary>
public class MediaCensusTests
{
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly List<InternalItemsQuery> _queries = new();

    public MediaCensusTests()
    {
        // The static top-parent cache keys on the configured library ids; the Guids
        // here are fresh per test so no stale entry can leak, but clear anyway.
        Jellyfin.Plugin.AlexaSkill.Alexa.Util.LibraryFilter.InvalidateCache();
    }

    private MediaCensus CreateCensus()
    {
        return new MediaCensus(
            _libraryManager.Object,
            _userManager.Object,
            NullLogger.Instance);
    }

    /// <summary>
    /// The mocked library: returns one row only for queries whose IncludeItemTypes
    /// contains Audio; every other family query comes back empty. Records every query.
    /// </summary>
    private void SetupMusicOnlyLibrary()
    {
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => _queries.Add(q))
            .Returns((InternalItemsQuery q) =>
                Array.Exists(q.IncludeItemTypes ?? Array.Empty<Jellyfin.Data.Enums.BaseItemKind>(), k => k == Jellyfin.Data.Enums.BaseItemKind.Audio)
                    ? new List<BaseItem> { new AudioItem { Id = Guid.NewGuid(), Name = "song" } }
                    : new List<BaseItem>());
    }

    [Fact]
    public void CountFamilies_MusicOnlyLibrary_CountsMusicAlone()
    {
        SetupMusicOnlyLibrary();
        var census = CreateCensus();

        var counts = census.CountFamilies(new List<User>());

        // No linked users: the union is empty, but the census fails open so the
        // caller never prunes on a degraded setup.
        Assert.Equal(1, counts[MediaFamily.Music]);
        Assert.Equal(1, counts[MediaFamily.Video]);
    }

    [Fact]
    public void CountFamilies_NoUsers_FailsOpen()
    {
        var counts = CreateCensus().CountFamilies(new List<User>());

        Assert.All(MediaCensus.AllFamiliesPresent().Keys, family => Assert.Equal(1, counts[family]));
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void CountFamilies_UnresolvableUser_FailsOpen()
    {
        var config = new PluginConfiguration();
        var pluginUser = new User { Id = Guid.NewGuid() };
        config.Users.Add(pluginUser);
        _userManager.Setup(u => u.GetUserById(pluginUser.Id)).Returns((Jellyfin.Database.Implementations.Entities.User?)null);

        var counts = CreateCensus().CountFamilies(config.Users);

        Assert.All(MediaCensus.AllFamiliesPresent().Keys, family => Assert.Equal(1, counts[family]));
        _libraryManager.Verify(l => l.GetItemList(It.IsAny<InternalItemsQuery>()), Times.Never);
    }

    [Fact]
    public void CountFamilies_ThrowingQuery_FailsOpen()
    {
        var pluginUser = new User { Id = Guid.NewGuid() };
        _userManager
            .Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(TestHelpers.CreateJellyfinUser(id: pluginUser.Id));
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Throws(new InvalidOperationException("library explosion"));

        var counts = CreateCensus().CountFamilies(new List<User> { pluginUser });

        Assert.All(MediaCensus.AllFamiliesPresent().Keys, family => Assert.Equal(1, counts[family]));
    }

    [Fact]
    public void CountFamilies_UnrestrictedUser_QueriesWithoutScope_MusicOnlyFound()
    {
        SetupMusicOnlyLibrary();
        var pluginUser = new User { Id = Guid.NewGuid() };
        _userManager
            .Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(TestHelpers.CreateJellyfinUser(id: pluginUser.Id));

        var counts = CreateCensus().CountFamilies(new List<User> { pluginUser });

        Assert.Equal(1, counts[MediaFamily.Music]);
        Assert.Equal(0, counts[MediaFamily.Video]);
        Assert.Equal(0, counts[MediaFamily.Audiobook]);
        Assert.Equal(0, counts[MediaFamily.Podcast]);
        Assert.Equal(0, counts[MediaFamily.LiveTv]);

        // Unrestricted: no query may carry a TopParentIds scope. The LiveTvChannel
        // queries run too (the handler mirrors the PlayChannel query shape), each
        // bounded to Limit 1.
        Assert.NotEmpty(_queries);
        Assert.All(_queries, q => Assert.Empty(q.TopParentIds));
        Assert.All(_queries, q => Assert.Equal(1, q.Limit));
        Assert.Contains(_queries, q => Array.Exists(q.IncludeItemTypes!, k => k == Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel));
    }

    [Fact]
    public void CountFamilies_RestrictedUser_ScopeApplied_OnLibraryKinds_ButNotLiveTv()
    {
        SetupMusicOnlyLibrary();
        Guid libraryId = Guid.NewGuid();
        var pluginUser = new User { Id = Guid.NewGuid(), AllowedLibraryIds = new List<string> { libraryId.ToString() } };
        _userManager
            .Setup(u => u.GetUserById(pluginUser.Id))
            .Returns(TestHelpers.CreateJellyfinUser(id: pluginUser.Id));
        // GetItemById returns null (not a CollectionFolder), so the resolved scope
        // keeps the configured id itself (the LibraryFilter defensive fallback).
        _libraryManager.Setup(l => l.GetItemById(libraryId)).Returns((BaseItem?)null);

        var counts = CreateCensus().CountFamilies(new List<User> { pluginUser });

        Assert.Equal(1, counts[MediaFamily.Music]);

        foreach (InternalItemsQuery query in _queries)
        {
            bool isLiveTv = Array.Exists(query.IncludeItemTypes!, k => k == Jellyfin.Data.Enums.BaseItemKind.LiveTvChannel);
            if (isLiveTv)
            {
                // Out-of-library exemption: the live-TV query mirrors the PlayChannel
                // serving query and stays unfiltered.
                Assert.Empty(query.TopParentIds);
            }
            else
            {
                Assert.Contains(libraryId, query.TopParentIds);
            }
        }
    }

    [Fact]
    public void CountFamilies_UnionAcrossUsers_SecondUserFindsTheFamily()
    {
        Guid libraryA = Guid.NewGuid();
        Guid libraryB = Guid.NewGuid();
        var userA = new User { Id = Guid.NewGuid(), AllowedLibraryIds = new List<string> { libraryA.ToString() } };
        var userB = new User { Id = Guid.NewGuid(), AllowedLibraryIds = new List<string> { libraryB.ToString() } };
        _userManager.Setup(u => u.GetUserById(userA.Id)).Returns(TestHelpers.CreateJellyfinUser(id: userA.Id));
        _userManager.Setup(u => u.GetUserById(userB.Id)).Returns(TestHelpers.CreateJellyfinUser(id: userB.Id));
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((BaseItem?)null);

        // Only user B's scope holds music; user A's queries find nothing.
        _libraryManager
            .Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback((InternalItemsQuery q) => _queries.Add(q))
            .Returns((InternalItemsQuery q) =>
                Array.Exists(q.IncludeItemTypes ?? Array.Empty<Jellyfin.Data.Enums.BaseItemKind>(), k => k == Jellyfin.Data.Enums.BaseItemKind.Audio)
                && q.TopParentIds.Contains(libraryB)
                    ? new List<BaseItem> { new AudioItem { Id = Guid.NewGuid(), Name = "song" } }
                    : new List<BaseItem>());

        var counts = CreateCensus().CountFamilies(new List<User> { userA, userB });

        // The UNION: user B's music makes the family present for the instance even
        // though user A's scope is empty of it.
        Assert.Equal(1, counts[MediaFamily.Music]);
        Assert.Equal(0, counts[MediaFamily.Video]);
        Assert.Contains(_queries, q => q.TopParentIds.Contains(libraryA));
        Assert.Contains(_queries, q => q.TopParentIds.Contains(libraryB));
    }
}
