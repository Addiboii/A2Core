using Aion.Protocol.Native;

namespace Aion.World;

public sealed partial class GameSession
{
    readonly HashSet<int> playtestLoggedEntities = [];
    string? pendingSkillCastRefusalReason;

    void LogWorldEntities()
    {
        if (targets is null) return;
        foreach (var t in targets.Values)
            PlaytestDebug.EntityOnce(playtestLoggedEntities, t.Key, t.NpcId, t.Name);
    }

    void LogSkillCastFromReplies(byte[] plain, List<byte[]> replies)
    {
        if (ParseCast(plain) is not { } request) return;
        if (pendingSkillCastRefusalReason is { } loggedReason)
        {
            pendingSkillCastRefusalReason = null;
            return;
        }
        var outcome = CastOutcomeFromReplies(replies);
        var reason = outcome.Accepted == false ? "cast_rejected" : null;
        LogSkillCastOutcome(request.SkillId, request.TargetKey, outcome, reason: reason);
    }

    void LogBasicAtkCast(int skillId, int targetKey, List<byte[]> replies)
    {
        if (pendingSkillCastRefusalReason is not null)
        {
            pendingSkillCastRefusalReason = null;
            return;
        }
        LogSkillCastOutcome(skillId, targetKey, BasicAtkOutcomeFromReplies(replies), wire: "basic_atk");
    }

    void LogSkillCastOutcome(int skillId, int targetKey, (bool? Accepted, bool ResendAck) outcome, string wire = "skill_start", string? reason = null)
    {
        var skillName = data.Skill(skillId)?.Name ?? skillId.ToString();
        if (outcome.ResendAck)
        {
            PlaytestDebug.Action("skill_cast",
                ("wire", wire),
                ("skillId", skillId),
                ("skill", skillName),
                ("targetKey", targetKey),
                ("accepted", true),
                ("resendAck", true));
            return;
        }
        var accepted = outcome.Accepted ?? false;
        if (!accepted)
        {
            PlaytestDebug.Action("skill_cast",
                ("wire", wire),
                ("skillId", skillId),
                ("skill", skillName),
                ("targetKey", targetKey),
                ("accepted", false),
                ("reason", reason ?? "cast_rejected"));
            return;
        }
        PlaytestDebug.Action("skill_cast",
            ("wire", wire),
            ("skillId", skillId),
            ("skill", skillName),
            ("targetKey", targetKey),
            ("accepted", true));
    }

    (bool? Accepted, bool ResendAck) CastOutcomeFromReplies(List<byte[]> replies)
    {
        bool? accepted = null;
        var hasStart = false;
        foreach (var packet in replies)
        {
            if (packet.Length >= 2 && Opcode(packet) == 0x3802) hasStart = true;
            if (packet.Length < 6 || Opcode(packet) != 0x3801) continue;
            try
            {
                var reader = new PacketReader(packet) { Offset = 2 };
                if (packet.Length != 9 && packet.Length > 3 && (packet[2] & 3) != 0) reader.U8();
                accepted = reader.U16() == 0;
            }
            catch (ProtocolException) { break; }
        }
        return (accepted, accepted == true && !hasStart);
    }

    (bool? Accepted, bool ResendAck) BasicAtkOutcomeFromReplies(List<byte[]> replies)
    {
        bool? accepted = null;
        var hasStart = replies.Any(p => p.Length >= 2 && Opcode(p) == 0x3802);
        foreach (var packet in replies)
        {
            if (packet.Length < 4 || Opcode(packet) != BasicAtkRs) continue;
            accepted = BitConverter.ToUInt16(packet, 2) == 0;
            break;
        }
        return (accepted, accepted == true && !hasStart);
    }

