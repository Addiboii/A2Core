using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class Patch20261028Tests
{
    static long Ms(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeMilliseconds();

    static DungeonInfo Room(int id) => MapEventTables.Embedded.Dungeon(id) ?? throw new InvalidOperationException($"dungeon {id}");

    [Theory]
    [InlineData(600013)]
    [InlineData(600073)]
    public void New_hard_modes_ask_item_level_2700_and_have_a_four_minute_frenzy(int id)
    {
        var room = Room(id);
        Assert.Equal(2700, Patch20261028.EntryItemLevel(room));
        Assert.Equal(4 * 60 * 1000, Patch20261028.FrenzyMs(room));
        Assert.Equal(3, Patch20261028.RevivesPerPlayer(room));
    }

    [Theory]
    [InlineData(600004)]
    [InlineData(600033)]
    public void Krao_and_draupnir_hard_keep_their_gate_and_have_three_revives_without_a_frenzy_timer(int id)
    {
        var room = Room(id);
        Assert.Equal(room.ItemLevel, Patch20261028.EntryItemLevel(room));
        Assert.Equal(3, Patch20261028.RevivesPerPlayer(room));
        Assert.Equal(0, Patch20261028.FrenzyMs(room));
    }

    [Fact]
    public void Other_dungeons_keep_their_table_gate_and_unlimited_revives()
    {
        var room = Room(600012);
        Assert.Equal(room.ItemLevel, Patch20261028.EntryItemLevel(room));
        Assert.Equal(0, Patch20261028.RevivesPerPlayer(room));
        var otherHard = Room(600093);
        Assert.Equal(otherHard.ItemLevel, Patch20261028.EntryItemLevel(otherHard));
    }

    [Theory]
    [InlineData(600001, 0.85)]
    [InlineData(600012, 0.85)]
    [InlineData(600004, 0.81)]
    [InlineData(600073, 0.81)]
    [InlineData(600002, 1.0)]
    public void Boss_attack_cuts_by_expedition_difficulty(int id, double factor) =>
        Assert.Equal(factor, Patch20261028.BossDamageFactor(id, Room(id)), 6);

    [Theory]
    [InlineData(600063, 1, 0.85)]
    [InlineData(600065, 2, 0.81)]
    [InlineData(600068, 3, 0.81)]
    [InlineData(610062, 4, 0.81)]
    public void Transcendence_stages_follow_the_g_group_order(int id, int stage, double factor)
    {
        var room = Room(id);
        Assert.Equal(stage, Patch20261028.TranscendenceStage(id, room));
        Assert.Equal(factor, Patch20261028.BossDamageFactor(id, room), 6);
    }

    [Fact]
    public void Only_cube_bosses_are_scaled()
    {
        var room = Room(600004);
        Assert.True(Patch20261028.IsDungeonBoss(room, room.CubeBoss));
        Assert.False(Patch20261028.IsDungeonBoss(room, 1));
        Assert.Equal(81, Patch20261028.ScaleBossDamage(100, 0.81));
        Assert.Equal(1, Patch20261028.ScaleBossDamage(1, 0.5));
        Assert.Equal(100, Patch20261028.ScaleBossDamage(100, 1));
    }

    [Theory]
    [InlineData(600063, 0.935)]
    [InlineData(600065, 0.935)]
    [InlineData(600068, 1.0)]
    [InlineData(610062, 1.0)]
    [InlineData(600004, 1.0)]
    public void Transcendence_stage_1_and_2_boss_hp_is_6_5_percent_lower(int id, double factor) =>
        Assert.Equal(factor, Patch20261028.BossHpFactor(id, Room(id)), 6);

    [Fact]
    public void Boss_hp_scaling_touches_only_the_dungeon_bosses()
    {
        var room = Room(600063);
        Target Make(int key, int npc) => new() { Key = key, Name = "n", Hp = 10_000, HpMax = 10_000, NpcId = npc };
        var boss = Make(1, room.CubeBoss);
        var other = Make(2, 1);
        Patch20261028.ScaleBossHp([boss, other], 600063, room);
        Assert.Equal((9_350, 9_350), (boss.Hp, boss.HpMax));
        Assert.Equal((10_000, 10_000), (other.Hp, other.HpMax));
        var late = Make(3, Room(600068).CubeBoss);
        Patch20261028.ScaleBossHp([late], 600068, Room(600068));
        Assert.Equal(10_000, late.HpMax);
    }

    [Fact]
    public void Frenzy_starts_after_four_minutes_of_fighting_and_ends_when_the_fight_does()
    {
        var frenzy = new BossFrenzy();
        const int Timer = 4 * 60 * 1000;
        for (long t = 0; t < Timer; t += 10_000) Assert.Equal(1, frenzy.Hit(7, t, Timer));
        Assert.Equal(Patch20261028.FrenzyDamageFactor, frenzy.Hit(7, Timer + 5_000, Timer));
        Assert.True(frenzy.Enraged(7, Timer + 6_000, Timer));
        var later = Timer + 5_000 + Patch20261028.FrenzyResetMs + 1;
        Assert.Equal(1, frenzy.Hit(7, later, Timer));
        Assert.Equal(1, frenzy.Hit(8, 0, 0));
    }

    [Fact]
    public void Idle_return_is_thirty_minutes() =>
        Assert.Equal(TimeSpan.FromMinutes(30), Patch20261028.IdleReturnToLobby);

    [Fact]
    public async Task A_player_idle_for_thirty_minutes_is_kicked_to_the_lobby_but_pings_keep_no_one_in()
    {
        var store = new FakeCharacterStore();
        const long Start = 1_800_000_000_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Start);
        var ping = new Aion.Protocol.Native.PacketWriter().U16(0x3602).U64(1).ToArray();
        await session.HandleAsync(ping, Start + 25 * 60_000);
        Assert.DoesNotContain(await session.TickAsync(Start + 29 * 60_000), p => GameSessionTestHelpers.Op(p) == GameSession.KickNt);
        Assert.Null(session.CloseReason);
        var kicked = await session.TickAsync(Start + 31 * 60_000);
        Assert.Contains(kicked, p => GameSessionTestHelpers.Op(p) == GameSession.KickNt);
        Assert.NotNull(session.CloseReason);
    }

    [Fact]
    public async Task Activity_resets_the_idle_clock()
    {
        var store = new FakeCharacterStore();
        const long Start = 1_800_000_000_000;
        var session = await GameSessionTestHelpers.EnterWorldAsync(store, Start);
        await session.HandleAsync(new Aion.Protocol.Native.PacketWriter().U16(0x3622).I32(1).ToArray(), Start + 20 * 60_000);
        Assert.DoesNotContain(await session.TickAsync(Start + 40 * 60_000), p => GameSessionTestHelpers.Op(p) == GameSession.KickNt);
        Assert.Null(session.CloseReason);
    }

    [Fact]
    public void Gathering_needs_level_45_and_the_server_cap_resets_at_the_daily_reset()
    {
        Assert.NotNull(Patch20261028.GatherBlock(44));
        Assert.Null(Patch20261028.GatherBlock(45));
        var limiter = new GatherDailyLimiter(cap: 3);
        var day = Ms("2026-10-30T12:00:00Z");
        Assert.True(limiter.TryTake(day));
        Assert.True(limiter.TryTake(day));
        Assert.True(limiter.TryTake(day));
        Assert.False(limiter.TryTake(day));
        Assert.False(limiter.HasRoom(Ms("2026-10-31T06:59:00Z")));
        Assert.True(limiter.HasRoom(Ms("2026-10-31T07:00:00Z")));
        Assert.True(limiter.TryTake(Ms("2026-10-31T07:00:00Z")));
        Assert.Equal(1, limiter.Used(Ms("2026-10-31T08:00:00Z")));
    }

    [Fact]
    public void Character_deletion_waits_seven_days() =>
        Assert.Equal(TimeSpan.FromDays(7), GameSession.DeleteDelay);
}
