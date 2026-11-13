using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// What a bot's brain may read of its own session - the same things a game client sees on screen (its skills and their cooldowns,
/// its HP/MP, the monsters around it with their HP bars and who they are fighting, the loot on the ground). Bots never change state
/// through this: every action is a normal client request through <see cref="HandleAsync"/>, so all game rules apply to them.
/// Read only while the bot holds its map turn (the monsters move under the map lock).
/// </summary>
public sealed partial class GameSession
{
    /// <summary>A monster as a bot sees it.</summary>
    public sealed record BotSight(int Key, int NpcId, string Name, int Level, Vector3d Position, int Hp, int HpMax, bool Aggressive, NpcRank Rank,
        bool Engaged, int VictimKey, bool VictimIsNpc, int Radius, bool Air, double Direction, int AggroRange, bool HitGauge = false);

    /// <summary>Where it stands: the last reported position, else the entry spot (a client reports its first move only when it moves).</summary>
    public Vector3d? BotPosition => position ?? spawn?.Position;
    public IReadOnlyCollection<int> LearnedSkills => learned;
    public bool InCombat => inCombat;
    public SkillDefinition? SkillInfo(int skill) => SkillData(skill);
    public bool SkillReady(int skill, long now) => noCool || now >= cooldowns.GetValueOrDefault(CooldownKeyOf(skill));
    /// <summary>A chain follow-up the server offers right now (the client lights it up on the bar).</summary>
    public bool ChainReady(int skill, long now) => ChainOffered(skill, now);
    public IReadOnlyList<(int Slot, int SkillId)> ClassBar => classBar;
    /// <summary>The chain follow-ups lit up right now.</summary>
    public List<int> OfferedChains(long now) => offeredChains.Where(kv => now <= kv.Value).Select(kv => kv.Key).ToList();
    /// <summary>A map load is waiting for the client's "loaded" (0x3622): after a revive or any teleport.</summary>
    public bool Reloading => reloading;
    public int BotDodgeSkill => ClassDodge;
    public IEnumerable<(long Uid, Vector3d At, int ItemId)> BotDrops => drops.Values.Select(d => (d.Uid, d.At, d.ItemId));

    /// <summary>Monsters within <paramref name="radius"/> (living, attackable), nearest first. Hit-gauge objects (totems) are listed
    /// with HitGauge set: bots attack them only when a quest or map event names them.</summary>
    public List<BotSight> BotScan(double radius)
    {
        if (SharedMap is not { } map || BotPosition is not { } at) return [];
        var near = new List<(double D, BotSight S)>();
        foreach (var key in SpatialGrid.Npcs(map.Targets).Near(at.X, at.Y, radius + SpatialGrid.NpcMargin))
        {
            if (!map.Targets.TryGetValue(key, out var t) || t.Dead || !t.Attackable || t.Immune || t.Key == Combat.TrainingDummyKey) continue;
            double dx = t.Position.X - at.X, dy = t.Position.Y - at.Y, d = Math.Sqrt(dx * dx + dy * dy);
            if (d > radius) continue;
            map.Ai.Brains.TryGetValue(t.Key, out var brain);
            near.Add((d, new BotSight(t.Key, t.NpcId, t.Name, t.Level, t.Position, t.Hp, t.HpMax, t.Aggressive, t.Rank, brain?.Engaged == true,
                brain?.VictimKey ?? 0, brain?.VictimIsNpc == true, t.Radius, t.Air, t.Direction, (int)(t.Aggro?.Radius ?? 0), t.HitGauge)));
        }
        return near.OrderBy(n => n.D).Select(n => n.S).ToList();
    }

    /// <summary>An item as the bag window shows it.</summary>
    public sealed record BotItem(long Key, int TemplateId, string Location, int? Slot, long Quantity);

    public List<BotItem> BotItems() => storedItems.Select(kv => new BotItem(kv.Key, kv.Value.TemplateId, kv.Value.Location, kv.Value.Slot, kv.Value.Quantity)).ToList();
    public ItemInfo? BotItemInfo(int templateId) => data.Item(templateId);
    /// <summary>The held weapon's category (Sword, Bow...): the class's weapon.</summary>
    public string? BotWeaponCategory => data.Item(equippedWeapon)?.Category;
    public int BotRace => profileRace;
    public long BotKina => storedItems.Values.Where(i => i.TemplateId == KinaItemId && i.Location == "bag").Sum(i => i.Quantity);
    public bool BotPotionsSet => potionSlots.Any(p => p != 0);

