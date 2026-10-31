using System.Text;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;

namespace AionClientEditor.Serialization;

/// <summary>Binary writer mirroring <see cref="CUE4Parse.GameTypes.Aion2.Objects.FAion2DatFileArchive"/> string rules.</summary>
public sealed class FAion2DatFileWriter(Stream stream, VersionContainer versions) : BinaryWriter(stream, Encoding.UTF8, leaveOpen: true)
{
    private static readonly byte[] XorKeyLong = [0x25, 0x00, 0xa8, 0x00, 0x7e, 0x00, 0x91, 0x00];
    private static readonly byte[] XorKeyShort = [0x25, 0xa8, 0x7e, 0x91];

    public VersionContainer Versions { get; } = versions;

    public void WriteAion2String(string value, bool l10n = false)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var xorKey = l10n ? XorKeyShort : null;
        var utf8 = Encoding.UTF8.GetBytes(value + "\0");
        var buffer = utf8.AsSpan().ToArray();
        if (xorKey is not null) TensorUtils.Xor(buffer, xorKey);
        Write(buffer.Length);
        Write(buffer);
    }

    public void WriteUnencryptedFString(string value)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        Write(bytes.Length);
        Write(bytes);
    }

    public void WriteUnicodeAion2String(string value)
    {
        if (value.Length == 0)
        {
            Write(0);
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value + "\0");
        TensorUtils.Xor(bytes, XorKeyLong);
        Write(-bytes.Length / 2);
        Write(bytes);
    }

    public void WriteL10NString(string value) => WriteAion2String(value, l10n: true);

    public void WriteMap<TKey, TValue>(IReadOnlyDictionary<TKey, TValue> map, Action<TKey> writeKey, Action<TValue> writeValue)
        where TKey : notnull
    {
        Write(map.Count);
        foreach (var pair in map)
        {
            writeKey(pair.Key);
            writeValue(pair.Value);
        }
    }
}
