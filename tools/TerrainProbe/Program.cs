// TerrainProbe: the ground height grid of maps from their landscape components.
// Reads every LandscapeComponent in a map's packages (main level, sublevels, and World Partition
// cells with a .ubulk, where heightmaps keep their pixels), decodes the heightmap texture (BGRA8,
// height = R << 8 | G, as CUE4Parse's FLandscapeComponentDataInterface) and places each vertex
// with the component's world transform: Z = transform of (x, y, (height - 32768) / 128). Writes a
// float32 grid (NaN where no landscape) and a report per map, under research/local/ (extracted
// game data; not for the repository).
//
// Usage: TerrainProbe <Paks> <key report> <Oodle DLL> <usmap> <map package dir> <new output dir> [--all]
//        TerrainProbe <Paks> <key report> <Oodle DLL> <usmap> <maps.json> <output root> [--all]
//   maps.json: tools/map_geometry.py list ([{id, name, dir}]); each map goes to <output root>/<id>,
//   maps already extracted are skipped. --all scans every World Partition cell, not only those with a .ubulk.
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.Component.Landscape;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Versions;

if (args.Length is < 6 or > 7)
    throw new ArgumentException("Usage: TerrainProbe <Paks> <key report> <Oodle DLL> <usmap> <map package dir | maps.json> <output dir | root> [--all]");
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[5]);
if (!output.StartsWith(local, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Write under research/local/");
var scanAll = args.Length == 7 && args[6] == "--all";
var batch = args[4].EndsWith(".json", StringComparison.OrdinalIgnoreCase);
if (!batch && Directory.Exists(output)) throw new ArgumentException("Use a new output directory");
using var keyReport = JsonDocument.Parse(File.ReadAllText(args[1]));
OodleHelper.Initialize(Path.GetFullPath(args[2]));
using var provider = new DefaultFileProvider(args[0], SearchOption.AllDirectories,
    new VersionContainer(EGame.GAME_Aion2), StringComparer.OrdinalIgnoreCase);
provider.MappingsContainer = new FileUsmapTypeMappingsProvider(args[3]);
provider.Initialize();
provider.SubmitKey(new FGuid(), new FAesKey(keyReport.RootElement.GetProperty("key_hex").GetString()!));
provider.PostMount();
var umaps = provider.Files.Keys.Where(k => k.EndsWith(".umap", StringComparison.OrdinalIgnoreCase)).ToList();
var bulks = provider.Files.Keys.Where(k => k.EndsWith(".ubulk", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);

if (!batch)
{
    Console.WriteLine(Extract(args[4], output));
    return;
}
var maps = JsonSerializer.Deserialize<List<MapEntry>>(File.ReadAllText(args[4]), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
var summary = new List<string>();
foreach (var map in maps)
{
    var target = Path.Combine(output, map.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (Directory.Exists(target)) { summary.Add($"{map.Id} {map.Name}: already extracted"); continue; }
    string line;
    try { line = Extract(map.Dir, target); }
    catch (Exception error) { line = $"failed: {error.GetType().Name}: {error.Message}"; }
    summary.Add($"{map.Id} {map.Name}: {line}");
    Console.WriteLine(summary[^1]);
}
Directory.CreateDirectory(output);
File.WriteAllLines(Path.Combine(output, "batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt"), summary);

string Extract(string mapDir, string outputDir)
{
    var prefix = mapDir.TrimEnd('/') + "/";
    var cells = umaps.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        .Where(k => scanAll || !k.Contains("/_Generated_/", StringComparison.OrdinalIgnoreCase) || bulks.Contains(Path.ChangeExtension(k, ".ubulk")))
        .OrderBy(k => k, StringComparer.Ordinal).ToList();
    if (cells.Count == 0) return "no packages";
    var components = new List<Component>();
    var failures = new List<object>();
    foreach (var cell in cells)
    {
        try
        {
            foreach (var export in provider.LoadPackage(Path.ChangeExtension(cell, null)).GetExports())
                if (export is ULandscapeComponent component)
                    components.Add(Read(component, cell));
        }
        catch (Exception error)
        {
            failures.Add(new { cell, error = error.GetType().Name, error.Message });
        }
    }
    if (components.Count == 0) return $"no landscape ({cells.Count} packages, {failures.Count} failed)";

    // One grid per map, on the lattice of its most common landscape scale; other scales are reported.
    var scale = components.GroupBy(c => (Math.Round(c.StepX, 3), Math.Round(c.StepY, 3))).OrderByDescending(g => g.Count()).First().Key;
    var (stepX, stepY) = scale;
    var used = components.Where(c => Math.Round(c.StepX, 3) == stepX && Math.Round(c.StepY, 3) == stepY).ToList();
    var mixed = components.Except(used).ToList();
    var minX = used.Min(c => c.OriginX);
    var minY = used.Min(c => c.OriginY);
    var maxX = used.Max(c => c.OriginX + (c.Size - 1) * c.StepX);
    var maxY = used.Max(c => c.OriginY + (c.Size - 1) * c.StepY);
    var width = (int)Math.Round((maxX - minX) / stepX) + 1;
    var height = (int)Math.Round((maxY - minY) / stepY) + 1;
    if ((long)width * height > 64_000_000) return $"grid too large ({width}x{height}); not written";
    var grid = new float[(long)width * height];
    Array.Fill(grid, float.NaN);
    var offLattice = 0;
    foreach (var c in used)
    {
        var gx = (c.OriginX - minX) / stepX;
        var gy = (c.OriginY - minY) / stepY;
        if (Math.Abs(gx - Math.Round(gx)) > 0.01 || Math.Abs(gy - Math.Round(gy)) > 0.01) offLattice++;
        var ox = (int)Math.Round(gx);
        var oy = (int)Math.Round(gy);
        for (var y = 0; y < c.Size; y++)
            for (var x = 0; x < c.Size; x++)
                grid[(long)(oy + y) * width + ox + x] = c.Heights[y * c.Size + x];
    }

    Directory.CreateDirectory(outputDir);
    using (var stream = new FileStream(Path.Combine(outputDir, "terrain.bin"), FileMode.CreateNew))
    using (var writer = new BinaryWriter(stream))
    {
        // Header: magic, version, origin X/Y, step X/Y (world units), width, height; then float32 rows (Y major).
        writer.Write("AIONHGT1"u8);
        writer.Write(1);
        writer.Write(minX); writer.Write(minY); writer.Write(stepX); writer.Write(stepY);
        writer.Write(width); writer.Write(height);
        foreach (var value in grid) writer.Write(value);
    }
    var filled = grid.LongCount(v => !float.IsNaN(v));
    var report = new
    {
        map = mapDir, cellsScanned = cells.Count, components = components.Count, scanAll,
        grid = new { originX = minX, originY = minY, stepX, stepY, width, height, filled, coverage = (double)filled / grid.LongLength },
        heightRange = new { min = grid.Where(v => !float.IsNaN(v)).Min(), max = grid.Where(v => !float.IsNaN(v)).Max() },
        mixedScaleComponents = mixed.Select(c => new { c.Cell, c.Name, c.StepX, c.StepY }), offLatticeComponents = offLattice,
        componentList = used.Select(c => new { c.Cell, c.Name, c.OriginX, c.OriginY, c.Size, c.StepX }),
        failures,
        limitation = "Landscape only: no static meshes (rocks, buildings), water or navmesh. Rotated landscapes are not supported.",
    };
    File.WriteAllText(Path.Combine(outputDir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return $"{components.Count} components -> {width}x{height} grid, step {stepX}x{stepY}, {(double)filled / grid.LongLength:P1} covered, " +
           $"{failures.Count} failed packages, {mixed.Count} other-scale, {offLattice} off-lattice";
}

static Component Read(ULandscapeComponent component, string cell)
{
    var texture = component.GetHeightmap() ?? throw new InvalidOperationException("No heightmap texture");
    var mip = texture.GetMip(0) ?? throw new InvalidOperationException("No heightmap mip 0");
    var pixels = mip.BulkData?.Data ?? throw new InvalidOperationException("No heightmap pixels");
    var stride = texture.PlatformData.SizeX;
    var offsetX = (int)(texture.PlatformData.SizeX * component.HeightmapScaleBias.Z);
    var offsetY = (int)(texture.PlatformData.SizeY * component.HeightmapScaleBias.W);
    var size = component.ComponentSizeQuads + 1;
    var subVerts = component.SubsectionSizeQuads + 1;
    var transform = component.GetComponentTransform();
    var heights = new float[size * size];
    for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            // Vertex -> texel (subsections duplicate their shared edge vertex), as the engine does.
            var subNumX = Math.Max(0, (x - 1) / (subVerts - 1));
            var subNumY = Math.Max(0, (y - 1) / (subVerts - 1));
            var subX = x == 0 ? 0 : (x - 1) % (subVerts - 1) + 1;
            var subY = y == 0 ? 0 : (y - 1) % (subVerts - 1) + 1;
            var texelX = subNumX * subVerts + subX + offsetX;
            var texelY = subNumY * subVerts + subY + offsetY;
            var index = (texelX + texelY * stride) * 4;   // B G R A
            var raw = (pixels[index + 2] << 8) | pixels[index + 1];
            var world = transform.TransformPosition(new FVector(x, y, (raw - 32768) / 128f));
            heights[y * size + x] = world.Z;
        }
    var origin = transform.TransformPosition(new FVector(0, 0, 0));
    var stepped = transform.TransformPosition(new FVector(1, 1, 0));
    return new Component(cell, component.Name, origin.X, origin.Y, stepped.X - origin.X, stepped.Y - origin.Y, size, heights);
}

sealed record Component(string Cell, string Name, double OriginX, double OriginY, double StepX, double StepY, int Size, float[] Heights);
sealed record MapEntry(int Id, string Name, string Dir);
