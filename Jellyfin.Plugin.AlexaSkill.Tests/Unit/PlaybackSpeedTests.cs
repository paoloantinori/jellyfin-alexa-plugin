using System;
using Alexa.NET.Request;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-636 facts for the playback-speed vocabulary: the six-rate table, the
/// cycling ladder, the standing-preference resolution, the stream-to-content
/// tick arithmetic (the load-bearing position math), and the speed-slot
/// resolver (entity ids first, localized raw words and spoken decimals second,
/// the JF-583 EpisodePosition shape).
/// </summary>
public class PlaybackSpeedTests
{
    // ---- the served rate table ----

    [Theory]
    [InlineData(750, true)]
    [InlineData(1000, true)]
    [InlineData(1250, true)]
    [InlineData(1500, true)]
    [InlineData(1750, true)]
    [InlineData(2000, true)]
    [InlineData(0, false)]
    [InlineData(500, false)]
    [InlineData(1100, false)]
    [InlineData(2500, false)]
    [InlineData(-1000, false)]
    public void IsValidPerMille_MatchesTheSixStepTable(int rate, bool expected)
        => Assert.Equal(expected, PlaybackSpeed.IsValidPerMille(rate));

    // ---- cycling ----

    [Theory]
    [InlineData(1000, 1, 1250)]
    [InlineData(1250, 1, 1500)]
    [InlineData(1500, 1, 1750)]
    [InlineData(1750, 1, 2000)]
    [InlineData(2000, 1, 2000)]   // clamped at the top
    [InlineData(1000, -1, 750)]
    [InlineData(750, -1, 750)]    // clamped at the bottom
    [InlineData(1500, -1, 1250)]
    [InlineData(1100, 1, 1250)]   // invalid current seeds from normal
    public void Step_CyclesTheLadderAndClamps(int current, int direction, int expected)
        => Assert.Equal(expected, PlaybackSpeed.Step(current, direction));

    // ---- the standing preference ----

    [Fact]
    public void ResolveStandingRate_NullOverride_PlaysNormal()
    {
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = null;

        Assert.Equal(PlaybackSpeed.NormalPerMille, PlaybackSpeed.ResolveStandingRate(user));
    }

    [Fact]
    public void ResolveStandingRate_ValidOverride_Wins()
    {
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = 1500;

        Assert.Equal(1500, PlaybackSpeed.ResolveStandingRate(user));
    }

    [Fact]
    public void ResolveStandingRate_StaleInvalidValue_FailsClosedToNormal()
    {
        var user = TestHelpers.CreateTestUser();
        user.PodcastSpeedPerMille = 1337;

        Assert.Equal(PlaybackSpeed.NormalPerMille, PlaybackSpeed.ResolveStandingRate(user));
    }

    [Fact]
    public void ResolveStandingRate_NullUser_PlaysNormal()
        => Assert.Equal(PlaybackSpeed.NormalPerMille, PlaybackSpeed.ResolveStandingRate(null));

    // ---- the position arithmetic (content = stream x rate) ----

    [Fact]
    public void StreamTicksToContent_Rate1000_IsIdentity()
    {
        long ticks = TimeSpan.FromMinutes(12.5).Ticks;
        Assert.Equal(ticks, PlaybackSpeed.StreamTicksToContent(ticks, 1000));
    }

    [Fact]
    public void StreamTicksToContent_DoubleSpeed_DoublesContent()
    {
        // 60s of content at 2x plays in 30s of stream: a 30s stream offset IS
        // the full 60s of content (the spec's worked example).
        long stream30s = TimeSpan.FromSeconds(30).Ticks;
        Assert.Equal(TimeSpan.FromSeconds(60).Ticks, PlaybackSpeed.StreamTicksToContent(stream30s, 2000));
    }

    [Fact]
    public void StreamTicksToContent_HalfSteps_CarryQuarterPrecision()
    {
        long stream10s = TimeSpan.FromSeconds(10).Ticks;
        Assert.Equal(TimeSpan.FromSeconds(7.5).Ticks, PlaybackSpeed.StreamTicksToContent(stream10s, 750));
        Assert.Equal(TimeSpan.FromSeconds(12.5).Ticks, PlaybackSpeed.StreamTicksToContent(stream10s, 1250));
        Assert.Equal(TimeSpan.FromSeconds(15).Ticks, PlaybackSpeed.StreamTicksToContent(stream10s, 1500));
        Assert.Equal(TimeSpan.FromSeconds(17.5).Ticks, PlaybackSpeed.StreamTicksToContent(stream10s, 1750));
    }

    [Fact]
    public void StreamTicksToContent_TruncatesBelowAudibleDrift()
    {
        // 1 tick (100ns) at 1.5x: 1 * 1500 / 1000 = 1.5 -> truncates to 1 tick.
        Assert.Equal(1L, PlaybackSpeed.StreamTicksToContent(1, 1500));
        // 3 ticks at 1.75x: 5.25 -> 5.
        Assert.Equal(5L, PlaybackSpeed.StreamTicksToContent(3, 1750));
    }

    [Fact]
    public void StreamTicksToContent_ZeroStream_IsZero()
        => Assert.Equal(0L, PlaybackSpeed.StreamTicksToContent(0, 2000));

    // ---- the slot resolver: entity resolution first ----

