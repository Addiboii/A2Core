using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Agit decoration items from tools/data/agit_deco.json (tools/agit_deco_data.py).</summary>
public sealed class AgitDecoTables
{
    sealed record Row([property: JsonPropertyName("deco_id")] int DecoId, [property: JsonPropertyName("name")] string Name);
    sealed record File([property: JsonPropertyName("by_template")] Dictionary<string, Row> ByTemplate);

    readonly Dictionary<int, Row> byTemplate;

    AgitDecoTables(File file) => byTemplate = file.ByTemplate.ToDictionary(kv => int.Parse(kv.Key), kv => kv.Value);

    public static AgitDecoTables Embedded { get; } = Load();

    static AgitDecoTables Load()
    {
        using var stream = typeof(AgitDecoTables).Assembly.GetManifestResourceStream("agit_deco.json")
            ?? throw new InvalidOperationException("Missing embedded game data agit_deco.json");
        return new AgitDecoTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty agit_deco.json"));
    }

    public bool TryDecoId(int templateId, out int decoId)
    {
        if (byTemplate.TryGetValue(templateId, out var row)) { decoId = row.DecoId; return true; }
        decoId = 0;
        return false;
    }
}
