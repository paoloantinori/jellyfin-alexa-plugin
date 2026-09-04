using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Moq;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

internal static class TestHelpers
{
    internal static Entities.User CreateTestUser(
        Guid? id = null,
        string invocationName = "test",
        string jellyfinToken = "test-token",
        IReadOnlyList<string>? allowedLibraryIds = null)
    {
        return new Entities.User
        {
            Id = id ?? Guid.NewGuid(),
            InvocationName = invocationName,
            JellyfinToken = jellyfinToken,
            AllowedLibraryIds = allowedLibraryIds?.ToList()
        };
    }

    internal static DeviceToken CreateTestDeviceToken(
        string accessToken = "access",
        string refreshToken = "refresh",
        string tokenType = "Bearer",
        long expireTimestamp = 12345)
    {
        return new DeviceToken(accessToken, refreshToken, tokenType, expireTimestamp);
    }

    internal static void SetServerAddress(PluginConfiguration config, string address)
    {
        config.ServerAddress = address;
    }

    internal static SessionInfo CreateTestSession(ISessionManager sessionManager, ILoggerFactory loggerFactory)
    {
        return new SessionInfo(sessionManager, loggerFactory.CreateLogger<SessionInfo>());
    }

    /// <summary>
    /// Non-blocking form of <c>task.Wait(delay)</c>: true when the task
    /// completed within the delay. Used by the concurrency tests for the
    /// "cannot have completed yet" assertions (JF-449).
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <param name="delay">How long to observe it.</param>
    /// <returns>True when the task completed within the delay.</returns>
    internal static async Task<bool> CompletedWithinAsync(Task task, TimeSpan delay)
        => await Task.WhenAny(task, Task.Delay(delay)).ConfigureAwait(false) == task;

    internal static Context CreateTestContext(string deviceId = "test-device")
    {
        return new Context
        {
            System = new global::Alexa.NET.Request.AlexaSystem
            {
                User = new global::Alexa.NET.Request.User { AccessToken = Guid.NewGuid().ToString() },
                Device = new Device { DeviceID = deviceId }
            }
        };
    }

    internal static Context CreateContextWithApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>
                    {
                        { "Alexa.Presentation.APL", new { } }
                    }
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    internal static Context CreateContextWithoutApl()
    {
        return new Context
        {
            System = new AlexaSystem
            {
                Device = new Device
                {
                    DeviceID = "test-device",
                    SupportedInterfaces = new Dictionary<string, object>()
                },
                ApiAccessToken = "test-token",
                Application = new Application { ApplicationId = "test-app" }
            }
        };
    }

    /// <summary>
    /// The first AudioPlayer.Play directive of a response, or null (shared by the
    /// cross-media fallback test suites).
    /// </summary>
    internal static AudioPlayerPlayDirective? GetPlayDirective(SkillResponse response)
        => response.Response?.Directives?.FirstOrDefault(d => d is AudioPlayerPlayDirective) as AudioPlayerPlayDirective;

    /// <summary>
    /// The speech text of a response whose OutputSpeech may legitimately be null (a
    /// silent AudioPlayer start): null means no speech at all, which is exactly what
    /// the announce-off tests want to prove.
    /// </summary>
    internal static string? GetSpeechTextOrNull(SkillResponse response)
        => (response.Response?.OutputSpeech as PlainTextOutputSpeech)?.Text;

    /// <summary>
    /// Extract speech text from a SkillResponse, handling both plain text and SSML output.
    /// Strips SSML markup for content assertions.
    /// </summary>
    internal static string GetSpeechText(SkillResponse response)
    {
        if (response.Response.OutputSpeech is SsmlOutputSpeech ssml)
        {
            string raw = ssml.Ssml;
            raw = raw.Replace("<speak>", string.Empty).Replace("</speak>", string.Empty);
            raw = Regex.Replace(raw, "<break[^>]*>", " ");
            raw = Regex.Replace(raw, "<emphasis[^>]*>", string.Empty);
            raw = raw.Replace("</emphasis>", string.Empty);
            raw = Regex.Replace(raw, "<say-as[^>]*>", string.Empty);
            raw = raw.Replace("</say-as>", string.Empty);
            raw = Regex.Replace(raw, "<prosody[^>]*>", string.Empty);
            raw = raw.Replace("</prosody>", string.Empty);
            raw = Regex.Replace(raw, @"\s+", " ").Trim();
            return raw;
        }

        var speech = global::Xunit.Assert.IsType<PlainTextOutputSpeech>(response.Response.OutputSpeech);
        return speech.Text;
    }

