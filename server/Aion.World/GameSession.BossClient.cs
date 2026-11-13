using System.Globalization;
using System.Xml.Linq;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>The CANDIDATE numbers of the client-table boss mechanics (gamedata/provisional/boss-client.xml).</summary>
public sealed class BossClient
{
    public int WarnMinMs { get; private init; } = 1000;
    public double SpotRadius { get; private init; } = 300;
    public int GroggyEnergy { get; private init; } = 100;
    public bool GroggyWire { get; private init; }
    public int BerserkDamagePercent { get; private init; } = 200;
    public long BerserkOffsetMs { get; private init; } = 31_536_000_000;
    public double AddRadius { get; private init; } = 400;
    public int AddsMax { get; private init; } = 8;

    static readonly Lazy<BossClient> embedded = new(() =>
    {
        using var stream = typeof(BossClient).Assembly.GetManifestResourceStream("provisional/boss-client.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/boss-client.xml");
        return Parse(XDocument.Load(stream));
    });

    public static BossClient Embedded => embedded.Value;

    public static BossClient Parse(XDocument document)
    {
        var r = document.Root;
        static long L(XElement? e, string n, long fallback) => long.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        return new BossClient
        {
            WarnMinMs = (int)Math.Max(0, L(r, "warn_min_ms", 1000)), SpotRadius = Math.Max(50, L(r, "spot_radius", 300)),
            GroggyEnergy = (int)Math.Max(1, L(r, "groggy_energy", 100)),
            GroggyWire = string.Equals((string?)r?.Attribute("groggy_wire"), "true", StringComparison.OrdinalIgnoreCase),
            BerserkDamagePercent = (int)Math.Max(100, L(r, "berserk_damage_percent", 200)), BerserkOffsetMs = L(r, "berserk_offset_ms", 31_536_000_000),
            AddRadius = Math.Max(0, L(r, "add_radius", 400)), AddsMax = (int)Math.Max(0, L(r, "adds_max", 8)),
        };
    }
}

// Boss mechanics from the client tables (BossClient). The NPC AI (NpcAi.Telegraph) warns telegraphed skills (0x3848 per spot) and
// queues the adds a boss skill calls; here the session:
//  - places the adds (client Spawn effect: NPC, count, distance round the boss), as real monsters that go for the boss's victim;
//    they vanish (0x3642 flag 3) when their boss dies or gives up (CANDIDATE);
//  - breaks the groggy guard: each player skill (and summon skill) with GroggyGuardDecreaseEnergy takes its amount from the
//    boss's guard (groggy_energy); at 0 the boss is groggy for its NpcData GroggyTime (it stops and drops its cast, as a stun),
//    takes the stagger damage bonus (BossControl.Vulnerable), then its guard is full again. 0x3b04 UpdateGroggyInfo_NT is built from the usmap struct
//    (DevPacketData_Common_GroggyInfo) and sent only with groggy_wire (never seen live: CANDIDATE layout and EGroggyState values).
public sealed partial class GameSession
{
    static BossClient BossRules => BossClient.Embedded;
    public const int AddKeyBase = 0x130000;
    int addKeys;
    /// <summary>Adds alive by boss key.</summary>
    readonly Dictionary<int, List<int>> bossAdds = [];

    /// <summary>A player (or summon) skill's hit on a groggy-guard NPC: the guard drops; broken, the NPC is groggy.</summary>
    List<byte[]> GroggyHit(Target target, int skill, long now)
    {
        var cost = Combats.GroggyEnergy(skill);
        if (target.Dead || cost <= 0 || Combats.Boss(target.NpcId) is not { Groggy: "GroggyGuard" } boss) return [];
        if (now < target.GroggyUntil) return [];
        if (target.GroggyEnergy < 0) target.GroggyEnergy = BossRules.GroggyEnergy;
        target.GroggyEnergy = Math.Max(0, target.GroggyEnergy - cost);
        if (target.GroggyEnergy > 0) return GroggyUpdate(target, 0, now);
        var ms = boss.GroggyMs > 0 ? boss.GroggyMs : 5000;
        target.GroggyUntil = now + ms;
        target.VulnerableUntil = Math.Max(target.VulnerableUntil, target.GroggyUntil);
        Schedule(target.GroggyUntil, later =>
        {
            if (target.Dead || target.GroggyUntil == 0) return [];
            target.GroggyEnergy = BossRules.GroggyEnergy;   // the guard comes back full (CANDIDATE)
            return GroggyUpdate(target, 0, later);
        });
        return GroggyUpdate(target, 1, now);
    }

