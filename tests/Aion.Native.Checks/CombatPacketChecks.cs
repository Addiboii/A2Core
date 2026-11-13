using Aion.Protocol.Native;

/// <summary>
/// Byte-exact checks of the combat builders ported or corrected from the A2Core PR #4 review (2026-10-31) against official server
/// messages of the 2.0.6 recordings (research/local/capture-13700-*/timeline-13328/timeline.jsonl; "#n" is the timeline index).
/// The 2026-10-28 recording with the current client (capture-13700-20261028-121941-96b82b95) holds no combat message, so these
/// layouts rest on the 2.0.6 bytes; where the PR's own capture of the current client disagreed, the PR's form is not adopted.
/// </summary>
public static class CombatPacketChecks
{
    static int passed;

    static double F(uint bits) => BitConverter.UInt32BitsToSingle(bits);
    static Vector3d V(uint x, uint y, uint z) => new(F(x), F(y), F(z));

    static void Same(string name, byte[] built, string officialHex)
    {
        var expected = Convert.FromHexString(officialHex);
        if (!built.AsSpan().SequenceEqual(expected))
            throw new InvalidOperationException($"Combat packet mismatch: {name}\n built {Convert.ToHexString(built)}\n  want {officialHex.ToUpperInvariant()}");
        passed++;
    }

    public static int Run()
    {
        passed = 0;
        // 0x3801: presence 00, result, skill (every official reply; the dodge reply too).
        Same("CastResult (20261013-144836 #876)", GamePackets.CastResult(11020000), "0138000000e026a800");
        Same("DodgeResult (20261019-230917 #1340)", GamePackets.DodgeResult(11000100), "013800000024d9a700");
        // 0x3803 / 0x380e projectiles (NPC casters in the recordings).
        Same("SkillFireProjectile (20261013-144836 #15131)",
            PlayerPackets.SkillFireProjectile(24539, 64034, 1000210, 1, 10002101, V(0xc6b39072, 0xc6c547ed, 0x46978800), 600, 1),
            "0338dbbf0100a2f40312430f0001b5bde2047290b3c6ed47c5c600889746d804010000000100");
        Same("SkillProjectileCollision destroyed (20261013-144836 #15144)",
            PlayerPackets.SkillProjectileCollision(24539, 64034, 1000210, 1, 10002101, V(0xc6a639dd, 0xc6ca6152, 0x46978800), 1, true,
                V(0xc6a5b2fd, 0xc6ca94e3, 0x46978800)),
            "0e38dbbf0102a2f40312430f0001b5bde204dd39a6c65261cac60088974601000000010001fdb2a5c6e394cac600889746");
        Same("SkillProjectileCollision (20261019-231752 #31053)",
            PlayerPackets.SkillProjectileCollision(30114, 9655, 1603820, 1, 16038201, V(0x4604de40, 0xc5f4b0fd, 0x43868000), 1, false,
                V(0x46068355, 0xc5f465f3, 0x43868000)),
            "0e38a2eb0100b74bec78180001b9f2d20740de0446fdb0f4c50080864301000000010055830646f365f4c500808643");
        Same("SkillProjectileCollision target 0 (20261019-231752 #31098)",
            PlayerPackets.SkillProjectileCollision(30114, 0, 1603820, 1, 16038201, V(0x45b913f1, 0xc5fbe0dc, 0x43868000), 1, true,
                V(0x45bc18ba, 0xc5fb9c00, 0x43868000)),
            "0e38a2eb010200ec78180001b9f2d207f113b945dce0fbc50080864301000000010001ba18bc45009cfbc500808643");
        // 0x3804 presence 0x14 (a player's hit with the extra u8).
        Same("ProjectileHit form (20261019-231752 #73349)",
            GamePackets.ProjectileHit(20037, 9655, 11030000, 1103000011, 40, false, 0x54, 1),
            "0438c59c011400b74bf04da8005402cb71be4101000000904e28010100");
        // A second arrow: the flag byte, then the process uid u16 (community recordings 2026-11-04 to 06, 391 hits).
        Same("ProjectileHit seq 2: 01 then process uid 0002",
            GamePackets.ProjectileHit(20037, 9655, 11030000, 1103000011, 40, false, 0x54, 2),
            "0438c59c011400b74bf04da8005402cb71be4102000000904e28010200");
        // 0x3804 switch 04 with the cast's action uid (the official #14400 sample of PlayerPacketChecks, through SkillHit).
        Same("SkillHit with action uid (144836 combat-audit #14400)",
            GamePackets.SkillHit(37476, 5861, 13030000, 1303000011, 21, seq: 1, actionUid: 1),
            "0438e4a4020400e52d70d2c6000102cb33aa4d01000000904e150100");
        // 0xe005 groggy guard.
        Same("UpdateGroggyInfo (20261019-231752 #30737)", GamePackets.UpdateGroggyInfo(30114, 1, 50, 50), "05e0a2eb010301320000003200000002");
        Same("UpdateGroggyInfo broken (20261019-231752 #33475)", GamePackets.UpdateGroggyInfo(30114, 3, null, null), "05e0a2eb01000302");
        // 0x382b: the same abnormal applied again (dodge 3015, same uid 41), show_msg packed into the ready-to-remove byte.
        Same("AbnormalChange (20261019-231752 #1638)",
            PlayerPackets.AbnormalChange(9655, new AbnormalWire(41, 3015, 3000, 0x000001A156077181, CasterKey: 9655, Level: 1, SkillId: 11000100,
                RangeBase: V(0xc69bee16, 0xc6e76745, 0x46962983)), showMsg: true),
            "2b38b74b1329c70b0000b80b00000000000081710756a1010000b74b0124d9a7000216ee9bc64567e7c683299646");
        return passed;
    }
}
