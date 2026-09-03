using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
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
/// Tests that PlaySongIntentHandler uses ASR compound-word fallback via SearchWithAsrFallbackAsync.
/// Simulates the scenario where "lazy bones" (two words from ASR) finds nothing but the
/// joined variant "lazybones" returns a result.
/// </summary>
[Collection("Plugin")]
public class PlaySongAsrFallbackTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    public PlaySongAsrFallbackTests()
    {
        _fx.Config.AnnounceAudioPlays = true; // opt in: this class tests PlaySong announce behavior
    }

    private PlaySongIntentHandler CreateHandler()
    {
        return new PlaySongIntentHandler(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.LibraryManager.Object,
            _fx.UserManager.Object,
            _fx.UserDataManager.Object,
            _fx.LoggerFactory);
    }

    private static IntentRequest CreateSongIntentRequest(string song)
    {
        var intent = new Intent { Name = IntentNames.PlaySong };
        intent.Slots = new Dictionary<string, Slot>
        {
            ["song"] = new Slot { Name = "song", Value = song }
        };
        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    [Fact]
    public async Task PlaySong_AsrFallback_JoinedVariantFindsSong_ReturnsPlayback()
    {
        // Arrange: "lazy bones" returns empty, but "lazybones" returns a song
        var song = new Audio { Name = "Lazybones", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        var searchTerms = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns<InternalItemsQuery>(q =>
                string.Equals(q.SearchTerm, "lazybones", StringComparison.OrdinalIgnoreCase)
                    ? new List<BaseItem> { song }
                    : new List<BaseItem>());

        _fx.Config.AsrCompoundWordFixEnabled = true;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: playback started (has AudioPlayer directive)
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // The handler tried "lazy bones" first, then "lazybones" via ASR fallback
        Assert.True(searchTerms.Count >= 2,
            $"Expected at least 2 search calls, got {searchTerms.Count}: {string.Join(", ", searchTerms)}");
        Assert.Equal("lazy bones", searchTerms[0]);
        Assert.Equal("lazybones", searchTerms[1]);

        // When the user opts into audio announces (AnnounceAudioPlays = true), a successful
        // song play speaks the now-playing title.
        Assert.NotNull(response.Response.OutputSpeech);
        Assert.Contains("Lazybones", TestHelpers.GetSpeechText(response));
    }

    [Fact]
    public async Task PlaySong_AnnounceOff_SilentLaunch()
    {
        // With AnnounceAudioPlays off (the default), a successful song play is silent
        // (no OutputSpeech) while playback still starts.
        var song = new Audio { Name = "Lazybones", Id = Guid.NewGuid() };
        _fx.SetupUserMock();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<BaseItem> { song });

        _fx.Config.AnnounceAudioPlays = false;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazybones");
        SkillResponse response = await handler.HandleAsync(request, _fx.CreateContext(), _fx.CreateUser(), _fx.CreateSession(), CancellationToken.None);

        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.Null(response.Response.OutputSpeech);
    }

    [Fact]
    public async Task PlaySong_AsrFallbackDisabled_OriginalNotFound_ReturnsNotFound()
    {
        // Arrange: feature disabled — should NOT try ASR compound-word variants.
        // Note: the cross-media-type artist fallback will still trigger additional
        // searches (artist search) after the song search fails, which is expected.
        _fx.SetupUserMock();

        var searchTerms = new List<string?>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns(new List<BaseItem>());

        _fx.Config.AsrCompoundWordFixEnabled = false;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: song not found, session ended
        Assert.True(response.Response?.ShouldEndSession);
        string speech = TestHelpers.GetSpeechText(response);
        Assert.DoesNotContain("lazybones", speech);

        // The first search call is the original "lazy bones" song search (with SearchTerm).
        // Subsequent calls are from the cross-media-type artist fallback — they have different
        // search parameters. Verify no ASR compound-word variant ("lazybones") was tried.
        Assert.Contains("lazy bones", searchTerms);
        Assert.DoesNotContain("lazybones", searchTerms);
    }

    [Fact]
    public async Task PlaySong_AsrFallback_OriginalAlreadyFound_NoVariantsTried()
    {
        // Arrange: original query finds the song — no ASR fallback needed
        var song = new Audio { Name = "Lazy Bones", Id = Guid.NewGuid() };

        _fx.SetupUserMock();

        var searchTerms = new List<string>();
        _fx.LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => searchTerms.Add(q.SearchTerm))
            .Returns(new List<BaseItem> { song });

        _fx.Config.AsrCompoundWordFixEnabled = true;

        var handler = CreateHandler();
        var request = CreateSongIntentRequest("lazy bones");
        var context = _fx.CreateContext();
        var user = _fx.CreateUser();
        var session = _fx.CreateSession();

        // Act
        SkillResponse response = await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // Assert: playback started
        Assert.NotNull(response.Response?.Directives);
        Assert.NotEmpty(response.Response.Directives);
        Assert.True(response.Response.ShouldEndSession);

        // Only the original search — no ASR variants attempted
        Assert.Single(searchTerms);
        Assert.Equal("lazy bones", searchTerms[0]);
    }
}
