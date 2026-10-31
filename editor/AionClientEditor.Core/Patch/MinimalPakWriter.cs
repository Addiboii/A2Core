using System.Security.Cryptography;
using System.Text;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Pak.Objects;
using CUE4Parse.UE4.Versions;
using CUE4Parse.Utils;
using static CUE4Parse.UE4.Pak.Objects.EPakFileVersion;

namespace AionClientEditor.Patch;

/// <summary>Minimal UE pak writer (v8 index, no compression) sufficient for AION2 patch paks in tests.</summary>
static class MinimalPakWriter
{
    public sealed record Entry(string Path, byte[] Bytes);

    public static void Write(string outputPath, string mountPoint, IReadOnlyList<Entry> entries, FAesKey aesKey, EGame game)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);

        long offset = 0;
        var encodedEntries = new List<(string Path, long Offset, long Size, byte[] Stored)>();
        foreach (var entry in entries)
        {
            var plain = entry.Bytes;
            var stored = plain.Length.Align(16);
            var buffer = new byte[stored];
            plain.CopyTo(buffer, 0);
            buffer.EncryptInPlace(aesKey);
            writer.Write(buffer);
            encodedEntries.Add((entry.Path, offset, plain.Length, buffer));
            offset += stored;
        }

        var index = BuildIndex(mountPoint, encodedEntries, game);
        var indexOffset = stream.Position;
        writer.Write(index);
        var indexSize = stream.Position - indexOffset;

        using var sha1 = SHA1.Create();
        var indexHash = sha1.ComputeHash(index);

        // FPakInfo trailer (v8)
        writer.Write((int)PakFile_Version_FNameBasedCompressionMethod);
        writer.Write(indexOffset);
        writer.Write(indexSize);
        writer.Write(indexHash);
        writer.Write((byte)0); // EncryptedIndex
        writer.Write(new byte[16]); // EncryptionKeyGuid
        foreach (var method in new[] { CompressionMethod.None })
            writer.Write(PadMethod(method.ToString()));
        writer.Write(FPakInfo.PAK_FILE_MAGIC);
    }

    static byte[] BuildIndex(string mountPoint, List<(string Path, long Offset, long Size, byte[] Stored)> entries, EGame game)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        w.Write(mountPoint);
        w.Write(entries.Count);
        foreach (var (path, fileOffset, size, stored) in entries)
        {
            w.Write(new byte[20]); // hash placeholder
            WriteEncodedEntry(w, path, fileOffset, size, encrypted: true);
        }
        return ms.ToArray();
    }

    static void WriteEncodedEntry(BinaryWriter w, string path, long offset, long size, bool encrypted)
    {
        var bitfield = (uint)(size & 0x3f);
        bitfield |= encrypted ? 1u << 22 : 0;
        bitfield |= 1u << 31; // offset 32-bit
        bitfield |= 1u << 30; // uncompressed size 32-bit
        w.Write(bitfield);
        w.Write((uint)offset);
        w.Write((uint)size);
        w.Write(path);
    }

    static byte[] PadMethod(string name)
    {
        var bytes = new byte[32];
        Encoding.ASCII.GetBytes(name, bytes);
        return bytes;
    }
}

file static class AesWriteExtensions
{
    public static void EncryptInPlace(this byte[] data, FAesKey key)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(key.Key, null);
        encryptor.TransformBlock(data, 0, data.Length, data, 0);
    }
}
