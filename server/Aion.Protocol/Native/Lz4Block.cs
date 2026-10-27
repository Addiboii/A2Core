namespace Aion.Protocol.Native;

/// <summary>Raw LZ4 blocks (the 0xffff envelope). The encoder is the same naive matcher as
/// tools/aion_protocol.py lz4_block_encode, so both produce identical bytes.</summary>
public static class Lz4Block
{
    public static byte[] Decode(ReadOnlySpan<byte> data, int expectedSize, int maxSize = 4 * 1024 * 1024)
    {
        if (expectedSize < 0 || expectedSize > maxSize || data.Length > maxSize) throw new ProtocolException("LZ4 size exceeds configured bound");
        var output = new byte[expectedSize];
        int written = 0, offset = 0;
        while (offset < data.Length)
        {
            var token = Next(data, ref offset);
            var literals = Length(data, ref offset, token >> 4, expectedSize);
            if (written + literals > expectedSize || offset + literals > data.Length) throw new ProtocolException("LZ4 literals exceed declared output");
            data.Slice(offset, literals).CopyTo(output.AsSpan(written));
            offset += literals;
            written += literals;
            if (offset == data.Length) break;
            var distance = Next(data, ref offset) | Next(data, ref offset) << 8;
            if (distance < 1 || distance > written) throw new ProtocolException("LZ4 match offset outside decoded history");
            var match = Length(data, ref offset, token & 15, expectedSize) + 4;
            if (written + match > expectedSize) throw new ProtocolException("LZ4 match exceeds declared output");
            for (var i = 0; i < match; i++, written++) output[written] = output[written - distance];
        }
        if (written != expectedSize) throw new ProtocolException("LZ4 output differs from declared size");
        return output;
    }

    static int Next(ReadOnlySpan<byte> data, ref int offset)
    {
        if (offset >= data.Length) throw new ProtocolException("Truncated LZ4 block");
        return data[offset++];
    }

    static int Length(ReadOnlySpan<byte> data, ref int offset, int size, int expectedSize)
    {
        if (size != 15) return size;
        while (true)
        {
            var extra = Next(data, ref offset);
            size += extra;
            if (size > expectedSize) throw new ProtocolException("LZ4 run exceeds declared output");
            if (extra != 255) return size;
        }
    }

    public static byte[] Encode(ReadOnlySpan<byte> data)
    {
        var w = new PacketWriter(data.Length + 16);
        int size = data.Length, position = 0, literalStart = 0;
        while (position < size)
        {
            var match = 0;
            if (position + 4 <= size)
            {
                var candidate = data.Slice(position, 4);
                var searchStart = Math.Max(0, position - 65535);
                // Last occurrence wholly inside [searchStart, position), like bytes.rfind.
                for (var found = position - 4; found >= searchStart; found--)
                    if (data.Slice(found, 4).SequenceEqual(candidate)) { match = position - found; break; }
            }
            if (match == 0) { position++; continue; }
            var literalLength = position - literalStart;
            var matchLength = 4;
            while (position + matchLength < size && data[position + matchLength] == data[position - match + matchLength]) matchLength++;
            w.U8(Math.Min(literalLength, 15) << 4 | Math.Min(matchLength - 4, 15));
            if (literalLength >= 15) Extension(w, literalLength);
            w.Raw(data[literalStart..position]);
            w.U16(match);
            if (matchLength - 4 >= 15) Extension(w, matchLength - 4);
            position += matchLength;
            literalStart = position;
        }
        if (literalStart < size)
        {
            var literalLength = size - literalStart;
            w.U8(Math.Min(literalLength, 15) << 4);
            if (literalLength >= 15) Extension(w, literalLength);
            w.Raw(data[literalStart..size]);
        }
        return w.ToArray();
    }

    static void Extension(PacketWriter w, int length)
    {
        var remaining = length - 15;
        while (remaining >= 255) { w.U8(255); remaining -= 255; }
        w.U8(remaining);
    }
}
