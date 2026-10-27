using System.Text.RegularExpressions;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;
using CUE4Parse_Conversion.Textures;
using CUE4Parse_Conversion.Textures.BC;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SkiaSharp;

// Exports the client's painted world-map images for the staff dashboard.
//
//   MapImageExport <Paks> <key report> <Oodle DLL> <usmap> list <regex>
//   MapImageExport <Paks> <key report> <Oodle DLL> <usmap> export <out dir> [map-bases.json]
//   MapImageExport check <out dir> <subzones.json> <map-points.json> <mapId>...
//
// export: for every Data/WorldMap/<Name>.dat whose <Name> is a Map.dat map and has tiles
// UI/Map/WorldMap/<Name>/Res/<Name>_<i>_<j>, writes <out>/<mapId>.webp (stitched, <= 2048 px)
// and <out>/maps.json. Observed tile layout: the first index <i> is the horizontal (world X)
// sector, the second <j> the vertical (world Y) sector. Data/WorldMap/<Name>.dat gives
// StartWorldLocation (world X/Y at the image's top-left), SectorSize (tiles along X/Y) and
// SectorWorldSize (cm per tile), so px = (x - StartX) / (SectorSize.X * SectorWorldSize) * width
// and py = (y - StartY) / (SectorSize.Y * SectorWorldSize) * height.
// Optional map-bases.json (tools/data) adds instance layers that reuse their base map's image.
// check: draws subzone outlines (red) and map points (blue) on <out>/_full/<id>.png -> <out>/check-<id>.png.
const int MaxSide = 2048;
const long MaxBytes = 1_500_000;
var jsonOut = new JsonSerializerSettings { Formatting = Formatting.Indented };

if (args.Length >= 5 && args[0] == "check") { Check(args[1], args[2], args[3], args[4..]); return; }
if (args.Length < 6)
    throw new ArgumentException("Usage: MapImageExport <Paks> <key report> <Oodle DLL> <usmap> list <regex> | export <out dir> [map-bases.json]; or MapImageExport check <out dir> <subzones.json> <map-points.json> <mapId>...");
