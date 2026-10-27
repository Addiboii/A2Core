using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>Staff monster control from the dashboard's live map. Every member is called with the world lock held exclusively.</summary>
public sealed partial class MapInstance
{
    /// <summary>Keys of monsters staff placed (clear of map NPCs 2000+, players 100000+ and Kibelisks 900000+).</summary>
    public const int FirstStaffKey = 600_000;
    int staffSpawns;

    public sealed record MonsterView(int Key, int NpcId, string Name, int Level, double X, double Y, bool Dead, bool Hostile, bool Staff, bool Engaged);

    /// <summary>NPCs within <paramref name="radius"/> of a point (dead ones too), nearest first, at most <paramref name="limit"/>.</summary>
    public List<MonsterView> Monsters(double x, double y, double radius, int limit)
    {
        var near = new List<(double D, Target T)>();
        foreach (var key in SpatialGrid.Npcs(Targets).Near(x, y, radius + SpatialGrid.NpcMargin))
        {
            if (!Targets.TryGetValue(key, out var t) || t.Key == Combat.TrainingDummyKey || t.CorpseRemoved && t.Key >= FirstStaffKey && !RespawnAt.ContainsKey(t.Key)) continue;
            var d = Math.Sqrt((t.Position.X - x) * (t.Position.X - x) + (t.Position.Y - y) * (t.Position.Y - y));
            if (d <= radius) near.Add((d, t));
        }
        return near.OrderBy(n => n.D).Take(limit).Select(n => new MonsterView(n.T.Key, n.T.NpcId, n.T.Name, n.T.Level, Math.Round(n.T.Position.X),
            Math.Round(n.T.Position.Y), n.T.Dead, n.T.Hostile, n.T.Key >= FirstStaffKey,
            Ai.Brains.TryGetValue(n.T.Key, out var brain) && brain.Engaged)).ToList();
    }

    /// <summary>Every dead NPC respawns at the next tick of a player on the map; the number of them.</summary>
    public int RespawnAll(long now)
    {
        var count = 0;
        foreach (var t in Targets.Values)
            if (t.Dead && (t.Key < FirstStaffKey || RespawnAt.ContainsKey(t.Key)))
            {
                RespawnAt[t.Key] = now;
                count++;
            }
        return count;
    }

    /// <summary>Hostile NPCs within the radius die (no loot or experience) and respawn as usual; the number killed.</summary>
    public int ClearArea(double x, double y, double radius, long now, long respawnMs)
    {
        var packets = new List<byte[]>();
        var count = 0;
        foreach (var key in SpatialGrid.Npcs(Targets).Near(x, y, radius + SpatialGrid.NpcMargin))
        {
            if (!Targets.TryGetValue(key, out var t) || t.Dead || !t.Hostile || t.Key == Combat.TrainingDummyKey) continue;
            if ((t.Position.X - x) * (t.Position.X - x) + (t.Position.Y - y) * (t.Position.Y - y) > radius * radius) continue;
            t.Hp = 0;
            RespawnAt[t.Key] = now + respawnMs;
            packets.Add(GamePackets.NpcHp(t.Key, 0));
            packets.Add(GamePackets.TargetUpdate(t.Key));
            packets.AddRange(Ai.Killed(t));
            count++;
        }
        BroadcastAll(packets);
        return count;
    }

    /// <summary>Copies of a placed NPC (template and name from its MapData spawn) around a point; their keys.</summary>
    public List<int> Spawn(NpcSpawn template, Vector3d at, int count)
    {
        var keys = new List<int>();
        var packets = new List<byte[]>();
        for (var i = 0; i < count; i++)
        {
            var key = FirstStaffKey + ++staffSpawns;
            var angle = i * 2 * Math.PI / Math.Max(1, count);
            var offset = count == 1 ? 0 : 150 + 40 * count;
            var hp = Combat.HpFor(template);
            var target = new Target
            {
                Key = key, Name = template.Name, Hp = hp, HpMax = hp, Position = at with { X = at.X + Math.Cos(angle) * offset, Y = at.Y + Math.Sin(angle) * offset },
                NpcId = template.NpcId, Level = template.Level, Direction = 0, Relationship = template.Relationship, Aggressive = template.Aggressive,
                WalkSpeed = template.WalkSpeed, RunSpeed = template.RunSpeed, Radius = template.Radius, Function = template.Function,
                Aggro = template.Aggro, Skills = template.Skills ?? [], NoRotation = template.NoRotation, Air = template.Air, AirMonster = template.Air && template.SubType.EndsWith("Monster", StringComparison.Ordinal), DamageScale = Combat.DamageScale(template.SubType, template.Grade),
            };
            Targets[key] = target;
            keys.Add(key);
            packets.Add(GamePackets.NpcAppear(target.Appearance()));
        }
        // Small maps show every NPC; streaming maps show the new ones at the next visibility check (within a second).
        BroadcastAll(packets);
        return keys;
    }

    /// <summary>Removes the monsters staff placed (they die and never respawn; targets are never deleted); the number removed.</summary>
    public int RemoveStaffSpawns()
    {
        var packets = new List<byte[]>();
        var count = 0;
        foreach (var t in Targets.Values)
        {
            if (t.Key < FirstStaffKey || t.CorpseRemoved && t.Dead && !RespawnAt.ContainsKey(t.Key)) continue;
            t.Hp = 0;
            t.CorpseRemoved = true;
            RespawnAt.Remove(t.Key);
            packets.AddRange(Ai.Killed(t));
            packets.Add(GamePackets.Death(t.Key));
            count++;
        }
        BroadcastAll(packets);
        return count;
    }
}
