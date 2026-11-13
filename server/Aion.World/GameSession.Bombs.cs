using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>The bomb boxes and the bomb skill (gamedata/bomb-boxes.xml: OBSERVED values, the provisional ones named in the file).</summary>
public sealed record BombRule(int BoxEnv, int TowerEnv, int Abnormal, int DurationMs, int Skill, int Charges, int BoxCooldownMs,
    int AimAbnormal, int Projectile, int DamageEffect, int SecondEffect, int StunAbnormal, int StunMs, int TowerHits, int AttackSpeed,
    int EndMs, int FireMs, double BlastRadius, int Damage, double FlightCmPerMs)
{
    static readonly Lazy<BombRule> embedded = new(() =>
    {
        using var stream = typeof(BombRule).Assembly.GetManifestResourceStream("bomb-boxes.xml")
            ?? throw new InvalidOperationException("Missing embedded game data bomb-boxes.xml");
        return Parse(XDocument.Load(stream));
    });

    public static BombRule Embedded => embedded.Value;

    public static BombRule Parse(XDocument document)
    {
        var e = document.Root ?? throw new InvalidDataException("bomb-boxes.xml: empty");
        int I(string name) => int.Parse((string?)e.Attribute(name) ?? throw new InvalidDataException($"bomb-boxes.xml: no {name}"), CultureInfo.InvariantCulture);
        double D(string name) => double.Parse((string?)e.Attribute(name) ?? throw new InvalidDataException($"bomb-boxes.xml: no {name}"), CultureInfo.InvariantCulture);
        var rule = new BombRule(I("box_env"), I("tower_env"), I("abnormal"), I("duration_ms"), I("skill"), I("charges"), I("box_cooldown_ms"),
            I("aim_abnormal"), I("projectile"), I("damage_effect"), I("second_effect"), I("stun_abnormal"), I("stun_ms"), I("tower_hits"),
            I("attack_speed"), I("end_ms"), I("fire_ms"), D("blast_radius"), I("damage"), D("flight_cm_per_ms"));
        if (rule.Charges < 1 || rule.TowerHits < 1 || rule.BlastRadius <= 0 || rule.FlightCmPerMs <= 0)
            throw new InvalidDataException("bomb-boxes.xml: charges, tower_hits, blast_radius and flight_cm_per_ms must be positive");
        return rule;
    }
}

// Bomb boxes and the bomb skill, as the official server did them (capture-13700-20261107-014953, Brigade Base Neutralization):
//  - box (InteractEnvObj): started, completed, finish state, reply; AbnormalAdd 160010 (1 h); SkillAutoLoadUpdate 0x3822
//    {01, skill 5001, charges 3}; the box stays and is usable again 10 s later (0x363b).
//  - aim: SkillGroundReady_RQ -> 0x384c SkillGroundReady_NT {key, skill, 01}, 0x384b SkillGroundReady_RS {result u16, skill},
//    AbnormalAdd 160012 (no end); SkillGroundStop_RQ -> 0x3851 SkillGroundStop_NT {key, result u16, skill}, the aim abnormal off.
//  - throw: SkillGroundStart_RQ -> 0x3802 SkillStart (mask 4 + the charges left while any are left), 0x384e SkillGroundStart_RS
//    {result u16, skill}, the aim abnormal off (reason 3), on the last charge 160010 off (reason 16); 450 ms later 0x3803
//    SkillFireProjectile, 600 ms after the start 0x3806; when the bomb lands the hits (see bomb-boxes.xml).
// The opcodes are the catalogue's skill block at the live offset +0x157 (0x384b-0x3851, 0x3822 named so by their fields).
public sealed partial class GameSession
{
    public const ushort SkillGroundReadyRq = 0x384a, SkillGroundReadyRs = 0x384b, SkillGroundReadyNt = 0x384c,
        SkillGroundStartRq = 0x384d, SkillGroundStartRs = 0x384e, SkillGroundStopRq = 0x384f, SkillGroundStopNt = 0x3851,
        SkillAutoLoadUpdateNt = 0x3822, SkillFireProjectileNt = 0x3803, UpdateEnvObjHitCountNt = 0x8d93, CharPhaseUpdateNt = 0x4000;

    const byte ExpireSkillUsed = 3, ExpireChargesUsed = 16;

    static BombRule Bombs => BombRule.Embedded;

    /// <summary>Throws left from the last box; 0 without a bomb.</summary>
    int bombCharges;
    /// <summary>Box indexes waiting for their cooldown; tower hit counts by object index (this run).</summary>
    readonly HashSet<int> coolingBoxes = [];
    readonly Dictionary<int, int> towerHits = [];
    byte bombAction;

    bool IsBombBox(WorldObject o) => o.Env == Bombs.BoxEnv;
    bool IsBombTower(WorldObject o) => o.Env == Bombs.TowerEnv;

