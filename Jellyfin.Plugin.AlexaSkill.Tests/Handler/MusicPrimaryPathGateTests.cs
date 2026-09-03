#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
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
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-467: the PRIMARY paths of the four core music intents (PlaySong, PlayAlbum,
/// FindSong, PlayMoodMusic) play music directly, so the global music flag
/// (PluginConfiguration.MusicEnabled, global-only: no per-user override exists)
/// gates each handler at ENTRY via BaseHandler.IfMediaTypeDisabled (the shared
/// disabled-type response, "MediaTypeNotAvailable"). JF-464 closed only the
/// cross-media fallback slice; these tests pin the primary-path gates: music
/// disabled + a VALID slot speaks the disabled-type response, sends no
/// AudioPlayer directive, and issues ZERO library queries (the library mocks
/// always ARM the would-be hit, so a pass proves the gate stopped the path, not
/// that the library was empty). The empty-slot precedence is pinned too: a
/// music-disabled user with NO input still gets the slot prompt. With music
/// enabled, the control test proves the gate is a no-op pass-through.
/// </summary>
[Collection("Plugin")]
public class MusicPrimaryPathGateTests : PluginTestBase, IDisposable
{
    private readonly Mock<ISessionManager> _sessionManagerMock;
    private readonly Mock<ILibraryManager> _libraryManagerMock;
    private readonly Mock<IUserManager> _userManagerMock;
    private readonly Mock<IUserDataManager> _userDataManagerMock;
    private readonly PluginConfiguration _config;
    private readonly ILoggerFactory _loggerFactory;
    private readonly List<InternalItemsQuery> _queries = new();

    public MusicPrimaryPathGateTests()
    {
        _sessionManagerMock = new Mock<ISessionManager>();
        _libraryManagerMock = new Mock<ILibraryManager>();
        _userManagerMock = new Mock<IUserManager>();
        _userDataManagerMock = new Mock<IUserDataManager>();
        _config = new PluginConfiguration();
        TestHelpers.SetServerAddress(_config, "https://test.example.com");
        _loggerFactory = LoggerFactory.Create(b => { });
        TestHelpers.EnsurePluginInstance(
            _config, _loggerFactory,
            c => c.MusicEnabled = _config.MusicEnabled,
            "alexa-music-primary-gate-test");

        // Record every issued query: the disabled-flag assertions prove the whole
        // path never RUNS, not merely that it returned nothing.
        _libraryManagerMock.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns<InternalItemsQuery>(q =>
            {
                _queries.Add(q);
                return AnswerQuery(q);
            });
    }

    public void Dispose() => _loggerFactory.Dispose();

    // Handler 1: PlaySong. The armed song (Audio + SearchTerm) would play on the
    // first query if the gate let the path run.
    [Fact]
    public async Task PlaySong_MusicDisabled_SpeaksDisabledResponse_NoQuery()
    {
        DisableMusic();
        SetupUserMock();

        var handler = CreateSongHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlaySong, ("song", "bohemian rhapsody")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Handler 2: PlayAlbum. The armed album (MusicAlbum) and its tracks would
    // play on the first two queries if the gate let the path run.
    [Fact]
    public async Task PlayAlbum_MusicDisabled_SpeaksDisabledResponse_NoQuery()
    {
        DisableMusic();
        SetupUserMock();

        var handler = CreateAlbumHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayAlbum, ("album", "dark side of the moon")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Handler 3: FindSong. A first invocation WITH keywords (valid slot) is a
    // searching turn: gated at the shared entry, before the n-gram index and the
    // DB fallback.
    [Fact]
    public async Task FindSong_MusicDisabled_SpeaksDisabledResponse_NoQuery()
    {
        DisableMusic();
        SetupUserMock();

        var handler = CreateFindSongHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.FindSongIntent, ("titleKeywords", "bohemian rhapsody")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Handler 3, empty-slot precedence: a BARE FindSong invocation (no session
    // state, no slot values) still gets the keywords prompt, not the disabled
    // message. The prompt path issues no query either way.
    [Fact]
    public async Task FindSong_MusicDisabled_BareInvocationStillPrompts()
    {
        DisableMusic();
        SetupUserMock();

        var handler = CreateFindSongHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.FindSongIntent),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        TestHelpers.AssertSessionOpen(response);
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.DoesNotContain("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_queries);
    }

    // Handler 5: PlayArtistSongs (found ungated by the final review pass, same
    // class as the other four). The armed artist search would play on the first
    // inline-tier query if the gate let the path run.
    [Fact]
    public async Task PlayArtistSongs_MusicDisabled_SpeaksDisabledResponse_NoQuery()
    {
        DisableMusic();
        SetupUserMock();

        var handler = new PlayArtistSongsIntentHandler(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayArtistSongs, ("musician", "pink floyd")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Handler 4: PlayMoodMusic. The armed genre tracks (Audio + Genres) would
    // play on the first mood query if the gate let the path run; the gate also
    // covers the SearchByArtistGenreAsync tier and the cross-media fallback
    // behind it (JF-464) without any query.
    [Fact]
    public async Task PlayMoodMusic_MusicDisabled_SpeaksDisabledResponse_NoQuery()
    {
        DisableMusic();
        SetupUserMock();

        var handler = CreateMoodHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlayMoodMusic, ("mood", "relaxing")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Control: music enabled + armed hit still plays (the gate is a pass-through
    // no-op when the flag is on; byte-identical enabled behavior, JF-467 AC).
    [Fact]
    public async Task PlaySong_MusicEnabled_ArmedHitStillPlays()
    {
        SetupUserMock();

        var handler = CreateSongHandler();
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlaySong, ("song", "bohemian rhapsody")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        Assert.NotNull(TestHelpers.GetPlayDirective(response));
        Assert.NotEmpty(_queries);
    }

    // Read-source alignment (JF-467, binding implementation note): the handler's
    // INJECTED configuration still says music enabled, but the LIVE
    // Plugin.Instance configuration (what a standard-API configuration
    // replacement mutates; handlers are DI singletons that capture _config once)
    // says disabled. The entry gate must read the live one and fire.
    // BasePlugin.Configuration's setter is private (verified via reflection), so
    // the "replaced object" is simulated as a distinct configuration object that
    // disagrees with the injected copy: the pinned property is the same.
    [Fact]
    public async Task EntryGate_ReadsLivePluginConfig_NotInjectedCopy()
    {
        Plugin.Instance!.Configuration.MusicEnabled = false;
        var staleConfig = new PluginConfiguration();
        TestHelpers.SetServerAddress(staleConfig, "https://test.example.com");
        SetupUserMock();

        var handler = CreateSongHandler(staleConfig);
        SkillResponse response = await handler.HandleAsync(
            CreateIntent(IntentNames.PlaySong, ("song", "bohemian rhapsody")),
            CreateContext(), CreateUser(), CreateSession(), CancellationToken.None);

        AssertDisabledTell(response);
        Assert.Empty(_queries);
    }

    // Read-source alignment, JF-464 shared gate: TryEntityFallbackAsync reads the
    // same live source (BaseHandler.IsMusicEnabled) after the JF-467 switch, not
    // the injected copy. Old code (injected _config, music enabled here) would
    // have run the artist search; the live read stops it with zero queries.
    [Fact]
    public async Task SharedFallbackGate_ReadsLivePluginConfig_NotInjectedCopy()
    {
        Plugin.Instance!.Configuration.MusicEnabled = false;
        var staleConfig = new PluginConfiguration();
        TestHelpers.SetServerAddress(staleConfig, "https://test.example.com");

        var probe = new SharedGateProbeHandler(_sessionManagerMock.Object, staleConfig, _loggerFactory);
        SkillResponse? result = await probe.CallTryEntityFallbackAsync(
            "abbey road", CreateUserJellyfin(), CreateUser(), CreateSession(), CreateContext(), "en-US",
            _libraryManagerMock.Object, _userDataManagerMock.Object, "read-source probe", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_queries);
    }

    // Read-source alignment, null tolerance: when Plugin.Instance is absent
    // (off-host unit tests), both gates fall back to the injected configuration
    // instead of throwing or silently passing.
    [Fact]
    public async Task SharedFallbackGate_FallsBackToInjectedConfig_WhenInstanceAbsent()
    {
        Plugin.ResetInstance();
        var injected = new PluginConfiguration { MusicEnabled = false };
        TestHelpers.SetServerAddress(injected, "https://test.example.com");

        var probe = new SharedGateProbeHandler(_sessionManagerMock.Object, injected, _loggerFactory);
        SkillResponse? result = await probe.CallTryEntityFallbackAsync(
            "abbey road", CreateUserJellyfin(), CreateUser(), CreateSession(), CreateContext(), "en-US",
            _libraryManagerMock.Object, _userDataManagerMock.Object, "fallback probe", CancellationToken.None);

        Assert.Null(result);
        Assert.Empty(_queries);
    }

    private static IntentRequest CreateIntent(string intentName, params (string Name, string Value)[] slots)
    {
        var intent = new Intent { Name = intentName };
        if (slots.Length > 0)
        {
            intent.Slots = new Dictionary<string, Slot>();
            foreach ((string name, string value) in slots)
            {
                intent.Slots[name] = new Slot { Name = name, Value = value };
            }
        }

        return new IntentRequest { Intent = intent, Locale = "en-US", RequestId = "test-req" };
    }

    private PlaySongIntentHandler CreateSongHandler(PluginConfiguration? config = null)
        => new(
            _sessionManagerMock.Object,
            config ?? _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private PlayAlbumIntentHandler CreateAlbumHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private FindSongIntentHandler CreateFindSongHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private PlayMoodMusicIntentHandler CreateMoodHandler()
        => new(
            _sessionManagerMock.Object,
            _config,
            _libraryManagerMock.Object,
            _userManagerMock.Object,
            _userDataManagerMock.Object,
            _loggerFactory);

    private static Context CreateContext() => TestHelpers.CreateTestContext();
    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_sessionManagerMock.Object, _loggerFactory);
    private static Entities.User CreateUser() => TestHelpers.CreateTestUser();
    private static Jellyfin.Database.Implementations.Entities.User CreateUserJellyfin()
        => new("testuser", "test", "test");

    private void SetupUserMock()
    {
        _userManagerMock.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(CreateUserJellyfin());
    }

    /// <summary>
    /// Disables the global music flag on both references. BOTH writes are
    /// load-bearing: when EnsurePluginInstance created the Plugin the two configs
    /// are the same object, but when an instance pre-existed they differ.
    /// </summary>
    private void DisableMusic()
    {
        _config.MusicEnabled = false;
        Plugin.Instance!.Configuration.MusicEnabled = false;
    }

    /// <summary>
    /// The shared disabled-response assertions: the localized MediaTypeNotAvailable
    /// text (a Tell, so the session ends and the message is terminal) and no
    /// AudioPlayer directive.
    /// </summary>
    private static void AssertDisabledTell(SkillResponse response)
    {
        Assert.NotNull(response);
        Assert.True(response.Response.ShouldEndSession ?? true, "the disabled response must end the session (Tell)");
        Assert.Null(TestHelpers.GetPlayDirective(response));
        Assert.Contains("not available", TestHelpers.GetSpeechText(response), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Arms EVERY would-be hit on the gated paths: the song search (Audio), the
    /// album search (MusicAlbum) and its tracks (Audio under a ParentId), the
    /// mood genre tracks (Audio + Genres), and the cross-media artist fallback
    /// (MusicArtist + songs). A passing disabled-flag test therefore proves the
    /// GATE stopped the path, never an empty library.
    /// </summary>
    private List<BaseItem> AnswerQuery(InternalItemsQuery q)
    {
        bool isArtistQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicArtist);
        bool isAudioQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.Audio);
        bool isAlbumQuery = q.IncludeItemTypes != null && q.IncludeItemTypes.Contains(BaseItemKind.MusicAlbum);
        bool hasGenres = q.Genres != null && q.Genres.Count > 0;

        // Mood genre track search (Audio + Genres): armed with playable tracks.
        if (hasGenres && isAudioQuery)
        {
            return new List<BaseItem> { new Audio { Name = "Armed Mood Track", Id = Guid.NewGuid() } };
        }

        // Cross-media artist fallback (MusicArtist, no Genres) and its songs.
        if (isArtistQuery)
        {
            return new List<BaseItem> { new MusicArtist { Name = "Armed Artist", Id = Guid.NewGuid() } };
        }

        // Album search (MusicAlbum) and album tracks (Audio under the album's ParentId).
        if (isAlbumQuery)
        {
            return new List<BaseItem> { new MusicAlbum { Name = "Armed Album", Id = Guid.NewGuid() } };
        }

        if (isAudioQuery)
        {
            return new List<BaseItem> { new Audio { Name = "Bohemian Rhapsody", Id = Guid.NewGuid() } };
        }

        return new List<BaseItem>();
    }
}
