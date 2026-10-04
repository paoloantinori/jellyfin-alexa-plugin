using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AlexaSkill.Alexa.Cache;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Alexa.Cache;

/// <summary>
/// JF-645 item 3: the TTL cache behind the kana genre-resolution tier's
/// vocabulary. Pinned behaviors: hit returns the SAME entry instance (the tier
/// matches directly on the cached candidates, no rebuild), expiry removes the
/// entry and reports a miss (the SearchResultCache shape, pinned deterministically
/// through the FakeTimeProvider clock seam instead of sleeping), and the SCOPE key
/// separates Jellyfin users and library restrictions (a vocabulary fetched for one
/// scope must never serve another).
/// </summary>
[Collection("Plugin")]
public class GenreVocabularyCacheTests : PluginTestBase
{
    private static GenreVocabulary MakeVocabulary(params string[] tagNames)
    {
        var candidates = new List<BaseItem>();
        var codes = new Dictionary<Guid, (string Primary, string? Alternate)>();
        foreach (string name in tagNames)
        {
            var tag = new MusicArtist { Name = name, Id = Guid.NewGuid() };
            candidates.Add(tag);
            codes[tag.Id] = (name, null);
        }

        return new GenreVocabulary(candidates, codes);
    }

    [Fact]
    public void PutThenTryGet_SameUserAndScope_ReturnsCachedInstance()
    {
        var cache = new GenreVocabularyCache();
        Guid userId = Guid.NewGuid();
        Guid[] scope = { Guid.NewGuid(), Guid.NewGuid() };
        GenreVocabulary vocabulary = MakeVocabulary("Jazz");

        cache.Put(userId, scope, vocabulary);

        Assert.True(cache.TryGet(userId, scope, out GenreVocabulary? cached));
        Assert.Same(vocabulary, cached);
    }

    [Fact]
    public void TryGet_ExpiredEntry_ReturnsFalseAndRemoves()
    {
        // Deterministic expiry (the FakeTimeProvider seam, so no TTL test sleeps):
        // put under a fake clock, advance past the TTL, the entry reads as expired.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var cache = new GenreVocabularyCache(TimeSpan.FromMinutes(30), clock);
        Guid userId = Guid.NewGuid();
        cache.Put(userId, null, MakeVocabulary("Jazz"));
        Assert.Equal(1, cache.Count);

        clock.Advance(TimeSpan.FromMinutes(31));
        Assert.False(cache.TryGet(userId, null, out _));

        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void TryGet_DifferentJellyfinUser_ReturnsFalse()
    {
        var cache = new GenreVocabularyCache();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        cache.Put(first, null, MakeVocabulary("Jazz"));

        Assert.False(cache.TryGet(second, null, out _));
    }

    [Fact]
    public void TryGet_DifferentLibraryScope_ReturnsFalse()
    {
        var cache = new GenreVocabularyCache();
        Guid userId = Guid.NewGuid();
        Guid[] restricted = { Guid.NewGuid() };
        cache.Put(userId, restricted, MakeVocabulary("Jazz"));

        Assert.False(cache.TryGet(userId, null, out _));
        Assert.False(cache.TryGet(userId, new[] { Guid.NewGuid() }, out _));

        // The original scope still hits.
        Assert.True(cache.TryGet(userId, restricted, out _));
    }

    [Fact]
    public void TryGet_LibraryScopeOrderInsensitive_SameSetHits()
    {
        var cache = new GenreVocabularyCache();
        Guid userId = Guid.NewGuid();
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        cache.Put(userId, new[] { a, b }, MakeVocabulary("Jazz"));

        // The scope key is order-normalized, so the same library set resolves the
        // same entry however the caller ordered its ids.
        Assert.True(cache.TryGet(userId, new[] { b, a }, out _));
    }

    [Fact]
    public void Put_SameScopeTwice_Overwrites()
    {
        var cache = new GenreVocabularyCache();
        Guid userId = Guid.NewGuid();
        GenreVocabulary first = MakeVocabulary("Jazz");
        GenreVocabulary second = MakeVocabulary("Rock");

        cache.Put(userId, null, first);
        cache.Put(userId, null, second);

        Assert.True(cache.TryGet(userId, null, out GenreVocabulary? cached));
        Assert.Same(second, cached);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Clear_RemovesEveryScope()
    {
        var cache = new GenreVocabularyCache();
        cache.Put(Guid.NewGuid(), null, MakeVocabulary("Jazz"));
        cache.Put(Guid.NewGuid(), null, MakeVocabulary("Rock"));

        cache.Clear();

        Assert.Equal(0, cache.Count);
    }
}
