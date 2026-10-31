using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AchievementRewardBatchTests
{
    [Fact]
    public void Batch_reply_writes_taken_steps_as_byte_map_values()
    {
        // The three groups of the 2026-10-30 batch claim (category Achievement, all groups).
        var results = new List<(int, IReadOnlyList<byte>, IReadOnlyDictionary<int, int>)>
        {
            (0x17D78465, [], new Dictionary<int, int> { [0x17D90B04] = 1 }),
            (0x17D78466, [], new Dictionary<int, int> { [0x17E84D44] = 1 }),
            (0x17D785F7, [], new Dictionary<int, int> { [0x1A692BC4] = 1, [0x1A692C28] = 1, [0x1A692C8C] = 1, [0x1A692CF0] = 1 }),
        };
        var packet = GameSession.RewardBatchReply(1, 0, false, results);
        Assert.Equal(59, packet.Length);

        Assert.Equal(Convert.FromHexString(
            "8A56" + "0000" + "01" + "00000000" + "00" + "03" +
            "6584D717" + "00" + "01" + "040BD917" + "01" +
            "6684D717" + "00" + "01" + "444DE817" + "01" +
            "F785D717" + "00" + "04" + "C42B691A01" + "282C691A01" + "8C2C691A01" + "F02C691A01"), packet);

        // Walk it as the client does: every count stays a real count and the body is consumed exactly.
        var at = 11;
        for (var g = 0; g < 3; g++)
        {
            at += 4;
            var groupSteps = packet[at++];
            at += groupSteps;
            var entries = packet[at++];
            Assert.InRange(entries, 1, 4);
            at += entries * 5;
        }
        Assert.Equal(packet.Length, at);
    }

    [Fact]
    public void Batch_reply_with_nothing_taken_uses_non_zero_result()
    {
        var packet = GameSession.RewardBatchReply(7, 0x7C853, true, [], 1);
        Assert.Equal(0x568a, BitConverter.ToUInt16(packet, 0));
        Assert.Equal(1, BitConverter.ToUInt16(packet, 2));
        Assert.Equal(7, packet[4]);
        Assert.Equal(0x7C853, BitConverter.ToInt32(packet, 5));
    }
}
