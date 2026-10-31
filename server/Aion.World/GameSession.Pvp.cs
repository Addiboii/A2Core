using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    static PvpTables Pvp => PvpTables.Embedded;

    /// <summary>Whether this session may cast hostile skills at another player (duels and arenas are handled separately).</summary>
    bool MayFactionPvp(WorldPlayer victim)
    {
        if (worldPlayer is null || victim.Session.profileRace == profileRace || victim.Session.dead || victim.Hidden) return false;
        if (Pvp.IsAbyssMap(worldMap) || Abyss.IsAbyssMap(worldMap)) return true;
        if (Pvp.IsInvasionMap(worldMap))
            return level >= 45 && victim.Session.level >= 45;
        if (level < Pvp.ProtectBelowLevel || victim.Session.level < Pvp.ProtectBelowLevel) return false;
        if (riftPvpOn == 0 || victim.Session.riftPvpOn == 0) return false;
        return InRiftGarrisonZone(worldMap, worldPlayer.Position) && InRiftGarrisonZone(worldMap, victim.Position);
    }

    static bool InRiftGarrisonZone(int mapId, Vector3d at)
    {
        var prefix = Pvp.RiftSubzonePrefix;
        foreach (var area in Subzones.OnMap(mapId))
            if (area.Name.StartsWith(prefix, StringComparison.Ordinal) && area.Contains(at)) return true;
        return false;
    }

    /// <summary>Abyss-only kill payout; field/invasion faction kills do not grant AP here.</summary>
    List<byte[]> FactionKillRewards(WorldPlayer victim, long now) =>
        OnAbyssMap || Abyss.IsAbyssMap(worldMap) ? AbyssKill(victim, now) : Achieve("KillPC", 1, now: now);
}
