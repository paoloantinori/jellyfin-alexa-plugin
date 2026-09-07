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
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
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
/// restart at 0); server-progress seeds (flag absent/false) keep minting directly;
/// raw-static launches keep the directive offset unchanged.
/// </summary>
[Collection("Plugin")]
public class ResumeConfirmationTranscodeBaseTests : PluginTestBase, IDisposable
{
    private const string DeviceId = "test-device";

    private readonly HandlerTestFixture _fx = new();
    private readonly DeviceQueueManager _queueManager;
    private readonly string _tempDir;

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
    }

    public void Dispose()
    {
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

    /// <summary>
    /// Test seam for <c>BaseItem.GetMediaStreams()</c> (virtual): overriding the streams
    /// lets the tests exercise the REAL codec probe + routing decision (under the test
    /// host a plain item's probe degrades to unknown codec and keeps the static URL).
    /// </summary>
    private sealed class EpisodeWithStreams : MediaBrowser.Controller.Entities.TV.Episode
    {
        private readonly List<MediaStream> _streams;

        public EpisodeWithStreams(string name, Guid id, params MediaStream[] streams)
        {
            Name = name;
            Id = id;
            _streams = streams.ToList();
        }

        public override IReadOnlyList<MediaStream> GetMediaStreams() => _streams;
    }

    private static MediaStream Stream(MediaStreamType type, string codec) => new() { Type = type, Codec = codec };

    private static long MinutesToMs(double minutes) => (long)TimeSpan.FromMinutes(minutes).TotalMilliseconds;

    private static EpisodeWithStreams Eac3Episode(Guid id)
        => new("Ribs", id, Stream(MediaStreamType.Video, "h264"), Stream(MediaStreamType.Audio, "eac3"));

    private static EpisodeWithStreams AacEpisode(Guid id)
        => new("FreeCommerce", id, Stream(MediaStreamType.Video, "h264"), Stream(MediaStreamType.Audio, "aac"));

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
}
