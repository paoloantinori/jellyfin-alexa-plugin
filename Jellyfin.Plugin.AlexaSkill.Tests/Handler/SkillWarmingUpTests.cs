using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Exceptions;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler.Intent;
using Jellyfin.Plugin.AlexaSkill.Alexa.Pipeline;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-419/JF-419.2: the warming gate is two-layered. Layer 1: handlers that run cold
/// NON-artist queries first (title/album/keyword/mood searches on the same cold
/// database) call BaseHandler.GuardIndexReady at entry (a thin wrap of
/// IndexWarmingGate.EnsureReady; the authoritative roster of gated handlers is
/// WarmingGateCoverageTests' assembly scan, not any hand-maintained list), before
/// their "searching" announcement. Layer 2: ArtistSearch.SearchAsync itself throws
/// <see cref="SkillWarmingUpException"/>, covering every caller including BaseHandler
/// fallbacks. The request pipeline translates the throw into the session-ending
/// SkillWarmingUp Tell; MediaInfo's enrichment catch degrades instead.
/// </summary>
[Collection("Plugin")]
public class SkillWarmingUpTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture();

    public SkillWarmingUpTests()
    {
        // PluginTestBase resets Plugin.Instance; the pipeline path reads
        // Plugin.Instance.Configuration.ServerAddress during session resolution.
        // Pass the SAME config/factory so the class has one live instance.
        EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            cfg => { },
            "warming-tests");
    }

    private static IntentRequest CreateIntentRequest(string intentName, string? musician = null, string? song = null, string? titleKeywords = null, string? genre = null)
    {
        var intent = new Intent { Name = intentName };
        intent.Slots = new Dictionary<string, Slot>();
        if (musician != null)
        {
            intent.Slots["musician"] = new Slot { Name = "musician", Value = musician };
        }

        if (genre != null)
        {
            intent.Slots["genre"] = new Slot { Name = "genre", Value = genre };
        }

        // AddToQueue/PlayNext read Slots["song"] with the indexer (KeyNotFoundException
        // when absent) and refuse EMPTY song values before reaching the artist search.
        if (song != null)
        {
            intent.Slots["song"] = new Slot { Name = "song", Value = song };
        }

        if (titleKeywords != null)
        {
            intent.Slots["titleKeywords"] = new Slot { Name = "titleKeywords", Value = titleKeywords };
        }

        return new IntentRequest { Intent = intent, Locale = "it-IT", RequestId = "test-req" };
    }

    /// <summary>
    /// Layer-1 reachability: the entry points that previously had NO gate (AddToQueue,
    /// QueryArtistLibrary, PlayNext, PlayByGenre) now refuse at entry via the shared
    /// guard. The PlaySong/PlayAlbum/FindSong/SearchMedia/PlayMoodMusic entry gates and
    /// the PlayArtistSongs inline-path guard follow the identical one-line shape; the
    /// choke-point throw itself is proven in ArtistSearchTests.
    /// </summary>
    [Fact]
    public Task AddToQueue_WhileIndexWarming_ThrowsAtEntry()
        => AssertEntryGateFiresAsync(artistIndex => new AddToQueueIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.LoggerFactory, artistIndex: artistIndex),
            CreateIntentRequest(IntentNames.AddToQueue, musician: "pink floyd", song: "shine on you crazy diamond"));

    [Fact]
    public Task QueryArtistLibrary_WhileIndexWarming_ThrowsAtEntry()
        => AssertEntryGateFiresAsync(artistIndex => new QueryArtistLibraryIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
                artistIndex: artistIndex),
            CreateIntentRequest(IntentNames.QueryArtistLibrary, musician: "pink floyd"));

    [Fact]
    public Task PlayNext_WhileIndexWarming_ThrowsAtEntry()
        => AssertEntryGateFiresAsync(artistIndex => new PlayNextIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.LoggerFactory, artistIndex: artistIndex),
            CreateIntentRequest(IntentNames.PlayNext, musician: "pink floyd", song: "shine on you crazy diamond"));

    [Fact]
    public Task PlayByGenre_WhileIndexWarming_ThrowsAtEntry()
        => AssertEntryGateFiresAsync(artistIndex => new PlayByGenreIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
                artistIndex: artistIndex),
            CreateIntentRequest(IntentNames.PlayByGenre, genre: "jazz"));

    private async Task AssertEntryGateFiresAsync(
        Func<IArtistIndex, BaseHandler> createHandler,
        IntentRequest request)
    {
        BaseHandler handler = createHandler(Mock.Of<IArtistIndex>(i => i.IsReady == false));
        _fx.SetupUserMock();

        await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(request, TestHelpers.CreateTestContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None));
    }

    private Context CreateAuthenticatableContext(Entities.User user)
        => new()
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = user.Id.ToString() },
                Device = new Device { DeviceID = "test-device" }
            }
        };

    /// <summary>
    /// Drives a handler through the REAL pipeline (auth + session resolution +
    /// translation): one shared seam test for the handler-throw-to-Tell contract.
    /// </summary>
    private async Task<SkillResponse> ExecuteViaPipelineAsync(BaseHandler handler, IntentRequest request)
    {
        var user = TestHelpers.CreateTestUser();
        _fx.Config.Users.Add(user);

        // HandleRequestAsync resolves the Jellyfin session before HandleAsync runs
        _fx.SessionManager
            .Setup(s => s.GetSessionByAuthenticationToken(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(_fx.CreateSession());

        var pipeline = new RequestPipeline(
            Array.Empty<IRequestInterceptor>(),
            Array.Empty<IResponseInterceptor>(),
            _fx.LoggerFactory.CreateLogger<RequestPipeline>());

        return await pipeline.ExecuteAsync(handler, request, CreateAuthenticatableContext(user), null, CancellationToken.None);
    }

    private static void AssertWarmingTell(SkillResponse response)
    {
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession);
        var speech = (response.Response.OutputSpeech as PlainTextOutputSpeech)?.Text ?? string.Empty;
        Assert.Contains("preparando", speech, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pipeline translates the choke point exception into the user-facing
    /// session-ending warming Tell (the single response-building site).
    /// </summary>
    [Fact]
    public async Task Pipeline_TranslatesWarmingExceptionToWarmingTell()
    {
        var handler = new WarmingStubHandler(_fx.SessionManager.Object, _fx.Config, _fx.LoggerFactory);
        var request = CreateIntentRequest(IntentNames.PlayArtistSongs, musician: "pink floyd");

        SkillResponse response = await ExecuteViaPipelineAsync(handler, request);

        AssertWarmingTell(response);
    }

    /// <summary>
    /// Review round 2: the handler-to-pipeline seam must ALSO be proven with a REAL
    /// handler, not only the stub (a broad catch added inside a handler would be
    /// invisible to the stub test). PlaySong has an entry gate on the song index
    /// (JF-419.3), so the throw travels the real HandleRequestAsync path into the
    /// pipeline catch.
    /// </summary>
    [Fact]
    public async Task Pipeline_RealHandlerWarmingIndex_ReturnsWarmingTell()
    {
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
            _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
            artistIndex: ReadyArtistIndex(),
            songNgramIndex: Mock.Of<ISongNgramIndex>(i => i.IsReady == false));

        // Title-only request: the path whose fast resource IS the song index
        var request = CreateIntentRequest(IntentNames.PlaySong, song: "bohemian rhapsody");

        SkillResponse response = await ExecuteViaPipelineAsync(handler, request);

        AssertWarmingTell(response);
    }

    /// <summary>
    /// JF-419.3: FindSong/PlaySong gate on the index their path actually uses (the
    /// song n-gram index), NOT the artist index. Artist-ready + song-index-warming
    /// is the real post-restart divergence (the song index loads all Audio items and
    /// takes longer): the artist gate would pass and the request would fall to the
    /// cold DB path. These tests need the artist index READY to prove the song gate
    /// is the one firing.
    /// </summary>
    [Fact]
    public Task FindSong_SongIndexWarmingArtistReady_ThrowsAtEntry()
        => AssertSongEntryGateFiresAsync(index => new FindSongIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
                artistIndex: ReadyArtistIndex(), songNgramIndex: index),
            CreateIntentRequest(IntentNames.FindSongIntent, titleKeywords: "cater street"));

    [Fact]
    public Task PlaySong_SongIndexWarmingArtistReady_ThrowsAtEntry()
        => AssertSongEntryGateFiresAsync(index => new PlaySongIntentHandler(
                _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
                _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
                artistIndex: ReadyArtistIndex(), songNgramIndex: index),
            CreateIntentRequest(IntentNames.PlaySong, song: "bohemian rhapsody"));

    /// <summary>
    /// Review round 1 finding 4: a MUSICIAN-scoped PlaySong resolves the artist
    /// first and never touches the song index, so it gates on the ARTIST index -
    /// BEFORE the "searching" announcement (no announcement-then-refusal).
    /// </summary>
    [Fact]
    public Task PlaySong_MusicianScoped_ArtistIndexWarming_ThrowsAtEntry()
    {
        var handler = new PlaySongIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
            _fx.UserManager.Object, _fx.UserDataManager.Object, _fx.LoggerFactory,
            artistIndex: Mock.Of<IArtistIndex>(i => i.IsReady == false),
            songNgramIndex: Mock.Of<ISongNgramIndex>(i => i.IsReady == true));

        return AssertThrowsWarmingAsync(
            handler,
            CreateIntentRequest(IntentNames.PlaySong, musician: "pink floyd", song: "comfortably numb"),
            "artist");
    }

    /// <summary>
    /// Review round 1 finding 1: AddToQueue's song query is an unbounded Audio
    /// SearchTerm scan; during the post-restart window (artist ready, song index
    /// still loading) the song gate must refuse it.
    /// </summary>
    [Fact]
    public Task AddToQueue_SongIndexWarmingArtistReady_ThrowsAtEntry()
    {
        var handler = new AddToQueueIntentHandler(
            _fx.SessionManager.Object, _fx.Config, _fx.LibraryManager.Object,
            _fx.UserManager.Object, _fx.LoggerFactory,
            artistIndex: ReadyArtistIndex(),
            songNgramIndex: Mock.Of<ISongNgramIndex>(i => i.IsReady == false));

        return AssertThrowsWarmingAsync(
            handler,
            CreateIntentRequest(IntentNames.AddToQueue, musician: "pink floyd", song: "shine on you crazy diamond"),
            "song");
    }

    private async Task AssertThrowsWarmingAsync(BaseHandler handler, IntentRequest request, string expectedIndexName)
    {
        _fx.SetupUserMock();
        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(request, TestHelpers.CreateTestContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None));
        Assert.StartsWith(expectedIndexName, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IArtistIndex ReadyArtistIndex()
        => Mock.Of<IArtistIndex>(i => i.IsReady == true);

    private async Task AssertSongEntryGateFiresAsync(
        Func<ISongNgramIndex?, BaseHandler> createHandler,
        IntentRequest request)
    {
        BaseHandler handler = createHandler(Mock.Of<ISongNgramIndex>(i => i.IsReady == false));
        _fx.SetupUserMock();

        var ex = await Assert.ThrowsAsync<SkillWarmingUpException>(() =>
            handler.HandleAsync(request, TestHelpers.CreateTestContext(), TestHelpers.CreateTestUser(), _fx.CreateSession(), CancellationToken.None));
        Assert.Contains("song", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A handler whose HandleAsync throws the warming exception, simulating any
    /// artist-search entry point hitting the choke point.
    /// </summary>
    private sealed class WarmingStubHandler : BaseHandler
    {
        public WarmingStubHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
            : base(sessionManager, config, loggerFactory)
        {
        }

        public override bool CanHandle(Request request) => true;

        public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
            => throw new SkillWarmingUpException("artist");
    }
}
