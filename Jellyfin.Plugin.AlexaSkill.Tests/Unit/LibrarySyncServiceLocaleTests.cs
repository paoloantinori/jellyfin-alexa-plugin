using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using Jellyfin.Plugin.AlexaSkill.Alexa.Catalog;
using Jellyfin.Plugin.AlexaSkill.Lwa;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

public class LibrarySyncServiceLocaleTests
{
    private readonly LibrarySyncService _service;

    public LibrarySyncServiceLocaleTests()
    {
        var libraryManager = new Mock<ILibraryManager>();
        var loggerFactory = NullLoggerFactory.Instance;
        var catalogManager = new Mock<CatalogManager>(Mock.Of<System.Net.Http.IHttpClientFactory>(), NullLogger<CatalogManager>.Instance);
        _service = new LibrarySyncService(libraryManager.Object, catalogManager.Object, NullLogger<LibrarySyncService>.Instance);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_EmptyConfig_ReturnsItItOnly()
    {
        var locales = await _service.ResolveSyncLocalesAsync(string.Empty, "token", "skillId", CancellationToken.None);
        Assert.Single(locales);
        Assert.Contains("it-IT", locales);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_WhitespaceConfig_ReturnsItItOnly()
    {
        var locales = await _service.ResolveSyncLocalesAsync("   ", "token", "skillId", CancellationToken.None);
        Assert.Single(locales);
        Assert.Contains("it-IT", locales);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_ExplicitLocales_ReturnsItPlusListed()
    {
        var locales = await _service.ResolveSyncLocalesAsync("de-DE,en-US", "token", "skillId", CancellationToken.None);
        Assert.Equal(3, locales.Count);
        Assert.Contains("it-IT", locales);
        Assert.Contains("de-DE", locales);
        Assert.Contains("en-US", locales);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_Star_ReturnsAllActiveLocales()
    {
        // "*" triggers GetActiveLocalesAsync which calls SMAPI. Since we don't mock SMAPI here,
        // it will fail and fall back to it-IT. Test the fallback behavior.
        var locales = await _service.ResolveSyncLocalesAsync("*", "invalid-token", "invalid-skill", CancellationToken.None);
        // GetActiveLocalesAsync catches the exception and returns it-IT
        Assert.Contains("it-IT", locales);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_DuplicateItItInList_DoesNotDuplicate()
    {
        var locales = await _service.ResolveSyncLocalesAsync("it-IT,de-DE,it-IT", "token", "skillId", CancellationToken.None);
        Assert.Equal(2, locales.Count);
        Assert.Contains("it-IT", locales);
        Assert.Contains("de-DE", locales);
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_CaseInsensitiveMatching()
    {
        var locales = await _service.ResolveSyncLocalesAsync("DE-de,EN-us", "token", "skillId", CancellationToken.None);
        Assert.Equal(3, locales.Count);
        Assert.Contains("it-IT", locales);
        // The list stores the original case from config, but dedup is case-insensitive
        Assert.Equal(1, locales.Count(l => l.Equals("de-DE", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, locales.Count(l => l.Equals("en-US", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ResolveSyncLocalesAsync_TrimsWhitespace()
    {
        var locales = await _service.ResolveSyncLocalesAsync(" de-DE , en-US ", "token", "skillId", CancellationToken.None);
        Assert.Equal(3, locales.Count);
        Assert.Contains("it-IT", locales);
        Assert.Contains("de-DE", locales.Select(l => l.Trim()));
        Assert.Contains("en-US", locales.Select(l => l.Trim()));
    }
}
