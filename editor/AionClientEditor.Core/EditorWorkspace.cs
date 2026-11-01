using AionClientEditor.Patch;
using AionClientEditor.Serialization;
using AionClientProbe;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider.Objects;
using CUE4Parse.GameTypes.Aion2.Objects;
using System.Text.Json;

namespace AionClientEditor;

public sealed class EditorWorkspace : IDisposable
{
    public AionClientSession Session { get; }
    public PatchProject Patch { get; private set; }
    public string? KeyJsonPath { get; }

    EditorWorkspace(AionClientSession session, PatchProject patch, string? keyJsonPath)
    {
        Session = session;
        Patch = patch;
        KeyJsonPath = keyJsonPath;
    }

    public static EditorWorkspace Open(string paksFolder, string keyJsonPath, string? oodleDll = null, string? usmap = null)
    {
        oodleDll = AionClientSession.ResolveOodleDll(oodleDll);
        usmap = AionClientSession.TryResolveUsmap(usmap)
            ?? throw new FileNotFoundException("Usmap not found", usmap ?? AionClientSession.DefaultUsmapPath());
        var session = AionClientSession.Open(paksFolder, keyJsonPath, oodleDll, usmap);
        return new EditorWorkspace(session, PatchStorage.LoadOrCreate(), keyJsonPath);
    }

    public IEnumerable<string> ListTablePaths() =>
        Session.Provider.Files.Values
            .Where(f => f.Path.Contains("/Data/", StringComparison.OrdinalIgnoreCase)
                && (f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase)
                    || f.Path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.Path)
            .Distinct()
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    public TableDocument LoadTable(string virtualPath)
    {
        var file = Session.Provider.Files.Values.First(f => f.Path.Equals(virtualPath, StringComparison.OrdinalIgnoreCase));
        var raw = file.Read();
        if (virtualPath.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase))
        {
            var l10n = ClientDatSerializer.ReadL10N(raw, virtualPath, Session.Provider);
            return TableDocument.FromL10N(virtualPath, l10n, raw);
        }

        if (virtualPath.Contains("/Data/Table/", StringComparison.OrdinalIgnoreCase))
        {
            var table = new FAion2DataTableFile(file, Session.Provider);
            return TableDocument.FromDataTable(virtualPath, table, raw);
        }

        throw new NotSupportedException($"Unsupported table path: {virtualPath}");
    }

    public void SaveTable(TableDocument doc)
    {
        byte[] onDisk;
        if (doc.Kind == TableKind.L10N)
        {
            var l10n = doc.L10N ?? throw new InvalidOperationException("L10N document empty");
            onDisk = ClientDatSerializer.SerializeOnDiskL10N(l10n, doc.VirtualPath, Session.Provider, doc.OriginalOnDisk);
        }
        else
        {
            var table = doc.DataTable ?? throw new InvalidOperationException("Data table document empty");
            onDisk = ClientDatSerializer.SerializeOnDiskTable(table, doc.VirtualPath, Session.Provider, doc.OriginalOnDisk);
        }

        PatchStorage.SavePayload(Patch, doc.VirtualPath, onDisk);
        Patch = PatchStorage.LoadOrCreate();
    }

    public void ExportPatchJson(string path) => Patch.Save(path);

    public void ImportPatchJson(string path)
    {
        Patch = PatchProject.Load(path);
        Patch.Save(PatchStorage.ManifestPath);
    }

    public void ReplacePatch(PatchProject patch) => Patch = patch;

    public void BuildPatchPak(string gamePaksFolder)
    {
        var keyHex = JsonDocument.Parse(File.ReadAllText(KeyJsonPath!)).RootElement.GetProperty("key_hex").GetString()!;
        PatchPakBuilder.DeployToGamePaks(gamePaksFolder, Patch, new FAesKey(keyHex));
    }

    public void Dispose() => Session.Dispose();
}

public enum TableKind { DataTable, L10N }

public sealed class TableDocument
{
    public required string VirtualPath { get; init; }
    public required TableKind Kind { get; init; }
    public FAion2DataTableFile? DataTable { get; init; }
    public FAion2L10NFile? L10N { get; init; }
    public byte[] OriginalOnDisk { get; init; } = [];

    public static TableDocument FromDataTable(string path, FAion2DataTableFile table, byte[] raw) =>
        new() { VirtualPath = path, Kind = TableKind.DataTable, DataTable = table, OriginalOnDisk = raw };

    public static TableDocument FromL10N(string path, FAion2L10NFile l10n, byte[] raw) =>
        new() { VirtualPath = path, Kind = TableKind.L10N, L10N = l10n, OriginalOnDisk = raw };

    public IEnumerable<(string Key, string Value)> FlatRows()
    {
        if (Kind == TableKind.L10N && L10N is not null)
            return L10N.Entries.Select(e => (e.Key, e.Value));
        return [];
    }

    public Editing.DataTableGridModel? BuildDataTableGrid() =>
        Kind == TableKind.DataTable && DataTable is { } table ? Editing.DataTableGridModel.From(table) : null;
}
