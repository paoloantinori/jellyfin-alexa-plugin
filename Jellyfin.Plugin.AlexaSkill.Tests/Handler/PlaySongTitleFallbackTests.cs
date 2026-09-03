#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-383 generalization: PlaySong's exact SearchTerm query misses abbreviated tagged
/// titles ("decatur street" vs "Decatur St."). On exact miss it must fall back to
/// (a) the artist's own songs scored by KeywordMatcher (when a musician slot is present;
/// bounded by one artist's catalog, safe for the Alexa time budget), or (b) the n-gram
/// index (when no musician is present; O(1) lookup, canonicalizes abbreviations).
/// </summary>
[Collection("Plugin")]
public class PlaySongTitleFallbackTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlaySongIntentHandler CreateSongHandler(ISongNgramIndex? ngramIndex = null)
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory,
            songNgramIndex: ngramIndex);
    }

    private static IntentRequest CreateSongIntent(string song, string? musician = null)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    // With a musician slot: exact SearchTerm misses ("decatur street" vs "Decatur St."),
    // but the artist's own songs contain the track. The fallback must fetch the artist's
    // songs WITHOUT the name pre-filter and let KeywordMatcher (with abbreviation
    // canonicalization) match, instead of returning "song not found".
    [Fact]
    public async Task PlaySong_ExactMiss_WithMusician_FallsBackToArtistSongsScored()
    {
        _fx.SetupUserMock();
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "Decatur St.", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Artist search: returns the artist
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "The Twilight Singers", Id = artistId } };
                }

                // Exact song search (SearchTerm): misses
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                // Artist songs (ArtistIds + Audio, no name pre-filter): the real track
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("decatur street", "twilight singers");

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    // Without a musician slot: exact SearchTerm misses; the n-gram index (which
    // canonicalizes abbreviations) finds the track. O(1) lookup, no full-catalog scan.

    // JF-384: the live repro verbatim. "Decature Street" arrives as "the cater street"
    // (accent drift on one word). Exact keyword match vetoes (100% coverage); the phonetic
    // stage on the artist's songs finds "Decatur St." via the un-drifted "street".
    [Fact]
    public async Task PlaySong_ExactMiss_AccentDriftOnOneWord_PhoneticStageFinds()
    {
        _fx.SetupUserMock();
        var artistId = Guid.NewGuid();
        var song = new Audio { Name = "Decatur St.", Id = Guid.NewGuid() };

        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                if (q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.MusicArtist))
                {
                    return new List<BaseItem> { new MusicArtist { Name = "The Twilight Singers", Id = artistId } };
                }

                // Exact song search (SearchTerm + ArtistIds): misses
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                // Artist-songs fallback (ArtistIds + Audio, no SearchTerm): the real track
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && q.SearchTerm == null
                    && q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio))
                {
                    return new List<BaseItem> { song };
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler();
        var request = CreateSongIntent("the cater street", "twilight singers");

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }

    [Fact]
    public async Task PlaySong_ExactMiss_NoMusician_UsesNgramIndex()
    {
        _fx.SetupUserMock();
        var song = new Audio { Name = "Decatur St.", Id = Guid.NewGuid() };
        var fakeIndex = new TestHelpers.FakeSongIndex((song, 100.0));

        _fx.LibraryManager.Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                // Exact song search misses
                if (q.SearchTerm != null && q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Audio))
                {
                    return new List<BaseItem>();
                }

                return new List<BaseItem>();
            });

        var handler = CreateSongHandler(fakeIndex);
        var request = CreateSongIntent("decatur street"); // no musician slot

        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
    }
}
