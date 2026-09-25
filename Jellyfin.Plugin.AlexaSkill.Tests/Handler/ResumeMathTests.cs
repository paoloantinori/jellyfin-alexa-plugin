using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Moq;
using Xunit;
using JellyfinUser = Jellyfin.Database.Implementations.Entities.User;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-315 batch 2 characterization suite for the resume/position math members
/// (SortAndFindResumeIndex, FindResumeTrackIndex x2, GetAudiobookBookKey,
/// GetAudiobookStartTicks, FormatPosition, FormatTimeSpan, BuildPositionDisplay).
/// Written BEFORE the extraction and run green on the pre-refactor BaseHandler
/// code, then migrated with the move (receiver swap only); expectations pin
/// CURRENT behavior (including the rough edges: "1 hours" grammar, all-played
/// lists restarting at index 0) and were NOT edited by the move.
/// </summary>
[Collection("Plugin")]
public class ResumeMathTests : IDisposable
{
    private readonly HandlerTestFixture _fx = new();
    private readonly Dictionary<BaseItem, UserItemData?> _userData = new();

    public ResumeMathTests()
    {
        TestHelpers.EnsurePluginInstance(_fx.Config, _fx.LoggerFactory, c => { }, "alexa-resume-math-test");
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<JellyfinUser>(), It.IsAny<BaseItem>()))
            .Returns((JellyfinUser _, BaseItem i) => _userData.TryGetValue(i, out UserItemData? d) ? d : null);
    }

    public void Dispose() => _fx.LoggerFactory.Dispose();

    // ---- FormatPosition: compact card/display formatting, current behavior pinned ----

    [Theory]
    [InlineData(0, 0, "0s")]
    [InlineData(0, 30, "30s")]
    [InlineData(59, 59, "59m 59s")] // hours < 1, minutes >= 1
    [InlineData(90, 0, "1h 30m")] // hours >= 1
    [InlineData(125, 9, "2h 5m")] // seconds dropped on the hours branch
    [InlineData(60, 0, "1h 0m")]
    public void FormatPosition_Branches(long minutes, int seconds, string expected)
        => Assert.Equal(expected, ResumeMath.FormatPosition(TimeSpan.FromMinutes(minutes).Add(TimeSpan.FromSeconds(seconds)).Ticks));

    // ---- FormatTimeSpan: locale-template voice formatting (InvariantCulture) ----

    [Fact]
    public void FormatTimeSpan_Hours_UsesLocaleTemplate()
    {
        Assert.Equal("1 hours and 30 minutes", ResumeMath.FormatTimeSpan(TimeSpan.FromMinutes(90), "en-US"));
        Assert.Equal("1 ore e 30 minuti", ResumeMath.FormatTimeSpan(TimeSpan.FromMinutes(90), "it-IT"));
    }

    [Fact]
    public void FormatTimeSpan_MinutesSeconds_UsesLocaleTemplate()
        => Assert.Equal("45 minutes and 12 seconds", ResumeMath.FormatTimeSpan(TimeSpan.FromMinutes(45).Add(TimeSpan.FromSeconds(12)), "en-US"));

    [Fact]
    public void FormatTimeSpan_SecondsOnly_IncludingZero()
    {
        Assert.Equal("30 seconds", ResumeMath.FormatTimeSpan(TimeSpan.FromSeconds(30), "en-US"));
        Assert.Equal("0 seconds", ResumeMath.FormatTimeSpan(TimeSpan.Zero, "en-US"));
    }

    // ---- BuildPositionDisplay: position string from session state ----

    [Fact]
    public void BuildPositionDisplay_NoPlayState_Empty()
    {
        var session = _fx.CreateSession();
        session.PlayState = null!;

        Assert.Equal(string.Empty, ResumeMath.BuildPositionDisplay(session, "en-US"));
    }

    [Fact]
    public void BuildPositionDisplay_ZeroPosition_Empty()
    {
        var session = _fx.CreateSession();
        session.PlayState = new PlayerStateInfo { PositionTicks = 0 };

        Assert.Equal(string.Empty, ResumeMath.BuildPositionDisplay(session, "en-US"));
    }

    [Fact]
    public void BuildPositionDisplay_PositionWithoutRuntime_PositionOnly()
    {
        var session = _fx.CreateSession();
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(5).Ticks };
        session.NowPlayingItem = null;

        Assert.Equal("5 minutes and 0 seconds", ResumeMath.BuildPositionDisplay(session, "en-US"));
    }

    [Fact]
    public void BuildPositionDisplay_PositionWithRuntime_PositionOfTotal()
    {
        var session = _fx.CreateSession();
        session.PlayState = new PlayerStateInfo { PositionTicks = TimeSpan.FromMinutes(30).Ticks };
        session.NowPlayingItem = new BaseItemDto { RunTimeTicks = TimeSpan.FromHours(2).Ticks };

        Assert.Equal(
            "30 minutes and 0 seconds of 2 hours and 0 minutes",
            ResumeMath.BuildPositionDisplay(session, "en-US"));
    }

    // ---- GetAudiobookBookKey: parent-folder key with self fallback ----

    [Fact]
    public void GetAudiobookBookKey_ParentFolderIdWins()
    {
        Guid folder = Guid.NewGuid();
        var chapter = new AudioBook { Name = "Chapter 1", Id = Guid.NewGuid(), ParentId = folder };

        Assert.Equal(folder.ToString("N"), ResumeMath.GetAudiobookBookKey(chapter));
    }

    [Fact]
    public void GetAudiobookBookKey_NoParent_FallsBackToSelfId()
    {
        Guid id = Guid.NewGuid();
        var single = new Audio { Name = "Single File Book", Id = id, ParentId = Guid.Empty };

        Assert.Equal(id.ToString("N"), ResumeMath.GetAudiobookBookKey(single));
    }

    // ---- GetAudiobookStartTicks: tracker position first, fallback second (JF-563 order) ----

    [Fact]
    public void GetAudiobookStartTicks_ColdTracker_ReturnsFallback()
    {
        long fallback = TimeSpan.FromMinutes(2).Ticks;

        Assert.Equal(fallback, ResumeMath.GetAudiobookStartTicks(Guid.NewGuid().ToString("N"), fallback));
    }

    [Fact]
    public void GetAudiobookStartTicks_TrackedPositionBeatsFallback()
    {
        var tracker = TestHelpers.CreatePositionTracker("resume-math");
        using var trackerSwap = TestHelpers.SwapPluginPositionTracker(tracker);

        string key = Guid.NewGuid().ToString("N");
        // Segment 31 -> conservative (31-1)*10s = 5 min, must beat the 2 min fallback.
        tracker.RecordSegment(key, 31);

        Assert.Equal(
            TimeSpan.FromMinutes(5).Ticks,
            ResumeMath.GetAudiobookStartTicks(key, TimeSpan.FromMinutes(2).Ticks));
    }

    // ---- SortAndFindResumeIndex: single-pass favorites/rating sort + resume detection ----

    [Fact]
    public void SortAndFindResumeIndex_EmptyList_NoResume()
    {
        var (sorted, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem>(), TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        Assert.Empty(sorted);
        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_SingleInProgress_ResumeTicks()
    {
        var track = new Audio { Name = "Only", Id = Guid.NewGuid() };
        long progress = TimeSpan.FromMinutes(5).Ticks;
        StubUserData(track, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (sorted, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { track }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        Assert.Same(track, Assert.Single(sorted));
        Assert.Equal(0, index);
        Assert.Equal(progress, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_SingleInProgress_ResumeDisabled_ZeroTicks()
    {
        var track = new Audio { Name = "Only", Id = Guid.NewGuid() };
        StubUserData(track, new UserItemData { Key = "k", PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks, Played = false });

        var (_, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { track }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: false);

        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_PlayedItemWithPosition_IsNotInProgress()
    {
        var track = new Audio { Name = "Only", Id = Guid.NewGuid() };
        StubUserData(track, new UserItemData { Key = "k", PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks, Played = true });

        var (_, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { track }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_InProgressTrackWins()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        var t2 = new Audio { Name = "C", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        long progress = TimeSpan.FromMinutes(3).Ticks;
        StubUserData(t1, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (sorted, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { t0, t1, t2 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        // No ratings anywhere: order is untouched.
        Assert.Equal(new[] { t0, t1, t2 }, sorted);
        Assert.Equal(1, index);
        Assert.Equal(progress, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_AllPlayed_RestartsFromBeginning()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        StubUserData(t1, new UserItemData { Key = "k", Played = true });

        var (_, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { t0, t1 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        // Last played is the final item: lastPlayed+1 is out of range, so index 0.
        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_PartiallyPlayed_ResumesAfterLastPlayed()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        var t2 = new Audio { Name = "C", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        StubUserData(t1, new UserItemData { Key = "k", Played = true });

        var (sorted, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { t0, t1, t2 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        Assert.Equal(new[] { t0, t1, t2 }, sorted);
        Assert.Equal(2, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void SortAndFindResumeIndex_RatingSort_MapsResumeIndexThroughSortedOrder()
    {
        var plainRated = new Audio { Name = "A", Id = Guid.NewGuid() };
        var favorite = new Audio { Name = "B", Id = Guid.NewGuid() };
        var inProgress = new Audio { Name = "C", Id = Guid.NewGuid() };
        StubUserData(plainRated, new UserItemData { Key = "k", Rating = 3.0 });
        StubUserData(favorite, new UserItemData { Key = "k", Rating = 1.0, IsFavorite = true });
        long progress = TimeSpan.FromMinutes(7).Ticks;
        StubUserData(inProgress, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (sorted, index, ticks) = ResumeMath.SortAndFindResumeIndex(
            new List<BaseItem> { plainRated, favorite, inProgress }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        // Favorites first, then rating desc; the in-progress item lands last and the
        // resume index follows it into the sorted list.
        Assert.Equal(new[] { favorite, plainRated, inProgress }, sorted);
        Assert.Equal(2, index);
        Assert.Equal(progress, ticks);
    }

    // ---- FindResumeTrackIndex (both overloads) ----

    [Fact]
    public void FindResumeTrackIndex_ShortOverload_UserDataInProgress()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        long progress = TimeSpan.FromMinutes(4).Ticks;
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        StubUserData(t1, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (index, ticks) = ResumeMath.FindResumeTrackIndex(
            new List<BaseItem> { t0, t1 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: true);

        Assert.Equal(1, index);
        Assert.Equal(progress, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_ShortOverload_ResumeDisabled_ZeroTicks()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", PlaybackPositionTicks = TimeSpan.FromMinutes(4).Ticks, Played = false });

        var (index, ticks) = ResumeMath.FindResumeTrackIndex(
            new List<BaseItem> { t0 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, resumePosition: false);

        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_FullOverload_NoQueue_FallsBackToUserData()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        long progress = TimeSpan.FromMinutes(4).Ticks;
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        StubUserData(t1, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (index, ticks) = ResumeMath.FindResumeTrackIndex(
            new List<BaseItem> { t0, t1 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, null, null, resumePosition: true);

        Assert.Equal(1, index);
        Assert.Equal(progress, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_FullOverload_AllPlayedNoQueueData_StartsAtBeginning()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };

        var (index, ticks) = ResumeMath.FindResumeTrackIndex(
            new List<BaseItem> { t0 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, null, null, resumePosition: true);

        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_ItemPositionStateBeatsUserDataForSameTrack()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        // Server-side UserData says played with no position; the device queue's
        // ItemPositionState (recorded by HLS segment requests) must win for this track.
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        long cached = TimeSpan.FromMinutes(6).Ticks;

        var (index, ticks) = WithDeviceQueue("resume-math-idx", qm =>
        {
            var queue = qm.GetOrCreateQueue("test-device");
            queue.ItemPositionState[t0.Id.ToString("N")] = cached;
            return ResumeMath.FindResumeTrackIndex(
                new List<BaseItem> { t0 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, qm, "test-device", resumePosition: true);
        });

        Assert.Equal(0, index);
        Assert.Equal(cached, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_ItemPositionState_ResumeDisabled_ZeroTicks()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        long cached = TimeSpan.FromMinutes(6).Ticks;

        var (index, ticks) = WithDeviceQueue("resume-math-idx2", qm =>
        {
            var queue = qm.GetOrCreateQueue("test-device");
            queue.ItemPositionState[t0.Id.ToString("N")] = cached;
            return ResumeMath.FindResumeTrackIndex(
                new List<BaseItem> { t0 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, qm, "test-device", resumePosition: false);
        });

        Assert.Equal(0, index);
        Assert.Equal(0, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_UserDataInProgressOnEarlierTrack_WinsOverLaterQueueEntry()
    {
        // Per-item loop order: track 0's UserData in-progress state returns before the
        // loop ever reaches track 1's queue-cached position. Pins that ordering.
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        long progress = TimeSpan.FromMinutes(2).Ticks;
        StubUserData(t0, new UserItemData { Key = "k", PlaybackPositionTicks = progress, Played = false });

        var (index, ticks) = WithDeviceQueue("resume-math-idx3", qm =>
        {
            var queue = qm.GetOrCreateQueue("test-device");
            queue.ItemPositionState[t1.Id.ToString("N")] = TimeSpan.FromMinutes(9).Ticks;
            return ResumeMath.FindResumeTrackIndex(
                new List<BaseItem> { t0, t1 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, qm, "test-device", resumePosition: true);
        });

        Assert.Equal(0, index);
        Assert.Equal(progress, ticks);
    }

    [Fact]
    public void FindResumeTrackIndex_AllPlayed_ResumesAfterLastPlayed()
    {
        var t0 = new Audio { Name = "A", Id = Guid.NewGuid() };
        var t1 = new Audio { Name = "B", Id = Guid.NewGuid() };
        var t2 = new Audio { Name = "C", Id = Guid.NewGuid() };
        StubUserData(t0, new UserItemData { Key = "k", Played = true });
        StubUserData(t1, new UserItemData { Key = "k", Played = true });

        var (index, ticks) = ResumeMath.FindResumeTrackIndex(
            new List<BaseItem> { t0, t1, t2 }, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object, null, null, resumePosition: true);

        Assert.Equal(2, index);
        Assert.Equal(0, ticks);
    }

    // ---- FavoritesAndRatingsFirst (JF-315 batch 10; joins ResumeMath with the
    // JF-570 SortByRating consolidation). No direct coverage existed before: these
    // facts were written green on the pre-move BaseHandler code via a temporary
    // probe subclass and retargeted to the static home after the move (zero
    // expectation edits). ----

    [Fact]
    public void FavoritesAndRatingsFirst_NoRatings_ReturnsTheOriginalInstanceUnsorted()
    {
        var t0 = TestHelpers.CreateSong("A");
        var t1 = TestHelpers.CreateSong("B");
        StubUserData(t0, new UserItemData { Key = "k", IsFavorite = true }); // favorite but unrated
        StubUserData(t1, null);

        IReadOnlyList<BaseItem> input = new List<BaseItem> { t0, t1 };

        // No rating anywhere: the passthrough returns the SAME list instance.
        Assert.Same(input, ResumeMath.FavoritesAndRatingsFirst(input, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object));
    }

    [Fact]
    public void FavoritesAndRatingsFirst_SingleItem_PassesThrough()
    {
        var t0 = TestHelpers.CreateSong("Only");

        IReadOnlyList<BaseItem> input = new List<BaseItem> { t0 };

        Assert.Same(input, ResumeMath.FavoritesAndRatingsFirst(input, TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object));
    }

    [Fact]
    public void FavoritesAndRatingsFirst_FavoritesFirst_RatingDescWithinGroups()
    {
        var favLow = TestHelpers.CreateSong("favLow");
        var favHigh = TestHelpers.CreateSong("favHigh");
        var restLow = TestHelpers.CreateSong("restLow");
        var restHigh = TestHelpers.CreateSong("restHigh");
        // Insertion order is the opposite of the expected output on both axes.
        StubUserData(favLow, new UserItemData { Key = "k", IsFavorite = true, Rating = 3 });
        StubUserData(favHigh, new UserItemData { Key = "k", IsFavorite = true, Rating = 9 });
        StubUserData(restLow, new UserItemData { Key = "k", Rating = 4 });
        StubUserData(restHigh, new UserItemData { Key = "k", Rating = 8 });

        IReadOnlyList<BaseItem> result = ResumeMath.FavoritesAndRatingsFirst(
            new List<BaseItem> { favLow, favHigh, restLow, restHigh },
            TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object);

        Assert.Equal(new[] { favHigh, favLow, restHigh, restLow }, result);
    }

    [Fact]
    public void FavoritesAndRatingsFirst_RatingTies_KeepOriginalRelativeOrder()
    {
        var first = TestHelpers.CreateSong("first");
        var second = TestHelpers.CreateSong("second");
        StubUserData(first, new UserItemData { Key = "k", Rating = 7 });
        StubUserData(second, new UserItemData { Key = "k", Rating = 7 });

        // Equal ratings: the stable ThenBy(Index) keeps the original relative order.
        IReadOnlyList<BaseItem> result = ResumeMath.FavoritesAndRatingsFirst(
            new List<BaseItem> { first, second },
            TestHelpers.CreateJellyfinUser(), _fx.UserDataManager.Object);

        Assert.Equal(new[] { first, second }, result);
    }

    /// <summary>
    /// Create/seed/dispose scaffold for the queue-seeded tests (own temp dir per
    /// test per the JF-540 isolation rule).
    /// </summary>
    private T WithDeviceQueue<T>(string suffix, Func<Jellyfin.Plugin.AlexaSkill.Alexa.Playback.DeviceQueueManager, T> body)
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager(suffix);
        try
        {
            return body(queueManager);
        }
        finally
        {
            queueManager.Dispose();
        }
    }

    private void StubUserData(BaseItem item, UserItemData? data)
    {
        // One catch-all Setup in the ctor reads this map; Moq keeps only the LAST
        // matching Setup, so per-item Setups here would silently shadow each other.
        _userData[item] = data;
    }
}
