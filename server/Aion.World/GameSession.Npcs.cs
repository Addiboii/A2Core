using Aion.Protocol.Native;

namespace Aion.World;

// NPC visibility. Small maps (Poeta: 275 NPCs) send every NPC at world entry, as before. The levelling maps
// have thousands of spawns (map 1110: 16113), so there NPCs are streamed by distance: 0x3641 when one comes
// within NpcViewRange, 0x3642 flag 1 (the official out-of-view removal) past NpcForgetRange, checked on every
// position report and once a second (NPCs move). Map-wide NPC messages (AI moves, skills, HP, respawns) only
// reach players who see that NPC.
public sealed partial class GameSession
{
    /// <summary>Maps with more NPCs than this stream them by distance.</summary>
    public const int StreamNpcsAbove = 1000;
    public const double NpcViewRange = 8000, NpcForgetRange = 9000;
    readonly HashSet<int> seenNpcs = [];
    long npcViewCheckAt;
    public IReadOnlyCollection<int> SeenNpcs => seenNpcs;

    bool StreamsNpcs => targets is { Count: > StreamNpcsAbove };

    /// <summary>World entry: every NPC on a small map, the near ones on a big map.</summary>
    List<byte[]> NpcsAtEntry(Vector3d at)
    {
        seenNpcs.Clear();
        if (targets is null) return [];
        if (!StreamsNpcs)
        {
            seenNpcs.UnionWith(targets.Keys);
            // A removed corpse stays out of sight until its respawn (0x3641 then).
            return targets.Values.Where(t => !t.CorpseRemoved).Select(t => GamePackets.NpcAppear(Grounded(t).Appearance())).ToList();
        }
        return NpcVisibility(at);
    }

    /// <summary>
    /// Standing NPCs stand on the ground: the official appears sit ~20 below their MapData Z (research/npc-movement.md), the
    /// navmesh floor or the terrain under the spawn says where. Applied once per NPC, when the map's geometry has loaded and
    /// the ground is within 100 of the spawn height (bridges and platforms keep their Z); a route walker is placed by its walk.
    /// </summary>
    Target Grounded(Target t)
    {
        if (t.Snapped || t.Walking || t.Dead) return t;
        ApplyGeometry();
        if (ai.Nav is null && ai.Terrain is null) return t;
        var floor = ai.Nav?.FloorAt(t.Position.X, t.Position.Y, t.Position.Z, 250) ?? ai.Terrain?.GroundAt(t.Position.X, t.Position.Y);
        if (floor is { } z && Math.Abs(z - t.Position.Z) <= 100) t.Position = t.Position with { Z = z };
        t.Snapped = true;
        return t;
    }

    /// <summary>NPCs coming into view appear, those far away are removed (big maps only).</summary>
    List<byte[]> NpcVisibility(Vector3d at)
    {
        if (targets is null || !StreamsNpcs) return [];
        var replies = new List<byte[]>();
        // The NPCs seen (to forget) and those placed near enough to come into view (SpatialGrid), in key order.
        var candidates = new SortedSet<int>(seenNpcs);
        candidates.UnionWith(SpatialGrid.Npcs(targets).Near(at.X, at.Y, NpcViewRange + SpatialGrid.NpcMargin));
        foreach (var key in candidates)
        {
            if (!targets.TryGetValue(key, out var t)) continue;
            var d2 = (t.Position.X - at.X) * (t.Position.X - at.X) + (t.Position.Y - at.Y) * (t.Position.Y - at.Y);
            var seen = seenNpcs.Contains(t.Key);
            if (!seen && !t.CorpseRemoved && d2 <= NpcViewRange * NpcViewRange)
            {
                seenNpcs.Add(t.Key);
                replies.Add(GamePackets.NpcAppear(Grounded(t).Appearance()));
            }
            else if (seen && d2 > NpcForgetRange * NpcForgetRange)
            {
                seenNpcs.Remove(t.Key);
                replies.Add(GamePackets.Death(t.Key, 1));
            }
        }
        return replies;
    }

    /// <summary>The once-a-second visibility check from the tick (NPCs move too).</summary>
    List<byte[]> NpcVisibilityTick(long now)
    {
        if (now < npcViewCheckAt || position is not { } at || !StreamsNpcs) return [];
        npcViewCheckAt = now + 1000;
        return NpcVisibility(at);
    }

    /// <summary>
    /// Whether a map-wide NPC message is for this player: messages whose subject (the first varint after the
    /// opcode: moves, appear/despawn, HP, combat state, skill start) is an NPC it does not see are dropped.
    /// </summary>
    public bool ShouldSee(byte[] packet)
    {
        if (!StreamsNpcs || packet.Length < 3 || targets is null) return true;
        var key = 0;
        for (int i = 2, shift = 0; i < packet.Length && shift < 35; i++, shift += 7)
        {
            key |= (packet[i] & 0x7f) << shift;
            if (packet[i] < 0x80) break;
        }
        return !targets.ContainsKey(key) || seenNpcs.Contains(key);
    }

    /// <summary>The messages of a list this player should get (see ShouldSee).</summary>
    public List<byte[]> Visible(IEnumerable<byte[]> packets) => packets.Where(ShouldSee).ToList();
}