    void LogSkillCastRefusal(CastRequest request, int skillId, SkillDefinition? definition, bool meetsCastRules,
        bool chainAllowed, bool cooldownReady, bool castGapOk, SkillCost cost, IReadOnlyList<string> weapons)
    {
        string reason;
        if (definition is null) reason = "unknown_skill";
        else if (resources is null) reason = "no_resources";
        else if (dead) reason = "dead";
        else if (!chainAllowed) reason = "chain_window_closed";
        else if (CombatCastBlocked(skillId)) reason = "combat_blocked";
        else if (resources.Mp < cost.Mp) reason = "need_mp";
        else if (resources.Sp < cost.Sp) reason = "need_sp";
        else if (resources.Hp <= cost.Hp) reason = "need_hp";
        else if (!learned.Contains(skillId) && !IsDodgeId(skillId) && definition.ChainPrev == 0 && CarriedSkill != skillId)
            reason = "not_learned";
        else if (weapons.Count > 0 && !weapons.Contains(data.WeaponType(equippedWeapon) ?? "")) reason = "wrong_weapon";
        else if (!SpiritConditionMet(definition, skillId)) reason = "spirit_required";
        else if (!PeaceConditionMet(definition)) reason = "peace_required";
        else if (!ResourceConditionMet(definition)) reason = "resource_threshold";
        else if (!SkillCondMet(definition)) reason = "skill_cond_required";
        else if (!cooldownReady) reason = "cooldown";
        else if (!castGapOk) reason = "cast_gap";
        else reason = "cast_rules";

        pendingSkillCastRefusalReason = reason;
        PlaytestDebug.Action("skill_cast",
            ("skillId", skillId),
            ("skill", definition?.Name ?? skillId.ToString()),
            ("targetKey", request.TargetKey),
            ("accepted", false),
            ("reason", reason));
    }

    void LogCombatHit(int skillId, int targetKey, int amount, bool kill)
    {
        PlaytestDebug.Action(kill ? "kill" : "damage",
            ("skillId", skillId),
            ("targetKey", targetKey),
            ("amount", amount));
    }

    void LogTeleport(int mapId, Vector3d at, string source)
    {
        PlaytestDebug.Action("teleport",
            ("mapId", mapId),
            ("x", at.X),
            ("y", at.Y),
            ("z", at.Z),
            ("source", source));
    }

    void LogQuestEvent(string kind, string code, int step, int status, int[]? goals = null)
    {
        PlaytestDebug.Action(kind,
            ("quest", code),
            ("step", step),
            ("status", status),
            ("goals", goals is null ? "" : string.Join(',', goals)));
    }

    void LogNpcTalk(int npcKey, int npcId, string? name, string context)
    {
        PlaytestDebug.Action("npc_talk",
            ("npcKey", npcKey),
            ("npcId", npcId),
            ("name", name ?? ""),
            ("context", context));
    }

    void LogNpcTalkRefused(int npcKey, int npcId, string? name, string reason)
    {
        PlaytestDebug.Action("npc_talk_refused",
            ("npcKey", npcKey),
            ("npcId", npcId),
            ("name", name ?? ""),
            ("reason", reason));
        Note($"Npc talk refused: {name} ({npcId}) key={npcKey}: {reason}");
    }

    void LogMapEventBeat(string text) => PlaytestDebug.Action("map_event", ("text", text));

    void LogDeath(int killerKey) =>
        PlaytestDebug.Action("death", ("characterKey", playerKey), ("killerKey", killerKey));

    void LogResurrect(string source) =>
        PlaytestDebug.Action("resurrect", ("characterKey", playerKey), ("source", source));

    void LogLevelUp(int from, int to) =>
        PlaytestDebug.Action("level_up", ("from", from), ("to", to), ("characterKey", playerKey));

    void LogSkillPoints(int skillId, int level, string context) =>
        PlaytestDebug.Action("skill_point", ("skillId", skillId), ("level", level), ("context", context));

    void LogItemUse(int templateId, string? name) =>
        PlaytestDebug.Action("item_use", ("templateId", templateId), ("name", name ?? ""));

    void LogItemUseRefused(int templateId, string? effect, string reason) =>
        PlaytestDebug.Action("item_use_refused", ("templateId", templateId), ("effect", effect ?? ""), ("reason", reason));

    void LogItemGain(int templateId, int count, string source) =>
        PlaytestDebug.Action("item_gain", ("templateId", templateId), ("count", count), ("source", source));
}
