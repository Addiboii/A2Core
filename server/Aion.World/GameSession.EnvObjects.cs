using Aion.Protocol.Native;

namespace Aion.World;

// Map-event object directives (ported from A2Core PR #4, Artezianec): EnvObjChangePhase and SetEnvObjCanInteract. The
// state lives per map-event copy (cleared with the runs); a shown object gets a fresh 0x3634 with its phase byte, and
// 0x363c for the can-interact flag. Both wire forms are CANDIDATE (no official recording carries a phase change or a
// 0x363c). The server side is OURS: an object a script made not interactable refuses its use.
public sealed partial class GameSession
{
    readonly Dictionary<int, byte> envObjPhase = [];
    readonly Dictionary<int, bool> envObjCanInteract = [];

    /// <summary>A placed object's 0x3634 with the phase / can-interact state a map event gave it.</summary>
    byte[] EnvObjAppear(int index, WorldObject o, long now) =>
        GamePackets.EnvObjSpawn(WorldObjects.FirstKey + index, o.Env, o.At, o.Yaw, now, o.UniqueKey, (byte)o.Relationship,
            canInteract: envObjCanInteract.GetValueOrDefault(index, true) ? (byte)1 : (byte)0, phase: envObjPhase.GetValueOrDefault(index));

    void ClearEnvObjDirectives()
    {
        envObjPhase.Clear();
        envObjCanInteract.Clear();
    }

    void IMapEventHost.EnvObjChangePhase(string spawner, int phase)
    {
        var p = (byte)Math.Clamp(phase, 0, 255);
        foreach (var (index, o) in SpawnerObjects(spawner).ToList())
        {
            envObjPhase[index] = p;
            if (shownObjects.Contains(index)) mapEventOut.Add(EnvObjAppear(index, o, mapEventNow));
        }
    }

    void IMapEventHost.SetEnvObjCanInteract(string spawner, bool canInteract)
    {
        foreach (var (index, _) in SpawnerObjects(spawner).ToList())
        {
            envObjCanInteract[index] = canInteract;
            if (shownObjects.Contains(index)) mapEventOut.Add(GamePackets.InteractEnvObjCanInteract(WorldObjects.FirstKey + index, canInteract));
        }
    }

    /// <summary>Why the player may not use the placed object now (the reply's result 1), or null. Checked before the use starts.</summary>
    string? EnvObjRefusal(int index, WorldObject o)
    {
        if (!envObjCanInteract.GetValueOrDefault(index, true)) return "a map event made it not interactable";
        // EnvObjData InteractConditionList NotHaveAbnormalGroup (OBSERVED table rule; A2Core PR #4).
        if (o.Carry is { } box && buffs.Any(b => b.Group == box.Group || b.AbnormalId == box.Group)) return $"already carrying abnormal group {box.Group}";
        // EnvObjData CombatPower gate: the server computes no combat power, so its Level / ItemLevel stand in (OURS).
        if (o.RequiredCombatPower > 0 && (o.MinLevel > 0 && level < o.MinLevel || o.ItemLevel > 0 && EquipItemLevel() < o.ItemLevel))
            return $"combat power {o.RequiredCombatPower} (level {o.MinLevel}, item level {o.ItemLevel}) not reached";
        return null;
    }

    /// <summary>A used carried-item box with bDespawn hides for its FinishDuration (as a gathered node; A2Core PR #4, OURS timing).</summary>
    void CarryBoxUsed(int index, WorldObject o, long now, List<byte[]> replies)
    {
        if (o.Carry is not { Despawn: true } box) return;
        gathered[index] = now + Math.Max(box.FinishDuration, 1000);
        if (shownObjects.Remove(index)) replies.Add(GamePackets.EnvObjDespawn(WorldObjects.FirstKey + index));
    }
}
