#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request.Type;
using Jellyfin.Plugin.AlexaSkill.Alexa.Diagnostics;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-393: per-user diagnostic interaction logging. IsEnabled resolves the per-user
/// override to the global default; the per-device marker tracks play requests and
/// playback starts so control intents can report elapsed time since playback started
/// (the JF-392 'alexa stop' data collection).
/// </summary>
[Collection("Plugin")]
public class InteractionDiagnosticsTests : PluginTestBase
{
    private static (Entities.User User, PluginConfiguration Config) Setup(bool? userOverride, bool globalDefault)
    {
        var user = new Entities.User { Id = Guid.NewGuid(), DiagnosticInteractionLogging = userOverride };
        var config = new PluginConfiguration { DefaultDiagnosticInteractionLogging = globalDefault };
        return (user, config);
    }

    [Theory]
    [InlineData(null, false, false)]   // unset user, global off -> off
    [InlineData(null, true, true)]     // unset user, global on -> on
    [InlineData(true, false, true)]    // user override wins over global off
    [InlineData(false, true, false)]   // user override wins over global on
    public void IsEnabled_ResolvesPerUserOverrideToGlobalDefault(bool? userOverride, bool globalDefault, bool expected)
    {
        var (user, config) = Setup(userOverride, globalDefault);
        Assert.Equal(expected, InteractionDiagnostics.IsEnabled(user, config));
    }

    [Fact]
    public void IsEnabled_NullUser_FallsBackToGlobalDefault()
    {
        var config = new PluginConfiguration { DefaultDiagnosticInteractionLogging = true };
        Assert.True(InteractionDiagnostics.IsEnabled(null, config));
    }

    [Theory]
    [InlineData("PlaySongIntent", true)]
    [InlineData("PlayArtistSongsIntent", true)]
    [InlineData("PlayPlaylistIntent", true)]
    [InlineData("AMAZON.PauseIntent", false)]
    [InlineData("AMAZON.StopIntent", false)]
    [InlineData("SessionEndedRequest", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsPlayInitiatingIntent_DetectsPlayIntents(string? intent, bool expected)
    {
        Assert.Equal(expected, InteractionDiagnostics.IsPlayInitiatingIntent(intent));
    }

    [Fact]
    public void Marker_TracksPlayRequestToPlaybackStartedToStopped()
    {
        InteractionDiagnostics.ClearAll();
        const string device = "diag-test-device";

        // Before anything: elapsed lookups are null
        Assert.Null(InteractionDiagnostics.SincePlayRequest(device));
        Assert.Null(InteractionDiagnostics.SincePlaybackStarted(device));

        // Play request recorded
        InteractionDiagnostics.RecordPlayRequest(device, "PlaySongIntent", sessionNew: true);
        Assert.Equal("PlaySongIntent", InteractionDiagnostics.LastPlayIntent(device));
        Assert.True(InteractionDiagnostics.LastPlaySessionNew(device));
        Assert.NotNull(InteractionDiagnostics.SincePlayRequest(device));

        // Playback not started yet
        Assert.Null(InteractionDiagnostics.SincePlaybackStarted(device));

        // Playback started
        InteractionDiagnostics.RecordPlaybackStarted(device);
        Assert.NotNull(InteractionDiagnostics.SincePlaybackStarted(device));
        // Play-request origin survives
        Assert.Equal("PlaySongIntent", InteractionDiagnostics.LastPlayIntent(device));

        // Playback stopped: elapsed-since-start cleared, play-request origin kept
        InteractionDiagnostics.RecordPlaybackStopped(device);
        Assert.Null(InteractionDiagnostics.SincePlaybackStarted(device));
        Assert.Equal("PlaySongIntent", InteractionDiagnostics.LastPlayIntent(device));

        InteractionDiagnostics.ClearAll();
    }

    [Fact]
    public void Marker_NewPlayRequest_OverwritesPreviousOrigin()
    {
        InteractionDiagnostics.ClearAll();
        const string device = "diag-test-device-2";

        InteractionDiagnostics.RecordPlayRequest(device, "PlaySongIntent", sessionNew: true);
        InteractionDiagnostics.RecordPlayRequest(device, "PlayAlbumIntent", sessionNew: false);

        Assert.Equal("PlayAlbumIntent", InteractionDiagnostics.LastPlayIntent(device));
        Assert.False(InteractionDiagnostics.LastPlaySessionNew(device));

        InteractionDiagnostics.ClearAll();
    }

    [Fact]
    public async Task PlaybackStarted_DiagnosticsEnabled_LogsAndRecords()
    {
        InteractionDiagnostics.ClearAll();
        var (user, config) = Setup(userOverride: true, globalDefault: false);
        const string device = "diag-handler-device";
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var loggerFactory = LoggerFactory.Create(b => { });

        var handler = new PlaybackStartedEventHandler(
            sessionManagerMock.Object, config, loggerFactory, libraryManagerMock.Object);

        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0,
            RequestId = "diag-req"
        };

        var context = TestHelpers.CreateTestContext();
        context.System.Device = new global::Alexa.NET.Request.Device { DeviceID = device };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        // The marker was recorded: since-playback-start is now measurable
        Assert.NotNull(InteractionDiagnostics.SincePlaybackStarted(device));

        InteractionDiagnostics.ClearAll();
    }

    [Fact]
    public async Task PlaybackStarted_DiagnosticsDisabled_DoesNotRecord()
    {
        InteractionDiagnostics.ClearAll();
        var (user, config) = Setup(userOverride: false, globalDefault: false);
        const string device = "diag-handler-device-off";
        var sessionManagerMock = new Mock<ISessionManager>();
        var libraryManagerMock = new Mock<ILibraryManager>();
        var loggerFactory = NullLoggerFactory.Instance;

        var handler = new PlaybackStartedEventHandler(
            sessionManagerMock.Object, config, loggerFactory, libraryManagerMock.Object);

        var request = new AudioPlayerRequest
        {
            Type = "AudioPlayer.PlaybackStarted",
            Token = Guid.NewGuid().ToString(),
            OffsetInMilliseconds = 0,
            RequestId = "diag-req-off"
        };

        var context = TestHelpers.CreateTestContext();
        context.System.Device = new global::Alexa.NET.Request.Device { DeviceID = device };

        var session = TestHelpers.CreateTestSession(sessionManagerMock.Object, loggerFactory);

        await handler.HandleAsync(request, context, user, session, CancellationToken.None);

        Assert.Null(InteractionDiagnostics.SincePlaybackStarted(device));

        InteractionDiagnostics.ClearAll();
    }
}
