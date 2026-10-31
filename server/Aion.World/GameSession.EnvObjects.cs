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
        return null;
    }
}
