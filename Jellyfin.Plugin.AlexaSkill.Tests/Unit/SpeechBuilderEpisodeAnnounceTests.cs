using System;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// TASK-HIGH.1 (it-IT double-episode-number announce): for a daily podcast episode
/// whose source name carries the "Ep. N - Title" prefix, the voice announce reads the
/// number twice (once from the name, once from the episode-number context). The helper
/// replaces the TITLE portion of the it-IT voice announce with the extended weekday
/// date plus the prefix-stripped title. Display/APL metadata is NOT touched and every
/// non-it-IT locale (or non-Episode item, or missing PremiereDate) keeps the raw
/// item name byte-identically.
/// </summary>
[Collection("Plugin")]
public class SpeechBuilderEpisodeAnnounceTests
{
    private const string ItIt = "it-IT";
    private const string EnUs = "en-US";
    private static readonly DateTime Wednesday = new(2025, 9, 17, 6, 0, 0, DateTimeKind.Utc);

    private static Episode Episode(string name, DateTime? premiere)
        => new() { Name = name, PremiereDate = premiere, Id = Guid.NewGuid() };

    [Fact]
    public void EpisodeAnnounce_ItItEpisodeWithPremiereDate_FormatsDateAndStripsEpPrefix()
    {
        Assert.Equal(
            "Mercoledì 17 settembre – La diserzione dei medici e le altre storie di oggi",
            SpeechBuilder.FormatEpisodeAnnounceTitle(
                Episode("Ep. 1286 – La diserzione dei medici e le altre storie di oggi", Wednesday), ItIt));
    }

    [Fact]
    public void EpisodeAnnounce_ItItEpisode_HyphenPrefixVariantStripped()
    {
        Assert.Equal(
            "Mercoledì 17 settembre – Il titolo",
            SpeechBuilder.FormatEpisodeAnnounceTitle(Episode("Ep. 42 - Il titolo", Wednesday), ItIt));
    }

    [Fact]
    public void EpisodeAnnounce_ItItEpisode_MissingPremiereDate_ReturnsNull()
    {
        Assert.Null(SpeechBuilder.FormatEpisodeAnnounceTitle(Episode("Ep. 1286 – Titolo", null), ItIt));
    }

    [Fact]
    public void EpisodeAnnounce_NonEpisode_ReturnsNull()
    {
        Assert.Null(SpeechBuilder.FormatEpisodeAnnounceTitle(
            new Movie { Name = "Ep. 1286 – Titolo", PremiereDate = Wednesday, Id = Guid.NewGuid() }, ItIt));
    }

    [Fact]
    public void EpisodeAnnounce_EnUsLocale_ReturnsNull()
    {
        Assert.Null(SpeechBuilder.FormatEpisodeAnnounceTitle(
            Episode("Ep. 1286 – Titolo", Wednesday), EnUs));
    }

    [Fact]
    public void EpisodeAnnounce_ItItEpisodeWithoutEpPrefix_PrependsDateToFullName()
    {
        Assert.Equal(
            "Mercoledì 17 settembre – Il Post: le storie di oggi",
            SpeechBuilder.FormatEpisodeAnnounceTitle(Episode("Il Post: le storie di oggi", Wednesday), ItIt));
    }

    [Fact]
    public void EpisodeAnnounce_CultureIsItalian_MercolediNotWednesday()
    {
        string title = SpeechBuilder.FormatEpisodeAnnounceTitle(Episode("Ep. 1 – T", Wednesday), ItIt)!;
        Assert.StartsWith("Mercoledì 17 settembre", title, StringComparison.Ordinal);
        Assert.DoesNotContain("Wednesday", title, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderVideoSpeech_ItItEpisodeWithPrefix_AnnouncesDateInsteadOfRawName()
    {
        var builder = TestHelpers.CreateLaunchBuilder(new PluginConfiguration());
        SsmlOutputSpeech speech = Assert.IsType<SsmlOutputSpeech>(
            builder.BuildVideoLaunchSpeech(
                Episode("Ep. 1286 – La diserzione dei medici e le altre storie di oggi", Wednesday),
                ItIt, resumeTicks: 0, announceOn: true));

        Assert.Contains("Mercoledì 17 settembre – La diserzione", speech.Ssml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ep. 1286", speech.Ssml, StringComparison.Ordinal);
    }

    [Fact]
    public void BuilderVideoSpeech_ResumeArm_ItItEpisodeWithPrefix_AnnouncesDateInsteadOfRawName()
    {
        var builder = TestHelpers.CreateLaunchBuilder(new PluginConfiguration());
        PlainTextOutputSpeech speech = Assert.IsType<PlainTextOutputSpeech>(
            builder.BuildVideoLaunchSpeech(
                Episode("Ep. 1286 – La diserzione dei medici", Wednesday),
                ItIt, TimeSpan.FromMinutes(5).Ticks, announceOn: true));

        Assert.StartsWith("Riprendo Mercoledì 17 settembre – La diserzione dei medici dalla posizione", speech.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatEpisodeAnnounceTitle_EmptyTitleAfterPrefix_FallsBackToDate()
    {
        var item = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "Ep. 5 - ",
            PremiereDate = new DateTime(2026, 9, 17)
        };

        var result = SpeechBuilder.FormatEpisodeAnnounceTitle(item, "it-IT");

        // The designed empty-strip restore: when the strip leaves nothing, the
        // FULL name is kept after the date (the helper never returns a bare date).
        Assert.Equal("Giovedì 17 settembre – Ep. 5 - ", result);
    }
}
