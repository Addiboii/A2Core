using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ContentsUnlockTests
{
    [Fact]
    public void Gather_unlocks_at_45_not_10_after_october_patch()
    {
        Assert.DoesNotContain((byte)11, GamePackets.UnlockedContents(44));
        Assert.Contains((byte)11, GamePackets.UnlockedContents(45));
    }

    [Fact]
    public void World_entry_includes_chat_and_customizing_when_quests_unlocked()
    {
        var quest = new byte[] { 43, 45 };
        var list = GamePackets.WorldEntryContentsUnlocks(16, quest, []);
        Assert.Contains((byte)43, list);
        Assert.Contains((byte)45, list);
    }

    [Fact]
    public void World_entry_withholds_chat_until_starter_quest()
    {
        var list = GamePackets.WorldEntryContentsUnlocks(16, [], []);
        Assert.DoesNotContain((byte)45, list);
        Assert.DoesNotContain((byte)43, list);
    }
}
