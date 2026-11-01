using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Blake3;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Exceptions;
using CUE4Parse.Utils;
using K4os.Compression.LZ4;

namespace CUE4Parse.GameTypes.Aion2.Encryption.Aes;

public sealed partial class Aion2DatFileEncryption
{
    private static readonly byte[] NonceSuffix = Encoding.ASCII.GetBytes("nonc");

    public static byte[] EncryptDataTable(byte[] plaintext, string filePath)
    {
        var seed = Blake3Hash(Path.GetFileNameWithoutExtension(filePath.Replace('\\', '/')));
        if (!AesKeys.TryGetValue(seed, out var key))
            throw new ParserException($"No content key for {filePath}");

        return plaintext.Length <= 4096
            ? EncryptStreamDataTable(plaintext, seed, key)
            : EncryptCompressedDataTable(plaintext, seed, key);
    }

    public static byte[] EncryptL10N(byte[] plaintext, string filePath)
    {
        var parts = filePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new ParserException($"Unable to derive L10N locale from '{filePath}'");
        var seed = Blake3Hash($"L10NString_{parts[^2]}");
        if (!AesKeys.TryGetValue(seed, out var key))
            throw new ParserException("L10N encryption key not found in manifest");

        var payload = EncryptCompressedPayload(plaintext, key, out var packedSize);
        var header = new byte[0x10];
        BinaryPrimitives.WriteInt32LittleEndian(header, packedSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), (int)EEncryptionType.CompressedAES);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), plaintext.Length);
        FAion2DatFileArchive.DecryptData(header, DeriveHeaderKey(seed, EHeaderType.Localization));

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(2);
        writer.Write(header);
        writer.Write(payload);
        return output.ToArray();
    }

    static byte[] EncryptStreamDataTable(byte[] plaintext, ulong seed, FAesKey key)
    {
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, plaintext.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), (int)EEncryptionType.XorAES);
        FAion2DatFileArchive.DecryptData(prefix, DeriveHeaderKey(seed, EHeaderType.StreamDataTable));

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(13);
        writer.Write(prefix);
        writer.Write(EncryptStreamPayload(plaintext, key));
        return output.ToArray();
    }

    static byte[] EncryptStreamPayload(byte[] plaintext, FAesKey key)
    {
        using var nonceHasher = Hasher.New();
        nonceHasher.Update(key.Key);
        nonceHasher.Update(NonceSuffix);
        var nonce = BinaryPrimitives.ReadUInt64LittleEndian(nonceHasher.Finalize().AsSpan());

        var aligned = plaintext.Length.Align(16);
        var padded = new byte[aligned];
        plaintext.CopyTo(padded, 0);

        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key.Key, null);
        var counter = new byte[16];
        var stream = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(counter, nonce);
        var encrypted = new byte[aligned];
        for (var offset = 0; offset < aligned; offset += 16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(counter.AsSpan(8), (ulong)offset / 16);
            encryptor.TransformBlock(counter, 0, 16, stream, 0);
            var blockSize = Math.Min(16, aligned - offset);
            TensorUtils.Xor(padded.AsSpan(offset, blockSize), stream.AsSpan(0, blockSize), encrypted.AsSpan(offset, blockSize));
        }
        return encrypted[..plaintext.Length];
    }

    static byte[] EncryptCompressedDataTable(byte[] plaintext, ulong seed, FAesKey key)
    {
        var payload = EncryptCompressedPayload(plaintext, key, out var packedSize);
        var prefix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, packedSize);
        BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), (int)EEncryptionType.CompressedAES);
        var compressedKey = DeriveHeaderKey(seed, EHeaderType.CompressedDataTable);
        FAion2DatFileArchive.DecryptData(prefix, compressedKey);

        var suffix = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(suffix, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(suffix.AsSpan(4), plaintext.Length);
        FAion2DatFileArchive.DecryptData(suffix, compressedKey[8..16]);

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(13);
        writer.Write(prefix);
        writer.Write(suffix);
        writer.Write(payload);
        return output.ToArray();
    }

    static byte[] EncryptCompressedPayload(byte[] plaintext, FAesKey key, out int packedSize)
    {
        var lz4 = new byte[LZ4Codec.MaximumOutputSize(plaintext.Length)];
        packedSize = LZ4Codec.Encode(plaintext, 0, plaintext.Length, lz4, 0, lz4.Length);
        var body = new byte[0x20 + packedSize];
        lz4.AsSpan(0, packedSize).CopyTo(body.AsSpan(0x20));
        var aligned = body.Length.Align(16);
        var padded = new byte[aligned];
        body.CopyTo(padded, 0);
        EncryptAesEcbInPlace(padded, key);
        return padded;
    }

    static void EncryptAesEcbInPlace(byte[] data, FAesKey key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key.Key, null);
        var written = encryptor.TransformBlock(data, 0, data.Length, data, 0);
        if (written != data.Length) throw new CryptographicException("AES-ECB encryption failed");
    }
}
