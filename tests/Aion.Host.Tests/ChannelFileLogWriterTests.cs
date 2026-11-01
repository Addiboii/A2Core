using Aion.Host.Debug;
using Aion.Host.Logging;
using Xunit;

namespace Aion.Host.Tests;

public sealed class ChannelFileLogWriterTests
{
    [Fact]
    public void Debug_log_filename_uses_local_calendar_day()
    {
        var name = PlaytestLogPaths.DebugFileName(new DateTime(2026, 10, 30, 0, 26, 42, DateTimeKind.Local));
        Assert.Equal("debug-20261030.log", name);
    }

    static string TempLog() => Path.Combine(Path.GetTempPath(), "a2core-channellog-" + Guid.NewGuid().ToString("N") + ".log");

    static async Task<long> WaitForLengthAsync(string path, int timeoutMs = 3000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 50)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return new FileInfo(path).Length;
            await Task.Delay(50);
        }
        return File.Exists(path) ? new FileInfo(path).Length : -1;
    }

    [Fact]
    public async Task Writer_puts_lines_on_disk_without_dispose()
    {
        var path = TempLog();
        var writer = new ChannelFileLogWriter(path);
        try
        {
            writer.Enqueue("visible before shutdown");
            Assert.True(await WaitForLengthAsync(path) > 0, "the line must reach the disk while the writer is still open");
        }
        finally { await writer.DisposeAsync(); }
    }

    [Fact]
    public async Task Writer_moves_to_a_sibling_file_when_the_path_is_held_exclusively()
    {
        var path = TempLog();
        using var holder = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        var writer = new ChannelFileLogWriter(path);
        try
        {
            writer.Enqueue("still logged");
            var sibling = writer.Path;
            Assert.NotEqual(path, sibling);
            Assert.True(await WaitForLengthAsync(sibling) > 0, "the line must land in the sibling file");
        }
        finally
        {
            await writer.DisposeAsync();
            File.Delete(writer.Path);
        }
    }

    [Fact]
    public async Task Writer_appends_to_an_existing_shared_file()
    {
        var path = TempLog();
        File.WriteAllText(path, "old line\n");
        using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var writer = new ChannelFileLogWriter(path);
        try
        {
            writer.Enqueue("new line");
            for (var i = 0; i < 60 && new FileInfo(path).Length <= 9; i++) await Task.Delay(50);
            Assert.Equal(path, writer.Path);
            Assert.Contains("new line", File.ReadAllText(path));
        }
        finally { await writer.DisposeAsync(); }
    }
}
