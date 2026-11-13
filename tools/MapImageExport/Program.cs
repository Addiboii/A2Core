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

// Exports the client's painted world-map images for the staff dashboard (tools/export_map_images.py runs it).
//
//   MapImageExport <Paks> <key report> <Oodle DLL> <usmap> list <regex>
//   MapImageExport <Paks> <key report> <Oodle DLL> <usmap> export <out dir> [map-bases.json] [geometry-maps.json]
//   MapImageExport check <out dir> <subzones.json> <map-points.json> <mapId>...
//
// export: for every Data/WorldMap/<Name>.dat with tiles UI/Map/WorldMap/[SharedMap/]<Name>/Res/<Name>_<i>_<j>,
// writes <out>/<mapId>.webp (stitched, <= 2048 px) and <out>/maps.json. Observed tile layout: the first index <i> is
// the horizontal (world X) sector, the second <j> the vertical (world Y) sector. Data/WorldMap/<Name>.dat gives
// StartWorldLocation (world X/Y at the image's top-left), SectorSize (tiles along X/Y) and SectorWorldSize (cm per
// tile), so px = (x - StartX) / (SectorSize.X * SectorWorldSize) * width and py = (y - StartY) / (SectorSize.Y *
// SectorWorldSize) * height.
// Large maps also get detail tiles <out>/tiles/<mapId>/<x>_<y>.webp (DetailTile px each, the map at up to DetailSide
// px) that the dashboard loads when zoomed in past the overview's resolution.
// Which maps show a painting: the Map.dat map named <Name>, plus (optional geometry-maps.json, FullExtract's
// maps/geometry-maps-collision.json) every map whose level or shared level (MapData BasicInfo.SharingMap) is <Name>:
// most dungeons reuse a shared level (BG/SharingMap/...) and its painting under UI/Map/WorldMap/SharedMap/<Name>.
// Optional map-bases.json (tools/data) adds instance layers that reuse their base map's image.
// check: draws subzone outlines (red) and map points (blue) on <out>/_full/<id>.png -> <out>/check-<id>.png.
const int MaxSide = 2048;
const int DetailSide = 8192;
const int DetailTile = 1024;
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
        Export(OutDir(args[5]), args.Length > 6 && args[6].Length > 0 ? args[6] : null, args.Length > 7 ? args[7] : null);
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

UTexture? LoadTexture(string package)
{
    for (var attempt = 0; attempt < 3; attempt++)
        try { return provider.LoadPackage(package).GetExports().OfType<UTexture>().First(); }
        catch (Exception error) when (error is IOException or InvalidDataException) { Console.Error.WriteLine($"{package}: {error.Message}"); }
    return null;
}

