using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>
/// Summons, NPC skill extras (telegraphs, adds), groggy and crowd-control data from the client tables (tools/client_combat.py ->
/// tools/data/client-combat.json, OFFICIAL client data: Skill, SkillEffect, NpcData, ElementalSummon, SkillEffectFilterLocation).
/// What the server does with them (timings, radii, lifetimes) is CANDIDATE: see gamedata/provisional/summons.xml and the comments
/// where it is used.
/// </summary>
public sealed class ClientCombat
{
    /// <summary>A PC skill's Spawn effect: the NPC, how many, how far in front, its ElementalSummon group (Fire...; "" for others).</summary>
    public sealed record Summon(int Skill, int Npc, int Count, int Distance, string Element, int Variant, IReadOnlyList<int> Anchor);
    /// <summary>A summoned NPC (NpcData): level, move speed (cm/s), whether it can be hit or targeted, its own skills, its spawn act (Act id).</summary>
    public sealed record SummonNpc(int Npc, string Name, int Level, double Speed, bool Attackable, bool Targetable, int Radius, string Nameplate, IReadOnlyList<int> Skills,
        int SpawnAct = 0);
    /// <summary>A summoned NPC's skill: cooldown, cast, range (cm), its first Damage row (flat, ratio in 1/10000) and effect id.</summary>
    public sealed record SummonSkill(int Skill, int Cooldown, int Cast, int Range, int Flat, int Ratio, int Effect);
    /// <summary>NpcData groggy and berserk: ENpcGroggyType, GroggyTime ms, GroggyAttackMaxCount, BossBerserkInvokeTime s (0: none).</summary>
    public sealed record BossNpc(int Npc, string Groggy, int GroggyMs, int GroggyMax, int BerserkS);
    /// <summary>
    /// An NPC skill's extras: WideCastingAlertTime ms, GroundRadius cm, its SkillEffectFilterLocation (type, values, count min/max:
    /// several warned spots) and the adds its Spawn effects call (npc, count, distance).
    /// </summary>
    public sealed record NpcSkillExtra(int Skill, int AlertMs, int Ground, string? FilterType, IReadOnlyList<int> FilterValues, int FilterMin, int FilterMax,
        IReadOnlyList<(int Npc, int Count, int Distance)> Spawns)
    {
        /// <summary>Whether the skill warns its area before it lands (a telegraph: alert time, ground radius or warned spots).</summary>
        public bool Telegraphed => AlertMs > 0 || Ground > 0 || FilterType is not null;
    }

    readonly Dictionary<int, Summon> summons = [];
    readonly Dictionary<int, IReadOnlyList<string>> despawns = [];
    readonly Dictionary<int, SummonNpc> summonNpcs = [];
    readonly Dictionary<int, SummonSkill> summonSkills = [];
    readonly Dictionary<int, BossNpc> bossNpcs = [];
    readonly Dictionary<int, NpcSkillExtra> npcSkills = [];
    readonly Dictionary<int, int> groggyEnergy = [];
    readonly HashSet<int> ccFree = [];

