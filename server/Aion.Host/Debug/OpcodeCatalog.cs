using System.Reflection;
using System.Text.Json;

namespace Aion.Host.Debug;

/// <summary>Opcode hex to struct name (tools/data/opcode-names.json, embedded in the host).</summary>
public sealed class OpcodeCatalog
{
    readonly Dictionary<ushort, string> names;

    OpcodeCatalog(Dictionary<ushort, string> names) => this.names = names;

    public static OpcodeCatalog Load()
    {
        var assembly = typeof(OpcodeCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("opcode-names.json")
            ?? throw new InvalidOperationException("Embedded opcode-names.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        var map = new Dictionary<ushort, string>();
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (!prop.Name.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ushort.TryParse(prop.Name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var opcode)) continue;
            map[opcode] = prop.Value.GetString() ?? prop.Name;
        }
        return new OpcodeCatalog(map);
    }

    public string Name(ushort opcode) => names.TryGetValue(opcode, out var name) ? name : "?";

    public string Label(ushort opcode) => $"0x{opcode:x4} {Name(opcode)}";
}
