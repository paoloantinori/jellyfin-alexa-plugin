using System;
using Alexa.NET.Response;
using Jellyfin.Plugin.AlexaSkill.Alexa.Directive;
using Jellyfin.Plugin.AlexaSkill.Alexa.Util;
using Jellyfin.Plugin.AlexaSkill.Configuration;
using Jellyfin.Plugin.AlexaSkill.Tests.Unit;
using MediaBrowser.Controller.Entities;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-767 Finding B, the MINT side of the token-scope threading: the concat URL a
/// restricted user's launch hands the device must carry that user's library scope in
/// the JF-309 token (so the endpoint enumerates the scoped timeline; the endpoint-side
/// application is pinned in VideoAudioControllerTests). The round trip is the
/// load-bearing assert: the URL's token must validate AGAINST the collection parent
/// id and parse back to exactly the user's AllowedLibraryIds, which is the invariant
/// that keeps the mint's scope and the paged head's scope (both derived from
/// GetAllowedLibraryIds on the same user) on one timeline. The unrestricted row pins
/// the byte-compat contract: no scope, the legacy two-field token.
/// </summary>
public class PlaybackLaunchBuilderStreamTokenScopeTests
{
    private const string Secret = "scope-test-secret-please-not-in-prod-32+ch";

    private static (PlaybackLaunchBuilder Launch, PluginConfiguration Config) CreateLaunch()
    {
        var config = new PluginConfiguration();
        TestHelpers.SetServerAddress(config, "https://test.example.com");
        config.StreamTokenSecret = Secret;
        return (TestHelpers.CreateLaunchBuilder(config), config);
    }

    /// <summary>
    /// The load-bearing round trip shared by every Fact: pull the VideoApp source URL
    /// off the launch response and validate its token against the bound parent id,
    /// returning the scope the token carries (null = legacy/unrestricted).
    /// </summary>
    private static Guid[]? ValidatedScopeFromLaunch(SkillResponse response, string boundItemId)
    {
        var directive = Assert.Single(response.Response.Directives) as VideoAppLaunchDirective;
        Assert.NotNull(directive);
        int tokenAt = directive!.VideoItem.Source.IndexOf("token=", StringComparison.Ordinal);
        Assert.True(tokenAt >= 0, $"the launch URL carries a token: {directive.VideoItem.Source}");
        string token = directive.VideoItem.Source[(tokenAt + "token=".Length)..];
        Assert.True(
            StreamTokenHelper.TryValidate(token, boundItemId, Secret, out Guid[]? scope),
            $"the concat URL's token must validate against the bound parent id {boundItemId}");
        return scope;
    }

    [Fact]
    public void AlbumConcatUrl_RestrictedUser_TokenCarriesLibraryScope()
    {
        Guid albumId = Guid.NewGuid();
        Guid musicLib = Guid.NewGuid();
        var song = TestHelpers.CreateSong();
        var (launch, _) = CreateLaunch();

        SkillResponse response = launch.BuildVideoAppAudioResponse(
            song.Id.ToString(), song, TestHelpers.CreateTestUser(allowedLibraryIds: new[] { musicLib.ToString() }),
            context: TestHelpers.CreateContextWithVideoApp(),
            collectionParentId: albumId);

        Assert.Equal(new[] { musicLib }, ValidatedScopeFromLaunch(response, albumId.ToString()));
    }

    [Fact]
    public void AlbumConcatUrl_UnrestrictedUser_MintsLegacyToken()
    {
        Guid albumId = Guid.NewGuid();
        var song = TestHelpers.CreateSong();
        var (launch, _) = CreateLaunch();

        SkillResponse response = launch.BuildVideoAppAudioResponse(
            song.Id.ToString(), song, TestHelpers.CreateTestUser(),
            context: TestHelpers.CreateContextWithVideoApp(),
            collectionParentId: albumId);

        // Null scope = the legacy two-field shape (byte-compat with every pre-JF-767
        // consumer).
        Assert.Null(ValidatedScopeFromLaunch(response, albumId.ToString()));
    }

    [Fact]
    public void AudiobookConcatUrl_RestrictedUser_TokenCarriesLibraryScope()
    {
        Guid bookParent = Guid.NewGuid();
        Guid bookLib = Guid.NewGuid();
        var chapter = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            ParentId = bookParent
        };
        var (launch, _) = CreateLaunch();

        SkillResponse response = launch.BuildVideoAppAudioResponse(
            chapter.Id.ToString(), chapter, TestHelpers.CreateTestUser(allowedLibraryIds: new[] { bookLib.ToString() }),
            context: TestHelpers.CreateContextWithVideoApp());

        Assert.Equal(new[] { bookLib }, ValidatedScopeFromLaunch(response, bookParent.ToString()));
    }

    [Fact]
    public void AudiobookResumeUrl_RestrictedUser_TokenCarriesLibraryScope()
    {
        Guid bookParent = Guid.NewGuid();
        Guid bookLib = Guid.NewGuid();
        var chapter = new MediaBrowser.Controller.Entities.AudioBook
        {
            Name = "Chapter 1",
            Id = Guid.NewGuid(),
            ParentId = bookParent
        };
        var (launch, _) = CreateLaunch();

        SkillResponse response = launch.BuildAudiobookResumeResponse(
            chapter, TimeSpan.FromMinutes(5).Ticks, TestHelpers.CreateTestUser(allowedLibraryIds: new[] { bookLib.ToString() }),
            TestHelpers.CreateContextWithVideoApp());

        Assert.Equal(new[] { bookLib }, ValidatedScopeFromLaunch(response, bookParent.ToString()));
        // The resume slice rides alongside the token, unchanged.
        var directive = Assert.Single(response.Response.Directives) as VideoAppLaunchDirective;
        Assert.Contains($"?start={TimeSpan.FromMinutes(5).Ticks}&token=", directive!.VideoItem.Source, StringComparison.Ordinal);
    }
}
