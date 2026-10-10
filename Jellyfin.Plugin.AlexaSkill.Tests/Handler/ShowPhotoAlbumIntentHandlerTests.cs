using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using global::Alexa.NET;
using global::Alexa.NET.Request;
using global::Alexa.NET.Request.Type;
using global::Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Alexa.NET.Assertions;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// Handler-level pins for ShowPhotoAlbumIntent (JF-329): the audio-only
/// capability refusal, the visuals-off feature gate, album resolution
/// (incl. per-user library gating on every query), the empty-slot
/// recent-photos default, and the slideshow directive shape on the response.
/// </summary>
[Collection("Plugin")]
public class ShowPhotoAlbumIntentHandlerTests : PluginTestBase
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<InternalItemsQuery> _capturedQueries;
    private readonly Queue<IReadOnlyList<BaseItem>> _results;

    public ShowPhotoAlbumIntentHandlerTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userManagerMock
            .Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "http://localhost:8096");
        _loggerFactory = LoggerFactory.Create(b => { });
        _capturedQueries = new List<InternalItemsQuery>();
        _results = new Queue<IReadOnlyList<BaseItem>>();
        // The ONE GetItemList setup: captures every query and drains the
        // per-test result queue (an empty queue answers empty lists).
        _libraryManagerMock
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => _capturedQueries.Add(q))
            .Returns(() => _results.Count > 0 ? _results.Dequeue() : new List<BaseItem>());
    }

    private ShowPhotoAlbumIntentHandler CreateHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _loggerFactory);

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);

    private static IntentRequest CreateRequest(string? album = null)
    {
        var slots = new Dictionary<string, Slot>();
        if (album != null)
        {
            slots["photoAlbum"] = new Slot { Value = album };
        }

        return new IntentRequest
        {
            Intent = new Intent { Name = "ShowPhotoAlbumIntent", Slots = slots }
        };
    }

    private static Context AplContext() => TestHelpers.CreateContextWithApl();

    private static Context AudioOnlyContext() => TestHelpers.CreateTestContext();

    private static PhotoAlbum CreateAlbum(string name)
        => new() { Name = name, Id = Guid.NewGuid() };

    private static List<BaseItem> CreatePhotos(int count)
        => Enumerable.Range(0, count)
            .Select(i => new Photo { Name = $"photo{i}", Id = Guid.NewGuid() })
            .ToList<BaseItem>();

    private void QueueResults(params IReadOnlyList<BaseItem>[] results)
    {
        foreach (IReadOnlyList<BaseItem> result in results)
        {
            _results.Enqueue(result);
        }
    }

    private static AplRenderDocumentDirective? GetSlideshowDirective(SkillResponse response)
        => response.Response.Directives.OfType<AplRenderDocumentDirective>().FirstOrDefault();

    [Theory]
    [InlineData("ShowPhotoAlbumIntent", true)]
    [InlineData("PlaySongIntent", false)]
    [InlineData("AMAZON.PauseIntent", false)]
    public void CanHandle_ReturnsExpected(string intentName, bool expected)
    {
        var handler = CreateHandler();
        var request = new IntentRequest { Intent = new Intent { Name = intentName } };

        Assert.Equal(expected, handler.CanHandle(request));
    }

    [Fact]
    public void CanHandle_NonIntentRequest_ReturnsFalse()
    {
        var handler = CreateHandler();
        Assert.False(handler.CanHandle(new LaunchRequest()));
    }

    [Fact]
    public async Task Handle_AudioOnlyDevice_TellsScreenRequired()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("roma"), AudioOnlyContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("Echo Show", speech.Text);
        Assert.Empty(response.Response.Directives);
        Assert.Empty(_capturedQueries);
    }

    [Fact]
    public async Task Handle_VisualsDisabled_TellsFeatureDisabled()
    {
        // EnsurePluginInstance drops the syncFlag on the CREATION path, so the
        // flag is set on the config instance BEFORE construction (the plugin's
        // Configuration IS this instance) and re-asserted via the flag branch.
        _config.AplVisualsEnabled = false;
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory, c => c.AplVisualsEnabled = false, "jf329-photo-tests");
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("roma"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Equal(ResponseStringsGet("FeatureDisabled"), speech.Text);
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task Handle_AlbumFound_RendersSlideshowDirective()
    {
        var album = CreateAlbum("Roman Holiday");
        QueueResults(new List<BaseItem> { album }, CreatePhotos(4));
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("roman holiday"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("Roman Holiday", speech.Text);
        var directive = GetSlideshowDirective(response);
        Assert.NotNull(directive);
        Assert.Equal("slideshow", directive!.Token);
        string dsStr = directive.DataSources!.ToString();
        Assert.Contains("\"title\": \"Roman Holiday\"", dsStr);
        // Four bound image URLs, each the GetImageUrl shape (server + primary image).
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(dsStr, "/Images/Primary").Count);

        // The photos query is scoped to the resolved album, newest-to-oldest
        // only for the library default; the album walk is chronological.
        InternalItemsQuery photosQuery = _capturedQueries[^1];
        Assert.Equal(album.Id, photosQuery.ParentId);
        Assert.Equal((ItemSortBy.DateCreated, SortOrder.Ascending), photosQuery.OrderBy[0]);
    }

    [Fact]
    public async Task Handle_AlbumNotFound_TellsNotFound()
    {
        QueueResults(new List<BaseItem>(), new List<BaseItem>());
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("atlantis"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("atlantis", speech.Text);
        Assert.Null(GetSlideshowDirective(response));
    }

    [Fact]
    public async Task Handle_MultipleAlbums_PicksBestFuzzyMatch()
    {
        var albums = new List<BaseItem> { CreateAlbum("Roman Holiday"), CreateAlbum("Holiday in Rome") };
        QueueResults(albums, CreatePhotos(2));
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("roman holiday"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("Roman Holiday", speech.Text);
        Assert.NotNull(GetSlideshowDirective(response));
        InternalItemsQuery photosQuery = _capturedQueries[^1];
        Assert.Equal(albums[0].Id, photosQuery.ParentId);
    }

    [Fact]
    public async Task Handle_MultipleAlbums_BelowFuzzyBar_ShowsTopRankedMatch()
    {
        // Code-review F4: with SearchTerm matches in hand but none clearing the
        // fuzzy bar, the honest answer is the top-ranked match (announced by
        // name), never a not-found that contradicts the query's own matches.
        var albums = new List<BaseItem> { CreateAlbum("Asdf Album"), CreateAlbum("Zxcv Album") };
        QueueResults(albums, CreatePhotos(2));
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("qwertyuiop"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("Asdf Album", speech.Text);
        var directive = GetSlideshowDirective(response);
        Assert.NotNull(directive);
        Assert.Equal(albums[0].Id, _capturedQueries[^1].ParentId);
    }

    [Fact]
    public async Task Handle_EmptySlot_ShowsRecentPhotos()
    {
        QueueResults(CreatePhotos(5));
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest(), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("5", speech.Text);
        var directive = GetSlideshowDirective(response);
        Assert.NotNull(directive);
        Assert.Contains($"\"title\": \"{ResponseStringsGet("RecentPhotosTitle")}\"", directive!.DataSources!.ToString());

        // The default path issues exactly ONE query: unscoped (no ParentId),
        // newest first, capped at the slideshow bound.
        InternalItemsQuery q = Assert.Single(_capturedQueries);
        Assert.Equal(default, q.ParentId);
        Assert.Equal((ItemSortBy.DateCreated, SortOrder.Descending), q.OrderBy[0]);
        Assert.Equal(ShowPhotoAlbumIntentHandler.MaxSlideshowImages, q.Limit);
    }

    [Fact]
    public async Task Handle_PhotoLibraryEmpty_TellsNoPhotos()
    {
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest(), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Equal(ResponseStringsGet("NoPhotosFound"), speech.Text);
        Assert.Null(GetSlideshowDirective(response));
    }

    [Fact]
    public async Task Handle_AlbumWithoutPhotos_TellsAlbumEmpty()
    {
        QueueResults(new List<BaseItem> { CreateAlbum("Empty Trip") }, new List<BaseItem>());
        var handler = CreateHandler();

        var response = await handler.HandleAsync(
            CreateRequest("empty trip"), AplContext(), TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None);
        var speech = response.Tells<PlainTextOutputSpeech>();

        Assert.Contains("Empty Trip", speech.Text);
        Assert.Contains("photos", speech.Text);
        Assert.Null(GetSlideshowDirective(response));
    }

    [Fact]
    public async Task Handle_LibraryGating_AppliesTopParentIdsToEveryQuery()
    {
        Guid allowed = Guid.NewGuid();
        QueueResults(new List<BaseItem> { CreateAlbum("Roma") }, CreatePhotos(2));
        var handler = CreateHandler();

        await handler.HandleAsync(
            CreateRequest("roma"),
            AplContext(),
            TestHelpers.CreateTestUser(allowedLibraryIds: new[] { allowed.ToString() }),
            CreateSession(),
            CancellationToken.None);

        Assert.True(_capturedQueries.Count >= 2,
            $"expected the album query and the photos query, saw {_capturedQueries.Count}");
        Assert.All(_capturedQueries, q => Assert.Contains(allowed, q.TopParentIds));
        Assert.Equal(BaseItemKind.PhotoAlbum, _capturedQueries[0].IncludeItemTypes.Single());
        Assert.Equal(BaseItemKind.Photo, _capturedQueries[1].IncludeItemTypes.Single());
    }

    private static string ResponseStringsGet(string key)
        => Jellyfin.Plugin.AlexaSkill.Alexa.Locale.ResponseStrings.Get(key, "en-US");
}
