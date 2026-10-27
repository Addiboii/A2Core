using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.SkeletalMesh;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse_Conversion;
using CUE4Parse_Conversion.Options;
using CUE4Parse_Conversion.Textures.BC;
using CUE4Parse.UE4.Assets.Exports.StaticMesh;
using CUE4Parse.UE4.Assets.Exports.Actor;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse_Conversion.Textures;

if (args.Length is < 4 or > 8)
    throw new ArgumentException("Usage: AssetProbe <Paks directory> <key report> <Oodle DLL> <new local output> [usmap] [new local export directory] [Gltf2|UEFormat|Json] [asset-paths.json]");
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[3]);
if (!output.StartsWith(local, StringComparison.OrdinalIgnoreCase) || File.Exists(output))
    throw new ArgumentException("Use a new output file under research/local/");
var exportDirectory = args.Length >= 6 ? Path.GetFullPath(args[5]) : null;
// Json: write every export's properties (DataTables, data assets) instead of converting meshes.
var dumpJson = args.Length >= 7 && args[6] == "Json";
var meshFormat = args.Length >= 7 && !dumpJson ? args[6] switch
{
    "Gltf2" => EMeshFormat.Gltf2,
    "UEFormat" => EMeshFormat.UEFormat,
    _ => throw new ArgumentException("Mesh format must be Gltf2, UEFormat or Json")
} : EMeshFormat.Gltf2;
if (exportDirectory != null && (!exportDirectory.StartsWith(local, StringComparison.OrdinalIgnoreCase) || Directory.Exists(exportDirectory)))
    throw new ArgumentException("Use a new export directory under research/local/");
