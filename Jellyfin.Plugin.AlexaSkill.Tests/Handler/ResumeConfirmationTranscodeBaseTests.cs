using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alexa.NET.Request;
using Alexa.NET.Request.Type;
using Alexa.NET.Response;
using Alexa.NET.Response.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Handler;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Handler;

/// <summary>
/// JF-514: the resume-offer path's offset-provenance correction. The offer seeded
/// from the AudioPlayer CONTEXT carries a device-derived offset, which is relative
/// to the previous playback's OUTPUT timeline; for a transcode-routed item that
/// timeline starts at the stream's <c>?start=</c> base. Every Movie/Episode resolve
/// through <c>BaseHandler.ResolveAudioLaunchSource</c> records that launch base in
/// the per-device queue ledger (device+item keyed), and the resume-yes confirm
/// rebases: minted <c>?start=</c> = recorded base + device offset (item-absolute).
/// No recorded base falls back to the JF-507 interim rule (drop the offset,
/// restart at 0); raw-static launches keep the directive offset unchanged.
/// JF-520: the correction is shared with the ResumeIntent tail via
/// <c>BaseHandler.ResolveResumedAudioLaunch</c>, and the device-last-played
/// (UserData) offer seed classifies its position at seed time: transcode-routed
/// item + recorded base on the device flags the offer stream-relative too (the
/// event writers persist the raw device offset into UserData).
/// </summary>
[Collection("Plugin")]
public class ResumeConfirmationTranscodeBaseTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly string _tempDir;
    private readonly DeviceQueueManager? _previousPluginQueueManager;

    public ResumeConfirmationTranscodeBaseTests()
    {
        _tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "resume-transcode-base-tests-" + Guid.NewGuid());
        System.IO.Directory.CreateDirectory(_tempDir);
        _queueManager = new DeviceQueueManager(_tempDir, _fx.LoggerFactory.CreateLogger<DeviceQueueManager>());

        TestHelpers.EnsurePluginInstance(
            _fx.Config,
            _fx.LoggerFactory,
            c => { },
            "resume-transcode-base-tests");

        // The device-last-played offer path reads the ledger through Plugin.Instance
        // (LaunchRequestHandler has no injected queue manager); point it at this
        // suite's manager and restore the previous value on dispose.
        _previousPluginQueueManager = Jellyfin.Plugin.AlexaSkill.Plugin.Instance?.DeviceQueueManager;
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _queueManager;
        }
    }

    public void Dispose()
    {
        if (Jellyfin.Plugin.AlexaSkill.Plugin.Instance != null)
        {
            Jellyfin.Plugin.AlexaSkill.Plugin.Instance.DeviceQueueManager = _previousPluginQueueManager;
        }

        _queueManager.Dispose();
        try
        {
            if (System.IO.Directory.Exists(_tempDir))
            {
                System.IO.Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private static TestHelpers.TestEpisodeWithStreams Eac3Episode(Guid id)
        => new("Ribs", id, TestHelpers.TestStream(MediaStreamType.Video, "h264"), TestHelpers.TestStream(MediaStreamType.Audio, "eac3"));

    private static TestHelpers.TestEpisodeWithStreams AacEpisode(Guid id)
        => new("FreeCommerce", id, TestHelpers.TestStream(MediaStreamType.Video, "h264"), TestHelpers.TestStream(MediaStreamType.Audio, "aac"));

    private SessionInfo CreateSession() => TestHelpers.CreateTestSession(_fx.SessionManager.Object, _fx.LoggerFactory);

    private static IntentRequest YesIntent() => new() { Intent = new Intent { Name = "AMAZON.YesIntent" } };

    private static Dictionary<string, object> ResumeAttrs(Guid itemId, long offsetMs, bool streamRelative)
    {
        var resumeState = new ResumeHelper.ResumeState
        {
            ItemId = itemId.ToString(),
            OffsetMs = offsetMs,
            OffsetIsStreamRelative = streamRelative
        };
        return new Dictionary<string, object> { ["resume_state"] = JsonConvert.SerializeObject(resumeState) };
    }

    private YesIntentHandler CreateHandler() => new(
        _fx.SessionManager.Object, _fx.Config,
        _fx.LibraryManager.Object, _fx.UserManager.Object, _fx.LoggerFactory,
        _queueManager);

    private LaunchRequestHandler CreateLaunchHandler() => new(
        _fx.SessionManager.Object,
        _fx.Config,
        _fx.LibraryManager.Object,
        _fx.UserManager.Object,
        _fx.UserDataManager.Object,
        _fx.LoggerFactory);

    private static AudioPlayerPlayDirective SinglePlayDirective(SkillResponse response)
        => Assert.Single(response.Response.Directives.OfType<AudioPlayerPlayDirective>());

    private async Task<SkillResponse> ConfirmResumeAsync(Guid itemId, Dictionary<string, object> attrs, string deviceId = DeviceId)
    {
        _fx.LibraryManager.Setup(lm => lm.GetItemById(itemId)).Returns(Eac3Episode(itemId));
        return await CreateHandler().HandleAsync(
            YesIntent(),
            TestHelpers.CreateTestContext(deviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            attrs,
            CancellationToken.None);
    }

    /// <summary>
    /// Spec case (a): a recorded base B (20:00) plus a device-derived offset O (5:00)
    /// mints ?start=B+O (25:00, item-absolute) on the transcode URL, directive offset 0,
    /// and the ledger now carries the NEW base so the next cycle composes correctly.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_StreamRelativeOffset_WithRecordedBase_MintsBasePlusOffset()
    {
        var id = Guid.NewGuid();
        long baseMs = MinutesToMs(20);
        long offsetMs = MinutesToMs(5);
        _queueManager.RecordAudioTranscodeBase(DeviceId, id.ToString(), baseMs);

        SkillResponse response = await ConfirmResumeAsync(id, ResumeAttrs(id, offsetMs, streamRelative: true));

        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(baseMs + offsetMs).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(baseMs + offsetMs, _queueManager.GetAudioTranscodeBase(DeviceId, id.ToString()));
    }

    /// <summary>
    /// Spec case (b): no base recorded (pre-deploy launch, wiped ledger) plus a
    /// device-derived offset falls back to the JF-507 interim rule: the offset is
    /// dropped, playback restarts at 0, no stream-relative value is minted silently.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_StreamRelativeOffset_WithoutRecordedBase_DropsOffset()
    {
        var id = Guid.NewGuid();

        SkillResponse response = await ConfirmResumeAsync(id, ResumeAttrs(id, 300000, streamRelative: true));

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/alexaskill/api/video-audio/episode/{id}/audio.m3u8?token=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// Spec case (c): raw-static launches keep the caller's offset unchanged. Even with
    /// a recorded base and the stream-relative flag set, an Echo-decodable episode keeps
    /// the static URL and the directive offset: the correction is transcode-routed only.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_RawStaticLaunch_KeepsOffsetDespiteStreamRelativeFlag()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(AacEpisode(id));
        _queueManager.RecordAudioTranscodeBase(DeviceId, id.ToString(), MinutesToMs(20));

        var response = await CreateHandler().HandleAsync(
            YesIntent(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            ResumeAttrs(id, 300000, streamRelative: true),
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"/Audio/{id}/stream?static=true&api_key=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("video-audio", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(300000, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// Spec case (d): the ledger is keyed by device+item, so a base recorded for one
    /// item never serves another: confirming a DIFFERENT item with a device-derived
    /// offset finds no base and drops the offset.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_BaseDoesNotLeakAcrossItems()
    {
        var withBase = Guid.NewGuid();
        var other = Guid.NewGuid();
        _queueManager.RecordAudioTranscodeBase(DeviceId, withBase.ToString(), MinutesToMs(20));

        SkillResponse response = await ConfirmResumeAsync(other, ResumeAttrs(other, 300000, streamRelative: true));

        var directive = SinglePlayDirective(response);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        // The dropped restart records ITS base (0) for the item; a leak from the other
        // item's 20:00 base would instead have minted ?start=25:00 (20 + 5 minutes).
        Assert.Equal(0, _queueManager.GetAudioTranscodeBase(DeviceId, other.ToString()));
    }

    /// <summary>
    /// Keying, device axis: a base recorded on one device never serves a resume
    /// confirmed from another device (the offsets count different timelines).
    /// </summary>
    [Fact]
    public async Task ConfirmResume_BaseDoesNotLeakAcrossDevices()
    {
        var id = Guid.NewGuid();
        _queueManager.RecordAudioTranscodeBase(DeviceId, id.ToString(), MinutesToMs(20));

        SkillResponse response = await ConfirmResumeAsync(id, ResumeAttrs(id, 300000, streamRelative: true), deviceId: "other-device");

        var directive = SinglePlayDirective(response);
        Assert.DoesNotContain("?start=", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// Record side, transcode route: minting a transcode ?start= writes that base into
    /// the ledger (here via an item-absolute seed, flag false, the pre-JF-514 shape).
    /// </summary>
    [Fact]
    public async Task ConfirmResume_TranscodeMint_RecordsBaseInLedger()
    {
        var id = Guid.NewGuid();

        SkillResponse response = await ConfirmResumeAsync(id, ResumeAttrs(id, 300000, streamRelative: false));

        var directive = SinglePlayDirective(response);
        Assert.Contains($"?start={TimeSpan.FromMilliseconds(300000).Ticks}&", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(300000, _queueManager.GetAudioTranscodeBase(DeviceId, id.ToString()));
    }

    /// <summary>
    /// Record side, raw-static route: a Movie/Episode resolved to the raw static URL
    /// records base 0, which also invalidates any stale transcode base an older
    /// launch of the same item left behind.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_RawStaticMint_RecordsZeroBaseAndInvalidatesStaleBase()
    {
        var id = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(AacEpisode(id));
        _queueManager.RecordAudioTranscodeBase(DeviceId, id.ToString(), 600000);

        await CreateHandler().HandleAsync(
            YesIntent(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            ResumeAttrs(id, 300000, streamRelative: false),
            CancellationToken.None);

        Assert.Equal(0, _queueManager.GetAudioTranscodeBase(DeviceId, id.ToString()));
    }

    /// <summary>
    /// Back-compat: a resume_state serialized by a pre-JF-514 deploy (no
    /// offsetIsStreamRelative field) deserializes as item-absolute and keeps the
    /// historical mint-it-directly behavior.
    /// </summary>
    [Fact]
    public async Task ConfirmResume_LegacySessionJson_WithoutFlag_MintsOffsetDirectly()
    {
        var id = Guid.NewGuid();
        var attrs = new Dictionary<string, object>
        {
            ["resume_state"] = $"{{\"itemId\":\"{id}\",\"offsetMs\":300000,\"useResumePlaylist\":false}}"
        };

        SkillResponse response = await ConfirmResumeAsync(id, attrs);

        var directive = SinglePlayDirective(response);
        Assert.Contains($"?start={TimeSpan.FromMilliseconds(300000).Ticks}&", directive.AudioItem.Stream.Url, StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
    }

    /// <summary>
    /// Offer side: a resume offer seeded from the AudioPlayer CONTEXT (device-derived
    /// offset) stores the stream-relative flag in the session state, so the confirm
    /// side knows to rebase. This is the seed the correction composes with.
    /// </summary>
    [Fact]
    public async Task LaunchResumeOffer_FromAudioPlayerContext_SeedsStreamRelativeFlag()
    {
        var itemId = Guid.NewGuid();
        _fx.LibraryManager.Setup(lm => lm.GetItemById(itemId)).Returns(new Audio
        {
            Name = "Bohemian Rhapsody",
            Id = itemId
        });

        var context = TestHelpers.CreateTestContext(DeviceId);
        context.AudioPlayer = new PlaybackState
        {
            Token = itemId.ToString(),
            OffsetInMilliseconds = 45000
        };

        SkillResponse response = await CreateLaunchHandler().HandleAsync(
            new LaunchRequest { Locale = "en-US" },
            context,
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(response.SessionAttributes);
        Assert.True(response.SessionAttributes.ContainsKey("resume_state"));
        var state = JsonConvert.DeserializeObject<ResumeHelper.ResumeState>(
            response.SessionAttributes["resume_state"]!.ToString()!);
        Assert.NotNull(state);
        Assert.Equal(itemId.ToString(), state!.ItemId);
        Assert.Equal(45000, state.OffsetMs);
        Assert.True(state.OffsetIsStreamRelative, "an offer seeded from the AudioPlayer context offset must flag it as stream-relative (JF-514)");
    }

    // ========== JF-520: the device-last-played (UserData) seed classifies at seed time ==========

    /// <summary>
    /// The JF-514 residual closed by JF-520: the device-last-played offer reads its
    /// position from UserData, which the event writers filled with the RAW device
    /// offset (stream-relative) for a transcode-routed item. When this device's
    /// ledger carries a launch base, the seed flags the offer stream-relative and the
    /// confirm composes end-to-end: minted ?start = base + position (item-absolute),
    /// ledger advanced to the new base.
    /// </summary>
    [Fact]
    public async Task DeviceLastPlayedOffer_TranscodeItemWithRecordedBase_FlagsStreamRelativeAndRebasesOnConfirm()
    {
        var id = Guid.NewGuid();
        _fx.SetupUserMock();
        _fx.Config.NativeControlsForAudio = true;
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(Eac3Episode(id));
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks });
        _queueManager.RecordLastPlayed(DeviceId, id.ToString());
        _queueManager.RecordAudioTranscodeBase(DeviceId, id.ToString(), MinutesToMs(20));

        // Screen-capable device, no AudioPlayer token: the no-token NativeControlsForAudio
        // route offers the device's last-played item from UserData.
        SkillResponse offer = await CreateLaunchHandler().HandleAsync(
            new LaunchRequest { Locale = "en-US" },
            TestHelpers.CreateContextWithVideoApp(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(offer.SessionAttributes);
        Assert.True(offer.SessionAttributes.ContainsKey("resume_state"));
        var state = JsonConvert.DeserializeObject<ResumeHelper.ResumeState>(
            offer.SessionAttributes["resume_state"]!.ToString()!);
        Assert.NotNull(state);
        Assert.True(state!.OffsetIsStreamRelative, "a UserData position for a transcode-routed item with a recorded base is stream-relative (JF-520)");
        Assert.Equal(MinutesToMs(5), state.OffsetMs);

        // Confirming the offered state mints base + position and advances the ledger.
        SkillResponse response = await CreateHandler().HandleAsync(
            YesIntent(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            offer.SessionAttributes!,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(MinutesToMs(25)).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
        Assert.Equal(0, directive.AudioItem.Stream.OffsetInMilliseconds);
        Assert.Equal(MinutesToMs(25), _queueManager.GetAudioTranscodeBase(DeviceId, id.ToString()));
    }

    /// <summary>
    /// The complement: with NO recorded base (the position came from a VideoApp play,
    /// another client, or a pre-deploy launch) the UserData seed keeps the historical
    /// item-absolute classification, and the confirm mints the position directly.
    /// </summary>
    [Fact]
    public async Task DeviceLastPlayedOffer_TranscodeItemWithoutRecordedBase_StaysItemAbsolute()
    {
        var id = Guid.NewGuid();
        _fx.SetupUserMock();
        _fx.Config.NativeControlsForAudio = true;
        _fx.LibraryManager.Setup(lm => lm.GetItemById(id)).Returns(Eac3Episode(id));
        _fx.UserDataManager
            .Setup(u => u.GetUserData(It.IsAny<Jellyfin.Database.Implementations.Entities.User>(), It.IsAny<BaseItem>()))
            .Returns(new UserItemData { Key = "test", Played = false, PlaybackPositionTicks = TimeSpan.FromMinutes(5).Ticks });
        _queueManager.RecordLastPlayed(DeviceId, id.ToString());

        SkillResponse offer = await CreateLaunchHandler().HandleAsync(
            new LaunchRequest { Locale = "en-US" },
            TestHelpers.CreateContextWithVideoApp(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            CancellationToken.None);

        Assert.NotNull(offer.SessionAttributes);
        Assert.True(offer.SessionAttributes.ContainsKey("resume_state"));
        var state = JsonConvert.DeserializeObject<ResumeHelper.ResumeState>(
            offer.SessionAttributes["resume_state"]!.ToString()!);
        Assert.NotNull(state);
        Assert.False(state!.OffsetIsStreamRelative, "no recorded base means the position keeps the item-absolute classification (JF-520)");

        SkillResponse response = await CreateHandler().HandleAsync(
            YesIntent(),
            TestHelpers.CreateTestContext(DeviceId),
            TestHelpers.CreateTestUser(),
            CreateSession(),
            offer.SessionAttributes!,
            CancellationToken.None);

        var directive = SinglePlayDirective(response);
        Assert.Contains(
            $"?start={TimeSpan.FromMilliseconds(MinutesToMs(5)).Ticks}&",
            directive.AudioItem.Stream.Url,
            StringComparison.Ordinal);
    }
}
