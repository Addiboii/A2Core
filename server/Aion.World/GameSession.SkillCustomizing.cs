using System.Text.Json;
using Aion.Protocol.Native;

namespace Aion.World;

// Skill option customizing (SkillEffectFilterId → skill id, priority). SkillCondTable is not in the client export;
// defaults come from tools/data/class-skills.json (Skill.dat effect rows). Updates: 0x36EE / 0x36EF.
public sealed partial class GameSession
{
    public const ushort SkillCustomizingUpdateRq = 0x36EE, SkillCustomizingUpdateRs = 0x36EF;
    const string SkillCustomizingSetting = "skill_customizing";

    readonly List<(int CondId, int SkillId, byte Priority)> skillCustomizing = [];

    public IReadOnlyList<(int CondId, int SkillId, byte Priority)> SkillCustomizing => skillCustomizing;

    void LoadSkillCustomizing(IReadOnlyDictionary<string, JsonElement> settings)
    {
        skillCustomizing.Clear();
        if (!settings.TryGetValue(SkillCustomizingSetting, out var raw)) return;
        try
        {
            foreach (var row in raw.Deserialize<int[][]>() ?? [])
                if (row.Length >= 3) skillCustomizing.Add((row[0], row[1], (byte)row[2]));
        }
        catch (JsonException) { }
    }

    void EnsureDefaultSkillCustomizing()
    {
        if (skillCustomizing.Count > 0) return;
        foreach (var skillId in learned.Where(id => id >= 11_000_000 && data.Skill(id)?.Type == "Active"))
            skillCustomizing.AddRange(data.DefaultSkillCustomizing(skillId));
    }

    IReadOnlyList<(int CondId, int SkillId, byte Priority)> SkillCustomizingForSelect(string className, int atLevel)
    {
        if (skillCustomizing.Count > 0) return skillCustomizing;
        var rows = new List<(int CondId, int SkillId, byte Priority)>();
        foreach (var (skillId, _) in data.LearnedSkills(className, atLevel))
            if (skillId >= 11_000_000 && data.Skill(skillId)?.Type == "Active")
                rows.AddRange(data.DefaultSkillCustomizing(skillId));
        return rows;
    }

    IReadOnlyDictionary<int, IReadOnlyList<int>> SkillOptionMap()
    {
        EnsureDefaultSkillCustomizing();
        var map = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var skillId in learned.Where(id => id >= 11_000_000 && !IsDodgeId(id) && data.Skill(id)?.Type == "Active"))
        {
            var chosen = skillCustomizing.Where(r => r.SkillId == skillId || data.DefaultSkillCustomizing(skillId).Any(d => d.CondId == r.CondId))
                .OrderBy(r => r.Priority).Select(r => r.SkillId).Distinct().Take(3).ToList();
            if (chosen.Count == 0) chosen = data.SkillOptionIds(skillId).Where(id => id != 0).ToList();
            while (chosen.Count < 3) chosen.Add(0);
            map[skillId] = chosen.Take(3).ToList();
        }
        return map;
    }

    async Task<List<byte[]>> UpdateSkillCustomizingAsync(byte[] plain, CancellationToken ct)
    {
        if (plain.Length < 4) return [];
        try
        {
            var reader = new PacketReader(plain) { Offset = 2 };
            var count = reader.Varint();
            var rows = new List<(int CondId, int SkillId, byte Priority)>();
            for (var i = 0; i < count && i < 512; i++)
            {
                var cond = (int)reader.U32();
                var skill = (int)reader.U32();
                var priority = reader.U8();
                if (data.Skill(skill) is null) return [GamePackets.SkillCustomizingUpdate(1)];
                rows.Add((cond, skill, priority));
            }
            if (!reader.End) return [GamePackets.SkillCustomizingUpdate(1)];
            skillCustomizing.Clear();
            skillCustomizing.AddRange(rows);
            await SaveSettingAsync(SkillCustomizingSetting, rows.Select(r => new[] { r.CondId, r.SkillId, r.Priority }).ToArray(), ct);
            return [GamePackets.SkillCustomizingUpdate(0)];
        }
        catch (ProtocolException)
        {
            return [GamePackets.SkillCustomizingUpdate(1)];
        }
    }
}