    /// <summary>0x3822 SkillAutoLoadUpdate_NT: 01, skill, charges (OBSERVED "2238 01 89130000 03").</summary>
    public static byte[] SkillAutoLoad(int skill, int charges) => PacketWriter.Opcode(SkillAutoLoadUpdateNt).U8(1).I32(skill).U8((byte)charges).ToArray();

    /// <summary>A bomb box used: the bomb (abnormal and the skill's charges), the box back 10 s later.</summary>
    List<byte[]> TakeBomb(int index, int key, long now)
    {
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjStarted(key, playerKey), GamePackets.InteractEnvObjCompleted(key, playerKey),
            GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjReply(key),
        };
        replies.AddRange(RemoveScriptedBuffs(Bombs.AimAbnormal));
        replies.AddRange(AddScriptedBuff(Bombs.Abnormal, Bombs.DurationMs, now));
        bombCharges = Bombs.Charges;
        replies.Add(SkillAutoLoad(Bombs.Skill, bombCharges));
        coolingBoxes.Add(index);
        Schedule(now + Bombs.BoxCooldownMs, _ =>
        {
            coolingBoxes.Remove(index);
            return [GamePackets.InteractEnvObjAvailableState(key)];
        });
        return replies;
    }

    /// <summary>0x384a SkillGroundReady_RQ {skill i32}: aiming the bomb.</summary>
    List<byte[]> GroundReady(byte[] plain, long now)
    {
        var skill = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        if (skill != Bombs.Skill || bombCharges <= 0 || resources is null || dead)
        {
            Note($"SkillGroundReady refused: skill {skill}, {bombCharges} bomb charges ({Convert.ToHexString(plain)})");
            return [PacketWriter.Opcode(SkillGroundReadyRs).U16(EResult.Skill_Failed).I32(skill).ToArray()];
        }
        return [PacketWriter.Opcode(SkillGroundReadyNt).Varint(playerKey).I32(skill).U8(1).ToArray(),
            PacketWriter.Opcode(SkillGroundReadyRs).U16(0).I32(skill).ToArray(), .. AddScriptedBuff(Bombs.AimAbnormal, -1, now)];
    }

    /// <summary>0x384f SkillGroundStop_RQ: aiming cancelled.</summary>
    List<byte[]> GroundStop() =>
        [PacketWriter.Opcode(SkillGroundStopNt).Varint(playerKey).U16(0).I32(Bombs.Skill).ToArray(), .. RemoveBuffs(buffs.Where(b => b.AbnormalId == Bombs.AimAbnormal).ToList(), ExpireSkillUsed)];

    /// <summary>
    /// 0x384d SkillGroundStart_RQ {skill i32, SkillTargetInfo}: the throw at the aimed spot. The target info's location is read as
    /// the last Vec3 of the body (CANDIDATE: the client's request is not in the official recordings, they are encrypted).
    /// </summary>
    List<byte[]> GroundStart(byte[] plain, long now)
    {
        var skill = plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : 0;
        Note($"SkillGroundStart {Convert.ToHexString(plain)}");
        Vector3d? spot = null;
        if (plain.Length >= 18)
        {
            float x = BitConverter.ToSingle(plain, plain.Length - 12), y = BitConverter.ToSingle(plain, plain.Length - 8), z = BitConverter.ToSingle(plain, plain.Length - 4);
            if (float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && Math.Abs(x) < 1e6 && Math.Abs(y) < 1e6 && Math.Abs(z) < 1e6) spot = new Vector3d(x, y, z);
        }
        var yaw = plain.Length >= 22 ? BitConverter.ToSingle(plain, plain.Length - 16) : 0f;
        return ThrowBomb(skill, spot ?? position ?? Vector3d.Zero, float.IsFinite(yaw) ? yaw : 0, now, ground: true);
    }

    /// <summary>The bomb skill sent as a normal cast (0x3800): thrown at the cast's location. Null when it is no bomb.</summary>
    List<byte[]>? ThrowBombCast(byte[] plain, long now) =>
        bombCharges > 0 && ParseCast(plain) is { } cast && cast.SkillId == Bombs.Skill ? ThrowBomb(cast.SkillId, cast.Location, cast.Yaw, now, ground: false) : null;

    List<byte[]> ThrowBomb(int skill, Vector3d spot, double yaw, long now, bool ground)
    {
        if (skill != Bombs.Skill || bombCharges <= 0 || resources is null || dead)
            return [ground ? PacketWriter.Opcode(SkillGroundStartRs).U16(EResult.Skill_Failed).I32(skill).ToArray() : GamePackets.CastResult(skill, EResult.Skill_Failed)];
        bombCharges--;
        var uid = ++bombAction;
        var start = PacketWriter.Opcode(0x3802).Varint(playerKey).U8(bombCharges > 0 ? 4 : 0).I32(skill).U8(uid).U8(2).Varint(playerKey)
            .F32((float)yaw).Vec3(spot).Varint(Bombs.AttackSpeed).U8(1).Varint(0);
        if (bombCharges > 0) start.U8((byte)bombCharges);
        var replies = new List<byte[]> { start.ToArray(), ground ? PacketWriter.Opcode(SkillGroundStartRs).U16(0).I32(skill).ToArray() : GamePackets.CastResult(skill) };
        replies.AddRange(RemoveBuffs(buffs.Where(b => b.AbnormalId == Bombs.AimAbnormal).ToList(), ExpireSkillUsed));
        if (bombCharges == 0) replies.AddRange(RemoveBuffs(buffs.Where(b => b.AbnormalId == Bombs.Abnormal).ToList(), ExpireChargesUsed));
        worldPlayer?.Map?.Broadcast(worldPlayer, [replies[0]]);
        var from = position ?? spot;
        var flight = (int)Math.Clamp(Math.Sqrt(Math.Pow(spot.X - from.X, 2) + Math.Pow(spot.Y - from.Y, 2)) / Bombs.FlightCmPerMs, 200, 1000);
        var fireAt = now + Bombs.FireMs;
        Schedule(fireAt, _ => [PacketWriter.Opcode(SkillFireProjectileNt).Varint(playerKey).U8(0).Varint(playerKey).I32(skill).U8(uid)
            .Varint(Bombs.Projectile).Vec3(spot).Varint(flight).U8(1).U8(0).U8(0).U8(0).U8(1).U8(0).ToArray()]);
        Schedule(now + Bombs.EndMs, _ => [GamePackets.SkillEnd(playerKey, skill, uid)]);
        Schedule(fireAt + flight, (at, ct) => BombLandsAsync(skill, uid, spot, at, ct));
        return replies;
    }

    /// <summary>A no-damage 0x3804 (switch 00) with the bomb's attack speed (OBSERVED "0438 key 00 00 caster skill uid 02 effect 01000000 f855 01 00").</summary>
    byte[] BombEffect(int target, int skill, byte uid, int effect) =>
        PacketWriter.Opcode(0x3804).Varint(target).U8(0).U8(0).Varint(playerKey).I32(skill).U8(uid).U8(2).U32((uint)effect).U32(1)
            .Varint(Bombs.AttackSpeed).U8(1).U8(0).ToArray();

    async Task<List<byte[]>> BombLandsAsync(int skill, byte uid, Vector3d spot, long now, CancellationToken ct)
    {
        var replies = new List<byte[]>();
        var r2 = Bombs.BlastRadius * Bombs.BlastRadius;
        foreach (var target in (targets?.Values ?? Enumerable.Empty<Target>()).Where(t => t.Attackable && !t.Dead && t.Position.DistanceSquared(spot) <= r2).ToList())
        {
            var amount = target.Immune ? 0 : Bombs.Damage;
            target.Hp = Math.Max(0, target.Hp - amount);
            replies.Add(PlayerPackets.SkillEffect(target.Key, playerKey, skill, uid, Bombs.DamageEffect, 1, amount, Bombs.AttackSpeed));
            replies.Add(BombEffect(target.Key, skill, uid, Bombs.SecondEffect));
            if (!target.Dead)
            {
                mapEventNow = now;
                AddNpcBuff(target, Bombs.StunAbnormal, Bombs.StunMs);
                replies.AddRange(mapEventOut);
                mapEventOut.Clear();
            }
            replies.AddRange(AfterNpcDamage(target, skill, amount, now));
        }
        var objects = WorldObjects.ObjectsOn(worldMap);
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (!IsBombTower(o) || usedObjects.Contains(i) || Math.Pow(o.Position[0] - spot.X, 2) + Math.Pow(o.Position[1] - spot.Y, 2) > r2) continue;
            var key = WorldObjects.FirstKey + i;
            var hits = towerHits[i] = towerHits.GetValueOrDefault(i) + 1;
            replies.Add(BombEffect(key, skill, uid, Bombs.DamageEffect));
            replies.Add(BombEffect(key, skill, uid, Bombs.SecondEffect));
            replies.Add(PacketWriter.Opcode(UpdateEnvObjHitCountNt).Varint(key).I32(hits).ToArray());
            if (hits < Bombs.TowerHits)
            {
                replies.Add(PacketWriter.Opcode(CharPhaseUpdateNt).Varint(key).U8((byte)hits).ToArray());
                continue;
            }
            // Destroyed: its finish state and the uses it counts for (map event UseEnvObj, quest, achievements).
            usedObjects.Add(i);
            replies.Add(GamePackets.InteractEnvObjFinishState(key));
            replies.AddRange(MapEventDestroyed(MapEventObjectName(o), now));
            if (questLog is not null) replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(o.Env, level), ct)));
            replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(o.Name), now));
            replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(o.Env) == true, now));
        }
        if (killedNpcs.Count > 0 || killedLevels.Count > 0 || clearedEvents.Count > 0) killWorkDue = true;
        return replies;
    }

    void ResetBombs()
    {
        bombCharges = 0;
        coolingBoxes.Clear();
        towerHits.Clear();
    }
}
