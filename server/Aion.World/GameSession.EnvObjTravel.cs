using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>District Spacetime Rift env objects (1100007 Light / 1100006 Dark): into the other faction's field map.</summary>
    List<byte[]> EnterSpacetimeRift(long now)
    {
        if (resources is null || reloading || dead) return [];
        var destMap = profileRace == 2 ? 1010 : 1110;
        var point = profileRace == 2 ? "RiftPortal_D1_to_L1_Teleport_1" : "RiftPortal_L1_to_D1_Teleport_1";
        if (data.MapPoints(destMap).TryGetValue(point, out var p))
            return Teleport(destMap, new Vector3d(p[0], p[1], p[2]), now);
        return data.MapStart(destMap) is { } start ? Teleport(destMap, start.Position, now) : [];
    }

    /// <summary>Quest UseEnvObj credit must be persisted before any self-action teleport (portal, MoveMap, rift).</summary>
    async Task<List<byte[]>> CreditUseEnvObjQuestAsync(int envId, CancellationToken ct)
    {
        if (questLog is null) return [];
        return await Stored(() => QuestChangesAsync(questLog.UsedObject(envId, level), ct));
    }

    /// <summary>EnvObj usages that move the player after the interaction completes.</summary>
    List<byte[]> ApplyEnvObjSelfAction(WorldObject o, long now)
    {
        var replies = new List<byte[]>();
        if (o.Portal is { Length: > 0 } portal)
        {
            var moved = EnterPartyDungeon(portal, now, throughDoor: true);
            if (moved.Count == 0)
                Note($"portal {o.Name} (env {o.Env}) → dungeon [{string.Join(',', portal)}]: entry refused (see prior dungeon notes)");
            replies.AddRange(moved);
        }
        if (o.MoveTo is { Length: > 0 } point) replies.AddRange(MoveMapDoor(point, now));
        if (o.SpacetimeRift) replies.AddRange(EnterSpacetimeRift(now));
        return replies;
    }
}
