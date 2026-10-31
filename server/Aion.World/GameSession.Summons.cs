using Aion.Core.Characters;
using Aion.Protocol.Native;

namespace Aion.World;

// Player summons (Skill.dat Spawn effects, tools/data/pc-summons.json): the Spiritmaster's Summon: Fire/Water/Wind/Earth Spirit
// were accepted and did nothing (live 2026-10-30: no spirit, no damage). The summon appears next to the player, follows them
// and fights as a companion: the player's target first, then the nearest monster (NpcData AggroType ParentTarget). One summon
// of a group at a time (SkillEffect ElementalSummon group); a new cast replaces it. Its kills are the player's kills.
partial class GameSession
{
    /// <summary>Summon entity keys: above every map NPC and player key, per player and slot (OURS).</summary>
    public const int FirstSummonKey = 900_000_000;
    const int SummonSlots = 8;

    readonly Dictionary<string, int> summonKeys = new(StringComparer.Ordinal);
    /// <summary>The out spirits' EElementalSummonGroupType, by summon key (their 0xe239 mode icon).</summary>
    readonly Dictionary<int, byte> elementalModes = [];
    /// <summary>When each out spirit's server-side life time runs out, by summon key.</summary>
    readonly Dictionary<int, long> elementalExpiry = [];
    /// <summary>Replaced spirits: 0x3642 flag 7 is sent after the new one is out (official ~2.5 s).</summary>
    readonly Dictionary<int, long> pendingElementalDespawn = [];
    /// <summary>Official 2026-10-31 Fire→Water: old spirit 0x3642 about 2.5 s after the switch packets.</summary>
    internal const int ElementalReplaceDespawnMs = 2_500;
    /// <summary>0xe239 after the spirit's appear and aggro/target packets (client Jointstrike gate).</summary>
    readonly List<(int SummonKey, byte Group, long DueMs)> pendingElementalActivates = [];
    /// <summary>SkillEffect Caster_HasAbnormalId while a spirit is out (Jointstrike client gate; 0x382a on the player).</summary>
    public const int SpiritSummonStateAbnormal = 1_004_901;
    int lastSummonSlot = SummonSlots - 1;

    int SummonKey(int slot) => FirstSummonKey + (playerKey % 1_000_000) * SummonSlots + slot;

    /// <summary>EElementalSummonGroupType name from Skill.dat; 0 None.</summary>
    internal static byte ElementalGroupType(string group) => ElementalSummons.GroupType(group);

    /// <summary>The summoner's CharDesc, as 0x3645 sends it to others.</summary>
    CharDescWire SummonerDesc()
    {
        var profile = Profile();
        return new(CharacterNames.Display(CharacterName ?? character?.Name ?? "", StaffRole), profile?.PcId ?? 1, profile?.Race ?? 1, character?.Gender ?? 1);
    }

    /// <summary>The spirit's mode icon goes (0xe23a) when it leaves.</summary>
    List<byte[]> EndElementalMode(int key)
    {
        elementalExpiry.Remove(key);
        CancelPendingElementalActivate(key);
        return EndElementalModeIcon(key, lastNow);
    }

    List<byte[]> EndElementalModeIcon(int key, long now)
    {
        var replies = elementalModes.Remove(key, out var mode)
            ? new List<byte[]> { GamePackets.ElementalSummonDeactivate(mode, key) }
            : [];
        if (elementalModes.Count == 0) replies.AddRange(ClearSpiritSummonState(now));
        return replies;
    }

    List<byte[]> ApplySpiritSummonState(long now)
    {
        if (buffs.Any(b => b.AbnormalId == SpiritSummonStateAbnormal)) return [];
        var replies = new List<byte[]>();
        AddBuff(SpiritSummonStateAbnormal, -1, 0, now, replies, path: SelfAbnormalPath.Scripted);
        return replies;
    }

    List<byte[]> ClearSpiritSummonState(long now)
    {
        _ = now;
        var gone = buffs.Where(b => b.AbnormalId == SpiritSummonStateAbnormal).ToList();
        return gone.Count == 0 ? [] : RemoveBuffs(gone, ExpireEtc);
    }

    /// <summary>Return: All Spirits (Spiritmaster unlock at level 1; the HUD's despawn-all button, GlobalSetting
    /// elementalist_summon_despawn_all_skill_id).</summary>
    public const int ReturnAllSpiritsSkill = 16009999;

    /// <summary>A cast Dismiss or Return skill: the matching spirit leaves (its icon with it).</summary>
    List<byte[]> DismissFromSkill(int skillId)
    {
        if (skillId == ReturnAllSpiritsSkill) return Dismiss("Elemental");
        var mode = ElementalSummons.GroupTypeForDismissSkill(skillId);
        if (mode == 0 || !summonKeys.TryGetValue("Elemental", out var key)
            || !elementalModes.TryGetValue(key, out var current) || current != mode) return [];
        return Dismiss("Elemental");
    }

