using System.Text.Json;
using Aion.Application.Gameplay;
using Aion.Protocol.Native;
using Aion.World;

/// <summary>
/// Stigmas (GameSession.Stigmas.cs, tools/data/stigmas.json from build 26262663) against the in-memory store: learning and
/// raising for shards, the refusals (another class's stigma, character level, ascension grade, shards), slotting into the
/// stigma quick slots 20-23 (wire numbers, OBSERVED 2026-11-05) by level, the shard item, and a login that keeps saved stigmas the shards no longer cover.
/// Wire forms are CANDIDATE (no recording has a stigma); they reuse the recorded skill-point purchase layout.
/// </summary>
static class StigmaChecks
{
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    const int WrathWave = 11240000, BladeToss = 11080000, FocusedBlock = 11110000, TemplarStigma = 12070000;

    static string Hex(int value) => Convert.ToHexString(BitConverter.GetBytes(value));
    static byte[] LevelUp(int skill) => new PacketWriter().U16(0x383f).I32(skill).ToArray();
    static byte[] SlotSet(int slot, int type, int skill) =>
        new PacketWriter().U16(0x5601).U8(0).U8(slot).U8(type).I64(0).I32(0).I32(skill).U8(0).I32(0).U8(0).ToArray();
    static byte[] Shards(int spent) => new PacketWriter().U16(0x5105).Varint(1).U8(2).U16(spent).ToArray();   // {count, type 2, spent u16}

    /// <summary>
    /// <paramref name="created"/> logs in and creates a Gladiator on the store (not selected yet); the character is then set to
    /// <paramref name="level"/> and <paramref name="grade"/> and enters the world.
    /// </summary>
    public static async Task<int> RunAsync(Func<FakeStore, Task<GameSession>> created, long now)
    {
        var checks = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception("Check failed: " + name);
            checks++;
        }
        async Task<(GameSession Session, FakeStore Store, List<byte[]> Entry)> Enter(int level, int grade, Action<FakeStore>? before = null)
        {
            var store = new FakeStore();
            var session = await created(store);
            var state = store.States[0];
            store.States[0] = state with { Progress = state.Progress with { Level = level }, Ascension = grade };
            before?.Invoke(store);
            await session.HandleAsync(new PacketWriter().U16(0x361f).Varint((uint)store.States[0].WireId << 16).I32(0).Vec3(Vector3d.Zero).ToArray(), now);
            var entry = await session.HandleAsync(new PacketWriter().U16(0x3622).I32(1).ToArray(), now);
            return (session, store, entry);
        }

        var tables = StigmaTables.Embedded;
        Check(tables.SkillsOf("Gladiator").Count == 13 && tables.SkillsOf("Fighter").Count == 13 && tables.ClassOf(WrathWave) == "Gladiator"
              && tables.StepOf("Gladiator", WrathWave, 1) == new StigmaTables.Step(1, 22, 3) && tables.StepOf("Gladiator", WrathWave, 16) == new StigmaTables.Step(8, 22, 0)
              && tables.StepOf("Gladiator", WrathWave, 21) is null && tables.StepOf("Templar", WrathWave, 1) is null,
              "stigmas.json: 13 per class (Fighter too), level 1 needs level 22 and AscensionGrade_3, 8 shards from level 16, max 20");
        Check(tables.Shards(23) == 1 && tables.Shards(30) == 8 && tables.Shards(45) == 29 && tables.OpenSlots(21) == 0 && tables.OpenSlots(30) == 2
              && tables.OpenSlots(45) == 4 && tables.Slots.SequenceEqual([20, 21, 22, 23]) && tables.Spent(WrathWave, 7) == 9,
              "Exp.dat shards (1 at 23, 8 at 30, 29 at 45) and stigma slots (2 open at 30, 4 at 37+); levels 1-7 cost 9 shards");
        Check(GameData.Embedded.Skill(WrathWave) is { CooldownMs: 60000, Damage.Length: > 0 } && GameData.Embedded.Skill(13310000)?.CooldownMs == 60000,
              "stigma cast data from Skill.dat of 26262663 (Wrath Wave 60 s with its Damage row; Assassin 13310000 60 s, 90 s on 2.0.6)");

