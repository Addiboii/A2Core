using Aion.Application.Gameplay;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class AirspaceSiegeTests
{
    [Fact]
    public void Siege_schedule_phases_follow_weekly_cycle()
    {
        var siege = new AirspaceSiege();
        var schedule = AbyssTables.Embedded.Siege;
        var wait = schedule.CycleMs - schedule.ApplyMs - schedule.BattleMs - schedule.EndMs;
        var applyAt = schedule.AnchorMs + wait + schedule.ApplyMs / 2;
        Assert.Equal(AirspaceSiege.PhaseApply, siege.PhaseAt(applyAt, 1, out _, out _, out _));
        var battleAt = schedule.AnchorMs + wait + schedule.ApplyMs + schedule.BattleMs / 2;
        Assert.Equal(AirspaceSiege.PhaseBattle, siege.PhaseAt(battleAt, 1, out _, out _, out _));
    }

    [Fact]
    public void Apply_persists_in_snapshot_and_cancel_removes()
    {
        var siege = new AirspaceSiege();
        siege.Load(1204, null);
        var now = AbyssTables.Embedded.Siege.AnchorMs + AbyssTables.Embedded.Siege.CycleMs - AbyssTables.Embedded.Siege.ApplyMs - AbyssTables.Embedded.Siege.BattleMs - AbyssTables.Embedded.Siege.EndMs + 1000;
        Assert.True(siege.RegisterApply(1, 101, 42, now));
        var snap = siege.Snapshot();
        Assert.Contains(snap.Applications, a => a.GuildId == 42 && a.AirspaceId == 101);
        Assert.True(siege.CancelApply(1, 101, 42));
        Assert.DoesNotContain(siege.Snapshot().Applications, a => a.GuildId == 42);
    }

    [Fact]
    public void Battle_end_assigns_owner_and_clears_applications()
    {
        var data = GameData.Embedded;
        var world = new SharedWorld(data);
        var siege = world.Airspace;
        siege.Load(1204, new AirspaceWorldRecord([], [new AirspaceApplyRecord(1, 101, 99)]));
        var schedule = AbyssTables.Embedded.Siege;
        var wait = schedule.CycleMs - schedule.ApplyMs - schedule.BattleMs - schedule.EndMs;
        var battleStart = schedule.AnchorMs + wait + schedule.ApplyMs;
        siege.Tick(world, 1204, battleStart + 1000);
        var endAt = battleStart + schedule.BattleMs + 100;
        var packets = siege.Tick(world, 1204, endAt).ToList();
        Assert.Contains(packets, p => p.Length >= 2 && GameSessionTestHelpers.Op(p) == GameSession.AirspaceBattleProgressResultNt);
        Assert.Equal(99, siege.OwnerOf(101)?.GuildId);
        Assert.Empty(siege.Applicants(1, 101));
    }

    [Fact]
    public void Embedded_abyss_json_includes_siege_schedule()
    {
        var abyss = AbyssTables.Embedded;
        Assert.True(abyss.Siege.CycleMs > abyss.Siege.ApplyMs);
        Assert.True(abyss.SiegePayout.WinnerAp > abyss.SiegePayout.ParticipantAp);
        Assert.True(abyss.Siege.PvpKillPoints > abyss.Siege.PvpHitPoints);
    }

    [Fact]
    public void Char_airspace_point_nt_carries_player_key_and_total()
    {
        var p = AirspaceSiege.BuildCharAirspacePoint(100001, 50);
        Assert.Equal(GameSession.CharAirspacePointNt, GameSessionTestHelpers.Op(p));
    }

    [Fact]
    public void Battle_end_queues_offline_siege_ap_grants()
    {
        var data = GameData.Embedded;
        var world = new SharedWorld(data);
        var siege = world.Airspace;
        siege.Load(1204, new AirspaceWorldRecord([], [new AirspaceApplyRecord(1, 101, 99)]));
        var schedule = AbyssTables.Embedded.Siege;
        var wait = schedule.CycleMs - schedule.ApplyMs - schedule.BattleMs - schedule.EndMs;
        var battleStart = schedule.AnchorMs + wait + schedule.ApplyMs;
        siege.Tick(world, 1204, battleStart + 1000);
        var endAt = battleStart + schedule.BattleMs + 100;
        siege.Tick(world, 1204, endAt);
        var grants = siege.TakePendingApGrants();
        Assert.Contains(grants, g => g.GuildId == 99 && g.Ap == AbyssTables.Embedded.SiegePayout.WinnerAp);
    }
}
