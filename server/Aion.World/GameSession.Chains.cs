using System.Text.Json;
using System.Text.Json.Serialization;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// Skill behaviour from the client skill table (tools/skill_behaviour.py → tools/data/skill-behaviour.json): cast data for the
/// skills the class kits lack (chain follow-ups, auto-load skills), the chains and the auto-load charges. Loaded once.
/// </summary>
public sealed class SkillBehaviour
{
    public sealed record Entry(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("weapons")] string[] Weapons,
        [property: JsonPropertyName("cooldown_ms")] int CooldownMs,
        [property: JsonPropertyName("cost")] SkillCost Cost,
        [property: JsonPropertyName("damage")] DamageRow[] Damage,
        [property: JsonPropertyName("casting_ms")] int CastingMs,
        [property: JsonPropertyName("animation")] string? Animation,
        [property: JsonPropertyName("chain_prev")] int ChainPrev,
        [property: JsonPropertyName("chain_rate")] int ChainRate,
        [property: JsonPropertyName("chain_ms")] int ChainMs,
        [property: JsonPropertyName("autoload")] JsonElement? AutoLoad,
        [property: JsonPropertyName("projectiles")] int[]? Projectiles,
        [property: JsonPropertyName("fix_slot")] bool FixSlot,
        [property: JsonPropertyName("abnormals")] int[][]? Abnormals = null,
        [property: JsonPropertyName("disposition")] string? Disposition = null,
        [property: JsonPropertyName("target")] string? Target = null,
        [property: JsonPropertyName("charge")] bool Charge = false,
        [property: JsonPropertyName("group_cooldown")] int GroupCooldown = 0);

    /// <summary>An auto-load skill's charges: Stack (given by objects, as the bomb) or Time (refilled one by one).</summary>
    public sealed record Charges(bool Timed, int Max, int[] RefillMs);

    sealed record FileShape([property: JsonPropertyName("skills")] Dictionary<int, Entry> Skills);

    readonly Dictionary<int, Entry> skills;
    readonly Dictionary<int, SkillDefinition> definitions;
    readonly Dictionary<int, Charges> charges = [];

    SkillBehaviour(Dictionary<int, Entry> skills)
    {
        this.skills = skills;
        definitions = skills.ToDictionary(kv => kv.Key, kv => new SkillDefinition(kv.Value.Name, kv.Value.Type, kv.Value.Weapons, kv.Value.CooldownMs, kv.Value.Cost, kv.Value.Damage));
        foreach (var (id, e) in skills)
            if (e.AutoLoad is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() >= 2)
                charges[id] = new Charges(a[0].GetString() == "Time", a[1].GetInt32(),
                    a.GetArrayLength() > 2 ? a[2].EnumerateArray().Select(t => t.GetInt32()).ToArray() : []);
        chainIds = skills.Where(kv => kv.Value.ChainPrev != 0).GroupBy(kv => kv.Value.ChainPrev)
            .ToDictionary(g => g.Key, g => g.Select(kv => (kv.Key, kv.Value.ChainRate, kv.Value.ChainMs)).ToList());
    }

    readonly Dictionary<int, List<(int Id, int Rate, int Ms)>> chainIds;

    static readonly Lazy<SkillBehaviour> embedded = new(() =>
    {
        using var stream = typeof(SkillBehaviour).Assembly.GetManifestResourceStream("skill-behaviour.json")
            ?? throw new InvalidOperationException("Missing embedded game data skill-behaviour.json");
        return new SkillBehaviour(JsonSerializer.Deserialize<FileShape>(stream)?.Skills ?? throw new InvalidDataException("skill-behaviour.json: empty"));
    });

    public static SkillBehaviour Embedded => embedded.Value;

    public int Count => skills.Count;
    public Entry? Of(int skill) => skills.GetValueOrDefault(skill);
    public SkillDefinition? Definition(int skill) => definitions.GetValueOrDefault(skill);
    /// <summary>The follow-ups a skill offers: (skill, chance in 1/10000, how long it stays open in ms).</summary>
    public IReadOnlyList<(int Id, int Rate, int Ms)> FollowUps(int skill) => chainIds.GetValueOrDefault(skill) ?? [];
    public Charges? ChargesOf(int skill) => charges.GetValueOrDefault(skill);
    public IEnumerable<int> ChargedSkills => charges.Keys;
}

// Chain (combo) skills and auto-load charges, as the official server sent them (tools/skill_traffic.py over 11 recordings):
//  - ChainSkillActiveList_NT 0x3818 {prev skill i32, count varint, follow-ups i32} right after the previous skill's start, before
//    its 0x3801; ChainSkillDeactiveList_NT 0x3819 {count, skills} when the follow-up is cast (before its 0x3801) or when the offer
//    runs out (ChainSkillAvailableTime 3000 ms, OBSERVED ~3050). A follow-up is castable only while offered.
//  - SkillAutoLoadUpdate_NT 0x3822 {mask: 1 count, 2 time; skill i32; count u8; [time varint]}: full charges when learned/at
//    entry, each refill (the next refill's time while not full). The caster's SkillStart carries mask 4 + charges left and mask
//    8 + time to the next refill. Refill times are the client table's AutoLoadTimeList (official 9,300 ms for a 10,000 entry:
//    PROVISIONAL, the cooldown reduction stat is not applied).
public sealed partial class GameSession
{
    public const ushort ChainSkillActiveListNt = 0x3818, ChainSkillDeactiveListNt = 0x3819;
    /// <summary>OBSERVED: the offer closed ~50 ms after its 3000 ms.</summary>
    const int ChainCloseSlackMs = 50;

