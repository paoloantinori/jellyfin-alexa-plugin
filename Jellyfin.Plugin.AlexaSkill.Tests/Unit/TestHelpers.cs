using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alexa.NET;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Entities;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
    /// Sets Plugin.Instance with the provided configuration so IfFeatureDisabled
    /// can read from Plugin.Instance.Configuration. When the instance already exists,
    /// only the specific flag is synced via <paramref name="syncFlag"/>.
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

        var tmpDir = Path.Combine(Path.GetTempPath(), tempDirSuffix + "-" + Guid.NewGuid());
        Directory.CreateDirectory(tmpDir);

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
