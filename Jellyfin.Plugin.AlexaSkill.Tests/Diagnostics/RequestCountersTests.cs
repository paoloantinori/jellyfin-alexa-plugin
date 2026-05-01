using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Diagnostics;

public class RequestCountersTests
{
    [Fact]
    public void TotalRequests_StartsAtZero()
    {
        var counters = new RequestCounters();
        Assert.Equal(0, counters.TotalRequests);
    }

    [Fact]
    public void TotalErrors_StartsAtZero()
    {
        var counters = new RequestCounters();
        Assert.Equal(0, counters.TotalErrors);
    }

    [Fact]
    public void IncrementRequests_IncrementsByOne()
    {
        var counters = new RequestCounters();
        counters.IncrementRequests();
        Assert.Equal(1, counters.TotalRequests);
    }

    [Fact]
    public void IncrementErrors_IncrementsByOne()
    {
        var counters = new RequestCounters();
        counters.IncrementErrors();
        Assert.Equal(1, counters.TotalErrors);
    }

    [Fact]
    public void IncrementType_TracksPerTypeCount()
    {
        var counters = new RequestCounters();
        counters.IncrementType("IntentRequest");
        counters.IncrementType("IntentRequest");
        counters.IncrementType("AudioPlayerRequest");

        Assert.Equal(2, counters.PerType["IntentRequest"]);
        Assert.Equal(1, counters.PerType["AudioPlayerRequest"]);
    }

    [Fact]
    public void IncrementType_NewTypeStartsAtOne()
    {
        var counters = new RequestCounters();
        counters.IncrementType("NewType");
        Assert.Equal(1, counters.PerType["NewType"]);
    }

    [Fact]
    public void PerType_IsEmptyByDefault()
    {
        var counters = new RequestCounters();
        Assert.Empty(counters.PerType);
    }

    [Fact]
    public void MultipleIncrements_AccumulateCorrectly()
    {
        var counters = new RequestCounters();
        for (int i = 0; i < 100; i++)
        {
            counters.IncrementRequests();
            if (i % 10 == 0)
            {
                counters.IncrementErrors();
            }
        }

        Assert.Equal(100, counters.TotalRequests);
        Assert.Equal(10, counters.TotalErrors);
    }

    [Fact]
    public async Task Counters_AreThreadSafe()
    {
        var counters = new RequestCounters();
        var tasks = new List<Task>();

        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                counters.IncrementRequests();
                counters.IncrementType("ConcurrentTest");
            }));
        }

        await Task.WhenAll(tasks);

        Assert.Equal(100, counters.TotalRequests);
        Assert.Equal(100, counters.PerType["ConcurrentTest"]);
    }
}
