using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using global::Alexa.NET.Request.Type;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using UserItemData = MediaBrowser.Controller.Entities.UserItemData;
using MediaBrowser.Controller.Entities.Audio;
using Jellyfin.Plugin.AlexaSkill.Alexa.Apl;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-635: a plain Tell with no screen content dismisses the seek-mode player (live:
/// RateItem's confirmation closed the album). The keep-alive: during a VideoApp-routed
/// medium the confirmation carries the NowPlaying RenderDocument (the screen-content
/// evidence class: MediaInfo's doc-carrying Tell left the audio running).
/// </summary>
[Collection("Plugin")]
public class RateItemKeepAliveTests : PluginTestBase
{
    private readonly HandlerTestFixture _fx = new HandlerTestFixture("https://test.example.com");

    public RateItemKeepAliveTests()
    {
        TestHelpers.EnsurePluginInstance(new PluginConfiguration(), _fx.LoggerFactory, c => { }, "rate-keepalive");
    }

    private RateItemIntentHandler CreateHandler(DeviceQueueManager queueManager)
        => new(
            _fx.SessionManager.Object,
            _fx.Config,
            _fx.UserDataManager.Object,
            _fx.UserManager.Object,
            _fx.LibraryManager.Object,
            _fx.LoggerFactory,
            queueManager);

    [Fact]
    public async Task SeekModeVideoAppMedium_ConfirmationCarriesTheNowPlayingKeepAlive()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("rate-keepalive");
        var song = new Audio { Name = "Seek Mode Song", Id = Guid.NewGuid(), Path = "/music/s.flac" };
        _fx.SetupUserMock();
        var data = new UserItemData { Key = song.Id.ToString(), Rating = null };
        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>())).Returns(data);
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _fx.Config.SeekEnabled = true;
        string deviceId = "rate-keepalive-device";
        queueManager.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.VideoApp);
        var handler = CreateHandler(queueManager);

        // The device must report APL (the keep-alive is an APL document) AND carry the
        // stale audio token: the real Echo Show during seek-mode playback. Built from
        // the token helper (which owns the PlaybackState shape), then APL is added to
        // the device's interface map.
        global::Alexa.NET.Request.Context context = TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId);
        context.System!.Device!.SupportedInterfaces =
            new Dictionary<string, object> { ["Alexa.Presentation.APL"] = new { } };

        var response = await handler.HandleAsync(
            Request("5"), context,
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Contains("Seek Mode Song", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        Assert.NotEmpty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    [Fact]
    public async Task PlainAudioMedium_ConfirmationStaysADirectiveFreeTell()
    {
        var queueManager = TestHelpers.CreateDeviceQueueManager("rate-plain");
        var song = new Audio { Name = "Plain Song", Id = Guid.NewGuid(), Path = "/music/p.flac" };
        _fx.SetupUserMock();
        var data = new UserItemData { Key = song.Id.ToString(), Rating = null };
        _fx.UserDataManager.Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>())).Returns(data);
        _fx.LibraryManager.Setup(l => l.GetItemById(song.Id)).Returns(song);
        _fx.Config.SeekEnabled = true;
        string deviceId = "rate-plain-device";
        queueManager.RecordLastPlayed(deviceId, song.Id.ToString(), DeviceQueueManager.LaunchRoute.Audio);
        var handler = CreateHandler(queueManager);

        var response = await handler.HandleAsync(
            Request("5"), TestHelpers.CreateContextWithToken(song.Id.ToString(), deviceId),
            TestHelpers.CreateTestUser(), null!, CancellationToken.None);

        Assert.Contains("Plain Song", TestHelpers.GetSpeechText(response), StringComparison.Ordinal);
        // The keep-alive is seek-mode-only: a plain audio Tell stays lean.
        Assert.Empty(response.Response.Directives.OfType<AplRenderDocumentDirective>());
    }

    private static IntentRequest Request(string stars)
        => new()
        {
            Locale = "en-US",
            Intent = new global::Alexa.NET.Request.Intent
            {
                Name = "RateItemIntent",
                Slots = new Dictionary<string, global::Alexa.NET.Request.Slot>
                {
                    ["star_rating"] = new global::Alexa.NET.Request.Slot { Value = stars }
                }
            }
        };
}
