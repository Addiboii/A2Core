using AionClientProbe;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class ToolingPathTests
{
    [Fact]
    public void DefaultUsmapPath_exists_in_repo_checkout()
    {
        var path = AionClientSession.DefaultUsmapPath();
        Assert.True(File.Exists(path), path);
        Assert.StartsWith(AionClientSession.DefaultMappingsFolder(), path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FindRepoRoot_finds_research_local_tooling()
    {
        var root = AionClientSession.FindRepoRoot();
        Assert.True(Directory.Exists(Path.Combine(root, "research", "local", "tooling")));
    }

    [Fact]
    public void TryResolveOodle_dll_uses_repo_tooling_when_present()
    {
        var expected = Path.Combine(AionClientSession.DefaultToolingRoot(), "oodle-2.9.16", "oodle-data-shared.dll");
        if (!File.Exists(expected))
            return;

        Assert.Equal(Path.GetFullPath(expected), AionClientSession.TryResolveOodleDll(null));
    }
}