    /// <summary>
    /// Asserts that the response keeps the session open (ShouldEndSession is explicitly false).
    /// This is more precise than checking null || false — ResponseBuilder.Ask() always sets false.
    /// </summary>
    internal static void AssertSessionOpen(SkillResponse response, string message = "Session should remain open")
    {
        global::Xunit.Assert.NotNull(response);
        global::Xunit.Assert.False(response.Response.ShouldEndSession ?? true, message);
    }

    /// <summary>
    /// Mints <c>&lt;suffix&gt;-&lt;guid&gt;</c> under the temp path, creates it, and
    /// registers it with PluginTempDirCleanup for deletion at process exit
    /// (JF-453/JF-486). Test code minting GUID temp dirs MUST go through this
    /// helper so the Register call cannot be forgotten.
    /// </summary>
    internal static string CreateRegisteredTempDir(string suffix)
    {
        var dir = Path.Combine(Path.GetTempPath(), suffix + "-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        PluginTempDirCleanup.Shared.Register(dir);
        return dir;
    }

    /// <summary>
    /// Sets Plugin.Instance with the provided configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration. When the instance already exists,
    /// only the specific flag is synced via <paramref name="syncFlag"/>. The temp
    /// dir minted for the mocked paths is registered with PluginTempDirCleanup for
    /// deletion at process exit (JF-453).
    /// </summary>
    internal static void EnsurePluginInstance(
        PluginConfiguration config,
        ILoggerFactory loggerFactory,
        Action<PluginConfiguration> syncFlag,
        string tempDirSuffix)
    {
        if (Plugin.Instance != null)
        {
            syncFlag(Plugin.Instance.Configuration);
            return;
        }

        var tmpDir = CreateRegisteredTempDir(tempDirSuffix);

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.PluginsPath).Returns(tmpDir);
        appPaths.Setup(p => p.PluginConfigurationsPath).Returns(tmpDir);
        appPaths.Setup(p => p.DataPath).Returns(tmpDir);
        appPaths.Setup(p => p.CachePath).Returns(tmpDir);
        appPaths.Setup(p => p.LogDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.ConfigurationDirectoryPath).Returns(tmpDir);
        appPaths.Setup(p => p.SystemConfigurationFilePath).Returns(Path.Combine(tmpDir, "system.xml"));
        appPaths.Setup(p => p.ProgramDataPath).Returns(tmpDir);
        appPaths.Setup(p => p.ProgramSystemPath).Returns(tmpDir);
        appPaths.Setup(p => p.TempDirectory).Returns(tmpDir);
        appPaths.Setup(p => p.VirtualDataPath).Returns(tmpDir);

        var xmlSerializer = new Mock<IXmlSerializer>();
        xmlSerializer
            .Setup(x => x.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>()))
            .Returns(config);

        var userManager = new Mock<IUserManager>();

        var plugin = new Plugin(
            appPaths.Object,
            xmlSerializer.Object,
            loggerFactory,
            userManager.Object);

        plugin.Configuration.ServerAddress = "http://localhost:8096";
    }

    /// <summary>
    /// Polls <paramref name="condition"/> until it returns true or the timeout
    /// elapses (JF-419.3: shared by the index services' timing tests instead of
    /// duplicating the deadline/while loop).
    /// </summary>
    /// <returns>True when the condition was met before the timeout.</returns>
    internal static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null, int pollMs = 25)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(pollMs).ConfigureAwait(false);
        }

        return condition();
    }

    /// <summary>
    /// The ONE hardware Play-button request (PlaybackControllerRequest's request
    /// type is read-only, so it is deserialized from the wire JSON). Was an inline
    /// literal in PauseResumeStateTests and DispatchRoutingTests (JF-433 simplify).
    /// </summary>
    internal static global::Alexa.NET.Request.Type.PlaybackControllerRequest CreatePlayCommand()
    {
        const string json = @"{""requestId"":""test"",""type"":""PlaybackController.PlayCommandIssued"",""timestamp"":""2024-01-01T00:00:00Z"",""locale"":""en-US"",""playbackRequestMethod"":""PLAY""}";
        return Newtonsoft.Json.JsonConvert.DeserializeObject<global::Alexa.NET.Request.Type.PlaybackControllerRequest>(json)!;
    }

    /// <summary>
    /// JF-440: the ONE song-index fake (was duplicated as FakeSongIndex in
    /// PlayArtistSongsIntentHandlerTests and FakeNgramIndex in PlaySongTitleFallbackTests).
    /// Returns the fixed scored set from both stages (no test distinguishes them).
    /// </summary>
    internal sealed class FakeSongIndex : ISongNgramIndex
    {
        private readonly List<(BaseItem Item, double Score)> _results;
        public bool IsReady => true;
        public bool IsDisabled => false;
        public int SongCount => _results.Count;
        public int NgramCount => _results.Count;

        public FakeSongIndex(params (BaseItem Item, double Score)[] results) => _results = results.ToList();

        public List<(BaseItem Item, double Score)> Search(string[] keywordTokens, string locale, Guid[]? topParentIds = null) => _results;
        public List<(BaseItem Item, double Score)> SearchPhonetic(string[] keywordTokens, string locale, Guid[]? topParentIds = null) => _results;
    }
}