using var keyReport = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
var detexPath = Path.GetFullPath("third_party/CUE4Parse/CUE4Parse-Conversion/Resources/Detex.dll");
if (File.Exists(detexPath)) DetexHelper.Initialize(detexPath);
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[3]);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(keyReport.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();

switch (args[4])
{
    case "list":
        var pattern = new Regex(args[5], RegexOptions.IgnoreCase);
        foreach (var key in provider.Files.Keys.Where(k => pattern.IsMatch(k)).OrderBy(k => k)) Console.WriteLine(key);
        break;
    case "export":
        Export(OutDir(args[5]), args.Length > 6 ? args[6] : null);
        break;
    default:
        throw new ArgumentException($"Unknown mode {args[4]}");
}

static string OutDir(string path)
{
    var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
    var full = Path.GetFullPath(path);
    if (!(full + Path.DirectorySeparatorChar).StartsWith(local, StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("Output directory must be under research/local/");
    Directory.CreateDirectory(Path.Combine(full, "_full"));
    return full;
}

JObject DataFile(string path, bool mapData)
{
    var file = provider.Files.Values.First(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    FAion2DataFile table = mapData ? new FAion2MapDataFile(file, provider) : new FAion2DataTableFile(file, provider);
    return JObject.FromObject(table);
}

void Export(string outDir, string? basesPath)
{
    var mapIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in (JArray)DataFile("AION2/Content/Data/Table/Map.dat", false)["Properties"]!["Data"]!)
        mapIds.TryAdd((string)row["Name"]!, (long)row["ID"]!["Value"]!);
    var tilePattern = new Regex(@"^AION2/Content/UI/Map/WorldMap/(?<folder>[^/]+)/Res/(?<name>[^/]+)_(?<i>\d\d)_(?<j>\d\d)\.uasset$", RegexOptions.IgnoreCase);
    var tilesByFolder = provider.Files.Keys.Select(k => tilePattern.Match(k))
        .Where(m => m.Success && m.Groups["name"].Value.Equals(m.Groups["folder"].Value, StringComparison.OrdinalIgnoreCase))
        .GroupBy(m => m.Groups["folder"].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(m => (Package: m.Value[..^".uasset".Length], I: int.Parse(m.Groups["i"].Value), J: int.Parse(m.Groups["j"].Value))).ToList(), StringComparer.OrdinalIgnoreCase);
    var maps = new SortedDictionary<long, JObject>();
    var skipped = new List<object>();
    foreach (var dataPath in provider.Files.Keys.Where(k => Regex.IsMatch(k, @"^AION2/Content/Data/WorldMap/[^/]+\.dat$", RegexOptions.IgnoreCase)).OrderBy(k => k))
    {
        var name = Path.GetFileNameWithoutExtension(dataPath);
        if (!mapIds.TryGetValue(name, out var mapId)) { skipped.Add(new { name, reason = "no Map.dat entry with this Name" }); continue; }
        if (!tilesByFolder.TryGetValue(name, out var tiles)) { skipped.Add(new { name, mapId, reason = "no UI/Map/WorldMap tiles" }); continue; }
        var d = (JObject)DataFile(dataPath, true)["Properties"]!["Data"]!;
        double startX = (double)d["StartWorldLocation"]!["X"]!, startY = (double)d["StartWorldLocation"]!["Y"]!;
        int sectorsX = (int)d["SectorSize"]!["X"]!, sectorsY = (int)d["SectorSize"]!["Y"]!;
        double sectorWorld = (double)d["SectorWorldSize"]!;
        int nx = tiles.Max(t => t.I) + 1, ny = tiles.Max(t => t.J) + 1;
        if (nx != sectorsX || ny != sectorsY) { skipped.Add(new { name, mapId, reason = $"tile grid {nx}x{ny} != SectorSize {sectorsX}x{sectorsY}" }); continue; }

        // Decode only the mip level the output needs (cooked tiles are up to 2048 px each).
        var mip = 1;
        while (mip * Math.Max(nx, ny) < MaxSide) mip *= 2;
        var decoded = new List<(int I, int J, SKBitmap Bitmap)>();
        int tileW = 0, tileH = 0;
        foreach (var tile in tiles)
        {
            var texture = provider.LoadPackage(tile.Package).GetExports().OfType<UTexture>().First();
            tileW = Math.Max(tileW, texture.PlatformData.SizeX);
            tileH = Math.Max(tileH, texture.PlatformData.SizeY);
            decoded.Add((tile.I, tile.J, texture.Decode(mip)?.ToSkBitmap() ?? throw new InvalidDataException($"{tile.Package} did not decode")));
        }
        var scale = Math.Min(1.0, (double)MaxSide / Math.Max(tileW * nx, tileH * ny));
        var width = (int)Math.Round(tileW * nx * scale);
        var height = (int)Math.Round(tileH * ny * scale);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        foreach (var (i, j, bitmap) in decoded)
        {
            using var image = SKImage.FromBitmap(bitmap);
            surface.Canvas.DrawImage(image, SKRect.Create((float)(i * width / (double)nx), (float)(j * height / (double)ny), (float)(width / (double)nx), (float)(height / (double)ny)), paint);
            bitmap.Dispose();
        }
        using var snapshot = surface.Snapshot();
        var file = $"{mapId}.webp";
        var quality = 85;
        byte[] bytes;
        while (true)
        {
            using var data = snapshot.Encode(SKEncodedImageFormat.Webp, quality);
            bytes = data.ToArray();
            if (bytes.Length <= MaxBytes || quality <= 45) break;
            quality -= 10;
        }
        File.WriteAllBytes(Path.Combine(outDir, file), bytes);
        using (var png = snapshot.Encode(SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(outDir, "_full", $"{mapId}.png"), png.ToArray());

        double spanX = sectorsX * sectorWorld, spanY = sectorsY * sectorWorld;
        double a = width / spanX, e = height / spanY;
        maps[mapId] = JObject.FromObject(new
        {
            name, file, width, height, bytes = bytes.Length, webpQuality = quality,
            world = new
            {
                originX = startX, originY = startY, pxPerCmX = a, pxPerCmY = e,
                affine = new[] { a, 0, -startX * a, 0, e, -startY * e },
                note = "px = (x - originX) * pxPerCmX; py = (y - originY) * pxPerCmY. Image right = +X, image down = +Y.",
                minX = startX, maxX = startX + spanX, minY = startY, maxY = startY + spanY
            },
            source = new
            {
                data = dataPath, startWorldLocation = new[] { startX, startY }, sectorSize = new[] { sectorsX, sectorsY },
                sectorWorldSize = sectorWorld, sectorTextureSize = (int?)d["SectorTextureSize"], tiles = $"UI/Map/WorldMap/{name}/Res/{name}_<X>_<Y>",
                cookedTileSize = new[] { tileW, tileH }
            }
        });
        Console.WriteLine($"{mapId} {name}: {nx}x{ny} tiles of {tileW}x{tileH} -> {width}x{height}, {bytes.Length} bytes (q{quality})");
    }
    // Instance layers share their base map's world coordinates (CANDIDATE: same level, same frame).
    if (basesPath != null)
    {
        var bases = JObject.Parse(File.ReadAllText(basesPath))["bases"]!.ToObject<Dictionary<long, long>>()!;
        foreach (var (layer, baseId) in bases)
            if (!maps.ContainsKey(layer) && maps.TryGetValue(baseId, out var baseEntry) && baseEntry["baseMapId"] == null)
            {
                var alias = (JObject)baseEntry.DeepClone();
                alias["baseMapId"] = baseId;
                maps[layer] = alias;
            }
    }
    File.WriteAllText(Path.Combine(outDir, "maps.json"), JsonConvert.SerializeObject(maps.ToDictionary(p => p.Key.ToString(), p => p.Value), jsonOut));
    File.WriteAllText(Path.Combine(outDir, "skipped.json"), JsonConvert.SerializeObject(skipped, jsonOut));
    Console.WriteLine($"maps.json: {maps.Count} entries ({maps.Count(m => m.Value["baseMapId"] == null)} images); {skipped.Count} WorldMap data files skipped");
}

void Check(string outDir, string subzonesPath, string pointsPath, string[] ids)
{
    var maps = JObject.Parse(File.ReadAllText(Path.Combine(outDir, "maps.json")));
    var subzones = JObject.Parse(File.ReadAllText(subzonesPath))["maps"]!;
    var points = JObject.Parse(File.ReadAllText(pointsPath))["maps"]!;
    foreach (var id in ids)
    {
        var entry = maps[id] ?? throw new ArgumentException($"{id} not in maps.json");
        var imageId = entry["baseMapId"]?.ToString() ?? id;
        var t = entry["world"]!["affine"]!.ToObject<double[]>()!;
        SKPoint P(double x, double y) => new((float)(t[0] * x + t[1] * y + t[2]), (float)(t[3] * x + t[4] * y + t[5]));
        using var bitmap = SKBitmap.Decode(Path.Combine(outDir, "_full", imageId + ".png"));
        using var canvas = new SKCanvas(bitmap);
        using var line = new SKPaint { Color = new SKColor(220, 20, 20), Style = SKPaintStyle.Stroke, StrokeWidth = 3, IsAntialias = true };
        using var dot = new SKPaint { Color = new SKColor(20, 60, 230), Style = SKPaintStyle.Fill, IsAntialias = true };
        using var text = new SKPaint { Color = SKColors.Black, TextSize = 18, IsAntialias = true };
        var polygons = 0; var marks = 0;
        foreach (var zone in subzones[id] as JArray ?? new JArray())
        {
            var pts = zone["points"]!.Select(p => P((double)p[0]!, (double)p[1]!)).ToArray();
            if (pts.Length < 2) continue;
            using var path = new SKPath();
            path.AddPoly(pts, true);
            canvas.DrawPath(path, line);
            var c = new SKPoint(pts.Average(p => p.X), pts.Average(p => p.Y));
            canvas.DrawText(((string)zone["name"]!).Replace("_Subzone", ""), c.X, c.Y, text);
            polygons++;
        }
        foreach (var point in (points[id] as JObject)?.Properties() ?? [])
        {
            var p = P((double)point.Value[0]!, (double)point.Value[1]!);
            canvas.DrawCircle(p, 7, dot);
            marks++;
        }
        canvas.Flush();
        using var image = SKImage.FromBitmap(bitmap);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(outDir, $"check-{id}.png"), png.ToArray());
        Console.WriteLine($"check-{id}.png: {polygons} subzones, {marks} map points");
    }
}
