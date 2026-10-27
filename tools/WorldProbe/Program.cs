using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using CUE4Parse.GameTypes.Aion2.Objects;
using Newtonsoft.Json.Linq;

if (args.Length is < 6 or > 7 || (args.Length == 7 && args[6] != "--map-summary"))
    throw new ArgumentException("WorldProbe <Paks> <key-report> <oodle-dll> <usmap> <selection-json> <new-local-output-directory> [--map-summary]");
var mapSummary = args.Length == 7;
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[5]);
if (!output.StartsWith(local, StringComparison.OrdinalIgnoreCase) || Directory.Exists(output))
    throw new ArgumentException("Use a new directory under research/local/");
var selection = System.Text.Json.JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[4]))!;
if (selection.Length > (mapSummary ? 4096 : 64)) throw new ArgumentException("Selection exceeds probe limit");
if (mapSummary && selection.Any(p => !p.Contains("/Data/Map/", StringComparison.OrdinalIgnoreCase)
    || !p.EndsWith("/MapData.dat", StringComparison.OrdinalIgnoreCase)))
    throw new ArgumentException("Summary mode accepts only Data/Map/.../MapData.dat paths");
using var key = JsonDocument.Parse(File.ReadAllText(args[1]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
CUE4Parse.Globals.FatalObjectSerializationErrors = true;
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[3]);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(key.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();
Directory.CreateDirectory(output);
// Mount-visible inventory is separate from the IoStore-only asset catalog.
// Paths do not establish payload readability or class semantics.
File.WriteAllText(Path.Combine(output, "mounted-file-inventory.json"), System.Text.Json.JsonSerializer.Serialize(
    provider.Files.Values.Select(f => f.Path).Distinct().OrderBy(p => p).ToArray(),
    new JsonSerializerOptions {WriteIndented = true}));
File.WriteAllText(Path.Combine(output, "table-inventory.json"), System.Text.Json.JsonSerializer.Serialize(
    provider.Files.Values.Where(f => f.Path.Contains("/Data/", StringComparison.OrdinalIgnoreCase) && f.Path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        .Select(f => f.Path).Distinct().OrderBy(p => p).ToArray(), new JsonSerializerOptions {WriteIndented = true}));
var reports = new List<object>();
for (var p = 0; p < selection.Length; p++)
{
    var path = selection[p];
    var entries = new List<object>();
    try
    {
        if (new[] {".ini", ".uproject", ".uplugin", ".json", ".txt"}.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            var file = provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (file.Size > 8 * 1024 * 1024) throw new InvalidDataException("Text artifact exceeds 8 MiB probe limit");
            var filename = $"artifact-{p:D3}{Path.GetExtension(path)}";
            File.WriteAllBytes(Path.Combine(output, filename), file.Read());
            reports.Add(new {path, status = "artifact_extracted", file = filename});
            Console.WriteLine($"{path}: extracted metadata artifact");
            continue;
        }
        if (path.EndsWith("/L10NString.dat", StringComparison.OrdinalIgnoreCase))
        {
            var file = provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var text = new FAion2L10NFile(file, provider);
            var filename = $"localization-{p:D3}.json";
            File.WriteAllText(Path.Combine(output, filename),
                JsonConvert.SerializeObject(text, Formatting.Indented));
            reports.Add(new {path, status = "localization_decoded", file = filename,
                entry_count = text.Entries.Count});
            continue;
        }
        if (path.EndsWith(".dat", StringComparison.OrdinalIgnoreCase))
        {
            var file = provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            var isMapData = path.Contains("/Data/Map/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/Data/WorldMap/", StringComparison.OrdinalIgnoreCase);
            FAion2DataFile table = isMapData
                ? new FAion2MapDataFile(file, provider)
                : new FAion2DataTableFile(file, provider);
            if (table.Properties.Count == 0) throw new InvalidDataException("No table properties decoded");
            var filename = $"table-{p:D3}.json";
            if (mapSummary)
            {
                var data = JObject.FromObject(table)["Properties"]?["Data"] as JObject
                    ?? throw new InvalidDataException("Map data object absent");
                var spawns = data["SpawnInfoList"] as JArray
                    ?? throw new InvalidDataException("SpawnInfoList absent");
                var summary = new {
                    MapId = data["MapId"], BasicInfo = data["BasicInfo"],
                    SpawnRecords = spawns.Count,
                    PositionRecords = spawns.Sum(s => (s["Positions"] as JArray)?.Count ?? 0),
                    NpcIds = spawns.SelectMany(s => s["NpcIdList"] as JArray ?? new JArray()).Select(v => (long)v["Value"]!).Distinct().Order().ToArray(),
                    EnvObjIds = spawns.SelectMany(s => s["EnvObjIdList"] as JArray ?? new JArray()).Select(v => (long)v["Value"]!).Distinct().Order().ToArray(),
                    CollectionCounts = data.Properties().Where(v => v.Value is JContainer).ToDictionary(v => v.Name, v => ((JContainer)v.Value).Count),
                    Limitation = "Decoded metadata summary; positions omitted. Reader Version=0 is not an on-disk version. Counts are not live entity counts."
                };
                File.WriteAllText(Path.Combine(output, filename), JsonConvert.SerializeObject(summary, Formatting.Indented));
            }
            else File.WriteAllText(Path.Combine(output, filename), JsonConvert.SerializeObject(table, Formatting.Indented));
            reports.Add(new {path, status = mapSummary ? "map_data_summarized" : isMapData ? "map_data_decoded" : "table_decoded", file = filename, version = table.Version});
            Console.WriteLine(isMapData
                ? $"{path}: decoded map data (reader version field {table.Version})"
                : $"{path}: decoded table version {table.Version}");
            continue;
        }
        var package = provider.LoadPackage(path);
        if (package.ExportMapLength > 20000) throw new InvalidDataException("Package export count exceeds probe limit");
        for (var i = 0; i < package.ExportsLazy.Length; i++)
        {
            try
            {
                var obj = package.ExportsLazy[i].Value;
                var filename = $"package-{p:D3}-export-{i:D5}.json";
                File.WriteAllText(Path.Combine(output, filename), JsonConvert.SerializeObject(obj, Formatting.Indented));
                entries.Add(new {index = i, obj.Name, obj.ExportType, status = "decoded", file = filename});
            }
            catch (Exception e) { entries.Add(new {index = i, status = "failed", error = e.ToString()}); }
        }
        reports.Add(new {path, status = "inspected", exports = entries});
        Console.WriteLine($"{path}: inspected {entries.Count} exports");
    }
    catch (Exception e) { reports.Add(new {path, status = "failed", error = e.ToString()}); Console.WriteLine($"{path}: {e.Message}"); }
}
File.WriteAllText(Path.Combine(output, "summary.json"), System.Text.Json.JsonSerializer.Serialize(
    new {profile = "GAME_Aion2", packages = reports, limitation = "Observed cooked properties; actor semantics and authoritative server behavior are not inferred."},
    new JsonSerializerOptions {WriteIndented = true}));

