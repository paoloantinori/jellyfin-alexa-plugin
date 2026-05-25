using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.ProactiveEvents;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

[Collection("Plugin")]
public class ProactiveEventRateLimiterTests : PluginTestBase
{
    [Fact]
    public void CanSend_Initially_ReturnsTrue()
    {
        var limiter = new ProactiveEventRateLimiter();

        Assert.True(limiter.CanSend("user1"));
    }

    [Fact]
    public void CanSend_RespectsHourlyLimit()
    {
        var limiter = new ProactiveEventRateLimiter();

        for (int i = 0; i < ProactiveEventRateLimiter.MaxEventsPerHour; i++)
        {
            Assert.True(limiter.CanSend("user1"));
            limiter.RecordSend("user1");
        }

        Assert.False(limiter.CanSend("user1"));
    }

    [Fact]
    public void CanSend_DifferentUsers_AreIndependent()
    {
        var limiter = new ProactiveEventRateLimiter();

        for (int i = 0; i < ProactiveEventRateLimiter.MaxEventsPerHour; i++)
        {
            limiter.RecordSend("user1");
        }

        Assert.False(limiter.CanSend("user1"));
        Assert.True(limiter.CanSend("user2"));
    }

    [Fact]
    public void RecordSend_DoesNotThrow()
    {
        var limiter = new ProactiveEventRateLimiter();

        limiter.RecordSend("user1");
        limiter.RecordSend("user1");
        limiter.RecordSend("user1");

        Assert.True(limiter.CanSend("user1"));
    }
}

[Collection("Plugin")]
public class ProactiveEventClientBuildTests : PluginTestBase
{
    [Fact]
    public void BuildMediaContentAvailableEvent_CreatesValidPayload()
    {
        var payload = ProactiveEventClient.BuildMediaContentAvailableEvent(
            "ALBUM",
            "Dark Side of the Moon",
            "Pink Floyd");

        Assert.Equal("AMAZON.MediaContent.Available", payload["type"]?.ToString());

        var content = payload["payload"]?["content"];
        Assert.NotNull(content);
        Assert.Equal("Dark Side of the Moon", content["name"]?["value"]?.ToString());

        var metadata = payload["payload"]?["metadata"];
        Assert.NotNull(metadata);
        Assert.Equal("ALBUM", metadata["contentType"]?.ToString());
        Assert.Equal("Pink Floyd", metadata["artistName"]?["value"]?.ToString());
    }

    [Fact]
    public void BuildMediaContentAvailableEvent_EpisodeIncludesSeasonAndEpisode()
    {
        var payload = ProactiveEventClient.BuildMediaContentAvailableEvent(
            "EPISODE",
            "Pilot",
            "Breaking Bad",
            seasonNumber: 1,
            episodeNumber: 1);

        var metadata = payload["payload"]?["metadata"];
        Assert.NotNull(metadata);
        Assert.Equal("EPISODE", metadata["contentType"]?.ToString());
        Assert.Equal(1, metadata["seasonNumber"]?.Value<int>());
        Assert.Equal(1, metadata["episodeNumber"]?.Value<int>());
    }

    [Fact]
    public void BuildMediaContentAvailableEvent_MovieWithoutArtist()
    {
        var payload = ProactiveEventClient.BuildMediaContentAvailableEvent(
            "MOVIE",
            "Inception");

        var metadata = payload["payload"]?["metadata"];
        Assert.NotNull(metadata);
        Assert.Equal("MOVIE", metadata["contentType"]?.ToString());
        Assert.Null(metadata["artistName"]);
    }

    [Fact]
    public void BuildMediaContentAvailableEvent_HasAvailabilityTimestamp()
    {
        var payload = ProactiveEventClient.BuildMediaContentAvailableEvent(
            "MOVIE",
            "Test Movie");

        var availability = payload["payload"]?["availability"];
        Assert.NotNull(availability);
        Assert.NotNull(availability["startTime"]);
        Assert.Equal("Jellyfin", availability["provider"]?["name"]?.ToString());
    }

    [Fact]
    public void BuildMediaContentAvailableEvent_HasUniqueUri()
    {
        var payload1 = ProactiveEventClient.BuildMediaContentAvailableEvent("MOVIE", "A");
        var payload2 = ProactiveEventClient.BuildMediaContentAvailableEvent("MOVIE", "B");

        var uri1 = payload1["payload"]?["content"]?["uri"]?.ToString();
        var uri2 = payload2["payload"]?["content"]?["uri"]?.ToString();

        Assert.NotEqual(uri1, uri2);
        Assert.StartsWith("amzn1.alexa-skill.event.", uri1);
    }
}

[Collection("Plugin")]
public class ProactiveEventClientSendTests : PluginTestBase
{
    [Fact]
    public async Task SendEventAsync_ReturnsFalse_WhenNoLwaClientId()
    {
        var logger = NullLogger<ProactiveEventClient>.Instance;
        var client = new ProactiveEventClient(logger);

        // Plugin.Instance is null in test context, so config will be null
        var result = await client.SendEventAsync("user1", new JObject());

        Assert.False(result);
    }
}
