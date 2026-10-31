using Aion.Application.Gameplay;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    /// <summary>Legacy name used by Native.Checks when <see cref="GameSessionOptions.AutoResurrectMsOverride"/> is unset.</summary>
    public const int ReviveDelayMs = 60_000;

    /// <summary>EAppearReasonType on respawn (OBSERVED with 0x3647 / 0x3621 after CharRespawn_RQ).</summary>
    const byte RespawnAppearReason = 4;

    /// <summary>Invulnerability after accepting respawn, before the load completes (OBSERVED 3 s).</summary>
    const int RespawnProtectAbnormal = 10003, RespawnProtectMs = 3000;

    /// <summary>Invulnerability right after the own appearance on respawn load (OBSERVED 5 s).</summary>
    const int RespawnLoadProtectAbnormal = 10002, RespawnLoadProtectMs = 5000;

    bool dead;
    long reviveAt;
    long defaultResurrectAllowedAt;
    long reloadStartedMs;
    int lastKillerKey;
    int lastNpcHitSkill;
    bool pendingRespawnLoadProtect;
    Vector3d? diedAt;
    public bool Dead => dead;
    public ConnectionPacketTrace PacketTrace { get; } = new();

    int ResolveAutoResurrectMs() => options.AutoResurrectMsOverride ?? data.AutoResurrectMs(level);
    public int ResurrectAutoDelayMs => ResolveAutoResurrectMs();

    int NpcDataIdForKey(int entityKey)
    {
        if (targets?.TryGetValue(entityKey, out var t) == true) return t.NpcId;
        return 0;
    }

    /// <summary>The player died: 0x8d04 (DiePvE), combat off, rebirth buff kept; auto-rez from Resurrect.dat.</summary>
    List<byte[]> Die(int killerKey, long now)
    {
        dead = true;
        LogDeath(killerKey);
        diving = false;
        lastKillerKey = killerKey;
        List<byte[]> deathLimit = [];
        if (copy is { } dungeonCopy) { dungeonCopy.Deaths++; deathLimit = NotifyDungeonDeathCount(); }
        reviveAt = now + ResolveAutoResurrectMs();
        defaultResurrectAllowedAt = now + data.DefaultResurrectWaitMs(level);
        diedAt = position;
        inCombat = false;
        var npcDataId = NpcDataIdForKey(killerKey);
        var skill = lastNpcHitSkill;
        lastNpcHitSkill = 0;
        var kill = GamePackets.Kill(playerKey, skill, killerKey, 0, "", dieType: 2, npcCasterDataId: npcDataId,
            npcDieActId: 0, reason: 1);
        worldPlayer?.Map?.Broadcast(worldPlayer, [kill]);
        return [.. deathLimit, .. ForceDismount(), .. ClearBuffs(), kill, GamePackets.CombatState(playerKey, false)];
    }

    /// <summary>0x8d22 CharRespawn_RQ {ERespawnType u8, item uid i64}.</summary>
    async Task<List<byte[]>> RespawnRequestAsync(byte type, long itemUid, long now, CancellationToken ct)
    {
        if (!dead) return [RidePackets.RespawnReply(1)];
        if (now < defaultResurrectAllowedAt && type != RidePackets.RespawnItem)
            return [RidePackets.RespawnReply(1)];
        if (type == RidePackets.RespawnItem)
        {
            if (!TryResolveRebirthItem(itemUid, out var row)) return [RidePackets.RespawnReply(1)];
            if (DungeonRevivesLeft() == 0) return [RidePackets.RespawnReply(1)];
            if (Store is not { } store || CharacterId is not { } character)
                return [RidePackets.RespawnReply(1)];
            var used = await Stored(async () => await store.ConsumeItemAsync(character, row.InstanceId, row.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return [RidePackets.RespawnReply(1)];
            return [RidePackets.RespawnReply(0), .. await InventoryChangesAsync(ct, 0, DecreaseUseRebirthItem), .. Revive(now, inPlace: true)];
        }
        return [RidePackets.RespawnReply(0), .. Revive(now)];
    }

    bool TryResolveRebirthItem(long itemUid, out ItemState row)
    {
        row = default!;
        if (storedItems.TryGetValue(itemUid, out var found) && found.Location == "bag" && IsRebirthItem(found.TemplateId))
        {
            row = found;
            return true;
        }
        return false;
    }

    List<byte[]> UseRebirth(bool use, long now)
    {
        if (!use) return [RidePackets.UseRebirthReply(0, false)];
        if (!dead || !CanRebirth || DungeonRevivesLeft() == 0) return [RidePackets.UseRebirthReply(1, false)];
        var reborn = RidePackets.Reborn(playerKey);
        worldPlayer?.Map?.Broadcast(worldPlayer, [reborn]);
        return [RidePackets.UseRebirthReply(0, true), .. UseRebirthBuff(), reborn, .. Revive(now, inPlace: true)];
    }

    const byte DecreaseUseRebirthItem = 85;

    /// <summary>Revives this player may still take in the dungeon copy (Hard Expeditions: 3 each since Oct 28 2026); -1 without a limit.</summary>
    int DungeonRevivesLeft()
    {
        if (copy is not { } c || MapEventTables.Embedded.Dungeon(c.DungeonId) is not { } room) return -1;
        var limit = Patch20261028.RevivesPerPlayer(room);
        return limit > 0 ? Math.Max(0, limit - c.Revives.GetValueOrDefault(playerKey)) : -1;
    }

    Vector3d RespawnPoint()
    {
        if (DungeonRevivalPoint() is { } dungeon) return dungeon;
        if (data.MapRespawn(worldMap, profileRace) is { } map) return map;
        return data.MapStart(worldMap)?.Position ?? position ?? spawn?.Position ?? GamePackets.SpawnPosition;
    }

    List<byte[]> Revive(long now, bool inPlace = false)
    {
        if (DungeonRevivesLeft() is var left && left >= 0 && copy is { } run)
        {
            if (left == 0) { inPlace = false; instanceExitAt = now; }
            else run.Revives[playerKey] = run.Revives.GetValueOrDefault(playerKey) + 1;
        }
        dead = false;
        LogResurrect(inPlace ? "in_place" : "respawn");
        defaultResurrectAllowedAt = 0;
        reviveAt = 0;
        if (resources is null) return [];
        (resources.Hp, resources.Mp) = (resources.HpMax, resources.MpMax);
        if (inPlace && diedAt is { } fell)
            return [resources.Update(), .. Teleport(worldMap, fell, now)];
        var at = RespawnPoint();
        var replies = new List<byte[]> { PlayerPackets.CharDisappear(playerKey, RespawnAppearReason) };
        replies.AddRange(Teleport(worldMap, at, now, RespawnAppearReason));
        AddBuff(RespawnProtectAbnormal, RespawnProtectMs, 0, now, replies, path: SelfAbnormalPath.DeathProtection);
        pendingRespawnLoadProtect = true;
        replies.Add(resources.Update());
        return replies;
    }

    /// <summary>Timer-driven auto-rez at the respawn point (same replies as an accepted CharRespawn_RQ).</summary>
    List<byte[]> AutoRevive(long now) => [RidePackets.RespawnReply(0), .. Revive(now)];

    List<byte[]> EnsureAliveAfterLoad()
    {
        if (resources is null || dead || resources.Hp > 0) return [];
        resources.Hp = resources.HpMax;
        resources.Mp = resources.MpMax;
        return [resources.Update()];
    }

    List<byte[]> RespawnLoadProtectAfterAppear(long now)
    {
        if (!pendingRespawnLoadProtect) return [];
        pendingRespawnLoadProtect = false;
        var replies = new List<byte[]>();
        AddBuff(RespawnLoadProtectAbnormal, RespawnLoadProtectMs, 0, now, replies, path: SelfAbnormalPath.DeathProtection);
        return replies;
    }

    void NoteReloadStarted(long now) => reloadStartedMs = now;

    void WarnStuckReload(long now)
    {
        if (!reloading || reloadStartedMs == 0 || now < reloadStartedMs + 45_000) return;
        MapEventLog?.Invoke($"Death/reload: still waiting for 0x3622 after {(now - reloadStartedMs) / 1000}s (dead={dead}, map={worldMap})");
        reloadStartedMs = now;
    }
}