    /// <summary>Merchants on the map within <paramref name="radius"/>, nearest first.</summary>
    public List<(int Key, int NpcId, Vector3d At)> BotMerchants(double radius)
    {
        if (SharedMap is not { } map || BotPosition is not { } at) return [];
        return map.Targets.Values.Where(t => !t.Hostile && !t.Dead && ShopTables.Embedded.IsMerchant(t.NpcId) && t.Position.DistanceSquared(at) <= radius * radius)
            .OrderBy(t => t.Position.DistanceSquared(at)).Select(t => (t.Key, t.NpcId, t.Position)).Take(5).ToList();
    }

    public bool InInstance => instanceKey != 0;

    /// <summary>Living NPCs (friendly and hostile) within <paramref name="radius"/>: quest givers, talk partners, quest monsters.</summary>
    public List<(int Key, int NpcId, Vector3d At, bool Hostile, string Name)> BotNpcs(double radius)
    {
        if (SharedMap is not { } map || BotPosition is not { } at) return [];
        return map.Targets.Values.Where(t => !t.Dead && t.Key != Combat.TrainingDummyKey && t.Position.DistanceSquared(at) <= radius * radius)
            .Select(t => (t.Key, t.NpcId, t.Position, t.Hostile, t.Name)).ToList();
    }

    /// <summary>The map event runs of the copy it stands in (its own, or the party leader's when it is in their copy): the event tracker.</summary>
    public IReadOnlyList<MapEventRun> BotMapEventRuns => (CopyOwner ?? this).mapEventRuns;

    /// <summary>Objects on the map the run's open UseEnvObj objectives want (key, place, reach, name), as the quest marker shows them.</summary>
    public List<(int Key, Vector3d At, double Reach, string Name)> BotWantedObjects(MapEventRun run)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var runs = BotMapEventRuns;
        var found = new List<(int, Vector3d, double, string)>();
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (usedObjects.Contains(i) || MapEventHidden(i, o)) continue;
            var name = MapEventObjectName(o, runs);
            if (run.WantsObject(name)) found.Add((WorldObjects.FirstKey + i, o.At, Math.Max(150, o.InteractRange), name));
        }
        return found;
    }

    /// <summary>The trigger areas with this name on the map (key, centre): the client reports walking into one with 0x3636 and its key.</summary>
    public List<(int Key, Vector3d At)> BotTriggers(string name)
    {
        var triggers = WorldObjects.TriggersOn(worldMap);
        var found = new List<(int, Vector3d)>();
        for (var i = 0; i < triggers.Count; i++)
            if (string.Equals(triggers[i].Name, name, StringComparison.OrdinalIgnoreCase)) found.Add((WorldObjects.FirstTriggerKey + i, triggers[i].At));
        return found;
    }

    /// <summary>Inside the named trigger area now (the map event's EnterVolumePC check).</summary>
    public bool BotInsideTrigger(string name) =>
        BotPosition is { } at && WorldObjects.TriggersOn(worldMap).Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase) && t.Contains(at));

    /// <summary>Gather nodes within <paramref name="radius"/> it can collect now (its gather mastery, not used up): key, place, reach.</summary>
    public List<(int Key, Vector3d At, double Reach)> BotGatherNodes(double radius)
    {
        if (BotPosition is not { } at) return [];
        var objects = WorldObjects.ObjectsOn(worldMap);
        var found = new List<(int, Vector3d, double)>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 0; i < objects.Count; i++)
        {
            var o = objects[i];
            if (o.Gather is not { Length: > 0 } gather || o.Position.Length < 3 || usedObjects.Contains(i) || MapEventHidden(i, o)) continue;
            if (gathered.TryGetValue(i, out var back) && back > now) continue;
            if (gather.Length > 4 && gather[4] > gatherLevel) continue;
            if (o.At.DistanceSquared(at) > radius * radius) continue;
            found.Add((WorldObjects.FirstKey + i, o.At, Math.Max(150, o.InteractRange)));
        }
        return found;
    }

    /// <summary>The quest's rewards were paid (status Rewarded, or the record moved on to its next quest).</summary>
    public bool BotQuestRewarded(string code) => questLog?.Get(code) is not { } q || q.Status >= QuestLog.Rewarded;

    /// <summary>Other players near (bots too): to spread out instead of crowding one spot.</summary>
    public List<Vector3d> BotPlayersNear(double radius)
    {
        if (SharedMap is not { } map || BotPosition is not { } at) return [];
        return map.Players.Values.Where(p => p.Key != playerKey && p.Position.DistanceSquared(at) <= radius * radius).Select(p => p.Position).ToList();
    }
}
