using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>Boss and monster AI data (gamedata/provisional/boss-ai.xml, Stage 6): opening, gap-close, rotations, HP phases, enrage.</summary>
public sealed class BossAiTable
{
    public sealed record Phase(int HpPercent, IReadOnlyList<int> Skills);
    public sealed record Boss(int Npc, IReadOnlyList<int> Rotation, IReadOnlyList<Phase> Phases, long BerserkMs, long BerserkOffsetMs);

    public int OpeningMinMs { get; private init; } = 1000;
    public int OpeningMaxMs { get; private init; } = 4000;
    public double GapCloseCm { get; private init; } = 1500;
    readonly Dictionary<int, Boss> bosses = [];
    readonly Dictionary<int, NpcSkill> skills = [];

    /// <summary>A boss skill declared in boss-ai.xml (not in any template's skill list).</summary>
    public NpcSkill? Skill(int id) => skills.GetValueOrDefault(id);

    static readonly Lazy<BossAiTable> embedded = new(() =>
    {
        using var stream = typeof(BossAiTable).Assembly.GetManifestResourceStream("provisional/boss-ai.xml")
            ?? throw new InvalidOperationException("Missing embedded game data provisional/boss-ai.xml");
        return Parse(XDocument.Load(stream));
    });

    public static BossAiTable Embedded => embedded.Value;

    public static BossAiTable Parse(XDocument document)
    {
        static long L(XElement? e, string n, long fallback) => long.TryParse((string?)e?.Attribute(n), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
        static List<int> Ids(string? text) => (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList();
        var d = document.Root?.Element("defaults");
        var table = new BossAiTable
        {
            OpeningMinMs = (int)Math.Max(0, L(d, "opening_min_ms", 1000)),
            OpeningMaxMs = (int)Math.Max(0, L(d, "opening_max_ms", 4000)),
            GapCloseCm = Math.Max(0, L(d, "gap_close_cm", 1500)),
        };
        foreach (var b in document.Root?.Elements("boss") ?? [])
        {
            var phases = b.Elements("phase").Select(p => new Phase((int)Math.Clamp(L(p, "hp_percent", 0), 0, 100), Ids((string?)p.Attribute("skills"))))
                .OrderByDescending(p => p.HpPercent).ToList();
            var boss = new Boss((int)L(b, "npc", 0), Ids((string?)b.Attribute("rotation")), phases, L(b, "berserk_ms", 0), L(b, "berserk_offset_ms", 0));
            table.bosses[boss.Npc] = boss;
        }
        foreach (var k in document.Root?.Elements("skill") ?? [])
            table.skills[(int)L(k, "id", 0)] = new NpcSkill(L(k, "range", 0), L(k, "range", 0), (int)L(k, "cooldown_ms", 0), (int)L(k, "cast_ms", 0),
                (int)L(k, "effect", 0), (int)L(k, "damage_min", 0), (int)L(k, "damage_max", 0));
        return table;
    }

    public Boss? Of(int npc) => bosses.GetValueOrDefault(npc);

    /// <summary>The template's skills in the boss's priority order (listed first, the rest in their own order).</summary>
    public IEnumerable<int> Ordered(int npc, IReadOnlyList<int> skills) =>
        Of(npc) is { Rotation.Count: > 0 } boss ? boss.Rotation.Where(skills.Contains).Concat(skills.Where(s => !boss.Rotation.Contains(s))) : skills;
}