/// <summary>
/// JF-446: the ONE ready artist-index fake. History: one private copy lived in
/// ArtistSearchTests at HEAD and was hoisted here (unchanged shape) so the new
/// JF-446 gate tests could share it. Ready by default (pass
/// <c>isReady: false</c> for warming-gate shapes); phonetic codes are optional so a
/// test can exercise either the phonetic or the plain FuzzyMatcher overload.
/// </summary>
internal sealed class FakeArtistIndex : IArtistIndex
{
    private readonly IReadOnlyList<BaseItem> _artists;
    private readonly Dictionary<Guid, (string Primary, string? Alternate)> _phoneticCodes;
    private readonly bool _isReady;

    public FakeArtistIndex(
        IEnumerable<BaseItem> artists,
        Dictionary<Guid, (string Primary, string? Alternate)>? phoneticCodes = null,
        bool isReady = true)
    {
        _artists = artists.ToList();
        _phoneticCodes = phoneticCodes ?? new Dictionary<Guid, (string Primary, string? Alternate)>();
        _isReady = isReady;
    }

    public bool IsReady => _isReady;
    public bool IsDisabled => false;
    public int Count => _artists.Count;

    public IReadOnlyList<BaseItem> GetArtists(Guid[]? topParentIds = null) => _artists;

    public bool TryGetPhoneticCode(Guid artistId, out (string Primary, string? Alternate) codes)
    {
        codes = default;
        return _phoneticCodes.TryGetValue(artistId, out codes);
    }

    // The fake's state is fixed per instance, so it is already pinned: capture is the
    // identity (the same contract ArtistIndexService.SnapshotView honors).
    public IArtistIndex CaptureSnapshot() => this;
}

/// <summary>
/// JF-465: the ONE handler-test fixture (the six standard mocks + config +
/// logger factory, and the SetupUserMock/CreateSession/CreateContext/CreateUser
/// builders). History: the block was copy-pasted across ~33 handler test classes
/// (seed: PlayByGenreFallbackTests vs PlayByGenreIntentHandlerTests, near
/// line-for-line) so every optional-ctor-dep edit touched every copy. Test classes
/// hold one instance and compose it, keeping their handler-specific CreateHandler
/// ctor call local. Handler subsets beyond these six mocks (extra indexes, queue
/// managers) stay as local fields in the owning test class.
/// </summary>
internal sealed class HandlerTestFixture
{
    internal Mock<ISessionManager> SessionManager { get; }
    internal Mock<ILibraryManager> LibraryManager { get; }
    internal Mock<IUserManager> UserManager { get; }
    internal Mock<IUserDataManager> UserDataManager { get; }
    internal PluginConfiguration Config { get; }
    internal ILoggerFactory LoggerFactory { get; }

