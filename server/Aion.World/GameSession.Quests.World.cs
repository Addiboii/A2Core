using Aion.Application.Gameplay;
using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    // Kill/object/area quest handlers (UseEnvObj, EnterVolumePC, fog, Kibelisks).
    // ---------- Quest world (objects, areas, fog, Kibelisks) ----------

    // ---------- World-map fog (the clouds on the M map) ----------

    /// <summary>Uncovered fog groups (stored; per connection on development logins).</summary>
    readonly SortedSet<int> fog = [];
    long fogCheckAt;
    public IReadOnlyCollection<int> Fog => fog;
    public const long FogCheckMs = 500;

    /// <summary>Entering a subzone uncovers its fog group: stored, then 0x56ad. Checked at most every FogCheckMs.</summary>
    async Task<List<byte[]>> UncoverFogAsync(Vector3d at, long now, CancellationToken ct)
    {
        if (resources is null || now < fogCheckAt) return [];
        fogCheckAt = now + FogCheckMs;
        var replies = ObjectVisibility(at, now);
        replies.AddRange(await AreaQuestsAsync(at, ct));
        if (Subzones.GroupAt(worldMap, at) is { } group && !fog.Contains(group)) replies.AddRange(await OpenFogAsync([group], ct));
        replies.AddRange(DismountIfSubzoneForbidden());
        return replies;
    }

    // ---------- Quest objects and trigger areas ----------

    /// <summary>Quest objects this client has been sent (index on the map).</summary>
    readonly HashSet<int> shownObjects = [];
    /// <summary>Trigger actors spawned for this player (index in the map's trigger list).</summary>
    readonly HashSet<int> shownTriggers = [];
    /// <summary>A map event's instance (dungeon, layer): long-range objects and every trigger actor are sent at entry (OBSERVED).</summary>
    bool InstanceMap => instanceKey != 0 && MapEventData.Serves(worldMap);
    /// <summary>
    /// Objects a map event's UseEnvObj objective consumed in this layer: gone for this player (OBSERVED 2026-10-19: each phoenix egg of
    /// HQ002 despawned 100 ms after its use, 0x3635 state 4 reason 3, and never came back; before this they could be used without end).
    /// </summary>
    readonly HashSet<int> usedObjects = [];
    /// <summary>Map-event EnvObjChangePhase per object index on this map layer.</summary>
    readonly Dictionary<int, byte> envObjPhase = [];
    /// <summary>Map-event SetEnvObjCanInteract; when absent the client default is interactable.</summary>
    readonly Dictionary<int, bool> envObjCanInteract = [];
    /// <summary>A timed interaction in progress: object index, key, when it completes.</summary>
    (int Index, int Key, long DueMs)? pendingUse;

    byte[] EnvObjSpawnPacket(int index, WorldObject o, long now)
    {
        var key = WorldObjects.FirstKey + index;
        envObjPhase.TryGetValue(index, out var phase);
        var canInteract = PlayerMayUseEnvObj(index, o);
        return GamePackets.EnvObjSpawn(key, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship,
            state: 1, canInteract: canInteract ? (byte)1 : (byte)0, reason: 1, phase: phase);
    }

    void PushEnvObjSpawn(int index, WorldObject o, long now) => mapEventOut.Add(EnvObjSpawnPacket(index, o, now));

    bool EnvObjInteractAllowed(int index) => !envObjCanInteract.TryGetValue(index, out var ok) || ok;

    /// <summary>Spawns quest objects within WorldObjects.VisibleRange (0x3634) and despawns those left behind (0x3635).</summary>
    List<byte[]> ObjectVisibility(Vector3d at, long now)
    {
        var replies = new List<byte[]>();
        var objects = WorldObjects.ObjectsOn(worldMap);
        // The objects shown (to despawn) and those placed in range (SpatialGrid), in index order.
        var candidates = new SortedSet<int>(shownObjects);
        candidates.UnionWith(SpatialGrid.Objects(worldMap).Near(at.X, at.Y, WorldObjects.VisibleRange));
        var instance = InstanceMap;
        if (instance) candidates.UnionWith(WorldObjects.LongRangeOn(worldMap));
        foreach (var i in candidates)
        {
            if (i >= objects.Count) continue;
            var o = objects[i];
            double dx = o.Position[0] - at.X, dy = o.Position[1] - at.Y;
            var near = (instance && o.LongRange || dx * dx + dy * dy <= WorldObjects.VisibleRange * WorldObjects.VisibleRange) && !MapEventHidden(i, o);
            if (CarryBoxExpired(i, now) && shownObjects.Remove(i))
            {
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
                continue;
            }
            if (near && !Gathered(i, now) && !Broken(i, now) && !CarryBoxGone(i, now) && !usedObjects.Contains(i) && shownObjects.Add(i))
                replies.Add(EnvObjSpawnPacket(i, o, now));
            else if (!near && shownObjects.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + i));
        }
        // Trigger actors: all of an instance map's at once (official), elsewhere within TriggerRange.
        var triggers = WorldObjects.TriggersOn(worldMap);
        for (var i = 0; i < triggers.Count; i++)
        {
            var t = triggers[i];
            if (t.Env == 0) continue;
            double dx = t.Position[0] - at.X, dy = t.Position[1] - at.Y;
            var near = instance || dx * dx + dy * dy <= WorldObjects.TriggerRange * WorldObjects.TriggerRange;
            if (near && shownTriggers.Add(i))
                replies.Add(GamePackets.TriggerSpawn(WorldObjects.FirstTriggerKey + i, t.Env, t.At, t.Direction, now, t.Id));
            else if (!near && shownTriggers.Remove(i))
                replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstTriggerKey + i));
        }
        return replies;
    }

    /// <summary>EnterVolumePC (trigger area, widened by the objective's value) and EnterSubZone objectives of active quests.</summary>
    async Task<List<byte[]>> AreaQuestsAsync(Vector3d at, CancellationToken ct)
    {
        if (questLog is null || !questLog.OpenAreaObjectives().Any()) return [];
        var triggers = WorldObjects.TriggersOn(worldMap);
        bool Inside(QuestObjective o) => o.Target is { } name && (o.Type == "EnterSubZone"
            ? Subzones.InSubzone(worldMap, name, at)
            : triggers.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)
                && t.Contains(at, o.Value ?? 0)));
        var changed = questLog.Entered(Inside, level);
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed, ct));
    }

    /// <summary>CollectItem objectives follow bag counts; called after inventory reloads.</summary>
    async Task<List<byte[]>> SyncQuestCollectItemsAsync(CancellationToken ct)
    {
        if (questLog is null) return [];
        var itemIds = questLog.All.Where(q => q.Status == QuestLog.Incomplete).SelectMany(q =>
            questLog.CurrentStep(q) is { } step
                ? step.Objectives.Take(6).Where((o, i) => o.Type == "CollectItem" && o.Item is { } id && q.Goals[i] < o.Count).Select(o => o.Item!.Value)
                : []).Distinct().ToList();
        if (itemIds.Count == 0) return [];
        var changed = new Dictionary<string, QuestRecord>();
        foreach (var itemId in itemIds)
        {
            var held = inventory.Values.Where(i => i.ItemId == itemId).Sum(i => i.Count);
            foreach (var record in questLog.Holding(itemId, held, level)) changed[record.Code] = record;
        }
        return changed.Count == 0 ? [] : await Stored(() => QuestChangesAsync(changed.Values, ct));
    }

    /// <summary>
    /// Using a quest object (0x3636 with its key): in range, 0x3638 started; after its InteractTime
    /// (EnvObjData; 0 at once) 0x363a completed, 0x8d6d finish state, 0x363b available state, then the 0x3637 reply
    /// (the official order of the player's own interaction, capture 20261013-144836 #10090-10093) and UseEnvObj quest
    /// progress. Without 0x8d6d/0x363b the client kept the interaction (the gather result panel) open.
    /// </summary>
    async Task<List<byte[]>> UseObjectAsync(byte[] plain, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        // Entering a trigger actor's volume (the client reports it): started, completed, finish and available states, the
        // reply (OBSERVED for every subspace trigger). The objectives themselves go by the position reports.
        foreach (var key in EnvObjKeys(plain))
            if (key is { } tk && tk - WorldObjects.FirstTriggerKey is var ti && ti >= 0 && ti < WorldObjects.TriggersOn(worldMap).Count)
                return [GamePackets.InteractEnvObjStarted(tk, playerKey), GamePackets.InteractEnvObjCompleted(tk, playerKey),
                    GamePackets.InteractEnvObjFinishState(tk), GamePackets.InteractEnvObjAvailableState(tk), GamePackets.InteractEnvObjReply(tk)];
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - WorldObjects.FirstKey is var i && i >= 0 && i < objects.Count) { index = i; break; }
        // No such object: refuse it, or the client waits for the reply with the player locked.
        if (index is not { } found || Gathered(found, now) || Broken(found, now) || CarryBoxGone(found, now) || usedObjects.Contains(found))
            return [GamePackets.InteractEnvObjReply(EnvObjKeys(plain).FirstOrDefault(k => k is not null) ?? 0, 1)];
        var o = objects[found];
        var key2 = WorldObjects.FirstKey + found;
        // A refusal carries the available state too, so the client's interaction never stays open on it.
        if (!PlayerMayUseEnvObj(found, o) || CarryRefused(o)) return [GamePackets.InteractEnvObjReply(key2, 1), GamePackets.InteractEnvObjAvailableState(key2)];
        if (o.Gather is not null && GatherFailReason(o) is { } gatherFail) return GatherRefusedPackets(key2, gatherFail);
        if (o.FlightRing) return FlyThroughRing(key2, now);
        if (position is { } at && Math.Sqrt(Math.Pow(at.X - o.Position[0], 2) + Math.Pow(at.Y - o.Position[1], 2)) > o.InteractRange + WorldObjects.InteractSlack)
            return [GamePackets.InteractEnvObjReply(key2, 1)];
        var replies = new List<byte[]> { GamePackets.InteractEnvObjStarted(key2, playerKey) };
        var interactTime = o.InteractTime > 0 ? o.InteractTime : o.Carry is not null ? CarryPickupMs : 0;
        if (interactTime > 0)
        {
            pendingUse = (found, key2, now + interactTime);
            return replies;
        }
        replies.AddRange(await CompleteUseAsync(found, ct, now));
        return replies;
    }

    /// <summary>
    /// Flying through a flight ring (EnvObjData Usage FlightRing; the client reports it as an interaction): the reply alone, the
    /// two abnormals the official sent (18999040 for 3 s, 18999041 for 0.1 s; not in the extracted tables) and the flight points
    /// refilled (OBSERVED capture 20261019-231752 #37632-#37639: 183000 back to 250000 within 2.6 s; ours at once).
    /// </summary>
    List<byte[]> FlyThroughRing(int key, long now)
    {
        var replies = new List<byte[]> { GamePackets.InteractEnvObjReply(key) };
        var at = position ?? Vector3d.Zero;
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal, 3000, now + 3000, CasterKey: playerKey, RangeBase: at)]));
        replies.Add(PlayerPackets.AbnormalAdd(playerKey, [new AbnormalWire(++buffUid, FlightRingAbnormal + 1, 100, now + 100, CasterKey: playerKey, RangeBase: at)]));
        if (resources is { FpMax: > 0 } r && r.Fp < r.FpMax)
        {
            r.Fp = r.FpMax;
            replies.Add(r.FpUpdate());
        }
        return replies;
    }

    public const int FlightRingAbnormal = 18999040;

    async Task<List<byte[]>> CompleteUseAsync(int index, CancellationToken ct, long now = 0)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        if (index >= objects.Count) return [];
        var key = WorldObjects.FirstKey + index;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjCompleted(key, playerKey), GamePackets.InteractEnvObjFinishState(key), GamePackets.InteractEnvObjAvailableState(key),
            GamePackets.InteractEnvObjReply(key),
        };
        replies.AddRange(await ObjectUsedAsync(index, now, ct));
        return replies;
    }

    /// <summary>What using an object does past the interaction packets.</summary>
    async Task<List<byte[]>> ObjectUsedAsync(int index, long now, CancellationToken ct)
    {
        var objects = WorldObjects.ObjectsOn(worldMap);
        var key = WorldObjects.FirstKey + index;
        var replies = new List<byte[]>();
        if (objects[index].Gather is not null) replies.AddRange(await CompleteGatherAsync(index, objects[index], now, ct));
        var destroyed = CarryUsed(index, objects[index], now, replies);
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.UsedObject(objects[index].Env, level), ct)));
        // A carried-item box comes back after its FinishDuration even when a map event counts it (FierceHornCave BombBox_02).
        var consumed = objects[index].Carry is null && (CopyOwner ?? this).mapEventRuns.Any(r => r.WantsObject(EventName(objects[index])));
        replies.AddRange(MapEventUsedObject(EventName(objects[index]), now));
        if (consumed || destroyed)
        {
            usedObjects.Add(index);
            shownObjects.Remove(index);
            replies.Add(GamePackets.EnvObjDespawn(key, 4, 3));
        }
        // A party dungeon's portal (EnvObjData Usage PartyDungeon): into the expedition.
        if (objects[index].Portal is { Length: > 0 } portal)
        {
            var moved = EnterPartyDungeon(portal, now, throughDoor: true);
            if (moved.Count == 0)
                Note($"portal {objects[index].Name} (env {objects[index].Env}) → dungeon [{string.Join(',', portal)}]: entry refused (see prior dungeon notes)");
            replies.AddRange(moved);
        }
        // A layer's exit door (Usage MoveMap: UsageValue names the teleport point outside).
        if (objects[index].MoveTo is { Length: > 0 } point) replies.AddRange(MoveMapDoor(point, now));
        // The expedition's reward cube (Usage OdEnergyCube): the final boss's reward, once per player per run.
        if (objects[index].Cube) replies.AddRange(await CubeRewardAsync(null, now, ct));
        replies.AddRange(Achieve("UseEnvObjSpawnerName", 1, e => e.Has(objects[index].Name), now));
        replies.AddRange(Achieve("UseEnvObj", 1, e => e.Envs?.Contains(objects[index].Env) == true, now));
        return replies;
    }

    async Task<List<byte[]>> OpenFogAsync(IReadOnlyList<int> groups, CancellationToken ct)
    {
        var added = groups.Where(g => !fog.Contains(g)).Distinct().Order().ToList();
        if (Store is { } store && CharacterId is { } character && added.Count > 0)
        {
            var stored = await Stored(async () =>
            {
                var opened = await store.OpenMapFogAsync(character, added, ct);
                return opened.Select(g => BitConverter.GetBytes(g)).ToList();
            });
            added = stored.Select(b => BitConverter.ToInt32(b)).ToList();
        }
        fog.UnionWith(added);
        return [.. added.Select(GamePackets.AddSubzoneGroup), .. added.Count > 0 ? Achieve("EnterSubzone", added.Count) : []];
    }

    /// <summary>GM RevealMap: every fog group uncovered (stored), sent as the full list.</summary>
    public async Task<List<byte[]>> RevealMapAsync(CancellationToken ct)
    {
        if (resources is null) throw new InvalidOperationException("Not in the world.");
        await OpenFogAsync(Subzones.AllGroups, ct);
        return [GamePackets.OpenSubzoneGroups(fog.ToList())];
    }

    // ---------- Kibelisks (teleport artifacts) ----------

    /// <summary>TeleportArtifact ids the character has bound to (stored; per connection on development logins).</summary>
    readonly List<int> engraved = [];
    public IReadOnlyList<int> Engraved => engraved;

    /// <summary>World entry: the engraved list (0x9232) and the map's Kibelisks (0x3634).</summary>
    List<byte[]> KibeliskSpawns(int mapId, long now)
    {
        var replies = new List<byte[]> { GamePackets.EngravedTeleportArtifacts(engraved) };
        var placed = Kibelisks.OnMap(mapId);
        for (var i = 0; i < placed.Count; i++)
        {
            var k = placed[i];
            replies.Add(GamePackets.EnvObjSpawn(Kibelisks.FirstKey + i, k.EnvObj, new Vector3d(k.Position[0], k.Position[1], k.Position[2]), k.Yaw, now, k.UniqueKey, k.Relationship));
        }
        return replies;
    }

    /// <summary>
    /// Candidate object keys in an InteractEnvObj_RQ. The live client (2026-10-15) sends a 01 byte, then
    /// the varint key, i64 0 and the EnvObj id; the earlier guesses (key right after the opcode) are kept after it.
    /// </summary>
    static int?[] EnvObjKeys(byte[] plain) =>
        [plain.Length > 3 && plain[2] == 1 ? QuestKey(plain, 3) : null, QuestKey(plain, 2), plain.Length >= 6 ? BitConverter.ToInt32(plain, 2) : null];

    /// <summary>The raw body of the last InteractEnvObj_RQ (research log).</summary>
    public byte[]? LastEnvObjRequest { get; private set; }

    /// <summary>
    /// Binding to a Kibelisk (official order): 0x3638 started, 0x363a completed, 0x3637 reply, then
    /// 0x9233 when newly bound and the quest progress (EngraveTeleportArtifact). The object is the
    /// one whose key the request carries (varint or i32 after the opcode).
    /// </summary>
    async Task<List<byte[]>> InteractEnvObjAsync(byte[] plain, long now, CancellationToken ct)
    {
        LastEnvObjRequest = plain;
        var placed = Kibelisks.OnMap(worldMap);
        int? index = null;
        foreach (var key in EnvObjKeys(plain))
            if (key is { } k && k - Kibelisks.FirstKey is var i && i >= 0 && i < placed.Count) { index = i; break; }
        if (index is not { } found) return await UseObjectAsync(plain, now, ct);
        var kibelisk = placed[found];
        var objectKey = Kibelisks.FirstKey + found;
        var replies = new List<byte[]>
        {
            GamePackets.InteractEnvObjStarted(objectKey, playerKey), GamePackets.InteractEnvObjCompleted(objectKey, playerKey), GamePackets.InteractEnvObjReply(objectKey),
        };
        var added = !engraved.Contains(kibelisk.ArtifactId);
        if (added && Store is { } store && CharacterId is { } character)
            added = (await Stored(async () => await store.EngraveTeleportArtifactAsync(character, kibelisk.ArtifactId, ct) ? [Array.Empty<byte>()] : [])).Count == 1;
        if (added)
        {
            engraved.Add(kibelisk.ArtifactId);
            replies.Add(GamePackets.EngravedTeleportArtifactAdd(kibelisk.ArtifactId));
            replies.AddRange(Achieve("EngraveTeleportArtifact", 1, e => e.Has(kibelisk.Artifact)));
        }
        if (questLog is not null)
            replies.AddRange(await Stored(() => QuestChangesAsync(questLog.Engraved(kibelisk.Artifact, level), ct)));
        return replies;
    }

    /// <summary>UseTeleportArtifact_RS results (CANDIDATE: the usmap names kTeleportArtifact_Fail_*, not their values).</summary>
    const int ArtifactNotFound = 1, ArtifactNotEngraved = 2, ArtifactCanNotMove = 3;
    /// <summary>How far from its Kibelisk a map's TeleportArtifact_* arrival point stands (Poeta 780 cm, Verteron within 3000).</summary>
    const double ArtifactArrivalRange = 3000;

    /// <summary>
    /// UseTeleportArtifact_RQ 0x9236 (the world map's teleport confirm): an engraved Kibelisk only. The dialog's cost is one
    /// teleport scroll, so one Telpo_Home item (530560001 / 560560002) in the bag is used up; without one the move is free
    /// (INFERRED: the cost table is not exported). Then 0x9237 and the move, across maps through the normal map load.
    /// </summary>
    async Task<List<byte[]>> UseTeleportArtifactAsync(int artifactId, long now, CancellationToken ct)
    {
        List<byte[]> Reply(int result) => [GamePackets.UseTeleportArtifactReply(artifactId, result)];
        if (!Kibelisks.TryPlacement(artifactId, out var mapId, out var at)) return Reply(ArtifactNotFound);
        if (!engraved.Contains(artifactId)) return Reply(ArtifactNotEngraved);
        if (resources is null || dead || reloading) return Reply(ArtifactCanNotMove);
        var replies = Reply(0);
        if (Store is { } store && CharacterId is { } character && TeleportScroll() is { } scroll)
        {
            var used = await Stored(async () => await store.ConsumeItemAsync(character, scroll.InstanceId, scroll.Revision, 1, ct) ? [[]] : []);
            if (used.Count == 0) return Reply(ArtifactCanNotMove);
            replies.AddRange(await InventoryChangesAsync(ct, 0, DecreaseItemUse));
        }
        Note($"UseTeleportArtifact {artifactId}: map {mapId}");
        replies.AddRange(Teleport(mapId, ArtifactArrival(mapId, at), now));
        return replies;
    }

    ItemState? TeleportScroll() =>
        storedItems.Values.Where(s => s.Location == "bag" && s.Quantity > 0 && Boxes.Use(s.TemplateId)?.Effect == "Telpo_Home")
            .OrderBy(s => s.TemplateId).FirstOrDefault();

    /// <summary>The map's TeleportArtifact_* point nearest the Kibelisk (MapData TeleportDataMap), else the Kibelisk itself.</summary>
    Vector3d ArtifactArrival(int mapId, Vector3d kibelisk)
    {
        var best = kibelisk;
        var bestDistance = ArtifactArrivalRange;
        foreach (var (name, p) in data.MapPoints(mapId))
        {
            if (!name.StartsWith("TeleportArtifact", StringComparison.OrdinalIgnoreCase) || p.Length < 3) continue;
            var d = Math.Sqrt(Math.Pow(p[0] - kibelisk.X, 2) + Math.Pow(p[1] - kibelisk.Y, 2) + Math.Pow(p[2] - kibelisk.Z, 2));
            if (d < bestDistance) { bestDistance = d; best = new Vector3d(p[0], p[1], p[2]); }
        }
        return best;
    }
}
