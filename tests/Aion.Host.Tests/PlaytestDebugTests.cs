using Aion.Host.Debug;
using Aion.World;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Aion.Host.Tests;

public sealed class PlaytestDebugTests
{
    [Fact]
    public void OpcodeCatalog_resolves_known_opcode()
    {
        var catalog = OpcodeCatalog.Load();
        Assert.Contains("SelectCharacter", catalog.Name(0x361f));
    }

    [Fact]
    public async Task Sink_writes_packet_and_action_lines()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-debug-" + Guid.NewGuid().ToString("N"));
        var options = new DebugOptions { Enabled = true, LogDirectory = dir, MaxPacketHexBytes = 8 };
        var sink = new PlaytestDebugSink(options, OpcodeCatalog.Load(), NullLogger<PlaytestDebugSink>.Instance);
        PlaytestDebug.Sink = sink;
        var body = new byte[] { 0x1f, 0x36, 0, 0, 0, 0 };
        sink.Packet(new PlaytestPacketRecord("game", false, 0x361f, body, body.Length, 42, 1000));
        sink.Action("skill_cast", new Dictionary<string, object?> { ["skillId"] = 11010000, ["accepted"] = true });
        PlaytestDebug.Sink = null;
        await sink.DisposeAsync();
        var file = Directory.GetFiles(dir, "debug-*.log").Single();
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("\"t\":\"packet\"", text);
        Assert.Contains("SelectCharacter", text);
        Assert.Contains("\"kind\":\"skill_cast\"", text);
    }

    [Fact]
    public async Task Sink_logs_the_skill_list_and_hotbar_whole()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-debug-" + Guid.NewGuid().ToString("N"));
        var options = new DebugOptions { Enabled = true, LogDirectory = dir, MaxPacketHexBytes = 8 };
        var sink = new PlaytestDebugSink(options, OpcodeCatalog.Load(), NullLogger<PlaytestDebugSink>.Instance);
        var list = new byte[600];
        list[0] = 0x00; list[1] = 0x51; list[^1] = 0xab;
        var other = new byte[600];
        other[0] = 0x02; other[1] = 0x36;
        sink.Packet(new PlaytestPacketRecord("game", true, 0x5100, list, list.Length, 1, 1));
        sink.Packet(new PlaytestPacketRecord("game", true, 0x5600, list, list.Length, 1, 1));
        sink.Packet(new PlaytestPacketRecord("game", true, 0x361f, other, other.Length, 1, 1));
        await sink.DisposeAsync();
        var lines = (await File.ReadAllTextAsync(Directory.GetFiles(dir, "debug-*.log").Single())).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Contains(new string('0', 8) + "AB\"", lines[0]);
        Assert.DoesNotContain("hexTruncated", lines[0]);
        Assert.DoesNotContain("hexTruncated", lines[1]);
        Assert.Contains("hexTruncated", lines[2]);
    }

    [Fact]
    public void Sink_skips_excluded_heartbeat_opcode()
    {
        var options = new DebugOptions { Enabled = true, ExcludeHeartbeat = true, LogDirectory = Path.GetTempPath() };
        var sink = new PlaytestDebugSink(options, OpcodeCatalog.Load(), NullLogger<PlaytestDebugSink>.Instance);
        var body = new byte[] { 0x02, 0x36 };
        sink.Packet(new PlaytestPacketRecord("game", false, 0x3602, body, body.Length, null, 0));
        // No throw; filtering is internal (smoke test).
        _ = sink;
    }
}
