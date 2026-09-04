using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.AlexaSkill.Tests.Unit;

/// <summary>
/// JF-453: pins the register-and-sweep contract that stops the
/// EnsurePluginInstance temp-dir leak. Uses its own sweeper instance, never the
/// shared one, so the assertions do not depend on xUnit's assembly lifecycle and
/// cannot drain the live registry mid-run (deleting a dir another running test's
/// Plugin.Instance still points at).
/// </summary>
public class PluginTempDirSweeperTests
{
    [Fact]
    public void Dispose_DeletesRegisteredDirs()
    {
        var sweeper = new PluginTempDirSweeper();
        string dir = Path.Combine(Path.GetTempPath(), "jf453-sweep-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        sweeper.Register(dir);

        sweeper.Dispose();

        Assert.False(Directory.Exists(dir), $"expected {dir} to be swept");
    }

    [Fact]
    public void Dispose_DeletesDirContents()
    {
        var sweeper = new PluginTempDirSweeper();
        string dir = Path.Combine(Path.GetTempPath(), "jf453-sweep-test-" + Guid.NewGuid());
        string file = Path.Combine(dir, "nested", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "leftover");
        sweeper.Register(dir);

        sweeper.Dispose();

        Assert.False(Directory.Exists(dir), "expected recursive sweep of dir contents");
    }

    [Fact]
    public void Dispose_SwallowsMissingRegisteredDirs()
    {
        var sweeper = new PluginTempDirSweeper();
        string neverCreated = Path.Combine(Path.GetTempPath(), "jf453-sweep-test-" + Guid.NewGuid());
        sweeper.Register(neverCreated);

        var ex = Record.Exception(() => sweeper.Dispose());

        Assert.Null(ex);
        Assert.False(Directory.Exists(neverCreated));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sweeper = new PluginTempDirSweeper();
        string dir = Path.Combine(Path.GetTempPath(), "jf453-sweep-test-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        sweeper.Register(dir);

        sweeper.Dispose();
        var ex = Record.Exception(sweeper.Dispose);

        Assert.Null(ex);
        Assert.False(Directory.Exists(dir));
    }
}