    static int Id(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    ClientCombat(JsonElement root)
    {
        foreach (var p in root.GetProperty("summons").EnumerateObject())
        {
            var e = p.Value;
            summons[Id(p.Name)] = new Summon(Id(p.Name), e.GetProperty("npc").GetInt32(), e.GetProperty("count").GetInt32(), e.GetProperty("distance").GetInt32(),
                e.GetProperty("element").GetString() ?? "", e.GetProperty("variant").GetInt32(),
                e.TryGetProperty("anchor", out var anchor) ? anchor.EnumerateArray().Select(a => a.GetInt32()).ToList() : []);
        }
        foreach (var p in root.GetProperty("despawns").EnumerateObject())
            despawns[Id(p.Name)] = p.Value.EnumerateArray().Select(g => g.GetString() ?? "").ToList();
        foreach (var p in root.GetProperty("summon_npcs").EnumerateObject())
        {
            var e = p.Value;
            summonNpcs[Id(p.Name)] = new SummonNpc(Id(p.Name), e.GetProperty("name").GetString() ?? "", e.GetProperty("level").GetInt32(), e.GetProperty("speed").GetDouble(),
                e.GetProperty("attackable").GetBoolean(), e.GetProperty("targetable").GetBoolean(), e.GetProperty("radius").GetInt32(),
                e.GetProperty("nameplate").GetString() ?? "", e.GetProperty("skills").EnumerateArray().Select(s => s.GetInt32()).ToList(),
                e.TryGetProperty("spawn_act", out var act) ? act.GetInt32() : 0);
        }
        foreach (var p in root.GetProperty("summon_skills").EnumerateObject())
        {
            var e = p.Value;
            summonSkills[Id(p.Name)] = new SummonSkill(Id(p.Name), e.GetProperty("cooldown").GetInt32(), e.GetProperty("cast").GetInt32(), e.GetProperty("range").GetInt32(),
                e.GetProperty("flat").GetInt32(), e.GetProperty("ratio").GetInt32(), e.GetProperty("effect").GetInt32());
        }
        foreach (var p in root.GetProperty("boss_npcs").EnumerateObject())
        {
            var e = p.Value;
            bossNpcs[Id(p.Name)] = new BossNpc(Id(p.Name), e.GetProperty("groggy").GetString() ?? "None", e.GetProperty("groggy_ms").GetInt32(),
                e.GetProperty("groggy_max").GetInt32(), e.GetProperty("berserk_s").GetInt32());
        }
        foreach (var p in root.GetProperty("npc_skills").EnumerateObject())
        {
            var e = p.Value;
            string? type = null; List<int> values = []; int min = 0, max = 0;
            if (e.TryGetProperty("filter", out var f) && f.GetArrayLength() >= 4)
            {
                type = f[0].GetString();
                values = f[1].EnumerateArray().Select(x => x.GetInt32()).ToList();
                min = f[2].GetInt32(); max = f[3].GetInt32();
            }
            var spawns = e.TryGetProperty("spawn", out var s)
                ? s.EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32())).ToList() : [];
            npcSkills[Id(p.Name)] = new NpcSkillExtra(Id(p.Name), e.TryGetProperty("alert_ms", out var a) ? a.GetInt32() : 0,
                e.TryGetProperty("ground", out var g) ? g.GetInt32() : 0, type, values, min, max, spawns);
        }
        foreach (var p in root.GetProperty("groggy_energy").EnumerateObject()) groggyEnergy[Id(p.Name)] = p.Value.GetInt32();
        foreach (var s in root.GetProperty("cc_free").EnumerateArray()) ccFree.Add(s.GetInt32());
    }

    static readonly Lazy<ClientCombat> embedded = new(() =>
    {
        using var stream = typeof(ClientCombat).Assembly.GetManifestResourceStream("client-combat.json")
            ?? throw new InvalidOperationException("Missing embedded game data client-combat.json");
        using var doc = JsonDocument.Parse(stream);
        return new ClientCombat(doc.RootElement);
    });

    public static ClientCombat Embedded => embedded.Value;

    public Summon? SummonOf(int skill) => summons.GetValueOrDefault(skill);
    /// <summary>The ElementalSummon groups a Despawn skill dismisses (16009999: all five), or null.</summary>
    public IReadOnlyList<string>? DespawnOf(int skill) => despawns.GetValueOrDefault(skill);
    public SummonNpc? Npc(int npc) => summonNpcs.GetValueOrDefault(npc);
    public SummonSkill? Skill(int skill) => summonSkills.GetValueOrDefault(skill);
    public BossNpc? Boss(int npc) => bossNpcs.GetValueOrDefault(npc);
    public NpcSkillExtra? NpcSkill(int skill) => npcSkills.GetValueOrDefault(skill);
    /// <summary>A player skill's GroggyGuardDecreaseEnergy (0: none).</summary>
    public int GroggyEnergy(int skill) => groggyEnergy.GetValueOrDefault(skill);
    /// <summary>Skill.dat IgnoreCannotUseSkill: usable while stunned, silenced or asleep (escape skills).</summary>
    public bool CcFree(int skill) => ccFree.Contains(skill);
    public int SummonSkillCount => summons.Count;
}
