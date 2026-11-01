using Aion.Host.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Aion.Host.Tests;

public sealed class ServerFileLoggerTests
{
    [Fact]
    public void Provider_writes_log_line_to_utf8_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-serverlog-" + Guid.NewGuid().ToString("N"));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Aion:ServerLog:Enabled"] = "true",
                ["Aion:ServerLog:LogDirectory"] = dir,
                ["Aion:ServerLog:MinLevel"] = "Information",
            })
            .Build();
        using var provider = ServerFileLoggerProvider.TryCreate(config)!;
        var logger = provider.CreateLogger("Test.Category");
        logger.LogInformation("hello playtest");
        provider.Dispose();
        var file = Directory.GetFiles(dir, "server-*.log").Single();
        var bytes = File.ReadAllBytes(file);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("Test.Category", text);
        Assert.Contains("hello playtest", text);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public void UniquePath_skips_a_file_the_launcher_tee_already_holds()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-serverlog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var tee = new FileStream(Path.Combine(dir, "server-20261030-005524.log"), FileMode.Append, FileAccess.Write, FileShare.Read);
        Assert.Equal(Path.Combine(dir, "server-20261030-005524-host.log"), ServerFileLoggerProvider.UniquePath(dir, "20261030-005524"));
        File.WriteAllText(Path.Combine(dir, "server-20261030-005524-host.log"), "");
        Assert.Equal(Path.Combine(dir, "server-20261030-005524-host-2.log"), ServerFileLoggerProvider.UniquePath(dir, "20261030-005524"));
    }
}