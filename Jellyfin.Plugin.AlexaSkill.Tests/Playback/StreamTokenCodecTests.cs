using System;
using Jellyfin.Plugin.AlexaSkill.Alexa.Playback;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Playback;

/// <summary>
/// JF-447: the sleep-timer token format ("<c>{guid}|sleep:{utcTicks}</c>") has ONE
/// owner (StreamTokenCodec); the mint site (SleepTimerIntentHandler) and every parse
/// site (event handlers, precompute) go through it. These tests pin the format so the
/// owners cannot drift apart again.
/// </summary>
public class StreamTokenCodecTests
{
    [Fact]
    public void Mint_AndParse_RoundTrip()
    {
        Guid itemId = Guid.NewGuid();
        long deadline = DateTimeOffset.UtcNow.AddMinutes(30).UtcTicks;

        string token = StreamTokenCodec.MintSleepTimerToken(itemId, deadline);

        Assert.True(StreamTokenCodec.TryGetItemId(token, out Guid parsedId));
        Assert.Equal(itemId, parsedId);
        Assert.True(StreamTokenCodec.TryGetSleepDeadlineUtcTicks(token, out long parsedDeadline));
        Assert.Equal(deadline, parsedDeadline);
    }

    [Fact]
    public void TryGetItemId_BareGuid_Parses()
    {
        Guid itemId = Guid.NewGuid();

        Assert.True(StreamTokenCodec.TryGetItemId(itemId.ToString(), out Guid parsed));
        Assert.Equal(itemId, parsed);
    }

    [Fact]
    public void TryGetItemId_NullOrGarbage_Fails()
    {
        Assert.False(StreamTokenCodec.TryGetItemId(null, out _));
        Assert.False(StreamTokenCodec.TryGetItemId(string.Empty, out _));
        Assert.False(StreamTokenCodec.TryGetItemId("not-a-guid", out _));
        Assert.False(StreamTokenCodec.TryGetItemId("not-a-guid|sleep:123", out _));
    }

    [Fact]
    public void TryGetItemId_UnknownSuffix_FailsInsteadOfSplitting()
    {
        // A future suffix owner must extend the codec, not parse ad hoc: an unrecognized
        // suffix makes the WHOLE token unparseable rather than silently splitting on '|'.
        Guid itemId = Guid.NewGuid();

        Assert.False(StreamTokenCodec.TryGetItemId($"{itemId}|other:xyz", out _));
    }

    [Fact]
    public void TryGetItemId_DoubleSleepSuffix_ParsesFirstItemId()
    {
        // Canonicalization guard: minting from an already-suffixed token used to stack
        // suffixes; whatever produces one, the item id still parses from the first
        // segment (the deadline suffix parse fails, pinned separately).
        Guid itemId = Guid.NewGuid();

        Assert.True(StreamTokenCodec.TryGetItemId($"{itemId}|sleep:111|sleep:222", out Guid parsed));
        Assert.Equal(itemId, parsed);
        Assert.False(StreamTokenCodec.TryGetSleepDeadlineUtcTicks($"{itemId}|sleep:111|sleep:222", out _));
    }

    [Fact]
    public void TryGetSleepDeadlineUtcTicks_BareGuid_Fails()
    {
        Assert.False(StreamTokenCodec.TryGetSleepDeadlineUtcTicks(Guid.NewGuid().ToString(), out _));
        Assert.False(StreamTokenCodec.TryGetSleepDeadlineUtcTicks(null, out _));
    }

    [Fact]
    public void TryGetSleepDeadlineUtcTicks_NonNumericDeadline_Fails()
    {
        Guid itemId = Guid.NewGuid();

        Assert.False(StreamTokenCodec.TryGetSleepDeadlineUtcTicks($"{itemId}|sleep:not-a-number", out _));
    }
}
