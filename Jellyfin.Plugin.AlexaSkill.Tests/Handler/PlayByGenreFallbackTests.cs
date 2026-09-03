#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-463: the genre slot is free-text (AMAZON.Genre in 16 locales,
/// AMAZON.SearchQuery in it-IT), so bare verb+title utterances can be stolen by
/// PlayByGenreIntent ("Reproduce abbey road" in es-ES/es-MX captures the title as
/// the genre). When the genre resolves to nothing, the handler must try the shared
/// cross-media artist fallback (TryEntityFallbackAsync, the same wiring
/// PlayMoodMusic uses) and play a confident artist match with the
/// FoundArtistInstead announcement; a resolved genre must never consult the
/// fallback at all.
/// </summary>
[Collection("Plugin")]
public class PlayByGenreFallbackTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    private PlayByGenreIntentHandler CreateHandler()
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);

    private static IntentRequest CreateGenreIntent(string genre, string locale = "es-ES")
    {
        var intent = new Intent { Name = IntentNames.PlayByGenre };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["genre"] = new Slot { Name = "genre", Value = genre }
        };
        return new IntentRequest { Intent = intent, Locale = locale, RequestId = "test-req" };
    }

    /// <summary>
    /// Mocks the library so the genre query misses (triggering the entity fallback),
    /// the artist SearchTerm query finds <paramref name="artistName"/>, and the artist
    /// songs query returns <paramref name="songs"/>.
    /// </summary>
    private void SetupGenreMissWithArtist(Guid artistId, string artistName, List<BaseItem> songs)
    {
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist);
                bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio);
                bool hasGenres = q.Genres != null && q.Genres.Count > 0;

                // Genre track search (Audio + Genres): the miss that arms the fallback.
                if (hasGenres && isAudioQuery)
                {
                    return new List<BaseItem>();
                }

                // Entity fallback: artist search via SearchTerm (no Genres).
                if (q.SearchTerm != null && isArtistQuery && !hasGenres)
                {
                    return new List<BaseItem> { new MusicArtist { Name = artistName, Id = artistId } };
                }

                // Artist songs (ArtistIds + Audio).
                if (q.ArtistIds != null && q.ArtistIds.Length > 0 && isAudioQuery)
                {
                    return songs;
                }

                return new List<BaseItem>();
            });
    }

    // AC#1: genre miss + artist exists plays the artist with the FoundArtistInstead
    // announcement naming the artist.
    [Fact]
    public async Task GenreMiss_ArtistExists_PlaysArtistWithAnnouncement()
    {
        _fx.SetupUserMock();
        var artistId = Guid.NewGuid();
        var songs = new List<BaseItem>
        {
            new Audio { Name = "Come Together", Id = Guid.NewGuid() },
            new Audio { Name = "Something", Id = Guid.NewGuid() }
        };
        SetupGenreMissWithArtist(artistId, "Abbey Road", songs);

        var handler = CreateHandler();
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("abbey road"), _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Equal(2, session.NowPlayingQueue.Count);
        string? speech = TestHelpers.GetSpeechTextOrNull(response);
        Assert.NotNull(speech);
        Assert.Contains("Abbey Road", speech, StringComparison.Ordinal);
    }

    // AC#2: genre miss + artist miss keeps the existing genre not-found speech and
    // never starts playback.
    [Fact]
    public async Task GenreMiss_NothingFound_ReturnsGenreNotFoundWithoutPlayback()
    {
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem>());

        var handler = CreateHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("abbey road"), _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.Null(TestHelpers.GetPlayDirective(response));
        string speech = TestHelpers.GetSpeechText(response);
        Assert.Contains("abbey road", speech, StringComparison.OrdinalIgnoreCase);
    }

    // AC#3: a genre the library can serve takes the normal genre path; the fallback
    // is never consulted (no MusicArtist query issued at all).
    [Fact]
    public async Task ResolvedGenre_PlaysGenre_NeverQueriesArtists()
    {
        _fx.SetupUserMock();
        var jazzSong = new Audio { Name = "So What", Id = Guid.NewGuid() };
        var queries = new List<InternalItemsQuery>();

        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                queries.Add(q);
                return new List<BaseItem> { jazzSong };
            });

        var handler = CreateHandler();
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("jazz"), _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        Assert.Single(session.NowPlayingQueue);

        var artistQueries = queries.Where(q => q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true).ToList();
        Assert.Empty(artistQueries);
    }

    // AC#4: with AnnounceCrossMediaSubstitution off the artist still plays, but no
    // FoundArtistInstead speech is emitted.
    [Fact]
    public async Task AnnouncementOff_ArtistStillPlaysSilently()
    {
        _fx.SetupUserMock();
        _fx.Config.AnnounceCrossMediaSubstitution = false;
        var artistId = Guid.NewGuid();
        var songs = new List<BaseItem> { new Audio { Name = "Come Together", Id = Guid.NewGuid() } };
        SetupGenreMissWithArtist(artistId, "Abbey Road", songs);

        var handler = CreateHandler();
        var session = _fx.CreateSession();
        SkillResponse response = await handler.HandleAsync(
            CreateGenreIntent("abbey road"), _fx.CreateContext(), _fx.CreateUser(), session, CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.NotNull(session.NowPlayingQueue);
        string? speech = TestHelpers.GetSpeechTextOrNull(response);
        Assert.True(string.IsNullOrWhiteSpace(speech) || !speech.Contains("Abbey Road", StringComparison.Ordinal));
    }
}
