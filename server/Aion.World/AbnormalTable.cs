using System.Globalization;
using System.Text.Json;

namespace Aion.World;

/// <summary>One SkillAbnormalEffect row: its id (what 0x3805 carries), EAbnormalEffectType and raw values.</summary>
public sealed record AbnormalEffectRow(int Id, string Type, IReadOnlyList<string> Values)
{
    public int Int(int i) => i < Values.Count && int.TryParse(Values[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>A SkillAbnormal row: group, type, how a second application overlaps, its effects.</summary>
public sealed record AbnormalRow(int Id, int Group, string Type, string OverlapTime, int OverlapCount, string Replace, IReadOnlyList<AbnormalEffectRow> Effects)
{
    /// <summary>The first damage-over-time effect (Dot_NormalCalc / Dot_Dmg / Dot_TargetHP / Dot_TargetMaxHP): values[0] = tick interval ms.</summary>
    public AbnormalEffectRow? Dot => Effects.FirstOrDefault(e => e.Type.StartsWith("Dot_", StringComparison.Ordinal));
}

/// <summary>
/// The client SkillAbnormal / SkillAbnormalEffect rows of the abnormals the skills apply (tools/abnormal_table.py ->
/// tools/data/abnormal-table.json, OFFICIAL client data).
/// </summary>
public sealed class AbnormalTable
{
    readonly Dictionary<int, AbnormalRow> rows = [];

    AbnormalTable(JsonElement root)
    {
        foreach (var p in root.GetProperty("abnormals").EnumerateObject())
        {
            var e = p.Value;
            var effects = e.GetProperty("effects").EnumerateArray().Select(r =>
            {
                var a = r.EnumerateArray().ToList();
                return new AbnormalEffectRow(a[0].GetInt32(), a[1].GetString() ?? "", a.Skip(2).Select(v => v.GetString() ?? "").ToList());
            }).ToList();
            var id = int.Parse(p.Name, CultureInfo.InvariantCulture);
            rows[id] = new AbnormalRow(id, e.GetProperty("group").GetInt32(), e.GetProperty("type").GetString() ?? "", e.GetProperty("overlap_time").GetString() ?? "",
                e.GetProperty("overlap_count").GetInt32(), e.GetProperty("replace").GetString() ?? "", effects);
        }
    }

    static readonly Lazy<AbnormalTable> embedded = new(() =>
    {
        using var stream = typeof(AbnormalTable).Assembly.GetManifestResourceStream("abnormal-table.json")
            ?? throw new InvalidOperationException("Missing embedded game data abnormal-table.json");
        using var doc = JsonDocument.Parse(stream);
        return new AbnormalTable(doc.RootElement);
    });

    public static AbnormalTable Embedded => embedded.Value;
    public AbnormalRow? Of(int abnormalId) => rows.GetValueOrDefault(abnormalId);
    public int Count => rows.Count;

    /// <summary>
    /// 0x382b show_msg for a second application: 0 for abnormals that stack and renew silently (AbnormalOverlapTimeType Renew:
    /// official 118000071, 187300011, 130000011 always 0), else 1 (87% of 32,294 official samples). CANDIDATE rule
    /// (167800011, also Renew, was 1 in 149 of 826).
    /// </summary>
    public bool ShowChange(int abnormalId) => Of(abnormalId)?.OverlapTime != "Renew";

    /// <summary>
    /// The ImmuneAbnormal effect of <paramref name="immunity"/> that blocks <paramref name="abnormalId"/>: values[5] lists the
    /// effect types it blocks (10000001's 100000010: Stun, Hold, KnockdownInstant, Move_CannotUseSkill...). An abnormal the table
    /// does not know counts as blocked when it is crowd control (<paramref name="crowdControl"/>).
    /// </summary>
    public AbnormalEffectRow? Blocking(int immunity, int abnormalId, bool crowdControl)
    {
        if (Of(immunity) is not { } shield) return null;
        var types = Of(abnormalId)?.Effects.Select(e => e.Type).ToHashSet();
        foreach (var e in shield.Effects.Where(e => e.Type == "ImmuneAbnormal" && e.Values.Count > 5))
        {
            var blocked = e.Values[5].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (types is null ? crowdControl : types.Overlaps(blocked)) return e;
        }
        return null;
    }

    /// <summary>The NPC crowd-control immunity of the official recordings: abnormal 10000001, effect 100000010 (uid 1 on the NPC).</summary>
    public const int CrowdControlImmunity = 10000001;
}
