using System;
using System.Threading.Tasks;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch-9 characterization suite for the video-launch announce SPEECH pair
/// (the two BuildVideoLaunchSpeech overloads), which had ZERO direct coverage before
/// the batch (handler suites exercise them only indirectly through the full launch
/// flow: the resume branch by content assertions, the fresh-play branch only via
/// the JF-349 OutputSpeech-presence checks). Written BEFORE the extraction and run
/// green on the pre-refactor BaseHandler code via a probe subclass, then retargeted
/// to PlaybackLaunchBuilder with receiver swaps only (expectations unchanged). They
/// pin CURRENT behavior: the resume branch composes PlainText "Resuming {name} from
/// {position}" via ResumeMath.FormatPosition and is SPOKEN EVEN WHEN announceOn is
/// false (position information, not the now-playing readout the gate controls), the
/// fresh-play branch delegates to the gated now-playing speech (SSML when on, null
/// when off, name XML-escaped), and the deps-fetched overload falls back to ticks 0
/// whenever either dependency is null or the stored position is null/zero.
/// </summary>
[Collection("Plugin")]
public class PlaybackLaunchBuilderVideoLaunchSpeechTests : PluginTestBase
{
    private const string Locale = "en-US";

    private readonly PluginConfiguration _config = new();
    private readonly PlaybackLaunchBuilder _builder;
    private readonly Mock<IUserDataManager> _userDataManager = new();
    private readonly Jellyfin.Database.Implementations.Entities.User _jellyfinUser = TestHelpers.CreateJellyfinUser();

    public PlaybackLaunchBuilderVideoLaunchSpeechTests()
    {
        _builder = TestHelpers.CreateLaunchBuilder(_config);
    }

    private static Movie Movie(string name = "Inception")
        => new() { Name = name, Id = Guid.NewGuid() };

    // ---- ticks overload: resume branch ----

    [Fact]
    public void VideoSpeech_ResumeMinutePosition_SpeaksPlainTextResumingSpeech()
    {
        PlainTextOutputSpeech speech = Assert.IsType<PlainTextOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie(), Locale, TimeSpan.FromMinutes(45).Ticks, announceOn: true));

        Assert.Equal("Resuming Inception from 45m 0s.", speech.Text);
    }

    [Fact]
    public void VideoSpeech_ResumeHourPosition_UsesFormatPositionHourBranch()
    {
        PlainTextOutputSpeech speech = Assert.IsType<PlainTextOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie(), Locale, TimeSpan.FromMinutes(90).Ticks, announceOn: true));

        Assert.Equal("Resuming Inception from 1h 30m.", speech.Text);
    }

    [Fact]
    public void VideoSpeech_ResumeAnnounceOff_StillSpeaksResumePosition()
    {
        // The gate controls the now-playing readout only: the resume announce is
        // position information and is spoken regardless of announceOn.
        PlainTextOutputSpeech speech = Assert.IsType<PlainTextOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie(), Locale, TimeSpan.FromMinutes(45).Ticks, announceOn: false));

        Assert.Equal("Resuming Inception from 45m 0s.", speech.Text);
    }

    // ---- ticks overload: fresh-play branch (the gated now-playing speech) ----

    [Fact]
    public void VideoSpeech_FreshPlayAnnounceOn_ReturnsSsmlNowPlaying()
    {
        SsmlOutputSpeech speech = Assert.IsType<SsmlOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie(), Locale, resumeTicks: 0, announceOn: true));

        Assert.Equal(
            "<speak><say-as interpret-as=\"interjection\">now playing</say-as><break time=\"300ms\"/><emphasis level=\"moderate\">Inception</emphasis></speak>",
            speech.Ssml);
    }

    [Fact]
    public void VideoSpeech_FreshPlayAnnounceOff_ReturnsNull()
    {
        Assert.Null(_builder.BuildVideoLaunchSpeech(Movie(), Locale, resumeTicks: 0, announceOn: false));
    }

    [Fact]
    public void VideoSpeech_FreshPlaySsml_EscapesTitle()
    {
        SsmlOutputSpeech speech = Assert.IsType<SsmlOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie("Bill & Ted's Excellent Adventure"), Locale, resumeTicks: 0, announceOn: true));

        Assert.Contains("Bill &amp; Ted&apos;s Excellent Adventure", speech.Ssml, StringComparison.Ordinal);
        Assert.DoesNotContain("Bill & Ted", speech.Ssml, StringComparison.Ordinal);
    }

    // ---- deps-fetched overload ----

    [Fact]
    public void VideoSpeech_StoredPosition_SpeaksResumePosition()
    {
        Movie movie = Movie();
        _userDataManager.Setup(u => u.GetUserData(_jellyfinUser, movie)).Returns(new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = TimeSpan.FromMinutes(45).Ticks
        });

        PlainTextOutputSpeech speech = Assert.IsType<PlainTextOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(movie, Locale, _userDataManager.Object, _jellyfinUser, announceOn: true));

        Assert.Equal("Resuming Inception from 45m 0s.", speech.Text);
    }

    [Fact]
    public void VideoSpeech_ZeroStoredPosition_FallsBackToGatedNowPlaying()
    {
        Movie movie = Movie();
        _userDataManager.Setup(u => u.GetUserData(_jellyfinUser, movie)).Returns(new UserItemData
        {
            Key = "test",
            PlaybackPositionTicks = 0
        });

        Assert.Null(_builder.BuildVideoLaunchSpeech(movie, Locale, _userDataManager.Object, _jellyfinUser, announceOn: false));
    }

    [Fact]
    public void VideoSpeech_NullStoredData_FallsBackToGatedNowPlaying()
    {
        Movie movie = Movie();
        _userDataManager.Setup(u => u.GetUserData(_jellyfinUser, movie)).Returns((UserItemData?)null);

        SsmlOutputSpeech speech = Assert.IsType<SsmlOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(movie, Locale, _userDataManager.Object, _jellyfinUser, announceOn: true));

        Assert.Contains("Inception", speech.Ssml, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoSpeech_NullUserDataManager_FallsBackToGatedNowPlaying()
    {
        // Either dependency null -> resumeTicks 0 -> the gated now-playing speech
        // (no NullReferenceException on the user-data read).
        SsmlOutputSpeech speech = Assert.IsType<SsmlOutputSpeech>(
            _builder.BuildVideoLaunchSpeech(Movie(), Locale, userDataManager: null, jellyfinUser: _jellyfinUser, announceOn: true));

        Assert.Contains("Inception", speech.Ssml, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoSpeech_NullJellyfinUser_FallsBackToGatedNowPlaying()
    {
        Assert.Null(_builder.BuildVideoLaunchSpeech(
            Movie(), Locale, _userDataManager.Object, jellyfinUser: null, announceOn: false));
    }
}
