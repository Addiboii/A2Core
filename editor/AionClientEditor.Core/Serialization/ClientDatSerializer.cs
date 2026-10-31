using CUE4Parse.Compression;
using CUE4Parse.FileProvider;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Encryption.Aes;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;

namespace AionClientEditor.Serialization;

public static class ClientDatSerializer
{
    public static byte[] SerializePlaintextTable(FAion2DataTableFile table, IFileProvider provider)
    {
        var mappings = provider.MappingsContainer?.MappingsForGame
            ?? throw new InvalidOperationException("Usmap mappings are required to serialize tables");
        using var stream = new MemoryStream();
        using var writer = new FAion2DatFileWriter(stream, provider.Versions);
        writer.Write(table.Version);
        writer.WriteAion2String(string.Join(",", table.Ids));
        if (table.Properties.Count != 1) throw new InvalidOperationException("Expected a single Data property");
        FAion2PropertyWriter.WritePropertyTag(writer, table.Properties[0], mappings);
        return stream.ToArray();
    }

    public static byte[] SerializeOnDiskTable(FAion2DataTableFile table, string virtualPath, IFileProvider provider, byte[]? originalOnDisk = null)
    {
        var plaintext = SerializePlaintextTable(table, provider);
        if (originalOnDisk is null || originalOnDisk.Length < 8 || BitConverter.ToUInt32(originalOnDisk, 0) != 13)
            return plaintext;
        Aion2DatFileEncryption.Initialize(provider);
        return Aion2DatFileEncryption.EncryptDataTable(plaintext, virtualPath);
    }

    public static byte[] SerializePlaintextL10N(FAion2L10NFile file, VersionContainer versions)
    {
        using var stream = new MemoryStream();
        using var writer = new FAion2DatFileWriter(stream, versions);
        writer.WriteL10NString(file.Namespace);
        writer.WriteMap(file.Entries, writer.WriteL10NString, writer.WriteL10NString);
        return stream.ToArray();
    }

    public static byte[] SerializeOnDiskL10N(FAion2L10NFile file, string virtualPath, IFileProvider provider, byte[]? originalOnDisk = null)
    {
        if (originalOnDisk is null || originalOnDisk.Length < 8 || BitConverter.ToUInt32(originalOnDisk, 0) != 2)
            return SerializePlaintextL10N(file, provider.Versions);
        using var inner = new MemoryStream();
        using (var writer = new FAion2DatFileWriter(inner, provider.Versions))
        {
            writer.Write(1);
            writer.WriteL10NString(file.Namespace);
            writer.WriteMap(file.Entries, writer.WriteL10NString, writer.WriteL10NString);
        }
        Aion2DatFileEncryption.Initialize(provider);
        return Aion2DatFileEncryption.EncryptL10N(inner.ToArray(), virtualPath);
    }

    public static byte[] SerializeMapHierarchy(FAion2MapHierarchyFile file)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(file.Version);
        writer.Write(file.Worlds.Length);
        foreach (var world in file.Worlds) // ReadArray layout: count then elements
        {
            writer.Write(world.Id);
            writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Map);
            writer.Write(world.Id);
            writer.Write(world.SubZones.Length);
            foreach (var sub in world.SubZones)
            {
                writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Subzone);
                writer.Write(sub.Id);
                writer.Write(sub.NPCTypes.Length);
                foreach (var npcType in sub.NPCTypes)
                {
                    writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPCType);
                    writer.Write((int)npcType.Id);
                    writer.Write(npcType.NPCs.Length);
                    foreach (var npc in npcType.NPCs)
                    {
                        writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.NPC);
                        writer.Write(npc.Id);
                        writer.Write(npc.Items.Length);
                        foreach (var item in npc.Items)
                        {
                            writer.Write((byte)FAion2MapHierarchyFile.EAionMapDataHierarchy.Item);
                            writer.Write(item.Id);
                            writer.Write(0);
                        }
                    }
                }
            }
        }
        return stream.ToArray();
    }

    public static FAion2DataTableFile ReadPlaintextTable(byte[] data, string virtualPath, IFileProvider provider) =>
        ReadTable(data, virtualPath, provider, encrypted: false);

    public static FAion2DataTableFile ReadTable(byte[] data, string virtualPath, IFileProvider provider, bool encrypted)
    {
        var file = new FakeGameFile(virtualPath, data);
        if (encrypted && data.Length >= 8 && BitConverter.ToUInt32(data, 0) == 13)
        {
            Aion2DatFileEncryption.Initialize(provider);
            data = Aion2DatFileEncryption.DecryptDataTable(data, virtualPath);
            file = new FakeGameFile(virtualPath, data);
        }
        return new FAion2DataTableFile(file, provider);
    }

    public static FAion2L10NFile ReadL10N(byte[] data, string virtualPath, IFileProvider provider) =>
        new(new FakeGameFile(virtualPath, data), provider);

    sealed class FakeGameFile : GameFile
    {
        readonly byte[] _data;

        public FakeGameFile(string path, byte[] data) : base(path.Replace('\\', '/'), data.Length) => _data = data;

        public override bool IsEncrypted => false;
        public override CompressionMethod CompressionMethod => CompressionMethod.None;
        public override byte[] Read(FByteBulkDataHeader? header = null) => _data;
        public override FArchive CreateReader(FByteBulkDataHeader? header = null) => new FByteArchive(Path, _data, null);
    }
}
