using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class LegionParityTests
{
    [Fact]
    public void Level_thresholds_match_global_table()
    {
        Assert.Equal(1, GuildTables.Embedded.LevelFromExp(0).Level);
        Assert.Equal(2, GuildTables.Embedded.LevelFromExp(1000).Level);
        Assert.Equal(20, GuildTables.Embedded.LevelFromExp(767_500).Level);
        Assert.Equal(767_500, GuildTables.Embedded.LevelFromExp(900_000).NextTotalExp);
    }

    [Fact]
    public async Task Donation_in_fake_store_grants_level_two()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 500_000, 1));
        store.GuildRow = new GuildView(1, "G", 1, 900, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        var costs = new List<(Guid, long, int)> { (store.Items[0].InstanceId, store.Items[0].Revision, 100_000) };
        var (result, remain, levelUp) = await store.DonateGuildAsync(charId, 1, costs, default);
        Assert.Equal("ok", result);
        Assert.Equal(2, levelUp);
        Assert.Equal(2, GuildTables.Embedded.LevelFromExp(store.GuildRow!.Exp).Level);
        Assert.Equal(2, remain);
    }

    [Fact]
    public async Task Agit_contract_extends_rental()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 2_000_000, 1));
        store.GuildRow = new GuildView(1, "G", 1, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        var costs = new List<(Guid, long, int)> { (store.Items[0].InstanceId, store.Items[0].Revision, 1_000_000) };
        var (result, agitId, end) = await store.ContractGuildAgitAsync(charId, 1, costs, 50_000, default);
        Assert.Equal("ok", result);
        Assert.Equal(1, agitId);
        Assert.Equal(50_000 + GuildTables.Embedded.AgitRentMs, end);
    }

    [Fact]
    public void Agit_deco_list_empty_body()
    {
        var body = PacketWriter.Opcode(GuildOpcodes.AgitDecoListNt).Varint(0).ToArray();
        Assert.Equal([0xaf, 0x8a, 0x00], body);
    }

    [Fact]
    public async Task Manual_join_creates_pending_request()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        var outcome = await store.JoinGuildAsync(charId, 9, "", "hi", 128, default);
        Assert.Equal("pending", outcome);
        var sent = await store.GuildJoinRequestsSentAsync(charId, default);
        Assert.Single(sent);
        Assert.Equal(9, sent[0].GuildId);
    }

    [Fact]
    public async Task Agit_deco_grant_required_before_equip()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.Items.Add(FakeCharacterStore.BagItem(534310048, 1, 1));
        store.GuildRow = new GuildView(1, "G", 1, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        Assert.Equal("missing", await store.EquipGuildAgitDecoAsync(charId, 534310048, 1, default));
        var grant = await store.GrantGuildAgitDecoAsync(charId, store.Items[0].InstanceId, store.Items[0].Revision, 534310048, default);
        Assert.Equal("ok", grant);
        Assert.Equal("ok", await store.EquipGuildAgitDecoAsync(charId, 534310048, 1, default));
    }

    [Fact]
    public void Agit_deco_table_maps_achievement_item()
    {
        Assert.True(AgitDecoTables.Embedded.TryDecoId(534310048, out var id));
        Assert.Equal(534310048, id);
    }

    [Fact]
    public async Task Wallet_donate_and_withdraw_with_officer_gate()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.Items.Add(FakeCharacterStore.BagItem(GameSession.KinaItemId, 200_000, 1));
        store.GuildRow = new GuildView(1, "G", 1, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        var costs = new List<(Guid, long, int)> { (store.Items[0].InstanceId, store.Items[0].Revision, 50_000) };
        var donate = await store.DonateGuildWalletAsync(charId, 50_000, costs, default);
        Assert.Equal("ok", donate.Result);
        Assert.Equal(50_000, donate.Wallet.First(w => w.Type == GuildOpcodes.WalletKinaType).Amount);
        var withdraw = await store.WithdrawGuildWalletAsync(charId, 10_000, default);
        Assert.Equal("ok", withdraw.Result);
        Assert.Equal(40_000, withdraw.Wallet.First(w => w.Type == GuildOpcodes.WalletKinaType).Amount);
    }

    [Fact]
    public async Task Union_create_sets_union_on_guild_row()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.GuildRow = new GuildView(1, "G", 1, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 0, "", 1, DateTimeOffset.UnixEpoch)]);
        var (result, id, name) = await store.CreateGuildUnionAsync(charId, "Alliance", 0, "", 7, "hi", default);
        Assert.Equal("ok", result);
        Assert.Equal(42, id);
        Assert.Equal("Alliance", name);
        Assert.Equal(42, store.GuildRow!.UnionId);
    }

    [Fact]
    public async Task Storage_sort_updates_last_sort_ms()
    {
        var store = new FakeCharacterStore();
        var charId = Guid.NewGuid();
        store.GuildRow = new GuildView(1, "G", 1, 0, 0, 0, "", "", "", DateTimeOffset.UnixEpoch,
            [new GuildMember(charId, "Hero", 45, 1, "", 1, DateTimeOffset.UnixEpoch)]);
        var (result, sortMs) = await store.SortGuildStorageAsync(charId, default);
        Assert.Equal("ok", result);
        Assert.True(sortMs > 0);
        Assert.Equal(sortMs, store.GuildRow!.StorageLastSortMs);
    }

    [Fact]
    public void Guild_tables_include_storage_extend_kina()
    {
        Assert.Equal(500_000, GuildTables.Embedded.StorageExtendKina);
        Assert.Equal(24, GuildTables.Embedded.StorageBaseSlots);
    }
}
