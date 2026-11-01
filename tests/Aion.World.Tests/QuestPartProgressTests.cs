using Aion.Protocol;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

/// <summary>
/// Episode HUD progress: Quest.dat Part membership and EQuestStatus in the journal (0x8d09/0x8d10/0x8d11); _roll_count is the part ordinal only.
/// </summary>
public sealed class QuestPartProgressTests
{
    [Fact]
    public void PartRollIndex_matches_observed_poeta_hero_order()
    {
        var book = QuestBook.Embedded;
        Assert.Equal(1, book.PartRollIndex("HQ1101010"));
        Assert.Equal(2, book.PartRollIndex("HQ1101011"));
        Assert.Equal(10, book.PartRollIndex("HQ1101070"));
    }

    [Fact]
    public void PartRollIndex_for_verteron_chapter_two()
    {
        var book = QuestBook.Embedded;
        var part = book.PartQuests("hero_verteron_02");
        Assert.Equal(15, part.Count);
        Assert.Equal("HQ1102010", part[0]);
        Assert.Equal(1, book.PartRollIndex("HQ1102010"));
        Assert.Equal(15, book.PartRollIndex(part[^1]));
    }

    [Fact]
    public void PartProgressPercent_counts_rewarded_and_partial_steps()
    {
        var book = QuestBook.Embedded;
        var log = new QuestLog(book, 1);
        const string part = "hero_poeta_01";
        Assert.Equal(0, log.PartProgressPercent(part));

        log.Set(QuestLog.New("HQ1101010", book.Find("HQ1101010")!) with { Status = QuestLog.Rewarded });
        Assert.Equal(10, log.PartProgressPercent(part));

        log.Set(QuestLog.New("HQ1101011", book.Find("HQ1101011")!) with { Step = 1, Goals = [1, 0, 0, 0, 0, 0] });
        Assert.True(log.PartProgressPercent(part) > 10, "partial credit on the second episode quest should raise the bar");
    }

    [Fact]
    public void QuestList_encodes_part_roll_count_on_common_quest()
    {
        var book = QuestBook.Embedded;
        var roll = book.PartRollIndex("HQ1101010");
        var packet = GamePackets.QuestList([new QuestWire(1101010, 2, 1, new int[6], 1, RollCount: roll)]);
        var r = new PacketReader(packet) { Offset = 2 };
        Assert.Equal(1, (int)r.Varint());
        Assert.Equal(1101010, r.I32());
        Assert.Equal((byte)2, r.U8());
        Assert.Equal(1, (int)r.Varint());
        for (var i = 0; i < 6; i++) r.Varint();
        Assert.Equal(1, r.I32());
        Assert.Equal(1, (int)r.Varint());
    }
}
