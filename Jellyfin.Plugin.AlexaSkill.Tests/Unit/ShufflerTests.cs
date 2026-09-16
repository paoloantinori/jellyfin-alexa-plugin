using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-315 batch 10 characterization suite for the Shuffle family
/// (Shuffle/ShuffleCopy/ShuffleAndCap, Alexa/Util/Shuffler.cs). The family had NO
/// direct tests before the move (only indirect coverage through the
/// radio/random handlers), so these facts pin the multiset-preservation and cap
/// contracts BEFORE the move: they were written green on the pre-move
/// BaseHandler code via a probe subclass, then retargeted to the static home
/// after the move (receiver swap only, zero expectation edits).
/// </summary>
public class ShufflerTests
{
    private static List<int> Range(int count) => Enumerable.Range(0, count).ToList();

    [Fact]
    public void ShuffleCopy_PreservesMultiset()
    {
        List<int> source = Range(200);

        List<int> result = Shuffler.ShuffleCopy(source);

        Assert.Equal(200, result.Count);
        Assert.Equal(source.OrderBy(i => i), result.OrderBy(i => i));
    }

    [Fact]
    public void ShuffleCopy_EmptySource_ReturnsEmptyList()
    {
        Assert.Empty(Shuffler.ShuffleCopy(Array.Empty<int>()));
    }

    [Fact]
    public void ShuffleAndCap_CapsAndKeepsSubsetOfTheMultiset()
    {
        List<int> source = Range(100);

        List<int> result = Shuffler.ShuffleAndCap(source, 15);

        Assert.Equal(15, result.Count);
        Assert.Subset(source.ToHashSet(), result.ToHashSet());
    }

    [Theory]
    [InlineData(5)] // cap == count
    [InlineData(10)] // cap > count
    public void ShuffleAndCap_CapAtOrAboveCount_ReturnsEverything(int cap)
    {
        List<int> source = Range(5);

        List<int> result = Shuffler.ShuffleAndCap(source, cap);

        Assert.Equal(5, result.Count);
        Assert.Equal(source.OrderBy(i => i), result.OrderBy(i => i));
    }

    [Fact]
    public void Shuffle_InPlace_EmptyAndSingletonDoNotThrow()
    {
        var empty = new List<int>();
        Shuffler.Shuffle(empty);
        Assert.Empty(empty);

        var singleton = new List<int> { 42 };
        Shuffler.Shuffle(singleton);
        Assert.Single(singleton, 42);
    }
}
