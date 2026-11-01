using System.Globalization;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;

namespace AionClientEditor.Editing;

public enum PropertyEditKind
{
    ReadOnly,
    Integer,
    Float,
    Double,
    Bool,
    String,
    Name,
    Enum,
    JsonComplex,
}

public static class PropertyValueFormat
{
    static readonly JsonSerializerSettings JsonSettings = new() { Formatting = Formatting.Indented };

    public static PropertyEditKind EditKindOf(FPropertyTag tag)
    {
        if (tag.Tag is null) return PropertyEditKind.ReadOnly;
        return tag.PropertyType.Text switch
        {
            "IntProperty" or "Int8Property" or "Int16Property" or "Int64Property"
                or "UInt16Property" or "UInt32Property" or "UInt64Property" => PropertyEditKind.Integer,
            "FloatProperty" => PropertyEditKind.Float,
            "DoubleProperty" => PropertyEditKind.Double,
            "BoolProperty" => PropertyEditKind.Bool,
            "StrProperty" => PropertyEditKind.String,
            "NameProperty" => PropertyEditKind.Name,
            "EnumProperty" => PropertyEditKind.Enum,
            "ByteProperty" when tag.TagData?.EnumName is { Length: > 0 } n && !n.Equals("None", StringComparison.OrdinalIgnoreCase)
                => PropertyEditKind.Enum,
            "ByteProperty" => PropertyEditKind.Integer,
            _ => PropertyEditKind.JsonComplex,
        };
    }

    public static string FormatCell(FPropertyTag tag)
    {
        if (tag.Tag is null) return "";
        return EditKindOf(tag) switch
        {
            PropertyEditKind.JsonComplex => CompactJson(JsonConvert.SerializeObject(tag.Tag)),
            _ => FormatScalar(tag) ?? CompactJson(JsonConvert.SerializeObject(tag.Tag)),
        };
    }

    public static string FormatDetails(FPropertyTag tag) =>
        tag.Tag is null ? "" : JsonConvert.SerializeObject(tag.Tag, JsonSettings);

    public static bool TryApply(FPropertyTag tag, string text, out string? error)
    {
        error = null;
        if (tag.Tag is null)
        {
            error = "Property has no value";
            return false;
        }

        try
        {
            switch (EditKindOf(tag))
            {
                case PropertyEditKind.Integer:
                    ApplyInteger(tag, text);
                    return true;
                case PropertyEditKind.Float:
                    if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                    { error = "Expected a float"; return false; }
                    tag.Tag = new FloatProperty(f);
                    return true;
                case PropertyEditKind.Double:
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    { error = "Expected a double"; return false; }
                    tag.Tag = new DoubleProperty(d);
                    return true;
                case PropertyEditKind.Bool:
                    tag.Tag = new BoolProperty(ParseBool(text));
                    return true;
                case PropertyEditKind.String:
                    tag.Tag = new StrProperty(text);
                    return true;
                case PropertyEditKind.Name:
                    tag.Tag = new NameProperty(new FName(text));
                    return true;
                case PropertyEditKind.Enum:
                    tag.Tag = new EnumProperty(new FName(text));
                    return true;
                default:
                    error = "This column is read-only; edit scalar fields in the grid.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    static void ApplyInteger(FPropertyTag tag, string text)
    {
        var kind = tag.Tag?.GetType().Name ?? tag.PropertyType.Text;
        switch (kind)
        {
            case nameof(IntProperty):
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    throw new FormatException("Expected an integer");
                tag.Tag = new IntProperty(i);
                return;
            case nameof(Int8Property):
                if (!sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i8))
                    throw new FormatException("Expected an integer");
                tag.Tag = new Int8Property(i8);
                return;
            case nameof(Int16Property):
                if (!short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i16))
                    throw new FormatException("Expected an integer");
                tag.Tag = new Int16Property(i16);
                return;
            case nameof(Int64Property):
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i64))
                    throw new FormatException("Expected an integer");
                tag.Tag = new Int64Property(i64);
                return;
            case nameof(UInt16Property):
                if (!ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u16))
                    throw new FormatException("Expected an integer");
                tag.Tag = new UInt16Property(u16);
                return;
            case nameof(UInt32Property):
                if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u32))
                    throw new FormatException("Expected an integer");
                tag.Tag = new UInt32Property(u32);
                return;
            case nameof(UInt64Property):
                if (!ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var u64))
                    throw new FormatException("Expected an integer");
                tag.Tag = new UInt64Property(u64);
                return;
            case nameof(ByteProperty):
                if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
                    throw new FormatException("Expected an integer");
                tag.Tag = new ByteProperty(b);
                return;
            default:
                throw new NotSupportedException($"Integer type {kind} is not supported");
        }
    }

    static string? FormatScalar(FPropertyTag tag) => tag.Tag switch
    {
        IntProperty ip => ip.Value.ToString(CultureInfo.InvariantCulture),
        Int8Property i8 => i8.Value.ToString(CultureInfo.InvariantCulture),
        Int16Property i16 => i16.Value.ToString(CultureInfo.InvariantCulture),
        Int64Property i64 => i64.Value.ToString(CultureInfo.InvariantCulture),
        UInt16Property u16 => u16.Value.ToString(CultureInfo.InvariantCulture),
        UInt32Property u32 => u32.Value.ToString(CultureInfo.InvariantCulture),
        UInt64Property u64 => u64.Value.ToString(CultureInfo.InvariantCulture),
        ByteProperty bp => bp.Value.ToString(CultureInfo.InvariantCulture),
        FloatProperty fp => fp.Value.ToString(CultureInfo.InvariantCulture),
        DoubleProperty dp => dp.Value.ToString(CultureInfo.InvariantCulture),
        BoolProperty bp => bp.Value ? "true" : "false",
        StrProperty sp => sp.Value ?? "",
        NameProperty np => np.Value.Text,
        EnumProperty ep => ep.Value.Text,
        _ => null,
    };

    static bool ParseBool(string text) => text.Trim() switch
    {
        "1" or "true" or "True" or "yes" or "Yes" => true,
        "0" or "false" or "False" or "no" or "No" => false,
        _ => bool.Parse(text),
    };

    static string CompactJson(string json) =>
        json.ReplaceLineEndings(" ").Length <= 120 ? json.ReplaceLineEndings(" ") : json.ReplaceLineEndings(" ")[..117] + "…";
}
