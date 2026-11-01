using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Item.dat ExtractionSkins mapped to Skin.dat ids (tools/data/item-decompose-skins.json).</summary>
public sealed class ItemDecomposeSkinsTables
{
    sealed record File([property: JsonPropertyName("items")] Dictionary<string, int> Items);

    readonly File file;
    ItemDecomposeSkinsTables(File file) => this.file = file;

    static readonly Lazy<ItemDecomposeSkinsTables> embedded = new(() =>
    {
        using var stream = typeof(ItemDecomposeSkinsTables).Assembly.GetManifestResourceStream("item-decompose-skins.json")
            ?? throw new InvalidOperationException("Missing embedded item-decompose-skins.json");
        return new ItemDecomposeSkinsTables(JsonSerializer.Deserialize<File>(stream)
            ?? throw new InvalidOperationException("Empty item-decompose-skins.json"));
    });

    public static ItemDecomposeSkinsTables Embedded => embedded.Value;

    public int Of(int templateId) =>
        file.Items.GetValueOrDefault(templateId.ToString(CultureInfo.InvariantCulture));
}
