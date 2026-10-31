using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>GodStone.dat summary (tools/data/godstones.json, tools/godstone_data.py).</summary>
public sealed class GodstoneTables
{
    public sealed record Proc(int Chance, int CooldownMs, string Kind, IReadOnlyDictionary<string, JsonElement> Fields);

    public sealed record Stone(string Name, int Grade, Proc Proc);

    sealed record File([property: JsonPropertyName("stones")] Dictionary<string, JsonElement> Stones);

    readonly Dictionary<int, Stone> stones;

    GodstoneTables(File file)
    {
        stones = [];
        foreach (var (key, row) in file.Stones)
        {
            if (!int.TryParse(key, out var id)) continue;
            var name = row.GetProperty("name").GetString() ?? "";
            var grade = row.GetProperty("grade").GetInt32();
            var proc = row.GetProperty("proc");
            var kind = proc.GetProperty("kind").GetString() ?? "";
            var fields = proc.EnumerateObject().Where(p => p.Name is not ("chance" or "cooldown_ms" or "kind"))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            var stone = new Stone(name, grade, new Proc(proc.GetProperty("chance").GetInt32(), proc.GetProperty("cooldown_ms").GetInt32(), kind, fields));
            stones[id] = stone;
        }
    }

    static readonly Lazy<GodstoneTables> embedded = new(() =>
    {
        using var stream = typeof(GodstoneTables).Assembly.GetManifestResourceStream("godstones.json")
            ?? throw new InvalidOperationException("Missing embedded game data godstones.json");
        return new GodstoneTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty godstones.json"));
    });

    public static GodstoneTables Embedded => embedded.Value;

    public Stone? Of(int id) => stones.GetValueOrDefault(id);

    public bool IsGodstoneItem(int templateId) => stones.ContainsKey(templateId);

    public int ProcDamage(Stone stone)
    {
        if (stone.Proc.Kind is "damage") return stone.Proc.Fields.GetValueOrDefault("amount").GetInt32();
        if (stone.Proc.Kind is "aoe_damage") return stone.Proc.Fields.GetValueOrDefault("amount").GetInt32();
        if (stone.Proc.Kind is "dot") return stone.Proc.Fields.GetValueOrDefault("tick").GetInt32();
        return 0;
    }

    public int ProcEffectId(Stone stone) => stone.Proc.Fields.GetValueOrDefault("effect_id").ValueKind == JsonValueKind.Number
        ? stone.Proc.Fields["effect_id"].GetInt32() : 900_000 + stone.Grade * 10_000 + Math.Abs(stone.Name.GetHashCode()) % 1000;

    public int? ProcAbnormalId(Stone stone) => stone.Proc.Fields.GetValueOrDefault("abnormal_id").ValueKind == JsonValueKind.Number
        ? stone.Proc.Fields["abnormal_id"].GetInt32() : null;

    public int ProcDurationMs(Stone stone) => stone.Proc.Fields.GetValueOrDefault("duration_ms").ValueKind == JsonValueKind.Number
        ? stone.Proc.Fields["duration_ms"].GetInt32() : 3000;

    public IEnumerable<int> StonesOfGrade(int grade) => stones.Where(p => p.Value.Grade == grade).Select(p => p.Key);
}
