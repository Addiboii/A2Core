using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.MappingsProvider;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Objects.UObject;

namespace AionClientEditor.Serialization;

public static class FAion2PropertyWriter
{
    public static void WritePropertyTagType(FAion2DatFileWriter Ar, FPropertyTagType value, TypeMappings mappings, string propertyType,
        FPropertyTagData? tagData, bool writeStructTag = true, ReadType type = ReadType.NORMAL)
    {
        switch (propertyType)
        {
            case "ArrayProperty":
                WriteArray(Ar, (ArrayProperty)value, mappings, tagData, writeStructTag);
                break;
            case "BoolProperty":
                Ar.Write(((BoolProperty)value).Value ? (byte)1 : (byte)0);
                break;
            case "ByteProperty":
                if (tagData?.EnumName is { } enumName && !enumName.Equals("None", StringComparison.OrdinalIgnoreCase))
                    Ar.WriteUnencryptedFString(((EnumProperty)value).Value.Text);
                else
                    Ar.Write(((ByteProperty)value).Value);
                break;
            case "DoubleProperty":
                Ar.Write(((DoubleProperty)value).Value);
                break;
            case "EnumProperty":
                Ar.WriteUnencryptedFString(((EnumProperty)value).Value.Text);
                break;
            case "FloatProperty":
                Ar.Write(((FloatProperty)value).Value);
                break;
            case "Int16Property":
                Ar.Write(((Int16Property)value).Value);
                break;
            case "Int64Property":
                Ar.Write(((Int64Property)value).Value);
                break;
            case "Int8Property":
                Ar.Write(((Int8Property)value).Value);
                break;
            case "IntProperty":
                Ar.Write(((IntProperty)value).Value);
                break;
            case "NameProperty":
                Ar.WriteAion2String(((NameProperty)value).Value.Text);
                break;
            case "SetProperty":
                WriteSet(Ar, (SetProperty)value, mappings, tagData);
                break;
            case "MapProperty":
                WriteMap(Ar, (MapProperty)value, mappings, tagData);
                break;
            case "StrProperty":
                Ar.WriteUnencryptedFString(((StrProperty)value).Value ?? "");
                break;
            case "StructProperty":
                WriteStruct(Ar, (StructProperty)value, mappings, tagData?.StructType);
                break;
            case "TextProperty":
                throw new NotSupportedException("TextProperty write is not implemented yet");
            case "UInt16Property":
                Ar.Write(((UInt16Property)value).Value);
                break;
            case "UInt32Property":
                Ar.Write(((UInt32Property)value).Value);
                break;
            case "UInt64Property":
                Ar.Write(((UInt64Property)value).Value);
                break;
            default:
                throw new NotSupportedException($"Unsupported property type {propertyType}");
        }
    }

    static void WriteStructTag(FAion2DatFileWriter Ar)
    {
        Ar.WriteUnencryptedFString("None");
        Ar.WriteUnencryptedFString("None");
        Ar.Write(0L);
        Ar.WriteUnencryptedFString("None");
        Ar.Write(new byte[16]);
        Ar.Write((byte)0);
    }

    static void WriteSet(FAion2DatFileWriter Ar, SetProperty value, TypeMappings mappings, FPropertyTagData? tagData)
    {
        var items = value.Value!.Properties;
        Ar.Write(items.Count);
        foreach (var item in items)
            WritePropertyTagType(Ar, item, mappings, tagData?.InnerType ?? "StructProperty", tagData?.InnerTypeData, true, ReadType.ARRAY);
        WriteArrayBody(Ar, new UScriptArray(items, tagData?.InnerType ?? "StructProperty", tagData?.InnerTypeData), mappings, tagData, writeStructTag: true);
    }

    static void WriteArray(FAion2DatFileWriter Ar, ArrayProperty value, TypeMappings mappings, FPropertyTagData? tagData, bool writeStructTag) =>
        WriteArrayBody(Ar, value.Value!, mappings, tagData, writeStructTag);