        // Learn and raise: a level-30, grade-3 Gladiator has 8 shards.
        var (glad, gladStore, _) = await Enter(30, 3);
        Check(glad.StigmaShardsLeft == 8, "level 30: 8 shards (Exp.dat BonusStigmaPoint)");
        var learn = await glad.HandleAsync(LevelUp(WrathWave), now);
        Check(learn.Count == 3 && learn[0].SequenceEqual(Convert.FromHexString("4038" + "0000" + Hex(WrathWave)))
              && learn[1].SequenceEqual(Convert.FromHexString("0F38" + "01" + Hex(WrathWave) + "0101" + "0000000000" + "02")) && learn[2].SequenceEqual(Shards(1))
              && glad.StigmaLevel(WrathWave) == 1 && gladStore.StigmaRows[WrathWave] == 1,
              "learning a stigma: 0x3840 result 0, SkillAdd_NT 0x380f (reason 2, as official #65042-65048), 0x5105 StigmaShard 1 spent; saved at level 1");
        var raise = await glad.HandleAsync(LevelUp(WrathWave), now);
        Check(raise.Count == 3 && raise[0][2] == 0 && Op(raise[1]) == 0x3813 && raise[1][^1] == 3 && raise[2].SequenceEqual(Shards(2))
              && glad.StigmaLevel(WrathWave) == 2 && gladStore.StigmaRows[WrathWave] == 2,
              "raising it: 0x3813 level 2 (reason 3), 2 shards spent; saved at level 2");
        var steps = new List<List<byte[]>>();
        for (var i = 0; i < 4; i++) steps.Add(await glad.HandleAsync(LevelUp(WrathWave), now));   // levels 3-5 (1 each), 6 (2): 1 left
        Check(steps[2][0].SequenceEqual(Convert.FromHexString("0F38" + "01" + Hex(WrathWave + 10) + "0505" + "0000000000" + "06")) && Op(steps[2][1]) == 0x3840
              && Op(steps[3][0]) == 0x3813 && BitConverter.ToInt32(steps[3][0], 3) == WrathWave + 10 && steps[3][0][^1] == 6 && Op(steps[3][1]) == 0x3840,
              "level 5 adds the variant (skill + 10, 0x380f reason 6) before 0x3840; level 6 updates it (0x3813 reason 6), as official #67912");
        Check(glad.StigmaLevel(WrathWave) == 6 && glad.StigmaShardsLeft == 1, "levels 3-6 cost 1, 1, 1, 2 (1 shard left)");
        var broke = await glad.HandleAsync(LevelUp(WrathWave), now);
        Check(broke.Single().SequenceEqual(Convert.FromHexString("4038" + "0200" + Hex(WrathWave))) && glad.StigmaLevel(WrathWave) == 6 && gladStore.StigmaRows[WrathWave] == 6,
              "level 7 costs 2 with 1 shard left: refused (result 2), nothing saved");
        Check((await glad.HandleAsync(LevelUp(TemplarStigma), now)).Single()[2] == 1 && !gladStore.StigmaRows.ContainsKey(TemplarStigma),
              "a Templar stigma on a Gladiator: refused (result 1)");
        var learnSecond = await glad.HandleAsync(LevelUp(BladeToss), now);
        Check(learnSecond[0][2] == 0 && glad.StigmaShardsLeft == 0 && (await glad.HandleAsync(LevelUp(FocusedBlock), now)).Single()[2] == 2,
              "a second stigma takes the last shard; a third is refused for shards (result 2)");
        Check((await glad.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelDownRq).I32(WrathWave).ToArray(), now))[0][2] == 1
              && glad.StigmaLevel(WrathWave) == 6, "lowering a stigma one level is refused (the official window resets instead); nothing changes");

        // Slots: 20 and 21 open at level 30, 22 not; only learned stigmas of the class, one slot each.
        var slotted = await glad.HandleAsync(SlotSet(20, 4, WrathWave), now);
        Check(slotted.Single()[2] == 0 && gladStore.QuickSlotRows[20].SkillId == WrathWave && glad.StigmaReady(WrathWave),
              "QuickSlotSet puts Wrath Wave on stigma slot 20 (0x5602 result 0); it is castable from there");
        Check((await glad.HandleAsync(SlotSet(22, 4, BladeToss), now)).Single()[2] == 1 && !gladStore.QuickSlotRows.ContainsKey(22),
              "slot 22 (the third) opens at level 32: refused at 30");
        Check((await glad.HandleAsync(SlotSet(21, 4, FocusedBlock), now)).Single()[2] == 1, "an unlearned stigma: refused");
        Check((await glad.HandleAsync(SlotSet(21, 4, WrathWave), now)).Single()[2] == 1, "a stigma already in slot 20 cannot take slot 21 too");
        Check((await glad.HandleAsync(SlotSet(3, 0, BladeToss), now)).Single()[2] == 1 && (await glad.HandleAsync(SlotSet(21, 0, 11010000), now)).Single()[2] == 1,
              "stigmas only in stigma slots; stigma slots hold only stigmas");
        Check(!glad.StigmaReady(BladeToss) && (await glad.HandleAsync(SlotSet(21, 0, BladeToss), now)).Single()[2] == 0 && glad.StigmaReady(BladeToss),
              "a learned but unslotted stigma is not castable until it sits in an open stigma slot");

        // The official client's way (QuickSlotContextSkillUpdate 0x5607 -> 0x5608, recordings of 2026-11-05/06).
        static byte[] ContextSkill(int slot, int position, int skill) => new PacketWriter().U16(0x5607).U8(slot).U8(position).I32(skill).ToArray();
        await glad.HandleAsync(new PacketWriter().U16(0x5603).U8(20).ToArray(), now);
        await glad.HandleAsync(new PacketWriter().U16(0x5603).U8(21).ToArray(), now);
        var put = await glad.HandleAsync(ContextSkill(20, 0, WrathWave), now);
        Check(put.Single().SequenceEqual(Convert.FromHexString("0856" + "00" + "0000" + "14" + "00" + Hex(WrathWave)))
              && gladStore.QuickSlotRows[20].SkillId == WrathWave && glad.StigmaReady(WrathWave),
              "0x5607 puts Wrath Wave on stigma slot 20: 0x5608 mask 0, result 0, slot 20, context 0, skill (observed layout); stored, castable");
        await glad.HandleAsync(ContextSkill(21, 0, BladeToss), now);
        var swap = await glad.HandleAsync(ContextSkill(21, 0, WrathWave), now);
        Check(swap.Single().SequenceEqual(Convert.FromHexString("0856" + "03" + "0000" + "15" + "00" + Hex(WrathWave) + "01" + "14"))
              && gladStore.QuickSlotRows[21].SkillId == WrathWave && gladStore.QuickSlotRows[20].SkillId == BladeToss,
              "moving Wrath Wave onto slot 21 trades places with Blade Toss: 0x5608 mask 3, is_swap 1, swap slot 20 (as the official #84812)");
        var cleared = await glad.HandleAsync(ContextSkill(20, 0, 0), now);
        Check(cleared.Single()[4] == 0 && !gladStore.QuickSlotRows.ContainsKey(20) && !glad.StigmaReady(BladeToss),
              "skill 0 clears the slot (official `00 0000 17 00 00000000`)");
        Check((await glad.HandleAsync(ContextSkill(22, 0, BladeToss), now)).Single()[3] == 1 && (await glad.HandleAsync(ContextSkill(3, 0, BladeToss), now)).Single()[3] == 1,
              "0x5607 keeps the stigma rules: the third slot is closed at level 30, a stigma goes only into a stigma slot");
        Check(glad.StigmaReady(WrathWave + 10) && !glad.StigmaReady(WrathWave + 20) && !glad.StigmaReady(BladeToss + 10),
              "the level-6 Wrath Wave casts as its variant (+10) from its slot; another level's variant or an unslotted stigma's is refused");
        var stigmaReset = await glad.HandleAsync(new PacketWriter().U16(GameSession.SkillLevelResetRq).U8(2).I32(WrathWave).ToArray(), now);
        Check(stigmaReset[0].SequenceEqual(Convert.FromHexString("1038" + Hex(WrathWave + 10)))
              && stigmaReset[1].SequenceEqual(Convert.FromHexString("4438" + "0000" + "02" + Hex(WrathWave) + "01" + Hex(WrathWave) + "00" + "01" + "02" + "0700"))
              && Op(stigmaReset[2]) == 0x3813 && stigmaReset[2][12] == 0 && stigmaReset[^1].SequenceEqual(Shards(1))
              && glad.StigmaLevel(WrathWave) == 0 && !gladStore.StigmaRows.ContainsKey(WrathWave) && !glad.StigmaReady(WrathWave + 10),
              "a stigma reset is free (official #64945): its variant deleted, 0x3844 level 0 with 7 shards refunded, 0x3813 level 0, 0x5105");

        // Ascension grade and character level gates (SkillAcquireData NeedAscensionGrade / NeedCharacterLevel).
        var (grade2, grade2Store, _) = await Enter(30, 2);
        Check((await grade2.HandleAsync(LevelUp(WrathWave), now)).Single()[2] == 2 && grade2Store.StigmaRows.Count == 0,
              "learning needs AscensionGrade_3: a grade-2 character is refused (result 2)");
        var (young, _, _) = await Enter(21, 3);
        Check((await young.HandleAsync(LevelUp(WrathWave), now)).Single()[2] == 2, "a level-21 character is refused (need level 22)");

        // Login keeps saved stigmas the shards no longer cover (a level-23 character, 1 shard, with Wrath Wave saved at level 10).
        var (over, overStore, overEntry) = await Enter(23, 3, s => s.StigmaRows[WrathWave] = 10);
        var skillList = overEntry.First(b => Op(b) == 0x5100);
        Check(over.StigmaLevel(WrathWave) == 10 && overStore.StigmaRows[WrathWave] == 10 && over.StigmaShardsLeft < 0
              && skillList.AsSpan().IndexOf(Convert.FromHexString("01" + Hex(WrathWave) + "0A0A")) > 0
              && skillList.AsSpan().IndexOf(Convert.FromHexString("01" + Hex(WrathWave + 20) + "0A0A")) > 0   // its level-10 variant (official 0x5100 #643)
              && overEntry.Any(b => b.SequenceEqual(Shards(15))),
              "world entry lists the saved level-10 stigma (0x5100) and its 15 shards spent; nothing is lowered or forgotten");
        Check((await over.HandleAsync(LevelUp(WrathWave), now)).Single()[2] == 2 && (await over.HandleAsync(LevelUp(BladeToss), now)).Single()[2] == 2
              && overStore.StigmaRows.Count == 1 && overStore.StigmaRows[WrathWave] == 10,
              "over budget: every further level and stigma is refused until the shards cover them");

        // Shard item 531900010 (GetStigmaShard ["1"]): used up, one shard more.
        var (charged, chargedStore, _) = await Enter(23, 3, s => s.Items.Add(new ItemState(Guid.NewGuid(), "b", 531900010, 2, "bag", null, 1, Empty, Empty, 7001)));
        await charged.InventoryChangesAsync(default);   // the bag as the store has it (as the item-use checks do)
        var used = await charged.HandleAsync(new PacketWriter().U16(0x5612).I64(7001).ToArray(), now);
        Check(BitConverter.ToUInt16(used[0], 2) == RidePackets.ItemUseSuccess && used.Any(b => b.SequenceEqual(Shards(0))) && chargedStore.StigmaItemShards == 1 && charged.StigmaShardsLeft == 2
              && chargedStore.Items.Single(i => i.TemplateId == 531900010).Quantity == 1,
              "a stigma shard item: one used, +1 shard (0x5105 StigmaShard, none spent), stored with the item's consumption");
        return checks;
    }

    static ushort Op(byte[] packet) => (ushort)(packet[0] | packet[1] << 8);
}

sealed partial class FakeStore
{
    // Stigmas (migration 072) in memory, with the Postgres store's compare-and-set on the previous level.
    public Dictionary<int, int> StigmaRows { get; } = [];
    public int StigmaItemShards { get; set; }
    public Task<StigmaState> StigmasAsync(Guid character, CancellationToken ct) =>
        Task.FromResult(new StigmaState(StigmaRows.Select(kv => new StigmaRecord(kv.Key, kv.Value)).ToList(), StigmaItemShards));
    public Task<bool> SetStigmaLevelAsync(Guid character, int skillId, int level, int expected, CancellationToken ct)
    {
        if (StigmaRows.GetValueOrDefault(skillId) != expected || level < 0 || level == 0 && expected == 0) return Task.FromResult(false);
        if (level == 0) StigmaRows.Remove(skillId);
        else StigmaRows[skillId] = level;
        return Task.FromResult(true);
    }
    public Task<bool> UseStigmaShardItemAsync(Guid character, Guid item, long revision, int shards, CancellationToken ct)
    {
        if (shards < 1 || !Consume(item, revision, 1)) return Task.FromResult(false);
        StigmaItemShards += shards;
        return Task.FromResult(true);
    }
}