using var keyReport = JsonDocument.Parse(File.ReadAllText(args[1]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
var detexPath = Path.GetFullPath("third_party/CUE4Parse/CUE4Parse-Conversion/Resources/Detex.dll");
if (File.Exists(detexPath)) DetexHelper.Initialize(detexPath);
CUE4Parse.Globals.FatalObjectSerializationErrors = true;
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
if (args.Length >= 5) provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[4]);
// ASSETPROBE_SHADERMAPS=1: also parse materials' inline shader maps (uniform expression sets, parameter layout).
provider.ReadShaderMaps = Environment.GetEnvironmentVariable("ASSETPROBE_SHADERMAPS") == "1";
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(keyReport.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();
Console.WriteLine($"Mounted file entries: {provider.Files.Count}; global data: {provider.GlobalData != null}");
// ASSETPROBE_LIST=<regex>: list the mounted file paths matching it and, with an export directory, save
// those raw files (cooked .ini files and the like) instead of loading packages.
var listPattern = Environment.GetEnvironmentVariable("ASSETPROBE_LIST");
if (!string.IsNullOrEmpty(listPattern))
{
    var regex = new System.Text.RegularExpressions.Regex(listPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var matches = provider.Files.Keys.Where(k => regex.IsMatch(k)).OrderBy(k => k).ToArray();
    foreach (var k in matches) Console.WriteLine($"  {k}  ({provider.Files[k].Size} bytes)");
    Console.WriteLine($"{matches.Length} matching files");
    if (exportDirectory != null)
        foreach (var k in matches)
        {
            var target = Path.Combine(exportDirectory, k.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.WriteAllBytes(target, provider.SaveAsset(k)); }
            catch (Exception e) { Console.WriteLine($"  {k}: {e.GetType().Name}: {e.Message}"); }
        }
    File.WriteAllText(output, JsonSerializer.Serialize(new {profile = "GAME_Aion2", listPattern, matches}, new JsonSerializerOptions {WriteIndented = true}));
    return;
}
// ASSETPROBE_CHUNKS=<file>: one 24-hex-digit FIoChunkId per line (its 12 raw bytes, as stored in shader
// archives); save each chunk's bytes as <export directory>/<id>.bin.
var chunkList = Environment.GetEnvironmentVariable("ASSETPROBE_CHUNKS");
if (!string.IsNullOrEmpty(chunkList) && exportDirectory != null)
{
    Directory.CreateDirectory(exportDirectory);
    var saved = new List<object>();
    foreach (var hex in File.ReadAllLines(chunkList).Select(l => l.Trim()).Where(l => l.Length == 24))
    {
        var id = System.Runtime.InteropServices.MemoryMarshal.Read<CUE4Parse.UE4.IO.Objects.FIoChunkId>(Convert.FromHexString(hex));
        var reader = provider.MountedVfs.OfType<CUE4Parse.UE4.IO.IoStoreReader>().FirstOrDefault(r => r.DoesChunkExist(id));
        if (reader == null) { saved.Add(new {hex, found = false}); continue; }
        var bytes = reader.Read(id);
        File.WriteAllBytes(Path.Combine(exportDirectory, hex + ".bin"), bytes);
        saved.Add(new {hex, found = true, container = reader.Name, bytes.Length});
    }
    Console.WriteLine($"chunks saved: {saved.Count}");
    File.WriteAllText(output, JsonSerializer.Serialize(new {profile = "GAME_Aion2", saved}, new JsonSerializerOptions {WriteIndented = true}));
    return;
}
var results = new List<object>();
var meshes = new ExportSession {MaxDegreeOfParallelism = 1};
var animations = new ExportSession {MaxDegreeOfParallelism = 1};
var paths = args.Length == 8 ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[7]))!
    : new[] {"GM_RootMesh", "Basebody/GM_BaseFull", "Basebody/GM_BaseHead", "SK_GM_Root", "Ani/Normal/GM_Normal_Idle", "Ani/Common/BW/GM_BW_Cidle", "Ani/Common/BW/GM_BW_Walk_B", "Ani/Common/BW/GM_BW_Walk_F"}.Select(s => "AION2/Content/Character/Player/GM/" + s).ToArray();
foreach (var path in paths)
{
    var suffix = path.Split('/').Last();
    try
    {
        var package = provider.LoadPackage(path);
        var objects = package.GetExports().ToArray();
        var exports = objects.Select(e => new {e.Name, e.ExportType, ReaderClass = e.GetType().FullName}).ToArray();
        if (exportDirectory != null && dumpJson)
        {
            Directory.CreateDirectory(exportDirectory);
            File.WriteAllText(Path.Combine(exportDirectory, suffix + ".json"),
                Newtonsoft.Json.JsonConvert.SerializeObject(objects, Newtonsoft.Json.Formatting.Indented));
        }
        else if (exportDirectory != null)
            foreach (var obj in objects)
            {
                if (obj is USkeletalMesh or UStaticMesh or ALandscapeProxy) meshes.Add(obj);
                else if (obj is UAnimSequence or USkeleton) animations.Add(obj);
                // Loose textures (e.g. a material instance's): <export>/textures/<name>.png.
                else if (obj is UTexture2D texture && texture.Decode() is { } bitmap)
                {
                    Directory.CreateDirectory(Path.Combine(exportDirectory, "textures"));
                    File.WriteAllBytes(Path.Combine(exportDirectory, "textures", texture.Name + ".png"),
                        bitmap.Encode(ETextureFormat.Png, false, out _));
                }
            }
        results.Add(new {path, status = "deserialized", exports});
        Console.WriteLine($"{suffix}: {exports.Length} exports");
    }
    catch (Exception e)
    {
        results.Add(new {path, status = "failed", error = e.ToString()});
        Console.WriteLine($"{suffix}: {e.GetType().Name}: {e.Message}");
    }
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var conversion = new List<object>();
if (exportDirectory != null)
{
    foreach (var result in await meshes.RunAsync(Path.Combine(exportDirectory, "meshes"), new ExportOptions(meshFormat: meshFormat)))
        conversion.Add(new {result.Success, result.ObjectPath, result.DiskFilePaths, Error = result.Error?.ToString()});
    foreach (var result in await animations.RunAsync(Path.Combine(exportDirectory, "animations"), new ExportOptions(meshFormat: EMeshFormat.UEFormat, exportMaterials: false)))
        conversion.Add(new {result.Success, result.ObjectPath, result.DiskFilePaths, Error = result.Error?.ToString()});
}
using var stream = new FileStream(output, FileMode.CreateNew);
JsonSerializer.Serialize(stream, new {profile = "GAME_Aion2", files = provider.Files.Count, results, conversion}, new JsonSerializerOptions {WriteIndented = true});
