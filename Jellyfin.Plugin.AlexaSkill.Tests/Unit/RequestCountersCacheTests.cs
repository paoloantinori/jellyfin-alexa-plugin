using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class RequestCountersCacheTests
{
    private readonly ILogger _logger;

    public RequestCountersCacheTests()
    {
        _logger = LoggerFactory.Create(b => { }).CreateLogger<RequestCountersCacheTests>();
    }

    // --- RequestCounters counter tests ---

    [Fact]
    public void CacheHits_InitiallyZero()
    {
        var counters = new RequestCounters();
        Assert.Equal(0, counters.CacheHits);
    }

    [Fact]
    public void CacheMisses_InitiallyZero()
    {
        var counters = new RequestCounters();
        Assert.Equal(0, counters.CacheMisses);
    }

    [Fact]
    public void IncrementCacheHit_IncrementsCounter()
    {
        var counters = new RequestCounters();
        counters.IncrementCacheHit();
        Assert.Equal(1, counters.CacheHits);
        Assert.Equal(0, counters.CacheMisses);
    }

    [Fact]
    public void IncrementCacheMiss_IncrementsCounter()
    {
        var counters = new RequestCounters();
        counters.IncrementCacheMiss();
        Assert.Equal(0, counters.CacheHits);
        Assert.Equal(1, counters.CacheMisses);
    }

    [Fact]
    public void IncrementCacheHit_MultipleCalls_Accumulates()
    {
        var counters = new RequestCounters();
        for (int i = 0; i < 5; i++)
        {
            counters.IncrementCacheHit();
        }

        Assert.Equal(5, counters.CacheHits);
    }

    [Fact]
    public void IncrementCacheMiss_MultipleCalls_Accumulates()
    {
        var counters = new RequestCounters();
        for (int i = 0; i < 3; i++)
        {
            counters.IncrementCacheMiss();
        }

        Assert.Equal(3, counters.CacheMisses);
    }

    // --- Thread safety test ---

    [Fact]
    public void IncrementCacheHit_ConcurrentIncrements_Accurate()
    {
        var counters = new RequestCounters();
        const int iterations = 100;

        Parallel.For(0, iterations, _ =>
        {
            counters.IncrementCacheHit();
            counters.IncrementCacheMiss();
        });

        Assert.Equal(iterations, counters.CacheHits);
        Assert.Equal(iterations, counters.CacheMisses);
    }
}
