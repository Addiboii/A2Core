using System.Buffers.Binary;
using System.Text;

namespace Aion.Protocol.Native;

/// <summary>
/// Little-endian message writer for the native ports. Bools share bytes as in the official
/// 0x3615: a bool takes the next bit of the open bool byte, or reserves a new byte at its own
/// position when none is open or all eight bits are used. The open byte carries across records.
/// </summary>
public sealed class PacketWriter
{
    byte[] buffer;
    int length;
    int boolAt = -1;
    int bits = 8;

    public PacketWriter(int capacity = 64) => buffer = new byte[Math.Max(16, capacity)];

    public PacketWriter(ReadOnlySpan<byte> prefix) : this(prefix.Length + 64) => Raw(prefix);

    public static PacketWriter Opcode(ushort opcode) => new PacketWriter().U16(opcode);

    public int Length => length;

    Span<byte> Grow(int count)
    {
        if (length + count > buffer.Length) Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + count));
        var span = buffer.AsSpan(length, count);
        length += count;
        return span;
    }

    public PacketWriter Raw(ReadOnlySpan<byte> data) { data.CopyTo(Grow(data.Length)); return this; }
    public PacketWriter Zeros(int count) { Grow(count).Clear(); return this; }
    public PacketWriter U8(int value) { Grow(1)[0] = (byte)value; return this; }
    public PacketWriter U16(int value) { BinaryPrimitives.WriteUInt16LittleEndian(Grow(2), (ushort)value); return this; }
    public PacketWriter I32(int value) { BinaryPrimitives.WriteInt32LittleEndian(Grow(4), value); return this; }
    public PacketWriter U32(uint value) { BinaryPrimitives.WriteUInt32LittleEndian(Grow(4), value); return this; }
    public PacketWriter I64(long value) { BinaryPrimitives.WriteInt64LittleEndian(Grow(8), value); return this; }
    public PacketWriter U64(ulong value) { BinaryPrimitives.WriteUInt64LittleEndian(Grow(8), value); return this; }
    public PacketWriter F32(double value) { BinaryPrimitives.WriteSingleLittleEndian(Grow(4), (float)value); return this; }
    public PacketWriter Vec3(Vector3d v) => F32(v.X).F32(v.Y).F32(v.Z);

    /// <summary>Canonical unsigned LEB128 (uint32).</summary>
    public PacketWriter Varint(long value)
    {
        if (value is < 0 or > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(value), "Varint exceeds uint32");
        var v = (uint)value;
        while (v >= 0x80) { U8((int)(v & 0x7F) | 0x80); v >>= 7; }
        return U8((int)v);
    }

    /// <summary>
    /// FString wire form: varint byte length + UTF-8 bytes, no terminator. The client's string reader (0x925ffe0) takes a
    /// LEB128 length (OBSERVED, research/local/notice-20261017); below 128 bytes it is the single byte used before.
    /// </summary>
    public PacketWriter String(string text)
    {
        var count = Encoding.UTF8.GetByteCount(text);
        if (count > 65535) throw new ArgumentException("String longer than 65535 bytes", nameof(text));
        Varint(count);
        Encoding.UTF8.GetBytes(text, Grow(count));
        return this;
    }

    /// <summary>Varint count + bytes (array&lt;ByteProperty&gt;).</summary>
    public PacketWriter Bytes(ReadOnlySpan<byte> data) => Varint(data.Length).Raw(data);

    public PacketWriter Bool(bool value)
    {
        if (bits == 8) { boolAt = length; U8(0); bits = 0; }
        if (value) buffer[boolAt] |= (byte)(1 << bits);
        bits++;
        return this;
    }

    public byte[] ToArray() => buffer.AsSpan(0, length).ToArray();
}

/// <summary>Bounds-checked little-endian reader; the packed bools mirror <see cref="PacketWriter"/>.</summary>
public sealed class PacketReader(ReadOnlyMemory<byte> data)
{
    int boolAt = -1, bits = 8;
    public int Offset { get; set; }
    public int Remaining => data.Length - Offset;
    public bool End => Offset == data.Length;

    public ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || Offset + count > data.Length) throw new IncompleteException("Message field exceeds body");
        var span = data.Span.Slice(Offset, count);
        Offset += count;
        return span;
    }

    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public float F32() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));
    public Vector3d Vec3() => new(F32(), F32(), F32());
    public uint Varint() { var value = NativeVarint.Read(data.Span, Offset, out var next); Offset = next; return value; }
    public string String() => new UTF8Encoding(false, true).GetString(Take(U8()));
    public byte[] Bytes() => Take(checked((int)Varint())).ToArray();

    public bool Bool()
    {
        if (bits == 8) { boolAt = Offset; Take(1); bits = 0; }
        return ((data.Span[boolAt] >> bits++) & 1) != 0;
    }
}

public class ProtocolException(string message) : Exception(message);
public sealed class IncompleteException(string message) : ProtocolException(message);

/// <summary>A world position (cm); kept as double, sent as three f32.</summary>
public readonly record struct Vector3d(double X, double Y, double Z)
{
    public static readonly Vector3d Zero = new(0, 0, 0);
    public static Vector3d operator -(Vector3d a, Vector3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    public double Distance2d(Vector3d other) => Math.Sqrt((X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y));
    public double DistanceSquared(Vector3d other) => (X - other.X) * (X - other.X) + (Y - other.Y) * (Y - other.Y) + (Z - other.Z) * (Z - other.Z);
}
