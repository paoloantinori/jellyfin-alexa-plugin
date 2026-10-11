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
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-624 item 3 pins: the tappable disambiguation text list (AlexaTextList behind
/// AplEnhancedScreens). Three groups: the gate/builder shape, the flag states of the
/// three wired ask surfaces (flag off must be today's shape byte-for-byte at the
/// directive level), and the tap-to-answer mapping in AplUserEventHandler (a tap is
/// the same transition as the spoken word on the state machine it renders for).
/// </summary>
[Collection("Plugin")]
public class AplDisambiguationListTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManager = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IUserManager> _userManager = new();
    private readonly Mock<IUserDataManager> _userDataManager = new();
    private readonly PluginConfiguration _config = new();
    private readonly ILoggerFactory _loggerFactory = LoggerFactory.Create(b => { });
    private readonly DeviceQueueManager _queueManager;

    public AplDisambiguationListTests()
    {
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _userManager.Setup(um => um.GetUserById(It.IsAny<Guid>()))
            .Returns(TestHelpers.CreateJellyfinUser());
        SetFlag(enabled: false);
        _queueManager = TestHelpers.CreateDeviceQueueManager("AplDisambiguationListTests");
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
            "apdisambig-list-tests");

    private AplUserEventHandler CreateTapHandler()
        => TestHelpers.CreateAplTapHandler(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            _queueManager,
            _loggerFactory);

    private static AplUserEventRequest CreateTap(string action, string ordinal)
        => new() { Arguments = new JArray(action, ordinal), Locale = "en-US" };

    private SessionInfo CreateSession()
        => TestHelpers.CreateTestSession(_sessionManager.Object, _loggerFactory);

    private static Dictionary<string, object> DisambiguationAttrs(
        List<DisambiguationHelper.MatchInfo> matches, int index, string type)
        => new()
        {
            ["disambig_matches"] = JsonConvert.SerializeObject(matches),
            ["disambig_index"] = index,
            ["disambig_type"] = type
        };

    private static Dictionary<string, object> FindSongAttrs(FindSongSessionData data)
        => new() { ["FindSongSessionData"] = JsonConvert.SerializeObject(data) };

    // ========== Gate ==========

    [Fact]
    public void Gate_FlagOff_ReturnsFalse()
    {
        SetFlag(false);
        Assert.False(AplHelper.DisambiguationListEnabled(TestHelpers.CreateContextWithApl()));
    }

    [Fact]
    public void Gate_FlagOn_AplContext_VisualsOn_ReturnsTrue()
    {
        SetFlag(true);
        Assert.True(AplHelper.DisambiguationListEnabled(TestHelpers.CreateContextWithApl()));
    }

    [Fact]
    public void Gate_FlagOn_NonAplContext_ReturnsFalse()
    {
        SetFlag(true);
        Assert.False(AplHelper.DisambiguationListEnabled(TestHelpers.CreateContextWithoutApl()));
    }

    [Fact]
    public void Gate_FlagOn_NullContext_ReturnsFalse()
    {
        SetFlag(true);
        Assert.False(AplHelper.DisambiguationListEnabled(null));
    }

    [Fact]
    public void Gate_FlagOn_VisualsOff_ReturnsFalse()
    {
        SetFlag(true);
        TestHelpers.EnsurePluginInstance(
            _config,
            _loggerFactory,
            c => c.AplVisualsEnabled = false,
            "apdisambig-list-tests");
        Assert.False(AplHelper.DisambiguationListEnabled(TestHelpers.CreateContextWithApl()));
    }

    // ========== Builder shape ==========

    [Fact]
    public void Builder_EmptyItems_ReturnsNull()
    {
        Assert.Null(AplHelper.BuildDisambiguationListDirective(
            "Which?", new List<ListDisplayItem>(), AplHelper.DisambiguationTapAction));
    }

    [Fact]
    public void Builder_DocumentShape_MatchesAlexaTextListContract()
    {
        var items = new List<ListDisplayItem>
        {
            new("First", "1"),
            new("Second", "2")
        };

        var directive = AplHelper.BuildDisambiguationListDirective("Which?", items, AplHelper.DisambiguationTapAction);

        Assert.NotNull(directive);
        Assert.Equal("disambiguationList", directive!.Token);

        JObject document = JObject.FromObject(directive.Document!);
        Assert.Equal("APL", document["type"]?.ToString());
        Assert.Equal("1.7", document["version"]?.ToString());

        // The alexa-layouts import (the spike-verified 1.7.0 line).
        JToken import = document["import"]!;
        Assert.Equal("alexa-layouts", import[0]?["name"]?.ToString());
        Assert.Equal("1.7.0", import[0]?["version"]?.ToString());

        // The mainTemplate declares payload (datasource binding) and renders ONE
        // AlexaTextList bound to the datasource.
        JToken main = document["mainTemplate"]!;
        Assert.Contains("payload", main["parameters"]!.Select(p => p.ToString()));
        JToken textList = main["items"]![0]!;
        Assert.Equal("AlexaTextList", textList["type"]?.ToString());
        Assert.Equal("${payload.disambigData.properties.title}", textList["headerTitle"]?.ToString());
        Assert.Equal("${payload.disambigData.properties.listItems}", textList["listItems"]?.ToString());

        // The documented primaryAction SendEvent shape: the payload-driven action
        // plus the row's ${ordinal} (the tap identity the handler maps).
        JToken sendEvent = textList["primaryAction"]![0]!;
        Assert.Equal("SendEvent", sendEvent["type"]?.ToString());
        Assert.Equal(
            "${payload.disambigData.properties.action}",
            sendEvent["arguments"]![0]?.ToString());
        Assert.Equal("${ordinal}", sendEvent["arguments"]![1]?.ToString());

        // The datasource mirrors the bindings the document references.
        JObject props = directive.DataSources!["disambigData"]!["properties"]!.ToObject<JObject>()!;
        Assert.Equal("Which?", props["title"]?.ToString());
        Assert.Equal(AplHelper.DisambiguationTapAction, props["action"]?.ToString());
        var rows = props["listItems"]!.ToObject<List<JObject>>()!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("First", rows[0]["primaryText"]?.ToString());
        Assert.Null(rows[0]["secondaryText"]);
        Assert.Null(rows[0]["imageThumbnailSource"]);
    }

    [Fact]
    public void Builder_RowMapping_OptionalFieldsOnlyWhenSet()
    {
        var items = new List<ListDisplayItem>
        {
            new("With Everything", "1", "The Subtitle", "http://art/1.jpg"),
            new("Bare", "2")
        };

        var directive = AplHelper.BuildDisambiguationListDirective("Which?", items, AplHelper.FindSongTapAction);

        var rows = directive!.DataSources!["disambigData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal("With Everything", rows[0]["primaryText"]?.ToString());
        Assert.Equal("The Subtitle", rows[0]["secondaryText"]?.ToString());
        Assert.Equal("http://art/1.jpg", rows[0]["imageThumbnailSource"]?.ToString());
        Assert.Equal("Bare", rows[1]["primaryText"]?.ToString());
        Assert.Null(rows[1]["secondaryText"]);
        Assert.Null(rows[1]["imageThumbnailSource"]);
    }

    // ========== AskFirstMatch flag states (byte-identity of the flag-off ask) ==========

    [Fact]
    public void AskFirstMatch_FlagOff_AttachesCarousel_TodaysShape()
    {
        SetFlag(false);
        var matches = new List<(Guid, string, string?)>
        {
            (Guid.NewGuid(), "Pink Floyd", "http://art/pf.jpg"),
            (Guid.NewGuid(), "P!nk", null)
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "artist", "en-US", TestHelpers.CreateContextWithApl());

        // Exactly one RenderDocument: the pre-JF-624 carousel (token "disambiguation",
        // hand-rolled document WITHOUT the alexa-layouts import).
        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("disambiguation", render.Token);
        JObject document = JObject.FromObject(render.Document!);
        Assert.Null(document["import"]);
        Assert.Null(document["mainTemplate"]!["items"]![0]!["primaryAction"]);
    }

    [Fact]
    public void AskFirstMatch_FlagOn_AttachesTextList_InsteadOfCarousel()
    {
        SetFlag(true);
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var matches = new List<(Guid, string, string?)>
        {
            (id1, "Pink Floyd", "http://art/pf.jpg"),
            (id2, "P!nk", null)
        };

        var response = DisambiguationHelper.AskFirstMatch(matches, "artist", "en-US", TestHelpers.CreateContextWithApl());

        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("disambiguationList", render.Token);
        JObject document = JObject.FromObject(render.Document!);
        Assert.Equal("AlexaTextList", document["mainTemplate"]!["items"]![0]!["type"]?.ToString());

        // Rows are the session-state matches in order, art preserved as thumbnails.
        var rows = render.DataSources!["disambigData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("Pink Floyd", rows[0]["primaryText"]?.ToString());
        Assert.Equal("http://art/pf.jpg", rows[0]["imageThumbnailSource"]?.ToString());
        Assert.Equal("P!nk", rows[1]["primaryText"]?.ToString());
    }

    // ========== AskMultipleArtists flag states (the JF-420.2 yes/no artist flow) ==========

    [Fact]
    public void AskMultipleArtists_NullContext_NoDirectives_TodaysShape()
    {
        SetFlag(true);
        var matches = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = Guid.NewGuid().ToString(), Name = "A" },
            new() { Id = Guid.NewGuid().ToString(), Name = "B" }
        };

        var response = DisambiguationHelper.AskMultipleArtists(matches, "en-US");

        response.Asks();
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void AskMultipleArtists_FlagOff_AplContext_NoDirectives_TodaysShape()
    {
        SetFlag(false);
        var matches = new List<DisambiguationHelper.MatchInfo>
        {
            new() { Id = Guid.NewGuid().ToString(), Name = "A" },
            new() { Id = Guid.NewGuid().ToString(), Name = "B" }
        };

        var response = DisambiguationHelper.AskMultipleArtists(matches, "en-US", TestHelpers.CreateContextWithApl());

        response.Asks();
        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public void AskMultipleArtists_FlagOn_AttachesFullListBeyondSpeakCap()
    {
        SetFlag(true);
        // Four matches: the spoken list caps at three names (JF-707) while the
        // tappable list shows every state row, so a tap reaches the rank speech
        // could not name in one breath (the JF-690 unbounded-caller shape).
        var matches = Enumerable.Range(1, 4)
            .Select(i => new DisambiguationHelper.MatchInfo { Id = Guid.NewGuid().ToString(), Name = $"Artist {i}" })
            .ToList();

        var response = DisambiguationHelper.AskMultipleArtists(matches, "en-US", TestHelpers.CreateContextWithApl());

        response.Asks();
        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("disambiguationList", render.Token);
        var rows = render.DataSources!["disambigData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal(4, rows.Count);
        Assert.Equal("Artist 4", rows[3]["primaryText"]?.ToString());
        Assert.Equal(
            AplHelper.DisambiguationTapAction,
            render.DataSources!["disambigData"]!["properties"]!["action"]?.ToString());

        // The session state keeps the full list and the winner-first cursor.
        var state = DisambiguationHelper.ReadState(response.SessionAttributes as Dictionary<string, object>);
        Assert.NotNull(state);
        Assert.Equal(4, state!.Value.Matches.Count);
        Assert.Equal(0, state.Value.Index);
    }

    // ========== FindSong found-multiple prompt flag states ==========

    [Fact]
    public async Task FindSong_FoundMultiple_FlagOff_NoAplDirective_TodaysShape()
    {
        SetFlag(false);
        var response = await RunFindSongFoundMultipleAsync(TestHelpers.CreateContextWithApl());

        Assert.False(response.Response.ShouldEndSession);
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    [Fact]
    public async Task FindSong_FoundMultiple_FlagOn_AttachesCandidateList()
    {
        SetFlag(true);
        var response = await RunFindSongFoundMultipleAsync(TestHelpers.CreateContextWithApl());

        var render = response.Response.Directives.OfType<AplRenderDocumentDirective>().Single();
        Assert.Equal("disambiguationList", render.Token);
        Assert.Equal(
            AplHelper.FindSongTapAction,
            render.DataSources!["disambigData"]!["properties"]!["action"]?.ToString());
        var rows = render.DataSources!["disambigData"]!["properties"]!["listItems"]!
            .ToObject<List<JObject>>()!;
        Assert.Equal(2, rows.Count);
        Assert.Equal("Hey Jude", rows[0]["primaryText"]?.ToString());
        Assert.Equal("The Beatles", rows[0]["secondaryText"]?.ToString());
        Assert.Equal("Hey There", rows[1]["primaryText"]?.ToString());
    }

    private async Task<SkillResponse> RunFindSongFoundMultipleAsync(Context context)
    {
        var first = TestHelpers.CreateSong("Hey Jude", Guid.NewGuid());
        first.Artists = new[] { "The Beatles" };
        var second = TestHelpers.CreateSong("Hey There", Guid.NewGuid());
        second.Artists = new[] { "Joe Cocker" };

        _libraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { first, second }.AsReadOnly());
        _libraryManager.Setup(lm => lm.GetItemById(first.Id)).Returns(first);
        _libraryManager.Setup(lm => lm.GetItemById(second.Id)).Returns(second);

        var handler = new FindSongIntentHandler(
            _sessionManager.Object,
            _config,
            _libraryManager.Object,
            _userManager.Object,
            _userDataManager.Object,
            _loggerFactory);

        var request = new IntentRequest
        {
            Intent = new Intent
            {
                Name = "FindSongIntent",
                Slots = new Dictionary<string, Slot> { ["titleKeywords"] = new() { Name = "titleKeywords", Value = "hey" } }
            },
            Locale = "en-US"
        };

        var sessionAttrs = FindSongAttrs(new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            Keywords = "hey"
        });

        return await handler.HandleAsync(
            request, context, TestHelpers.CreateTestUser(), CreateSession(), sessionAttrs, CancellationToken.None);
    }

    // ========== Tap mapping: a tap IS the spoken answer ==========

    [Fact]
    public async Task Tap_DisambigSelect_PlaysTheTappedMatch()
    {
        // Two artists in the disambiguation state, spoken cursor on the first;
        // tapping row 2 must play the SECOND artist (the tap moves the yes/no
        // cursor and answers yes, exactly like the spoken word would).
        var artistA = new MusicArtist { Name = "Artist A", Id = Guid.NewGuid() };
        var artistB = new MusicArtist { Name = "Artist B", Id = Guid.NewGuid() };
        var songA = TestHelpers.CreateSong("Song A", Guid.NewGuid());
        var songB = TestHelpers.CreateSong("Song B", Guid.NewGuid());

        _libraryManager.Setup(lm => lm.GetItemById(artistA.Id)).Returns(artistA);
        _libraryManager.Setup(lm => lm.GetItemById(artistB.Id)).Returns(artistB);
        _libraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { songB }.AsReadOnly());

        var attrs = DisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo>
            {
                new() { Id = artistA.Id.ToString(), Name = artistA.Name },
                new() { Id = artistB.Id.ToString(), Name = artistB.Name }
            },
            index: 0,
            DisambiguationHelper.MediaTypeArtist);

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.DisambiguationTapAction, "2"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var play = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains(songB.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(songA.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tap_DisambigSelect_SpokenCursorOnSecond_TapRowOne_PlaysFirst()
    {
        // The tap overrides the spoken cursor: after a spoken "no" advanced the
        // state to row 2, tapping row 1 still plays row 1.
        var artistA = new MusicArtist { Name = "Artist A", Id = Guid.NewGuid() };
        var artistB = new MusicArtist { Name = "Artist B", Id = Guid.NewGuid() };
        var songA = TestHelpers.CreateSong("Song A", Guid.NewGuid());

        _libraryManager.Setup(lm => lm.GetItemById(artistA.Id)).Returns(artistA);
        _libraryManager.Setup(lm => lm.GetItemById(artistB.Id)).Returns(artistB);
        _libraryManager
            .Setup(lm => lm.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { songA }.AsReadOnly());

        var attrs = DisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo>
            {
                new() { Id = artistA.Id.ToString(), Name = artistA.Name },
                new() { Id = artistB.Id.ToString(), Name = artistB.Name }
            },
            index: 1,
            DisambiguationHelper.MediaTypeArtist);

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.DisambiguationTapAction, "1"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var play = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains(songA.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tap_DisambigSelect_NoState_EmptyResponse()
    {
        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.DisambiguationTapAction, "1"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        // The stale-document degrade: the empty keep-alive, like an unknown item id.
        Assert.Empty(response.Response.Directives);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("")]
    [InlineData("abc")]
    public async Task Tap_DisambigSelect_InvalidOrdinal_EmptyResponse(string ordinal)
    {
        var attrs = DisambiguationAttrs(
            new List<DisambiguationHelper.MatchInfo> { new() { Id = Guid.NewGuid().ToString(), Name = "Only" } },
            index: 0,
            DisambiguationHelper.MediaTypeArtist);

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.DisambiguationTapAction, ordinal),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task Tap_FindSongSelect_PlaysTheTappedCandidate()
    {
        // The FindSong picker: tapping row 2 behaves like the spoken "the second
        // one" (the digit answer ResolvePick understands), playing that candidate
        // with the same announce the spoken pick gets.
        var songA = TestHelpers.CreateSong("Alpha", Guid.NewGuid());
        songA.Artists = new[] { "Artist A" };
        var songB = TestHelpers.CreateSong("Beta", Guid.NewGuid());
        songB.Artists = new[] { "Artist B" };

        _libraryManager.Setup(lm => lm.GetItemById(songA.Id)).Returns(songA);
        _libraryManager.Setup(lm => lm.GetItemById(songB.Id)).Returns(songB);

        var attrs = FindSongAttrs(new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Keywords = "song",
            Candidates = new List<FindSongCandidate>
            {
                new(songA.Id, songA.Name!, "Artist A", 100),
                new(songB.Id, songB.Name!, "Artist B", 90)
            }
        });

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.FindSongTapAction, "2"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        var play = response.HasDirective<AudioPlayerPlayDirective>();
        Assert.Contains(songB.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain(songA.Id.ToString(), play.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Contains("Beta", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tap_FindSongSelect_NoState_EmptyResponse()
    {
        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.FindSongTapAction, "1"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            new Dictionary<string, object>(),
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("3")]
    [InlineData("")]
    public async Task Tap_FindSongSelect_InvalidOrdinal_EmptyResponse(string ordinal)
    {
        var attrs = FindSongAttrs(new FindSongSessionData
        {
            State = FindSongState.Disambiguating,
            Keywords = "song",
            Candidates = new List<FindSongCandidate> { new(Guid.NewGuid(), "Only", null, 100) }
        });

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.FindSongTapAction, ordinal),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }

    [Fact]
    public async Task Tap_FindSongSelect_OutsideDisambiguatingState_EmptyResponse()
    {
        // A tap landing while the flow sits in another state (stale document)
        // degrades instead of forcing a pick.
        var attrs = FindSongAttrs(new FindSongSessionData
        {
            State = FindSongState.AwaitingKeywords,
            Keywords = "song"
        });

        var handler = CreateTapHandler();
        var response = await handler.HandleAsync(
            CreateTap(AplHelper.FindSongTapAction, "1"),
            TestHelpers.CreateTestContext(),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);

        Assert.Empty(response.Response.Directives);
    }
}
