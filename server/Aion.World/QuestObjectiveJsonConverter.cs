using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>
/// Quest objectives use <c>value</c> as an int (EnterVolumePC radius) or a string array (KillPCAbyssGrade, …).
/// </summary>
sealed class QuestObjectiveJsonConverter : JsonConverter<QuestObjective>
{
    public override QuestObjective Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var o = doc.RootElement;
        int? value = null;
        string[]? valueTokens = null;
        if (o.TryGetProperty("value", out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt32(out var n)) value = n;
            else if (val.ValueKind == JsonValueKind.Array)
                valueTokens = val.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToArray();
        }
        return new QuestObjective(
            Type: o.GetProperty("type").GetString() ?? "",
            Npc: o.TryGetProperty("npc", out var npc) && npc.ValueKind == JsonValueKind.Number ? npc.GetInt32() : null,
            Npcs: ReadIntArray(o, "npcs"),
            Item: o.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Number ? item.GetInt32() : null,
            Target: o.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.String ? target.GetString() : null,
            Count: o.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt32() : 1,
            Optional: o.TryGetProperty("optional", out var opt) && opt.ValueKind == JsonValueKind.True,
            Level: o.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.Number ? level.GetInt32() : null,
            Envs: ReadIntArray(o, "envs"),
            EnvNames: ReadStringArray(o, "env_names"),
            Value: value,
            ValueTokens: valueTokens,
            Chance: o.TryGetProperty("chance", out var chance) && chance.ValueKind == JsonValueKind.Number ? chance.GetInt32() : 0,
            Map: o.TryGetProperty("map", out var map) && map.ValueKind == JsonValueKind.Number ? map.GetInt32() : null,
            MovePoint: o.TryGetProperty("move_point", out var move) && move.ValueKind == JsonValueKind.String ? move.GetString() : null);
    }

    static int[]? ReadIntArray(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Number).Select(e => e.GetInt32()).ToArray();
    }

    static string[]? ReadStringArray(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        return arr.EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
    }

    public override void Write(Utf8JsonWriter writer, QuestObjective value, JsonSerializerOptions options) =>
        throw new NotSupportedException("quests.json is read-only at runtime");
}
