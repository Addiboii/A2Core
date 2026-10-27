using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Aion.Protocol.Native;

/// <summary>RC4 with resumable state: one keystream continues over every client body.</summary>
public sealed class Rc4
{
    readonly byte[] state = new byte[256];
    int i, j;

    public Rc4(ReadOnlySpan<byte> key)
    {
        if (key.IsEmpty) throw new ArgumentException("Empty RC4 key", nameof(key));
        for (var n = 0; n < 256; n++) state[n] = (byte)n;
        for (int n = 0, k = 0; n < 256; n++)
        {
            k = (k + state[n] + key[n % key.Length]) & 0xFF;
            (state[n], state[k]) = (state[k], state[n]);
        }
    }

    public byte[] Crypt(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        for (var n = 0; n < data.Length; n++)
        {
            i = (i + 1) & 0xFF;
            j = (j + state[i]) & 0xFF;
            (state[i], state[j]) = (state[j], state[i]);
            output[n] = (byte)(data[n] ^ state[(state[i] + state[j]) & 0xFF]);
        }
        return output;
    }
}

/// <summary>The client's advertised RSA public key (2048-bit, e=3 observed) and OAEP-SHA1 encryption to it.</summary>
public sealed record RsaPublicKey(BigInteger Modulus, BigInteger Exponent)
{
    public int Size => (int)((Modulus.GetBitLength() + 7) / 8);
    public long Bits => Modulus.GetBitLength();

    /// <summary>OAEP-SHA1 / MGF1-SHA1 / empty label (OpenSSL RSA_PKCS1_OAEP_PADDING).</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> message, Func<int, byte[]>? seeds = null)
    {
        seeds ??= RandomNumberGenerator.GetBytes;
        var encoded = OaepEncode(message, Size, seeds(20));
        if (new BigInteger(encoded, isUnsigned: true, isBigEndian: true) >= Modulus)
            encoded = OaepEncode(message, Size, seeds(20));  // one retry with a fresh seed
        var cipher = BigInteger.ModPow(new BigInteger(encoded, isUnsigned: true, isBigEndian: true), Exponent, Modulus);
        var bytes = cipher.ToByteArray(isUnsigned: true, isBigEndian: true);
        var output = new byte[Size];
        bytes.CopyTo(output, Size - bytes.Length);
        return output;
    }

    public static byte[] OaepEncode(ReadOnlySpan<byte> message, int size, ReadOnlySpan<byte> seed)
    {
        const int hlen = 20;
        if (message.Length < 2 || message.Length > size - 2 * hlen - 2) throw new ArgumentException("Message length outside OAEP-SHA1 capacity");
        if (seed.Length != hlen) throw new ArgumentException("OAEP seed must be 20 bytes");
        var db = new byte[size - hlen - 1];
        SHA1.HashData(ReadOnlySpan<byte>.Empty).CopyTo(db, 0);
        db[db.Length - message.Length - 1] = 1;
        message.CopyTo(db.AsSpan(db.Length - message.Length));
        var dbMask = Mgf1(seed, db.Length);
        for (var n = 0; n < db.Length; n++) db[n] ^= dbMask[n];
        var seedMask = Mgf1(db, hlen);
        var output = new byte[size];
        for (var n = 0; n < hlen; n++) output[1 + n] = (byte)(seed[n] ^ seedMask[n]);
        db.CopyTo(output, 1 + hlen);
        return output;
    }

    static byte[] Mgf1(ReadOnlySpan<byte> seed, int length)
    {
        var output = new byte[length];
        var input = new byte[seed.Length + 4];
        seed.CopyTo(input);
        for (int counter = 0, written = 0; written < length; counter++)
        {
            BinaryPrimitives.WriteInt32BigEndian(input.AsSpan(seed.Length), counter);
            var digest = SHA1.HashData(input);
            var count = Math.Min(digest.Length, length - written);
            digest.AsSpan(0, count).CopyTo(output.AsSpan(written));
            written += count;
        }
        return output;
    }
}

/// <summary>
/// Opening exchange of both native ports. The client greets in plaintext (login 0x3900, game
/// 0x3610): u16 tag, varint DER length, DER RSAPublicKey, then a short trailer. The server
/// answers with a self-generated 32-byte RC4 key, RSA-OAEP encrypted to that key (login
/// 0x3901, game 0x3611); afterwards client bodies are RC4 and server bodies plaintext.
/// </summary>
public static class NativeHandshake
{
    public const ushort LoginGreeting = 0x3900, GameGreeting = 0x3610;

    public static RsaPublicKey ReadGreeting(byte[] body, ushort expectedTag)
    {
        var reader = new PacketReader(body);
        if (reader.U16() != expectedTag) throw new ProtocolException($"First frame is not a 0x{expectedTag:x4} greeting");
        var der = reader.Take(checked((int)reader.Varint())).ToArray();
        var (tag, sequence, _) = DerTlv(der, 0);
        if (tag != 0x30) throw new ProtocolException("Not a DER sequence");
        var values = new List<BigInteger>();
        for (var offset = 0; offset < sequence.Length;)
        {
            (tag, var value, offset) = DerTlv(sequence, offset);
            if (tag != 2) throw new ProtocolException("Expected DER integer");
            values.Add(new BigInteger(value, isUnsigned: true, isBigEndian: true));
        }
        if (values.Count != 2) throw new ProtocolException("Expected modulus and exponent");
        if (values[0].GetBitLength() is < 1024 or > 8192 || values[1] < 3) throw new ProtocolException("Unsupported RSA public key");
        return new RsaPublicKey(values[0], values[1]);
    }

    static (int Tag, byte[] Value, int Next) DerTlv(byte[] data, int offset)
    {
        if (offset + 2 > data.Length) throw new ProtocolException("Truncated DER header");
        int tag = data[offset], length = data[offset + 1];
        offset += 2;
        if ((length & 0x80) != 0)
        {
            var width = length & 0x7F;
            if (width is < 1 or > 4 || offset + width > data.Length) throw new ProtocolException("Invalid DER length");
            if (data[offset] == 0) throw new ProtocolException("Noncanonical DER length");
            length = 0;
            for (var n = 0; n < width; n++) length = length << 8 | data[offset + n];
            offset += width;
            if (length < 128) throw new ProtocolException("Noncanonical DER length");
        }
        if (length < 0 || offset + length > data.Length) throw new ProtocolException("Truncated DER value");
        return (tag, data.AsSpan(offset, length).ToArray(), offset + length);
    }

    /// <summary>Framed 0x3901: result 0, parameter 60, varint cipher, 10 zero trailer bytes.</summary>
    public static byte[] LoginReply(RsaPublicKey key, ReadOnlySpan<byte> sessionKey, Func<int, byte[]>? seeds = null) =>
        NativeFraming.Frame(PacketWriter.Opcode(0x3901).U16(0).U16(60).Bytes(key.Encrypt(sessionKey, seeds)).Zeros(10).ToArray());

    /// <summary>Framed 0x3611: result 0, parameter 60000, one extra u16, varint cipher, 12 zero bytes.</summary>
    public static byte[] GameReply(RsaPublicKey key, ReadOnlySpan<byte> sessionKey, Func<int, byte[]>? seeds = null) =>
        NativeFraming.Frame(PacketWriter.Opcode(0x3611).U16(0).U16(60000).U16(0).Bytes(key.Encrypt(sessionKey, seeds)).Zeros(12).ToArray());
}
