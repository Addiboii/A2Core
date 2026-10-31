using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Carried items from env objects: the BombBox (EnvObjData InteractConditionList NotHaveAbnormalGroup 1196, FinishDuration
/// 10000, bDespawn) gives the player a bomb, and an EnvObj_Destroy object of the same map (the Communication Tower of
/// HQ1101070, E_L0_Layer_2_Tower_01) takes it: a landed hit (GameSession.EnvObjDestroy.cs) or a use consumes the bomb, breaks
/// it at once and counts its UseEnvObj; without the bomb neither does anything.
/// The bomb is a real buff of the box's group (<see cref="CarryAbnormal"/>): 160020 for group 1196 (ActivateSkill 5003,
/// AbnormalClear after 1 cast; E_L1_FQ_BombBox_01's map event adds the same one). It is scripted (ends with the map), never
/// restored on relog, and ends with a death, a map change, a cast of its skill, the plant or <see cref="CarryTimeoutMs"/>.
/// </summary>
public sealed partial class GameSession
{
    /// <summary>Despawned carried-item boxes: object index, when the box is back (FinishDuration after its use).</summary>
    readonly Dictionary<int, long> carryBoxGoneUntil = [];

    /// <summary>
    /// A carry box with InteractTime 0 is picked up after this (OURS): 0x3638 and 0x363a in one batch left the client in the
    /// box's InteractMotion with the player unable to move (live 2026-10-30); timed objects (Started, then Completed later) do not.
    /// </summary>
    public const int CarryPickupMs = 500;

    /// <summary>A carried item nobody used ends after this (OURS): its 0x382c gives the normal skills back.</summary>
    public const int CarryTimeoutMs = 300_000;

    Buff? CarriedBuff => buffs.FirstOrDefault(b => b.Carried);

    public int? CarriedGroup => CarriedBuff?.Group;

    /// <summary>The skill the carried item's ActivateSkill row gives (the bomb's 5003): castable while it lasts.</summary>
    int? CarriedSkill => CarriedBuff is { } buff
        ? Boxes.Abnormal(buff.AbnormalId)?.Effects.Where(e => e.Type == "ActivateSkill" && e.Values.Count > 0)
            .Select(e => int.TryParse(e.Values[0], out var s) ? (int?)s : null).FirstOrDefault(s => s is not null)
        : null;

    bool HasAbnormalGroup(int group) => buffs.Any(b => b.Group == group || b.AbnormalId == group);

    /// <summary>
    /// The abnormal a carry box of this group gives: the group's abnormal with the fewest AbnormalClear SkillStart charges
    /// (one tower per bomb), else its first. Group 1196: 160020 (1 cast of 5003) before 160030 (3 casts of 5004).
    /// </summary>
    static int? CarryAbnormal(int group) => Boxes.AbnormalsInGroup(group)
        .Select(id => (Id: id, Charges: ClearCharges(id)?.Count ?? int.MaxValue))
        .OrderBy(x => x.Charges).ThenBy(x => x.Id).Select(x => (int?)x.Id).FirstOrDefault();

    /// <summary>An abnormal's AbnormalClear SkillStart row [SkillStart, skill, casts]: it ends after that many casts of the skill.</summary>
    static (int Skill, int Count)? ClearCharges(int abnormalId)
    {
        foreach (var (_, type, values) in Boxes.Abnormal(abnormalId)?.Effects ?? [])
            if (type == "AbnormalClear" && values.Count >= 3 && values[0] == "SkillStart"
                && int.TryParse(values[1], out var skill) && int.TryParse(values[2], out var count) && count > 0)
                return (skill, count);
        return null;
    }

    bool CarryBoxGone(int index, long now)
    {
        if (!carryBoxGoneUntil.TryGetValue(index, out var until)) return false;
        if (now < until) return true;
        carryBoxGoneUntil.Remove(index);
        return false;
    }

    /// <summary>A used box whose FinishDuration has just passed: dropped from the gone set, so the caller despawns it once
    /// (bDespawn) and the next visibility pass shows it again.</summary>
    bool CarryBoxExpired(int index, long now)
    {
        if (!carryBoxGoneUntil.TryGetValue(index, out var until) || now < until) return false;
        carryBoxGoneUntil.Remove(index);
        return true;
    }

    /// <summary>A box refused while the player already has its group (NotHaveAbnormalGroup); a tower without the item.</summary>
    bool CarryRefused(WorldObject o) =>
        o.Carry is { } box ? HasAbnormalGroup(box.Group) : o.Destructible && WorldObjects.HasCarryBoxes(worldMap) && CarriedGroup is null;

    /// <summary>The box gives its item and despawns until FinishDuration; a tower takes the item. True when the object is destroyed.</summary>
    bool CarryUsed(int index, WorldObject o, long now, List<byte[]> replies)
    {
        if (o.Carry is { } box)
        {
            var key = WorldObjects.FirstKey + index;
            // The client's stop acknowledgement ends its interaction state even if it missed the reply (OURS, belt and braces).
            replies.Add(GamePackets.InteractEnvObjStopReply(key));
            if (CarryAbnormal(box.Group) is { } abnormal)
            {
                var speedBefore = BuffStat("MoveSpeed");
                if (AddBuff(abnormal, -1, 0, now, replies, scripted: true, path: SelfAbnormalPath.Scripted) is { } buff)
                {
                    buff.Carried = true;
                    buff.ExpireAt = now + CarryTimeoutMs;
                }
                replies.AddRange(BuffStatsChanged(speedBefore));
            }
            if (box.Despawn)
            {
                // The box stays shown in its finished state (0x8d6d) until FinishDuration has passed and only then despawns
                // (CarryBoxExpired), then comes back on the next visibility pass.
                carryBoxGoneUntil[index] = box.FinishDuration > 0 ? now + box.FinishDuration : long.MaxValue;
            }
            return false;
        }
        if (!o.Destructible || !WorldObjects.HasCarryBoxes(worldMap) || CarriedGroup is null) return false;
        replies.AddRange(DropCarried(ExpireEtc));
        return true;
    }

    /// <summary>The carried item ends (used on a tower, a map or copy change): 0x382c for its buff.</summary>
    List<byte[]> DropCarried(byte reason)
    {
        if (CarriedBuff is not { } buff) return [];
        var speedBefore = BuffStat("MoveSpeed");
        return [.. RemoveBuffs([buff], reason), .. BuffStatsChanged(speedBefore)];
    }

    /// <summary>AbnormalClear SkillStart: a cast of the carried item's skill (the bomb's 5003) uses up one charge.</summary>
    List<byte[]> CarrySkillStarted(int skill)
    {
        if (CarriedBuff is not { } buff || ClearCharges(buff.AbnormalId) is not { } clear || clear.Skill != skill) return [];
        if (++buff.ChargesUsed < clear.Count) return [];
        return DropCarried(ExpireEtc);
    }
}
