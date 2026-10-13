using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Assertions;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Locale;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-624 item 4 pins: the tappable paginated picker (AlexaPaginatedList behind
/// AplEnhancedScreens) for chapter/episode picking. Three groups: the builder
/// document contract, the flag states of the two wired surfaces (the
/// ChapterNotFound and NotFoundEpisode Tells; flag off must be today's shape,
/// byte-identical at the directive level and unchanged in speech), and the
/// tap-to-intent mapping in AplUserEventHandler (a tap is the same request the
/// spoken ask would have delivered, run by the real handler entry).
/// </summary>
[Collection("Plugin")]
public class AplPaginatedListTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IUserDataManager> _userDataManager = new();
    private readonly Mock<IChapterManager> _chapterManager = new();
    private readonly Mock<ITVSeriesManager> _tvSeriesManager = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly DeviceQueueManager _queueManager;

    public AplPaginatedListTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _userManager.Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        SetFlag(enabled: false);
        _queueManager = TestHelpers.CreateDeviceQueueManager("AplPaginatedListTests");
    }

    public void Dispose() => _queueManager.Dispose();

    private void SetFlag(bool enabled)
        => TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c =>
            {
                c.AplVisualsEnabled = true;
                c.AplEnhancedScreens = enabled;
            },
            "aplpaginated-list-tests");

    private AplUserEventHandler CreateTapHandler()
        => TestHelpers.CreateAplTapHandler(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            _queueManager,
            _loggerFactory,
            _chapterManager.Object,
            _tvSeriesManager.Object);

    private static AplUserEventRequest CreateTap(params string[] arguments)
        => new() { Arguments = new JArray(arguments), Locale = "en-US" };

    private SessionInfo CreateSession()
        => TestHelpers.CreateTestSession(_sessionManager.Object, _loggerFactory);

    /// <summary>
    /// A context with the APL interface and a connection-refused progressive
    /// endpoint: the real PlayEpisode entry re-run by an episode-picker tap sends
    /// its announce through the progressive vehicle, and the instant local
    /// refusal keeps the test hermetic (no external HTTP) while exercising the
    /// real construction, exactly like the spoken path.
    /// </summary>
    private static Context CreateTapContext()
        => new()
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    // A real Show declares both interfaces; the APL key gates the
                    // picker attach and the VideoApp key gates the episode launch
                    // (a device declaring ONLY APL would hit the honest degrade).
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        ["Alexa.Presentation.APL"] = new { },
                        ["VideoApp"] = new { }
                    }
                },
                ApiAccessToken = "test-token",
                ApiEndpoint = "http://127.0.0.1:9",
                Application = new Application { ApplicationId = "test-app" }
            }
        };

    private static List<ChapterInfo> FourChapters() => new()
    {
        new() { Name = "Chapter 1", StartPositionTicks = 0 },
        new() { Name = null, StartPositionTicks = TimeSpan.FromMinutes(10).Ticks },
        new() { Name = "The Journey", StartPositionTicks = TimeSpan.FromMinutes(20).Ticks },
        new() { Name = "Chapter 4", StartPositionTicks = TimeSpan.FromMinutes(30).Ticks },
    };

    private static global::MediaBrowser.Controller.Entities.TV.Episode BareEpisode(Guid seriesId, int season, int number)
        => new()
        {
            Name = $"S{season}E{number}",
            Id = Guid.NewGuid(),
            ParentIndexNumber = season,
            IndexNumber = number,
            SeriesId = seriesId
        };

    /// <summary>
    /// The JF-501 test seam shared with PlayEpisodeIntentHandlerTests (the ONE
    /// recording subclass for this handler): captures progressive speech so the
    /// surface pins run the REAL handler flow without network I/O.
    /// </summary>
    private PlayEpisodeIntentHandlerTests.RecordingPlayEpisodeHandler CreatePlayEpisodeHandler()
        => new(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            _tvSeriesManager.Object,
            _loggerFactory,
            new PlaySongIntentHandler(
                _sessionManager.Object,
                _config,
                _libraryManager.Object,
                _userManager.Object,
                _userDataManager.Object,
                _loggerFactory));

    private async Task<SkillResponse> RunPlayEpisodeMiss(
        PlayEpisodeIntentHandlerTests.RecordingPlayEpisodeHandler handler,
        Context context,
        string seasonValue = "1",
        string episodeValue = "99",
        List<BaseItem>? seasonEpisodes = null)
    {
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };

        // Deliberately UNSORTED input (E3, E1, E2) in the default shape: the
        // picker must order the rows by episode number, because the row ordinal
        // is not the pick value.
        seasonEpisodes ??= new List<BaseItem>
        {
            BareEpisode(series.Id, 1, 3),
            BareEpisode(series.Id, 1, 1),
            BareEpisode(series.Id, 1, 2)
        };

        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { series });
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode) && q.ParentIndexNumber == 1)))
            .Returns(seasonEpisodes);
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode) && q.ParentIndexNumber != 1)))
            .Returns(new List<BaseItem>());

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.PlayEpisode,
                Slots = new Dictionary<string, Slot>
                {
                    ["series_name"] = new() { Name = "series_name", Value = "The Office" },
                    ["season_number"] = new() { Name = "season_number", Value = seasonValue },
                    ["episode_number"] = new() { Name = "episode_number", Value = episodeValue }
                }
            },
            Locale = "en-US",
            RequestId = "test-req",
            DialogState = "COMPLETED"
        };

        return await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), CreateSession(), CancellationToken.None).ConfigureAwait(true);
    }

    // ========== Builder shape ==========

    [Fact]
    public void Builder_EmptyRows_ReturnsNull()
    {
        Assert.Null(AplHelper.BuildPaginatedListDirective(
            "Which?", new List<PaginatedListRow>(), AplHelper.ChapterPickTapAction));
    }

    [Fact]
    public void Builder_DocumentShape_MatchesAlexaPaginatedListContract()
    {
        var rows = new List<PaginatedListRow> { new("First"), new("Second") };

        var directive = AplHelper.BuildPaginatedListDirective("Which?", rows, AplHelper.ChapterPickTapAction);

        Assert.NotNull(directive);
        Assert.Equal("paginatedPicker", directive!.Token);
        Assert.Equal("MAX", directive.TimeoutType);

        JObject document = JObject.FromObject(directive.Document!);
        Assert.Equal("APL", document["type"]?.ToString());
        Assert.Equal("1.7", document["version"]?.ToString());

        // The alexa-layouts import (the spike-verified 1.7.0 line) and the same
        // no-backstack decision as the item-3 picker (a transient picker).
        JToken import = document["import"]!;
        Assert.Equal("alexa-layouts", import[0]?["name"]?.ToString());
        Assert.Equal("1.7.0", import[0]?["version"]?.ToString());
        Assert.Null(document["extensions"]);

        JToken main = document["mainTemplate"]!;
        Assert.Contains("payload", main["parameters"]!.Select(p => p.ToString()));
        JToken list = main["items"]![0]!;
        Assert.Equal("AlexaPaginatedList", list["type"]?.ToString());
        Assert.Equal("dark", list["theme"]?.ToString());
        Assert.Equal("${payload.paginatedData.properties.title}", list["headerTitle"]?.ToString());
        Assert.Equal("${payload.paginatedData.properties.listItems}", list["listItems"]?.ToString());

        // The documented primaryAction SendEvent shape (the Amazon page's
        // ListItemSelected example): the payload-driven action plus ${ordinal}.
        JToken sendEvent = list["primaryAction"]![0]!;
        Assert.Equal("SendEvent", sendEvent["type"]?.ToString());
        Assert.Equal("${payload.paginatedData.properties.action}", sendEvent["arguments"]![0]?.ToString());
        Assert.Equal("${ordinal}", sendEvent["arguments"]![1]?.ToString());

        // Deliberate minimal-risk shape: no background/scrim knobs and no back
        // button (the 2026-05-10 silent-failure class was a background param).
        Assert.Null(list["backgroundColorOverlay"]);
        Assert.Null(list["backgroundImageSource"]);
        Assert.Null(list["headerBackButton"]);

        // The datasource mirrors the bindings the document references.
        JObject props = directive.DataSources!["paginatedData"]!["properties"]!.ToObject<JObject>()!;
        Assert.Equal("Which?", props["title"]?.ToString());
        Assert.Equal(AplHelper.ChapterPickTapAction, props["action"]?.ToString());
        Assert.Equal(2, props["listItems"]!.ToObject<List<JObject>>()!.Count);
    }

    [Fact]
    public void Builder_RowMapping_OptionalFieldsAndPerRowCommands()
    {
        var rows = new List<PaginatedListRow>
        {
            new("With Subtitle", "10:00"),
            new("With Exact Tap", "S1 · E5", new[] { AplHelper.EpisodePickTapAction, "1", "5", "The Office" }),
            new("Bare")
        };

        var directive = AplHelper.BuildPaginatedListDirective("Which?", rows, AplHelper.ChapterPickTapAction);

        var listRows = directive!.DataSources!["paginatedData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal(3, listRows.Count);

        // Subtitle only when set.
        Assert.Equal("10:00", listRows[0]["secondaryText"]?.ToString());
        Assert.Null(listRows[2]["secondaryText"]);

        // Rows without explicit tap arguments ride the template-level ${ordinal}
        // (no per-row command); a row WITH arguments carries the documented
        // per-item primaryAction override with the exact strings.
        Assert.Null(listRows[0]["primaryAction"]);
        Assert.Null(listRows[2]["primaryAction"]);
        JToken rowCommand = listRows[1]["primaryAction"]![0]!;
        Assert.Equal("SendEvent", rowCommand["type"]?.ToString());
        Assert.Equal(AplHelper.EpisodePickTapAction, rowCommand["arguments"]![0]?.ToString());
        Assert.Equal("1", rowCommand["arguments"]![1]?.ToString());
        Assert.Equal("5", rowCommand["arguments"]![2]?.ToString());
        Assert.Equal("The Office", rowCommand["arguments"]![3]?.ToString());
    }

    [Fact]
    public void Builder_RowCap_KeepsThePrefix()
    {
        // The cap keeps a PREFIX (positional tap identity stays exact for every
        // rendered row); rows past it stay reachable by voice.
        var rows = Enumerable.Range(1, AplHelper.PaginatedListRowCap + 20)
            .Select(i => new PaginatedListRow($"Row {i}"))
            .ToList();

        var directive = AplHelper.BuildPaginatedListDirective("Which?", rows, AplHelper.ChapterPickTapAction);

        var listRows = directive!.DataSources!["paginatedData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal(AplHelper.PaginatedListRowCap, listRows.Count);
        Assert.Equal("Row 1", listRows[0]["primaryText"]?.ToString());
        Assert.Equal($"Row {AplHelper.PaginatedListRowCap}", listRows[^1]["primaryText"]?.ToString());
    }

    // ========== GoToChapter ChapterNotFound flag states ==========

    [Fact]
    public async Task GoToChapter_OutOfRange_FlagOff_TodaysShape_NoDirective()
    {
        SetFlag(false);
        var response = await RunGoToChapterOutOfRange(TestHelpers.CreateContextWithApl());

        // Flag-off byte-identity: the pre-JF-624 Tell, no directives at all.
        response.Tells();
        Assert.True(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives);
        Assert.Equal(
            ResponseStrings.Get("ChapterNotFound", "en-US", "99", "4"),
            TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task GoToChapter_OutOfRange_FlagOn_AttachesChapterPicker()
    {
        SetFlag(true);
        var response = await RunGoToChapterOutOfRange(TestHelpers.CreateContextWithApl());

        // The spoken response is UNCHANGED (the same Tell sentence); the picker
        // rides the same session-ending response.
        response.Tells();
        Assert.Equal(
            ResponseStrings.Get("ChapterNotFound", "en-US", "99", "4"),
            TestHelpers.GetSpeechText(response));

        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("paginatedPicker", render.Token);
        Assert.Equal("MAX", render.TimeoutType);

        JObject document = JObject.FromObject(render.Document!);
        Assert.Equal("AlexaPaginatedList", document["mainTemplate"]!["items"]![0]!["type"]?.ToString());

        JObject props = render.DataSources!["paginatedData"]!["properties"]!.ToObject<JObject>()!;
        Assert.Equal(AplHelper.ChapterPickTapAction, props["action"]?.ToString());
        Assert.Equal("Audiobook", props["title"]?.ToString());

        // Rows are the WHOLE chapter list in order (the ${ordinal} IS the chapter
        // number), numbered and position-subtitled; the unnamed chapter renders
        // the bare locale-neutral ordinal (the time subtitle identifies the row).
        var rows = props["listItems"]!.ToObject<List<JObject>>()!;
        Assert.Equal(4, rows.Count);
        Assert.Equal("1. Chapter 1", rows[0]["primaryText"]?.ToString());
        Assert.Equal("0:00", rows[0]["secondaryText"]?.ToString());
        Assert.Equal("2.", rows[1]["primaryText"]?.ToString());
        Assert.Equal("10:00", rows[1]["secondaryText"]?.ToString());
        Assert.Equal("3. The Journey", rows[2]["primaryText"]?.ToString());
        Assert.Equal("20:00", rows[2]["secondaryText"]?.ToString());
    }

    [Fact]
    public async Task GoToChapter_OutOfRange_FlagOn_NonAplContext_NoDirective()
    {
        SetFlag(true);
        var response = await RunGoToChapterOutOfRange(TestHelpers.CreateContextWithoutApl());

        response.Tells();
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    [Fact]
    public async Task GoToChapter_OutOfRange_FlagOn_VisualsOff_NoDirective()
    {
        SetFlag(true);
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => c.AplVisualsEnabled = false,
            "aplpaginated-list-tests");

        var response = await RunGoToChapterOutOfRange(TestHelpers.CreateContextWithApl());

        response.Tells();
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    [Fact]
    public async Task GoToChapter_NoChapters_FlagOn_NoPicker()
    {
        // The NoChapters branch has no list to pick from: never a picker.
        SetFlag(true);
        var handler = new GoToChapterIntentHandler(
            _sessionManager.Object, _config, _libraryManager.Object, _chapterManager.Object, _loggerFactory);
        var session = CreateSession();
        var audio = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audio;
        _chapterManager.Setup(c => c.GetChapters(audio.Id)).Returns(new List<ChapterInfo>());

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.GoToChapter,
                Slots = new Dictionary<string, Slot> { ["chapter_number"] = new() { Name = "chapter_number", Value = "3" } }
            },
            Locale = "en-US"
        };

        var response = await handler.HandleAsync(request, TestHelpers.CreateContextWithApl(), TestHelpers.CreateTestUser(), session, CancellationToken.None).ConfigureAwait(true);

        response.Tells();
        Assert.Empty(response.Response.Directives);
    }

    private async Task<SkillResponse> RunGoToChapterOutOfRange(Context context)
    {
        var handler = new GoToChapterIntentHandler(
            _sessionManager.Object, _config, _libraryManager.Object, _chapterManager.Object, _loggerFactory);
        var session = CreateSession();
        var audio = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audio;
        _chapterManager.Setup(c => c.GetChapters(audio.Id)).Returns(FourChapters());
        _libraryManager.Setup(l => l.GetItemById(audio.Id)).Returns(audio);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = IntentNames.GoToChapter,
                Slots = new Dictionary<string, Slot> { ["chapter_number"] = new() { Name = "chapter_number", Value = "99" } }
            },
            Locale = "en-US"
        };

        return await handler.HandleAsync(request, context, TestHelpers.CreateTestUser(), session, CancellationToken.None).ConfigureAwait(true);
    }

    // ========== PlayEpisode NotFoundEpisode flag states ==========

    [Fact]
    public async Task PlayEpisode_MissInExistingSeason_FlagOff_TodaysShape_NoDirective()
    {
        SetFlag(false);
        var response = await RunPlayEpisodeMiss(CreatePlayEpisodeHandler(), TestHelpers.CreateContextWithApl());

        response.Tells();
        Assert.True(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives);
        Assert.Contains("couldn't find", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayEpisode_MissInExistingSeason_FlagOn_AttachesEpisodePicker()
    {
        SetFlag(true);
        var response = await RunPlayEpisodeMiss(CreatePlayEpisodeHandler(), TestHelpers.CreateContextWithApl());

        // Spoken response unchanged: the same not-found sentence.
        Assert.Contains("couldn't find", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);

        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("paginatedPicker", render.Token);

        JObject props = render.DataSources!["paginatedData"]!["properties"]!.ToObject<JObject>()!;
        Assert.Equal("The Office", props["title"]?.ToString());

        // Rows are the season's episodes ordered by episode number, each carrying
        // its OWN exact tap arguments (the documented per-item override), so
        // gaps/specials can never misresolve through row position.
        var rows = props["listItems"]!.ToObject<List<JObject>>()!;
        Assert.Equal(3, rows.Count);
        Assert.Equal("S1E1", rows[0]["primaryText"]?.ToString());
        Assert.Equal("S1 · E1", rows[0]["secondaryText"]?.ToString());
        JToken args1 = rows[0]["primaryAction"]![0]!["arguments"]!;
        Assert.Equal(AplHelper.EpisodePickTapAction, args1[0]?.ToString());
        Assert.Equal("1", args1[1]?.ToString());
        Assert.Equal("1", args1[2]?.ToString());
        Assert.Equal("The Office", args1[3]?.ToString());
        JToken args3 = rows[2]["primaryAction"]![0]!["arguments"]!;
        Assert.Equal("3", args3[2]?.ToString());
    }

    [Fact]
    public async Task PlayEpisode_Miss_ZeroSeasonEpisodes_FlagOn_NoPicker()
    {
        // The wrong-SEASON shape (the asked season has no episodes): there is no
        // list to pick from, so nothing attaches even with the flag on.
        SetFlag(true);

        var response = await RunPlayEpisodeMiss(
            CreatePlayEpisodeHandler(),
            TestHelpers.CreateContextWithApl(),
            seasonValue: "9",
            episodeValue: "1",
            seasonEpisodes: new List<BaseItem>()).ConfigureAwait(true);

        response.Tells();
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    // ========== Tap mapping: a tap IS the spoken ask ==========

    [Fact]
    public async Task Tap_ChapterPick_SeeksToTheTappedChapter()
    {
        // The picker's ${ordinal} is the 1-based chapter number; tapping 3 must
        // land on the third chapter's start (20 minutes), exactly like the
        // spoken "chapter 3".
        var session = CreateSession();
        var audio = new Audio { Name = "Audiobook", Id = Guid.NewGuid() };
        session.FullNowPlayingItem = audio;
        _chapterManager.Setup(c => c.GetChapters(audio.Id)).Returns(FourChapters());
        _libraryManager.Setup(l => l.GetItemById(audio.Id)).Returns(audio);

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.ChapterPickTapAction, "3"),
            CreateTapContext(),
            TestHelpers.CreateTestUser(),
            session,
            new Dictionary<string, object>(),
            CancellationToken.None);

        var play = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains(audio.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal((int)TimeSpan.FromMinutes(20).TotalMilliseconds, play.AudioItem.Stream.OffsetInMilliseconds);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    public async Task Tap_ChapterPick_InvalidPayload_EmptyResponse(string chapter)
    {
        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.ChapterPickTapAction, chapter),
            CreateTapContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task Tap_ChapterPick_NothingPlaying_DelegatesToTheRealEntry()
    {
        // A stale document (playback ended after the picker rendered) must get
        // the flow's own honest answer through the REAL GoToChapter entry, not
        // the generic empty keep-alive: the delegation is stateless, and the
        // entry owns the no-media verdict.
        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.ChapterPickTapAction, "2"),
            CreateTapContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        response.Tells();
        Assert.Equal(
            ResponseStrings.Get("NoMediaPlaying", "en-US"),
            TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task Tap_EpisodePick_PlaysTheTappedEpisode()
    {
        // The row's exact arguments (season 1, episode 2, series name) re-enter
        // the REAL PlayEpisode flow: series resolution, the per-season query, and
        // the VideoApp launch all re-run; the launched item is the tapped one.
        var series = new global::MediaBrowser.Controller.Entities.TV.Series { Name = "The Office", Id = Guid.NewGuid() };
        var tapped = BareEpisode(series.Id, 1, 2);
        var season1 = new List<BaseItem> { BareEpisode(series.Id, 1, 1), tapped, BareEpisode(series.Id, 1, 3) };

        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Series))))
            .Returns(new List<BaseItem> { series });
        _libraryManager
            .Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(
                q => q.IncludeItemTypes != null && q.IncludeItemTypes.Any(t => t == BaseItemKind.Episode) && q.ParentIndexNumber == 1)))
            .Returns(season1);

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.EpisodePickTapAction, "1", "2", "The Office"),
            CreateTapContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        var launch = response.Response.Directives.OfType<VideoAppLaunchDirective>().Single();
        Assert.Contains(tapped.Id.ToString(), launch.VideoItem.Source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x", "2", "The Office")] // non-numeric season
    [InlineData("1", "0", "The Office")] // episode 0
    [InlineData("1", "2", "")] // empty series name
    [InlineData("1", "2")] // missing series argument entirely
    public async Task Tap_EpisodePick_InvalidPayload_EmptyResponse(params string[] tail)
    {
        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(new[] { AplHelper.EpisodePickTapAction }.Concat(tail).ToArray()),
            CreateTapContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void TapIntentSynthesis_CarriesRequestIdAndLocale()
    {
        // The review F1 pin: the synthesized request must look like the spoken
        // ask the delegated handler expects. A dropped RequestId silently kills
        // every progressive send the delegated flow makes (SearchingMedia ping,
        // the JF-501 launch announce); a dropped Locale falls back to default-
        // locale speech; a slot Name/Key mismatch reads as a missing slot.
        var source = new AplUserEventRequest
        {
            RequestId = "tap-req-1",
            Locale = "it-IT",
            Arguments = new JArray(AplHelper.EpisodePickTapAction, "2", "8", "Sailor Moon")
        };

        IntentRequest synthesized = AplUserEventHandler.SynthesizeTapIntent(
            IntentNames.PlayEpisode,
            source,
            ("series_name", "Sailor Moon"),
            ("season_number", "2"),
            ("episode_number", "8"));

        Assert.Equal("tap-req-1", synthesized.RequestId);
        Assert.Equal("it-IT", synthesized.Locale);
        Assert.Equal(IntentNames.PlayEpisode, synthesized.Intent.Name);
        Assert.Equal(3, synthesized.Intent.Slots!.Count);
        Assert.All(synthesized.Intent.Slots!, kv => Assert.Equal(kv.Key, kv.Value.Name));
        Assert.Equal("8", synthesized.Intent.Slots!["episode_number"].Value);
    }
}