    /// <summary>0x3b04 when groggy_wire is on (CANDIDATE: EGroggyState 0 guard, 1 groggy).</summary>
    List<byte[]> GroggyUpdate(Target target, byte state, long now)
    {
        if (!BossRules.GroggyWire) return [];
        var packet = GamePackets.UpdateGroggyInfo(target.Key, state, BossRules.GroggyEnergy, Math.Max(0, target.GroggyEnergy), 0,
            Combats.Boss(target.NpcId)?.GroggyMs ?? 0, state == 1 ? target.GroggyUntil : 0, false, 0, state == 1);
        worldPlayer?.Map?.BroadcastAll([packet], worldPlayer);
        return [packet];
    }

    /// <summary>The adds the bosses called since the last tick, placed and sent after their boss's victim; a reset boss's adds go.</summary>
    List<byte[]> BossAddsTick(NpcAi brainOwner, Dictionary<int, Target> map, long now)
    {
        var replies = new List<byte[]>();
        foreach (var boss in brainOwner.TakeResets())
        {
            if (!bossAdds.Remove(boss.Key, out var keys)) continue;
            foreach (var key in keys)
                if (map.Remove(key)) replies.Add(GamePackets.Death(key, 3));
        }
        foreach (var (boss, npc, count, distance, victim) in brainOwner.TakeAdds())
        {
            if (boss.Dead) continue;
            var mine = bossAdds.TryGetValue(boss.Key, out var list) ? list : bossAdds[boss.Key] = [];
            mine.RemoveAll(k => !map.TryGetValue(k, out var a) || a.Dead);
            for (var i = 0; i < count && mine.Count < BossRules.AddsMax; i++)
            {
                var angle = rng.NextDouble() * Math.PI * 2;
                var radius = distance > 0 ? distance : BossRules.AddRadius;
                var at = new Vector3d(boss.Position.X + Math.Cos(angle) * radius, boss.Position.Y + Math.Sin(angle) * radius, boss.Position.Z);
                if (brainOwner.Nav?.FloorAt(at.X, at.Y, at.Z, 500) is { } floor) at = at with { Z = floor };
                var yaw = Math.Atan2(boss.Position.Y - at.Y, boss.Position.X - at.X) * 180 / Math.PI;
                // Only fighting monsters (a hostile template with skills); the invisible effect NPCs some skills spawn are not placed
                // (the Lake Guardian's helpers are boss-mechanics.xml's).
                if (data.NpcFromTemplate(npc, $"Add_{boss.Key}", [at.X, at.Y, at.Z], yaw) is not { Skills.Length: > 0 } spawn
                    || !new Target { Key = 0, Name = "", HpMax = 1, Relationship = spawn.Relationship }.Hostile) continue;
                var key = AddKeyBase + (addKeys = addKeys % 0xFFFF + 1);
                var hp = Combat.HpFor(spawn);
                var add = new Target
                {
                    Key = key, Name = spawn.Name, Hp = hp, HpMax = hp, Position = at, NpcId = npc, Level = spawn.Level, Direction = yaw,
                    Relationship = boss.Relationship, Aggressive = true, WalkSpeed = spawn.WalkSpeed, RunSpeed = spawn.RunSpeed, Radius = spawn.Radius,
                    Function = spawn.Function, Aggro = spawn.Aggro, Skills = spawn.Skills ?? [], NoRotation = spawn.NoRotation, Air = spawn.Air,
                    AirMonster = spawn.Air && spawn.SubType.EndsWith("Monster", StringComparison.Ordinal),
                    DamageScale = Combat.DamageScale(spawn.SubType, spawn.Grade), Rank = NpcTactics.RankOf(spawn.SubType),
                };
                map[key] = add;
                mine.Add(key);
                replies.Add(GamePackets.NpcAppear(add.Appearance()));
                replies.AddRange(brainOwner.Engage(map, add, now, victim));
            }
        }
        return replies;
    }

    internal int AddsForChecks(int boss) => bossAdds.TryGetValue(boss, out var keys) ? keys.Count : 0;
    internal List<byte[]> GroggyHitForChecks(Target target, int skill, long now) => GroggyHit(target, skill, now);
    internal List<byte[]> BossAddsTickForChecks(long now) => targets is null ? [] : BossAddsTick(ai, targets, now);
    internal NpcAi AiForChecks => ai;
}
