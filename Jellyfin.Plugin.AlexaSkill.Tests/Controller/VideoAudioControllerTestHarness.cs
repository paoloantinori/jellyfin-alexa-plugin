using System;
using System.IO;
using Jellyfin.Plugin.AlexaSkill.Alexa;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static Jellyfin.Plugin.AlexaSkill.Tests.Unit.TestHelpers;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Controller;

/// <summary>
/// JF-792: the shared fixture of the VideoAudioController test classes. Owns
/// the per-instance surface only (logger factory, library/media-encoder mocks,
/// the per-class temp dir and its <see cref="VideoAudioCache"/>) and the
/// best-effort temp-dir delete on Dispose. Deliberately touches NO shared
/// static state: no <c>Plugin.Instance</c>, no encode gate, no live-encode
/// registries, so a class deriving from it may run OUTSIDE the Plugin
/// collection (in the parallel phase) as long as its tests stay on this
/// instance surface. The Plugin-collection member
/// (<see cref="VideoAudioControllerTests"/>) layers the shared-instance
/// ceremony on top: the per-test static resets (the former PluginTestBase
/// inheritance), <see cref="EnsurePluginInstance"/>, the shared config, and
/// the JF-731 encode-gate teardown backstop.
/// </summary>
public abstract class VideoAudioControllerTestHarness : IDisposable
{
    protected readonly ILoggerFactory _loggerFactory;
    protected readonly Mock<ILibraryManager> _libraryManagerMock;
    // Never configured (the JF-759/JF-765 marker-probe sweeps): ffmpeg
    // resolution in these tests comes only from paths injected at the
    // controller (CreateController ffmpegPath: / .FfmpegPath), which
    // ResolveFfmpegPath returns before ever consulting IMediaEncoder. Every
    // EncoderPath setup this fixture carried was provably inert and is deleted;
    // a new test that needs an encode passes a fake, and a test that only
    // needs the non-empty ffmpeg gate may set a plain string path (the 404
    // families' "/usr/bin/ffmpeg" assignments, never executed).
    protected readonly Mock<IMediaEncoder> _mediaEncoderMock;
    protected readonly VideoAudioCache _cache;
    protected readonly string _tempDir;

    protected VideoAudioControllerTestHarness()
    {
        _loggerFactory = LoggerFactory.Create(b => { });
        _libraryManagerMock = new Mock<ILibraryManager>();
        _mediaEncoderMock = new Mock<IMediaEncoder>();

        // Create a temp dir for the cache service used by controller tests
        _tempDir = CreateRegisteredTempDir("va-ctrl-test");

        var appPaths = new Mock<IApplicationPaths>();
        appPaths.Setup(p => p.CachePath).Returns(_tempDir);

        var cacheLogger = _loggerFactory.CreateLogger<VideoAudioCache>();
        _cache = new VideoAudioCache(appPaths.Object, cacheLogger);
    }

    public virtual void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup
        }

        GC.SuppressFinalize(this);
    }

    // ---- JF-499 W4: permission-denied deletes must not surface as 500s ----

    protected const UnixFileMode WritableDirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    protected const UnixFileMode ReadOnlyDirMode = UnixFileMode.UserRead | UnixFileMode.UserExecute;

    /// <summary>
    /// JF-499 W4 shared shape: run the act inside a directory whose mode denies
    /// writes (unlink needs write permission on the directory), always restoring the
    /// mode so the fixture's recursive temp cleanup still works.
    /// </summary>
    protected static void AssertSurvivesDeniedDirectory(string dir, Action act)
    {
#pragma warning disable CA1416, CA3003 // Unix-only test; test-created path
        File.SetUnixFileMode(dir, ReadOnlyDirMode);
        try
        {
            var ex = Record.Exception(act);
            Assert.Null(ex);
        }
        finally
        {
            File.SetUnixFileMode(dir, WritableDirMode);
        }
#pragma warning restore CA1416, CA3003
    }
}
