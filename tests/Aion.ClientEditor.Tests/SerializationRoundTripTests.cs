using System.Buffers.Binary;
using System.Text;
using AionClientEditor.Patch;
using AionClientEditor.Serialization;
using Blake3;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Xunit;

namespace Aion.ClientEditor.Tests;

public class SerializationRoundTripTests
{
    static DefaultFileProvider CreateProvider(bool mappings = false)
    {
        var provider = new DefaultFileProvider(RepoRoot(), SearchOption.TopDirectoryOnly, new VersionContainer(EGame.GAME_Aion2));
        if (mappings)
        {
            var usmap = Path.Combine(RepoRoot(), "research", "local", "tooling", "mappings", "AION2-5.3.2.0-2.0.6.0.usmap");
            provider.MappingsContainer = new FileUsmapTypeMappingsProvider(usmap);
        }
        return provider;
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "research", "local", "tooling", "mappings", "AION2-5.3.2.0-2.0.6.0.usmap")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
    }

    [Fact]
    public void MapHierarchy_binary_round_trip()
    {
        var original = new FAion2MapHierarchyFile(new FakeGameFile("AION2/Content/Data/Map/Foo/MapHierarchy.dat", SampleHierarchy()));
        var bytes = ClientDatSerializer.SerializeMapHierarchy(original);
        var round = new FAion2MapHierarchyFile(new FakeGameFile("AION2/Content/Data/Map/Foo/MapHierarchy.dat", bytes));
        Assert.Equal(original.Version, round.Version);
        Assert.Equal(original.Worlds.Length, round.Worlds.Length);
        Assert.Equal(original.Worlds[0].Id, round.Worlds[0].Id);
        Assert.Equal(original.Worlds[0].SubZones[0].NPCTypes[0].NPCs[0].Items[0].Id,
            round.Worlds[0].SubZones[0].NPCTypes[0].NPCs[0].Items[0].Id);
    }

    [Fact]
    public void Encrypted_l10n_container_round_trip()
    {
        var key = new FAesKey("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var path = "AION2/Content/Data/L10N/en-US/L10NString.dat";
        Aion2DatFileEncryption.AesKeys[Seed("L10NString_en-US")] = key;

        using var ms = new MemoryStream();
        using (var w = new AionClientEditor.Serialization.FAion2DatFileWriter(ms, new VersionContainer(EGame.GAME_Aion2)))
        {
            w.WriteL10NString("T");
            w.WriteMap(new Dictionary<string, string> { ["a"] = "b" }, w.WriteL10NString, w.WriteL10NString);
        }
        var plaintext = ms.ToArray();

        var encrypted = Aion2DatFileEncryption.EncryptL10N(plaintext, path);
        var decrypted = Aion2DatFileEncryption.DecryptL10N(encrypted, path);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Encrypted_small_dat_table_stream_round_trip()
    {
        var key = new FAesKey("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        var path = "AION2/Content/Data/Table/UnitTest.dat";
        Aion2DatFileEncryption.AesKeys[Seed("UnitTest")] = key;
        var plaintext = new byte[128];
        Random.Shared.NextBytes(plaintext);
        var encrypted = Aion2DatFileEncryption.EncryptDataTable(plaintext, path);
        var decrypted = Aion2DatFileEncryption.DecryptDataTable(encrypted, path);
        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void PatchProject_json_round_trip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "a2core-patch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "patch.json");
        var project = new PatchProject { BuildFingerprint = "test", Entries = [new() { VirtualPath = "AION2/Content/Data/Table/X.dat", PayloadFile = "abc.bin", Sha256 = "00" }] };
        project.Save(path);
        var loaded = PatchProject.Load(path);
        Assert.Equal(project.BuildFingerprint, loaded.BuildFingerprint);
        Assert.Single(loaded.Entries);
        Directory.Delete(dir, true);
    }

    static byte[] SampleHierarchy()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(1);
        w.Write(1);
        w.Write(100u);
        w.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Map);
        w.Write(100u);
        w.Write(1);
        w.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Subzone);
        w.Write(7u);
        w.Write(1);
        w.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPCType);
        w.Write((int)FAion2MapHierarchyFile.ENpcType.Monster);
        w.Write(1);
        w.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPC);
        w.Write(42u);
        w.Write(1);
        w.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Item);
        w.Write(99u);
        w.Write(0);
        return ms.ToArray();
    }

    static ulong Seed(string value)
    {
        using var hasher = Hasher.New();
        hasher.Update(Encoding.UTF8.GetBytes(value));
        return BinaryPrimitives.ReadUInt64LittleEndian(hasher.Finalize().AsSpan());
    }

    sealed class FakeGameFile : GameFile
    {
        readonly byte[] _data;
        public FakeGameFile(string path, byte[] data) : base(path, data.Length) => _data = data;
        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => _data;
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => new FByteArchive(Path, _data, null);
    }
}