    private static Slot EntitySlot(string id, string name)
    {
        var slot = new Slot { Name = "speed", Value = name };
        slot.Resolution = new global::Alexa.NET.Request.Resolution
        {
            Authorities = new[]
            {
                new global::Alexa.NET.Request.ResolutionAuthority
                {
                    Status = new global::Alexa.NET.Request.ResolutionStatus { Code = "ER_SUCCESS_MATCH" },
                    Values = new[]
                    {
                        new global::Alexa.NET.Request.ResolutionValueContainer
                        {
                            Value = new global::Alexa.NET.Request.ResolutionValue { Name = name, Id = id }
                        }
                    }
                }
            }
        };
        return slot;
    }

    [Theory]
    [InlineData("750", 750)]
    [InlineData("1000", 1000)]
    [InlineData("1250", 1250)]
    [InlineData("1500", 1500)]
    [InlineData("1750", 1750)]
    [InlineData("2000", 2000)]
    public void Resolve_EntityIdPerMille_YieldsDirectRate(string id, int perMille)
    {
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(EntitySlot(id, "whatever"), "it-IT");

        Assert.Equal(PlaybackSpeed.RequestKind.DirectRate, request.Kind);
        Assert.Equal(perMille, request.PerMille);
    }

    [Fact]
    public void Resolve_EntityIdFaster_YieldsFaster()
    {
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(EntitySlot(PlaybackSpeed.FasterId, "più veloce"), "it-IT");

        Assert.Equal(PlaybackSpeed.RequestKind.Faster, request.Kind);
    }

    [Fact]
    public void Resolve_EntityIdSlower_YieldsSlower()
    {
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(EntitySlot(PlaybackSpeed.SlowerId, "slower"), "en-US");

        Assert.Equal(PlaybackSpeed.RequestKind.Slower, request.Kind);
    }

    [Fact]
    public void Resolve_EntityIdUnservedRate_IsIgnoredToFallback()
    {
        // An id outside the served table must not mint a rate; the raw value
        // (which resolves nothing here) yields None -> the elicit prompt.
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(EntitySlot("1337", "uno e trentatré"), "it-IT");

        Assert.Equal(PlaybackSpeed.RequestKind.None, request.Kind);
    }

    // ---- the slot resolver: raw words and spoken decimals ----

    private static Slot RawSlot(string value)
        => new() { Name = "speed", Value = value };

    [Theory]
    [InlineData("it-IT", "più veloce", "Faster", 1000)]
    [InlineData("it-IT", "rallenta", "Slower", 1000)]
    [InlineData("it-IT", "uno e mezzo", "DirectRate", 1500)]
    [InlineData("en-US", "faster", "Faster", 1000)]
    [InlineData("en-US", "one and a half", "DirectRate", 1500)]
    [InlineData("de-DE", "langsamer", "Slower", 1000)]
    [InlineData("es-ES", "más rápido", "Faster", 1000)]
    [InlineData("fr-FR", "plus vite", "Faster", 1000)]
    [InlineData("pt-BR", "mais devagar", "Slower", 1000)]
    [InlineData("nl-NL", "sneller", "Faster", 1000)]
    [InlineData("hi-IN", "तेज़", "Faster", 1000)]
    [InlineData("ja-JP", "速く", "Faster", 1000)]
    [InlineData("ar-SA", "أسرع", "Faster", 1000)]
    public void Resolve_RawLocalizedWord_ResolvesWithoutEntityData(
        string locale, string spoken, string expectedKind, int expectedRate)
    {
        // The kind travels as its name (the enum is internal; a public test
        // method cannot carry it as a parameter).
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(RawSlot(spoken), locale);

        Assert.Equal(expectedKind, request.Kind.ToString());
        if (expectedKind == "DirectRate")
        {
            Assert.Equal(expectedRate, request.PerMille);
        }
    }

    [Theory]
    [InlineData("1,5", 1500)]
    [InlineData("1.5", 1500)]
    [InlineData("0.75", 750)]
    [InlineData("2", 2000)]
    [InlineData("2.0", 2000)]
    [InlineData("1.25", 1250)]
    [InlineData("1.75", 1750)]
    public void Resolve_SpokenDecimal_SnapsToQuarterSteps(string spoken, int expectedRate)
    {
        PlaybackSpeed.Request request = PlaybackSpeed.Resolve(RawSlot(spoken), "it-IT");

        Assert.Equal(PlaybackSpeed.RequestKind.DirectRate, request.Kind);
        Assert.Equal(expectedRate, request.PerMille);
    }

    [Theory]
    [InlineData("1.3")]     // not a served step
    [InlineData("3")]       // out of atempo range
    [InlineData("0.1")]     // out of atempo range
    [InlineData("tartaruga")] // not a speed word
    public void Resolve_UnrecognizedValue_IsNone(string spoken)
        => Assert.Equal(PlaybackSpeed.RequestKind.None, PlaybackSpeed.Resolve(RawSlot(spoken), "it-IT").Kind);

    [Fact]
    public void Resolve_MissingOrNullSlot_IsNone()
    {
        Assert.Equal(PlaybackSpeed.RequestKind.None, PlaybackSpeed.Resolve(null, "it-IT").Kind);
        Assert.Equal(PlaybackSpeed.RequestKind.None, PlaybackSpeed.Resolve(RawSlot(string.Empty), "it-IT").Kind);
        Assert.Equal(PlaybackSpeed.RequestKind.None, PlaybackSpeed.Resolve(RawSlot("   "), "it-IT").Kind);
    }
}