    static SkillBehaviour Behaviour => SkillBehaviour.Embedded;

    /// <summary>Follow-ups offered now: skill -> when the offer closes.</summary>
    readonly Dictionary<int, long> offeredChains = [];
    /// <summary>Charges of auto-load skills: skill -> (count, next refill time or 0).</summary>
    readonly Dictionary<int, (int Count, long RefillAt)> skillCharges = [];

    public static byte[] ChainActive(int prev, IReadOnlyList<int> followUps)
    {
        var w = PacketWriter.Opcode(ChainSkillActiveListNt).I32(prev).Varint(followUps.Count);
        foreach (var f in followUps) w.I32(f);
        return w.ToArray();
    }

    public static byte[] ChainDeactive(IReadOnlyList<int> skills)
    {
        var w = PacketWriter.Opcode(ChainSkillDeactiveListNt).Varint(skills.Count);
        foreach (var s in skills) w.I32(s);
        return w.ToArray();
    }

    /// <summary>0x3822 with the charges and, while not full, the time to the next refill.</summary>
    public static byte[] AutoLoadUpdate(int skill, int count, int? refillInMs)
    {
        var w = PacketWriter.Opcode(SkillAutoLoadUpdateNt).U8(refillInMs is null ? 1 : 3).I32(skill).U8((byte)count);
        if (refillInMs is { } t) w.Varint(t);
        return w.ToArray();
    }

    bool ChainOffered(int skill, long now) => offeredChains.TryGetValue(skill, out var until) && now <= until;

    /// <summary>Skill data: the class kit's, else the client table's (chain follow-ups, auto-load skills).</summary>
    SkillDefinition? SkillData(int skill) => data.Skill(skill) ?? Behaviour.Definition(skill);

    /// <summary>The chain packets of an accepted cast: its own offer closed, its follow-ups offered (and closed when they run out).</summary>
    List<byte[]> ChainReplies(int skill, long now)
    {
        var replies = new List<byte[]>();
        if (offeredChains.Remove(skill)) replies.Add(ChainDeactive([skill]));
        var offered = new List<int>();
        foreach (var (id, rate, ms) in Behaviour.FollowUps(skill))
        {
            if (rate < 10_000 && rng.Next(10_000) >= rate) continue;
            var until = now + ms + ChainCloseSlackMs;
            offeredChains[id] = until;
            offered.Add(id);
            Schedule(until, _ =>
            {
                if (!offeredChains.TryGetValue(id, out var still) || still != until) return [];
                offeredChains.Remove(id);
                return [ChainDeactive([id])];
            });
        }
        if (offered.Count > 0) replies.Add(ChainActive(skill, offered));
        return replies;
    }

    /// <summary>Charges of a Time auto-load skill, refilled up to now.</summary>
    (int Count, long RefillAt) ChargesNow(int skill, SkillBehaviour.Charges c) =>
        skillCharges.TryGetValue(skill, out var state) ? state : (c.Max, 0);

    int RefillMs(SkillBehaviour.Charges c, int count) => c.RefillMs.Length == 0 ? 10_000 : c.RefillMs[Math.Clamp(count, 0, c.RefillMs.Length - 1)];

    /// <summary>A charge used by a cast: the SkillStart's optional fields (charges left, time to the next refill), or null when none is left.</summary>
    (int Left, int? RefillIn)? UseCharge(int skill, long now)
    {
        if (Behaviour.ChargesOf(skill) is not { Timed: true } c) return (0, null);
        var (count, refillAt) = ChargesNow(skill, c);
        if (count <= 0) return null;
        count--;
        if (refillAt == 0)
        {
            refillAt = now + RefillMs(c, count);
            ScheduleRefill(skill, refillAt);
        }
        skillCharges[skill] = (count, refillAt);
        return (count, (int)(refillAt - now));
    }

    void ScheduleRefill(int skill, long at) => Schedule(at, now =>
    {
        if (Behaviour.ChargesOf(skill) is not { } c || !skillCharges.TryGetValue(skill, out var state) || state.RefillAt != at) return [];
        var count = Math.Min(c.Max, state.Count + 1);
        if (count >= c.Max)
        {
            skillCharges.Remove(skill);
            return [AutoLoadUpdate(skill, count, null)];
        }
        var next = now + RefillMs(c, count);
        skillCharges[skill] = (count, next);
        ScheduleRefill(skill, next);
        return [AutoLoadUpdate(skill, count, (int)(next - now))];
    });

    /// <summary>Full charges of the learned Time auto-load skills (sent at entry, as after the official SkillAdd).</summary>
    List<byte[]> AutoLoadAtEntry() =>
        learned.Where(s => Behaviour.ChargesOf(s) is { Timed: true }).Order()
            .Select(s => AutoLoadUpdate(s, ChargesNow(s, Behaviour.ChargesOf(s)!).Count, null)).ToList();

    void ResetChains()
    {
        offeredChains.Clear();
        skillCharges.Clear();
    }
}