    /// <summary>The spirit whose life time ran out leaves, as if dismissed.</summary>
    internal List<byte[]> SummonExpiryTick(long now)
    {
        var replies = new List<byte[]>();
        foreach (var key in pendingElementalDespawn.Where(p => now >= p.Value).Select(p => p.Key).ToList())
        {
            pendingElementalDespawn.Remove(key);
            replies.Add(GamePackets.Death(key, 7));
        }
        if (elementalExpiry.Count == 0) return replies;
        foreach (var key in elementalExpiry.Where(p => now >= p.Value).Select(p => p.Key).ToList())
            foreach (var group in summonKeys.Where(p => p.Value == key).Select(p => p.Key).ToList()) replies.AddRange(Dismiss(group));
        return replies;
    }

    /// <summary>Elemental switch: deactivate the old icon, keep the old NPC until <see cref="ElementalReplaceDespawnMs"/>.</summary>
    List<byte[]> ReplaceElementalSpirit(int key, Target old, long now)
    {
        summonKeys.Remove("Elemental");
        elementalExpiry.Remove(key);
        CancelPendingElementalActivate(key);
        targets!.Remove(key);
        var replies = EndElementalModeIcon(key, now).ToList();
        pendingElementalDespawn[key] = now + ElementalReplaceDespawnMs;
        old.Hp = 0;
        old.CorpseRemoved = true;
        respawnAt.Remove(key);
        seenNpcs.Remove(key);
        replies.AddRange(SummonAi.Killed(old));
        return replies;
    }

    /// <summary>A cast summon skill: the old summon of its group leaves, the new one appears by the player and joins the fight.</summary>
    List<byte[]> SummonFromSkill(int skillId, CastRequest request, long now)
    {
        if (targets is null || PcSummons.For(skillId) is not { } summon) return [];
        var replies = new List<byte[]>();
        // Elemental spirits: one at a time, whatever their element (Ancient included).
        var group = summon.Skill.Group.Length > 0 ? "Elemental" : $"npc{summon.Skill.Npc}";
        if (group == "Elemental" && summonKeys.TryGetValue("Elemental", out var prevKey) && targets.TryGetValue(prevKey, out var prev))
            replies.AddRange(ReplaceElementalSpirit(prevKey, prev, now));
        else
            replies.AddRange(Dismiss(group));
        // A fresh key for every spirit: the client still holds the despawning spirit of the same key while its icon turns off.
        var slot = Enumerable.Range(1, SummonSlots).Select(i => (lastSummonSlot + i) % SummonSlots).FirstOrDefault(s => !targets.ContainsKey(SummonKey(s)));
        lastSummonSlot = slot;
        var key = SummonKey(slot);
        var here = position ?? request.Location;
        var yaw = request.Yaw * Math.PI / 180;
        var hp = Math.Max(1, resources?.HpMax ?? Combat.HpForLevel(level));
        var template = summon.Template;
        var t = new Target
        {
            Key = key, Name = template.Name, Hp = hp, HpMax = hp, NpcId = summon.Skill.Npc,
            Position = here with { X = here.X + Math.Cos(yaw) * 150, Y = here.Y + Math.Sin(yaw) * 150 },
            // The spawn row inherits the caster's stats (AllOverrideParentSomeMultiply): it fights at the player's level.
            Level = Math.Max(template.Level, level), Direction = request.Yaw, Relationship = 22,   // ERelationshipEntity PC_Summon
            Aggressive = true, WalkSpeed = template.WalkSpeed, RunSpeed = template.RunSpeed, Radius = template.Radius,
            Function = "Companion", Skills = template.Skills, OwnerKey = playerKey, OwnerDesc = SummonerDesc(),
            SummonSourceSkill = skillId,
        };
        targets[key] = t;
        summonKeys[group] = key;
        replies.Add(GamePackets.NpcAppear(t.Appearance() with { Reason = NpcAppearance.NpcSpawnReason }));
        seenNpcs.Add(key);
        replies.AddRange(SummonAi.Follow(t, playerKey, true, now));
        var victimKey = targets.ContainsKey(request.TargetKey) ? request.TargetKey : selectedTarget;
        if (targets.TryGetValue(victimKey, out var victim) && victim.Hostile && !victim.Dead && victim.OwnerKey == 0)
            replies.AddRange(SummonAi.SummonAttack(t, victim, playerKey, now));
        if (ElementalSummons.GroupTypeForNpc(summon.Skill.Npc) is var mode and > 0)
        {
            elementalModes[key] = mode;
            elementalExpiry[key] = now + GamePackets.SpiritLifeTimeMs;
            pendingElementalActivates.Add((key, mode, now + 1));
        }
        return replies;
    }

