using Aion.Protocol.Native;

namespace Aion.World;

// What a player's skill does to a monster's mind and place: thrown / pulled / lifted (Abnormal_Move_*), taunted, aggro moved.
public sealed partial class NpcAi
{
    /// <summary>
    /// The monster stands at <paramref name="to"/> (a knockback, a pull): the server's position is the end point at once, the client
    /// is put on the ground by the end snap (GameSession.Displacement.cs) and the monster waits until <paramref name="until"/> before it acts again.
    /// OURS: no wall or ledge check beyond the navigation mesh's floor height.
    /// </summary>
    public void Relocate(IReadOnlyDictionary<int, Target> targets, Target target, Vector3d to, long until)
    {
        Sync(targets);
        if (!brains.TryGetValue(target.Key, out var brain)) return;
        var at = new Vector3d(to.X, to.Y, Height(brain, to.X, to.Y, to.Z));
        target.Position = at;
        target.DisplacedUntil = until;
        brain.Moving = false;
        brain.Path = null;
        brain.SentPosition = at;
        brain.Pending = null;
    }

    /// <summary>A taunt: the taunter becomes the monster's top hate and it turns on them.</summary>
    public List<byte[]> Taunt(IReadOnlyDictionary<int, Target> targets, Target target, long now, int tauntKey)
    {
        Sync(targets);
        if (!brains.TryGetValue(target.Key, out var brain) || target.Dead || target.RunSpeed == 0 || brain.Mode == Mode.Return) return [];
        awake.Add(target.Key);
        var top = brain.Hate.Count == 0 ? 0 : brain.Hate.Values.Max();
        brain.Hate[tauntKey] = Math.Max(brain.Hate.GetValueOrDefault(tauntKey), top) + 1;
        brain.CandidateKey = tauntKey;
        return StartChase(target, brain, now);
    }

    /// <summary>
    /// AggroTransfer: <paramref name="permille"/> of what the monster holds against <paramref name="fromKey"/> goes to
    /// <paramref name="toKey"/> (0: it is simply forgotten). The monster then follows whoever it hates most.
    /// </summary>
    public List<byte[]> TransferAggro(IReadOnlyDictionary<int, Target> targets, Target target, long now, int fromKey, int toKey, int permille)
    {
        Sync(targets);
        if (!brains.TryGetValue(target.Key, out var brain) || target.Dead) return [];
        var held = brain.Hate.GetValueOrDefault(fromKey);
        if (held <= 0) return [];
        var moved = (long)held * Math.Clamp(permille, 0, 1000) / 1000;
        brain.Hate[fromKey] = held - moved;
        if (toKey != 0) brain.Hate[toKey] = brain.Hate.GetValueOrDefault(toKey) + moved;
        if (brain.Hate[fromKey] <= 0) brain.Hate.Remove(fromKey);
        if (TopHate(brain) is not { } top || target.RunSpeed == 0 || brain.Mode == Mode.Return) return [];
        if (top == brain.CandidateKey && top == Victim(brain)) return [];
        brain.CandidateKey = top;
        return StartChase(target, brain, now);
    }

    /// <summary>Tests: the hate a monster holds against <paramref name="key"/>.</summary>
    internal long HateOf(Target target, int key) => brains.TryGetValue(target.Key, out var brain) ? brain.Hate.GetValueOrDefault(key) : 0;
}