    static void WriteArrayBody(FAion2DatFileWriter Ar, UScriptArray value, TypeMappings mappings, FPropertyTagData? tagData, bool writeStructTag)
    {
        var items = value.Properties;
        Ar.Write(items.Count);
        if (writeStructTag && tagData?.InnerType is "StructProperty")
            WriteStructTag(Ar);
        foreach (var item in items)
            WritePropertyTagType(Ar, item, mappings, tagData?.InnerType ?? "StructProperty", tagData?.InnerTypeData, writeStructTag, ReadType.ARRAY);
    }

    static void WriteMap(FAion2DatFileWriter Ar, MapProperty value, TypeMappings mappings, FPropertyTagData? tagData)
    {
        var map = value.Value!.Properties;
        Ar.Write(map.Count);
        foreach (var key in map.Keys)
            WritePropertyTagType(Ar, key, mappings, tagData?.InnerType ?? "StrProperty", tagData?.InnerTypeData, false, ReadType.MAP);
        Ar.Write(map.Count);
        foreach (var pair in map)
        {
            WritePropertyTagType(Ar, pair.Key, mappings, tagData?.InnerType ?? "StrProperty", tagData?.InnerTypeData, false, ReadType.MAP);
            WritePropertyTagType(Ar, pair.Value!, mappings, tagData?.ValueType ?? "StrProperty", tagData?.ValueTypeData, false, ReadType.MAP);
        }
    }

    static void WriteStruct(FAion2DatFileWriter Ar, StructProperty value, TypeMappings mappings, string? structName)
    {
        if (structName is null) throw new InvalidOperationException("Struct name is missing");
        if (value.Value!.StructType is not FStructFallback fallback)
            throw new NotSupportedException($"Struct type {value.Value.StructType?.GetType().Name} is not supported");

        if (structName is "ItemTableItem")
        {
            foreach (var tag in fallback.Properties)
                WritePropertyTag(Ar, tag, mappings);
            var itemTypeTag = fallback.Properties.FirstOrDefault(x => x.Name.Text == "ItemType");
            if (itemTypeTag?.Tag is { } itemType && itemType.GetValue(typeof(FAion2PropertyReader.EItemType)) is FAion2PropertyReader.EItemType val)
            {
                var extraName = val switch
                {
                    FAion2PropertyReader.EItemType.Currency => "ItemTableCurrencyInfo",
                    FAion2PropertyReader.EItemType.Equip => "ItemTableEquipmentInfo",
                    FAion2PropertyReader.EItemType.Usable => "ItemTableUsableInfo",
                    FAion2PropertyReader.EItemType.Misc => "ItemTableMiscInfo",
                    _ => null,
                };
                if (extraName is not null)
                {
                    foreach (var tag in fallback.Properties.Where(p => p.TagData?.StructType == extraName))
                        WritePropertyTag(Ar, tag, mappings);
                }
            }
            return;
        }

        if (mappings.Types.TryGetValue(structName, out var propMappings))
        {
            foreach (var index in Enumerable.Range(0, propMappings.CountProperties(true)))
            {
                propMappings.TryGetValue(index, out var info);
                var tag = fallback.Properties.FirstOrDefault(p => p.Name.Text == info.Name && p.ArrayIndex == info.Index)
                    ?? throw new InvalidOperationException($"Missing struct field {info.Name}[{info.Index}]");
                WritePropertyTag(Ar, tag, mappings);
            }
            return;
        }

        foreach (var tag in fallback.Properties)
            WritePropertyTag(Ar, tag, mappings);
    }

    public static void WritePropertyTag(FAion2DatFileWriter Ar, FPropertyTag tag, TypeMappings mappings) =>
        WritePropertyTagType(Ar, tag.Tag!, mappings, tag.PropertyType.Text, tag.TagData, true);
}