    /// <summary>Deferred 0xe239 once the spirit entity exists on the client (appear, 0x8d52/0x3835, …).</summary>
    internal List<byte[]> ElementalActivateTick(long now)
    {
        if (pendingElementalActivates.Count == 0) return [];
        var replies = new List<byte[]>();
        foreach (var pending in pendingElementalActivates.Where(p => now >= p.DueMs).ToList())
        {
            pendingElementalActivates.Remove(pending);
            if (!elementalModes.TryGetValue(pending.SummonKey, out var mode) || mode != pending.Group) continue;
            replies.Add(GamePackets.ElementalSummonActivate(mode, pending.SummonKey));
            replies.AddRange(ApplySpiritSummonState(now));
        }
        return replies;
    }

    void CancelPendingElementalActivate(int summonKey) =>
        pendingElementalActivates.RemoveAll(p => p.SummonKey == summonKey);

    /// <summary>Skill.dat CanUseWhenElementalSummonSpawned: Jointstrike and similar skills are usable only while a spirit of one
    /// of its groups is out. Summon: Fire/Water/… cast skills carry the field for their element but must not require
    /// an existing spirit (PcSummons Spawn rows).
    /// </summary>
    bool SpiritConditionMet(SkillDefinition? definition, int skillId = 0)
    {
        if (skillId > 0 && PcSummons.For(skillId) is { Skill.Group: { Length: > 0 } })
            return true;
        return definition?.NeedsSpirit is not { Length: > 0 } groups
            || elementalModes.Values.Any(mode => groups.Any(group => ElementalGroupType(group) == mode));
    }

    /// <summary>A spirit-conditioned attack is made together with the spirit: it strikes the same target at once.</summary>
    List<byte[]> JointStrike(SkillDefinition definition, CastRequest request, long now)
    {
        if (definition.NeedsSpirit is not { Length: > 0 } || targets is null
            || !summonKeys.TryGetValue("Elemental", out var key) || !targets.TryGetValue(key, out var spirit)) return [];
        var victimKey = targets.ContainsKey(request.TargetKey) ? request.TargetKey : selectedTarget;
        return targets.TryGetValue(victimKey, out var victim) && victim.Hostile && !victim.Dead && victim.OwnerKey == 0
            ? SummonAi.SummonAttack(spirit, victim, playerKey, now)
            : [];
    }

    /// <summary>The summon of the group leaves (0x3642 flag 7, as an ended spawner).</summary>
    List<byte[]> Dismiss(string group)
    {
        if (!summonKeys.Remove(group, out var key) || targets is null || !targets.Remove(key, out var old)) return [];
        pendingElementalDespawn.Remove(key);
        old.Hp = 0;
        old.CorpseRemoved = true;
        respawnAt.Remove(key);
        seenNpcs.Remove(key);
        return [.. EndElementalMode(key), .. SummonAi.Killed(old), GamePackets.Death(key, 7)];
    }

    /// <summary>Every summon of this character leaves: on map change, character switch, logout and disconnect. Summons are
    /// owned by the character; the session (and its player key) outlives it, so a summon left in the shared map followed
    /// the session's next character (live 2026-10-30 14:26: Zakl's Fire Spirit followed Strel).</summary>
    internal List<byte[]> DismissAllSummons()
    {
        var replies = new List<byte[]>();
        foreach (var group in summonKeys.Keys.ToList()) replies.AddRange(Dismiss(group));
        summonKeys.Clear();
        if (targets is null) return replies;
        foreach (var stray in targets.Values.Where(t => t.OwnerKey == playerKey).ToList())
        {
            targets.Remove(stray.Key);
            respawnAt.Remove(stray.Key);
            seenNpcs.Remove(stray.Key);
            pendingElementalDespawn.Remove(stray.Key);
            replies.AddRange(EndElementalMode(stray.Key));
            replies.AddRange(SummonAi.Killed(stray));
            replies.Add(GamePackets.Death(stray.Key, 7));
        }
        return replies;
    }

    /// <summary>A summon that died: it is gone (no corpse respawn).</summary>
    List<byte[]> SummonDied(Target summon)
    {
        foreach (var (group, key) in summonKeys.Where(p => p.Value == summon.Key).ToList()) summonKeys.Remove(group);
        targets?.Remove(summon.Key);
        respawnAt.Remove(summon.Key);
        seenNpcs.Remove(summon.Key);
        return [.. EndElementalMode(summon.Key), GamePackets.Death(summon.Key, 7)];
    }
}


partial class GameSession
{
    /// <summary>Tests: the summon a cast of <paramref name="skillId"/> at <paramref name="targetKey"/> places.</summary>
    internal List<byte[]> SummonFromSkillForTest(int skillId, int targetKey, long now) =>
        SummonFromSkill(skillId, new CastRequest(skillId, 0, 0, false, targetKey, 0, position ?? (targets is not null && targets.TryGetValue(targetKey, out var at) ? at.Position with { X = at.Position.X - 300 } : Vector3d.Zero)), now);
    internal List<byte[]> DismissFromSkillForTest(int skillId) => DismissFromSkill(skillId);
    /// <summary>The AI that ticks this map's NPCs: the shared map's one in the world, else the session's.</summary>
    NpcAi SummonAi => worldPlayer?.Map?.Ai ?? ai;
}