    /// <summary>
    /// <paramref name="serverAddress"/> defaults to the shared test address; the few
    /// suites that pin localhost pass it explicitly, and passing null leaves the
    /// address untouched (the no-address suites). <paramref name="configure"/> runs
    /// BEFORE the address is set so a suite can pin any other flag it needs.
    /// </summary>
    internal HandlerTestFixture(
        string? serverAddress = "https://test.example.com",
        Action<PluginConfiguration>? configure = null)
    {
        SessionManager = new Mock<ISessionManager>();
        LibraryManager = new Mock<ILibraryManager>();
        UserManager = new Mock<IUserManager>();
        UserDataManager = new Mock<IUserDataManager>();
        Config = new PluginConfiguration();
        configure?.Invoke(Config);
        if (serverAddress != null)
        {
            TestHelpers.SetServerAddress(Config, serverAddress);
        }

        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => { });
    }

    /// <summary>
    /// The standard GetUserById stub: a Jellyfin user named "testuser", so
    /// ResolveJellyfinUser-style paths have someone to resolve.
    /// </summary>
    internal void SetupUserMock()
        => UserManager.Setup(u => u.GetUserById(It.IsAny<Guid>()))
            .Returns(new Jellyfin.Database.Implementations.Entities.User("testuser", "test", "test"));

    /// <summary>
    /// Mocks the JF-411 indefinite album-by-artist flow on the fixture's LibraryManager:
    /// artist lookup, the artist's albums in the given (insertion) order, per-album
    /// track counts for the JF-443 COUNT queries, and per-album playback results. The
    /// count semantics mirror the TWO server mechanisms (Jellyfin BaseItemRepository
    /// 10.11.8/10.11.11): ParentId answers by entity link (well-formed albums),
    /// AlbumIds answers by matching the track's RAW Album tag against the album
    /// entity's Name (f.Name == e.Album; the JF-338 malformed-folder shape). Queries
    /// are recorded into <paramref name="queries"/> (when given) for assertions.
    /// Hoisted from PlayAlbumIntentHandlerTests for the DialogDelegation slim
    /// (JF-442); pair it with the caller's own GetPlayedTrackToken-style extraction.
    /// </summary>
    internal void SetupIndefiniteAlbumCatalog(
        BaseItem artist,
        List<BaseItem> artistAlbums,
        List<BaseItem> allTracks,
        IReadOnlyDictionary<Guid, BaseItem> firstTrackByAlbumId,
        List<InternalItemsQuery>? queries = null)
    {
        var albumNameById = artistAlbums.ToDictionary(a => a.Id, a => a.Name);

        LibraryManager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicArtist) == true)
                {
                    return new List<BaseItem> { artist };
                }

                if (q.IncludeItemTypes?.Contains(BaseItemKind.MusicAlbum) == true)
                {
                    return artistAlbums;
                }

                return new List<BaseItem>();
            });

        LibraryManager.Setup(l => l.GetItemsResult(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
            {
                queries?.Add(q);
                // JF-443 count queries are COUNT-only (Limit=0): the ParentId primary
                // counts by entity link, the AlbumIds fallback by raw-tag name match.
                if (q.Limit == 0)
                {
                    int count = q.ParentId != Guid.Empty
                        ? allTracks.Count(t => t.ParentId == q.ParentId)
                        : allTracks.Count(t => q.AlbumIds is { Length: > 0 }
                            && albumNameById.TryGetValue(q.AlbumIds[0], out string? albumName)
                            && string.Equals(t.Album, albumName, StringComparison.Ordinal));
                    return new QueryResult<BaseItem>
                    {
                        Items = new List<BaseItem>(),
                        TotalRecordCount = count
                    };
                }

                // Playback page queries (nonzero Limit): ParentId first, then the JF-338
                // AlbumIds retry when the folder link finds nothing.
                Guid playKey = q.ParentId != Guid.Empty
                    ? q.ParentId
                    : q.AlbumIds is { Length: > 0 } ? q.AlbumIds[0] : Guid.Empty;
                return firstTrackByAlbumId.TryGetValue(playKey, out BaseItem? track)
                    ? new QueryResult<BaseItem> { Items = new[] { track }, TotalRecordCount = 1 }
                    : new QueryResult<BaseItem> { Items = new List<BaseItem>(), TotalRecordCount = 0 };
            });
    }

    internal SessionInfo CreateSession() => TestHelpers.CreateTestSession(SessionManager.Object, LoggerFactory);

    internal Context CreateContext() => TestHelpers.CreateTestContext();

    internal Entities.User CreateUser() => TestHelpers.CreateTestUser();
}

/// <summary>
/// JF-467: the ONE shared-gate probe handler. History: a private copy lived in
/// CrossMediaFallbackMusicGateTests at HEAD and was hoisted here (plus the album
/// cascade accessor) so MusicPrimaryPathGateTests could share it. Minimal concrete
/// BaseHandler exposing the protected shared cross-media gates for direct testing
/// (same pattern as ContentAccessTests' TestMediaTypeHandler).
/// </summary>
internal sealed class SharedGateProbeHandler : BaseHandler
{
    public SharedGateProbeHandler(ISessionManager sessionManager, PluginConfiguration config, ILoggerFactory loggerFactory)
        : base(sessionManager, config, loggerFactory)
    {
    }

    public override bool CanHandle(Request request) => true;

    public override Task<SkillResponse> HandleAsync(Request request, Context context, Entities.User user, SessionInfo session, CancellationToken cancellationToken)
        => Task.FromResult(ResponseBuilder.Tell("test"));

    public Task<SkillResponse?> CallTryEntityFallbackAsync(
        string slotText,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        string logLabel,
        CancellationToken cancellationToken)
        => TryEntityFallbackAsync(
            slotText, jellyfinUser, user, session, context, locale,
            libraryManager, userDataManager, null, null, logLabel, cancellationToken);

    public Task<SkillResponse?> CallTryAlbumFallbackAsync(
        string slotText,
        Jellyfin.Database.Implementations.Entities.User jellyfinUser,
        Entities.User user,
        SessionInfo session,
        Context context,
        string locale,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        string logLabel,
        CancellationToken cancellationToken)
        => TryAlbumFallbackAsync(
            slotText, jellyfinUser, user, session, context, locale,
            libraryManager, userDataManager, null, logLabel, cancellationToken);
}
