using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using SkiaSharp;

// Exports the client's item icons for the staff item database (tools/build_item_database.py runs it).
//
//   ItemIconExport <Paks> <key report> <Oodle DLL> <usmap> <icons.json> <out dir> [size]
//
// icons.json: {"<IconRes key>": "/Game/UI/Resource/Texture/Item/.../<Texture>.<Texture>", ...} (the client's
// UnrealData/UI_ItemIconTable rows that the item table uses). Each texture is decoded at the smallest mip that is
// still >= size px (default 128) and written as <out>/<IconRes key>.webp. Existing files are kept (delete the folder
// to redo all). Prints a JSON summary: written, kept, failed [{key, path, error}].
// Reads the archives in place (opened shared, nothing changed); no extraction to disk is needed.
if (args.Length < 6)
    throw new ArgumentException("Usage: ItemIconExport <Paks> <key report> <Oodle DLL> <usmap> <icons.json> <out dir> [size]");
var size = args.Length > 6 ? int.Parse(args[6]) : 128;
using var keyReport = JsonDocument.Parse(File.ReadAllText(args[1]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
foreach (var detex in new[] { "third_party/CUE4Parse/CUE4Parse-Conversion/Resources/Detex.dll", Path.Combine(AppContext.BaseDirectory, "Detex.dll") })
    if (File.Exists(detex)) { DetexHelper.Initialize(Path.GetFullPath(detex)); break; }
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[3]);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(keyReport.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();

var icons = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(args[4]))!;
var outDir = Path.GetFullPath(args[5]);
Directory.CreateDirectory(outDir);
int written = 0, kept = 0;
var failed = new List<object>();
foreach (var (key, assetPath) in icons.OrderBy(i => i.Key, StringComparer.Ordinal))
{
    if (key.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || key.Contains("..")) { failed.Add(new { key, path = assetPath, error = "bad key" }); continue; }
    var file = Path.Combine(outDir, key + ".webp");
    if (File.Exists(file)) { kept++; continue; }
    // "/Game/UI/X/Name.Name" -> package "AION2/Content/UI/X/Name".
    var package = assetPath.Split('.')[0];
    if (package.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) package = "AION2/Content/" + package["/Game/".Length..];
    try
    {
        var texture = provider.LoadPackage(package).GetExports().OfType<UTexture>().First();
        var mips = texture.PlatformData.Mips;
        // The smallest mip that is still at least `size` px wide (else the largest one).
        var mip = 0;
        for (var i = 0; i < mips.Length; i++) if (mips[i].SizeX >= size) mip = i;
        // A mip whose data is not in the archive (streamed from a missing bulk file) falls back to the first one present.
        using var bitmap = (texture.Decode(mips[mip]) ?? texture.Decode())?.ToSkBitmap() ?? throw new InvalidDataException("no image data");
        using var image = bitmap.Width > size
            ? SKImage.FromBitmap(bitmap.Resize(new SKImageInfo(size, Math.Max(1, bitmap.Height * size / bitmap.Width)), SKFilterQuality.High))
            : SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Webp, 88);
        File.WriteAllBytes(file, data.ToArray());
        written++;
    }
    catch (Exception error)
    {
        failed.Add(new { key, path = assetPath, error = error.Message });
    }
    if ((written + failed.Count) % 250 == 0) Console.Error.WriteLine($"{written} written, {failed.Count} failed, {kept} kept");
}
Console.WriteLine(JsonSerializer.Serialize(new { written, kept, failed }, new JsonSerializerOptions { WriteIndented = true }));