void Export(string outDir, string? basesPath, string? geometryPath)
{
    var mapIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in (JArray)DataFile("AION2/Content/Data/Table/Map.dat", false)["Properties"]!["Data"]!)
        mapIds.TryAdd((string)row["Name"]!, (long)row["ID"]!["Value"]!);
    // Maps by the level they use: their own (by name) or the shared one (FullExtract's geometry-maps-collision.json).
    var mapsByLevel = new Dictionary<string, SortedSet<long>>(StringComparer.OrdinalIgnoreCase);
    void AddLevel(string? level, long id)
    {
        if (string.IsNullOrEmpty(level) || level == "None") return;
        var leaf = level.Split('/').Last().Split('.').Last();
        (mapsByLevel.TryGetValue(leaf, out var set) ? set : mapsByLevel[leaf] = []).Add(id);
    }
    foreach (var (name, id) in mapIds) AddLevel(name, id);
    if (geometryPath != null)
        foreach (var row in JArray.Parse(File.ReadAllText(geometryPath)))
        {
            AddLevel((string?)row["level"], (long)row["id"]!);
            AddLevel((string?)row["sharingMap"], (long)row["id"]!);
        }
    var tilePattern = new Regex(@"^AION2/Content/UI/Map/WorldMap/(?:SharedMap/)?(?<folder>[^/]+)/Res/(?<name>[^/]+)_(?<i>\d\d)_(?<j>\d\d)\.uasset$", RegexOptions.IgnoreCase);
    var tilesByFolder = provider.Files.Keys.Select(k => tilePattern.Match(k))
        .Where(m => m.Success && m.Groups["name"].Value.Equals(m.Groups["folder"].Value, StringComparison.OrdinalIgnoreCase))
        .GroupBy(m => m.Groups["folder"].Value, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.Select(m => (Package: m.Value[..^".uasset".Length], I: int.Parse(m.Groups["i"].Value), J: int.Parse(m.Groups["j"].Value))).ToList(), StringComparer.OrdinalIgnoreCase);
    var maps = new SortedDictionary<long, JObject>();
    var skipped = new List<object>();
    var tilesDir = Path.Combine(outDir, "tiles");
    var withoutNulls = JsonSerializer.CreateDefault(new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    foreach (var dataPath in provider.Files.Keys.Where(k => Regex.IsMatch(k, @"^AION2/Content/Data/WorldMap/[^/]+\.dat$", RegexOptions.IgnoreCase)).OrderBy(k => k))
    {
        var name = Path.GetFileNameWithoutExtension(dataPath);
        if (!mapsByLevel.TryGetValue(name, out var users) || users.Count == 0) { skipped.Add(new { name, reason = "no map uses this level" }); continue; }
        var mapId = users.Min;
        if (!tilesByFolder.TryGetValue(name, out var tiles)) { skipped.Add(new { name, mapId, reason = "no UI/Map/WorldMap tiles" }); continue; }
        var d = (JObject)DataFile(dataPath, true)["Properties"]!["Data"]!;
        double startX = (double)d["StartWorldLocation"]!["X"]!, startY = (double)d["StartWorldLocation"]!["Y"]!;
        int sectorsX = (int)d["SectorSize"]!["X"]!, sectorsY = (int)d["SectorSize"]!["Y"]!;
        double sectorWorld = (double)d["SectorWorldSize"]!;
        int nx = tiles.Max(t => t.I) + 1, ny = tiles.Max(t => t.J) + 1;
        if (nx != sectorsX || ny != sectorsY) { skipped.Add(new { name, mapId, reason = $"tile grid {nx}x{ny} != SectorSize {sectorsX}x{sectorsY}" }); continue; }

        // Decode only the mip level the output needs (cooked tiles are up to 2048 px each): the detail size when the
        // painting is much larger than the overview, else the overview size.
        int nativeW = 0, nativeH = 0;
        // A tile whose package fails to read (an Oodle decode error now and then) is retried, then left out (transparent).
        var textures = new List<(int I, int J, UTexture Texture)>();
        var missing = new List<string>();
        foreach (var t in tiles)
            if (LoadTexture(t.Package) is { } texture) textures.Add((t.I, t.J, texture));
            else missing.Add(t.Package);
        if (textures.Count == 0) { skipped.Add(new { name, mapId, reason = "no tile could be read" }); continue; }
        foreach (var t in textures) { nativeW = Math.Max(nativeW, t.Texture.PlatformData.SizeX); nativeH = Math.Max(nativeH, t.Texture.PlatformData.SizeY); }
        var detailed = Math.Max(nativeW * nx, nativeH * ny) >= MaxSide * 2;
        var target = detailed ? DetailSide : MaxSide;
        var mip = 1;
        while (mip * Math.Max(nx, ny) < target) mip *= 2;
        var scale = Math.Min(1.0, (double)target / Math.Max(nativeW * nx, nativeH * ny));
        int bigW = (int)Math.Round(nativeW * nx * scale), bigH = (int)Math.Round(nativeH * ny * scale);
        using var big = SKSurface.Create(new SKImageInfo(bigW, bigH, SKColorType.Rgba8888, SKAlphaType.Premul));
        big.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        foreach (var (i, j, texture) in textures)
        {
            SKBitmap? decoded = null;
            for (var attempt = 0; attempt < 3 && decoded == null; attempt++)
                try { decoded = texture.Decode(mip)?.ToSkBitmap(); }
                catch (Exception error) when (error is IOException or InvalidDataException) { Console.Error.WriteLine($"{name} tile {i},{j}: {error.Message}"); }
            if (decoded == null) { missing.Add($"{name}_{i:00}_{j:00}"); continue; }
            using var bitmap = decoded;
            using var image = SKImage.FromBitmap(bitmap);
            big.Canvas.DrawImage(image, SKRect.Create((float)(i * bigW / (double)nx), (float)(j * bigH / (double)ny), (float)(bigW / (double)nx), (float)(bigH / (double)ny)), paint);
        }
        using var bigSnapshot = big.Snapshot();

        // Detail tiles of the large painting.
        object? detail = null;
        if (detailed && Math.Max(bigW, bigH) > MaxSide)
        {
            var dir = Path.Combine(tilesDir, mapId.ToString());
            Directory.CreateDirectory(dir);
            int cols = (bigW + DetailTile - 1) / DetailTile, rows = (bigH + DetailTile - 1) / DetailTile;
            for (var tx = 0; tx < cols; tx++)
                for (var ty = 0; ty < rows; ty++)
                {
                    var rect = SKRectI.Create(tx * DetailTile, ty * DetailTile, Math.Min(DetailTile, bigW - tx * DetailTile), Math.Min(DetailTile, bigH - ty * DetailTile));
                    using var piece = bigSnapshot.Subset(rect);
                    using var encoded = piece.Encode(SKEncodedImageFormat.Webp, 80);
                    File.WriteAllBytes(Path.Combine(dir, $"{tx}_{ty}.webp"), encoded.ToArray());
                }
            detail = new { width = bigW, height = bigH, tile = DetailTile, cols, rows };
        }

        // The overview: the painting scaled to <= MaxSide.
        var overviewScale = Math.Min(1.0, (double)MaxSide / Math.Max(bigW, bigH));
        int width = (int)Math.Round(bigW * overviewScale), height = (int)Math.Round(bigH * overviewScale);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        surface.Canvas.DrawImage(bigSnapshot, SKRect.Create(0, 0, width, height), paint);
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
        var tileName = tiles[0].Package;
        var entry = JObject.FromObject(new
        {
            name, file, width, height, bytes = bytes.Length, webpQuality = quality, detail, missingTiles = missing.Count > 0 ? missing : null,
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
                sectorWorldSize = sectorWorld, sectorTextureSize = (int?)d["SectorTextureSize"],
                tiles = tileName[..tileName.LastIndexOf('/')] + $"/{name}_<X>_<Y>", cookedTileSize = new[] { nativeW, nativeH }
            }
        }, withoutNulls);
        maps[mapId] = entry;
        // Other maps on the same level share the painting.
        foreach (var other in users.Where(u => u != mapId && !maps.ContainsKey(u)))
        {
            var alias = (JObject)entry.DeepClone();
            alias["baseMapId"] = mapId;
            maps[other] = alias;
        }
        Console.WriteLine($"{mapId} {name}: {nx}x{ny} tiles of {nativeW}x{nativeH} -> {width}x{height}, {bytes.Length} bytes (q{quality})" +
            (detail != null ? $", detail {bigW}x{bigH}" : "") + (users.Count > 1 ? $", shared by {users.Count} maps" : "") + (missing.Count > 0 ? $", {missing.Count} tiles unreadable" : ""));
    }
    // Instance layers share their base map's world coordinates (CANDIDATE: same level, same frame).
    if (basesPath != null)
    {
        var bases = JObject.Parse(File.ReadAllText(basesPath))["bases"]!.ToObject<Dictionary<long, long>>()!;
        foreach (var (layer, baseId) in bases)
            if (!maps.ContainsKey(layer) && maps.TryGetValue(baseId, out var baseEntry))
            {
                var alias = (JObject)baseEntry.DeepClone();
                alias["baseMapId"] = (long?)baseEntry["baseMapId"] ?? baseId;
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
