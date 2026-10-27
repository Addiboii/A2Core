// NavProbe: maps' walkable navigation meshes (Recast/Detour) for the server.
// Reads every URecastNavMeshDataChunk (World Partition) and ARecastNavMesh (classic levels) in a
// map's packages (CUE4Parse decodes the Detour tiles), converts the polygons from Recast space
// (Y up) to Unreal space ((-x, -z, y)), links neighbours inside tiles (dtPoly.neis) and across
// tile borders (edges flagged 0x8000, matched by overlap like Detour's connectExtLinks), and
// writes nav.bin + report.json per map under research/local/ (extracted game data; not for the
// repository).
//
// Usage: NavProbe <Paks> <key report> <Oodle DLL> <usmap> <map package dir> <new output dir>
//        NavProbe <Paks> <key report> <Oodle DLL> <usmap> <maps.json> <output root>
//   maps.json: tools/map_geometry.py list; each map goes to <output root>/<id>, extracted maps are skipped.
using System.Text.Json;
using CUE4Parse.Compression;
using CUE4Parse.Encryption.Aes;
using CUE4Parse.FileProvider;
using CUE4Parse.MappingsProvider.Usmap;
using CUE4Parse.UE4.Assets.Exports.NavigationSystem;
using CUE4Parse.UE4.Assets.Exports.NavigationSystem.Detour;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.NavigationSystem.NavMesh;
using CUE4Parse.UE4.Versions;

if (args.Length != 6)
    throw new ArgumentException("Usage: NavProbe <Paks> <key report> <Oodle DLL> <usmap> <map package dir | maps.json> <output dir | root>");
var local = Path.GetFullPath("research/local") + Path.DirectorySeparatorChar;
var output = Path.GetFullPath(args[5]);
if (!output.StartsWith(local, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Write under research/local/");
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
    mapDir = mapDir.TrimEnd('/');
    var packages = umaps.Where(k => k.StartsWith(mapDir + "/", StringComparison.OrdinalIgnoreCase)).OrderBy(k => k, StringComparer.Ordinal).ToList();
    if (packages.Count == 0) return "no packages";

    var polys = new List<Poly>();
    var tiles = new List<object>();
    var failures = new List<object>();
    var versions = new HashSet<string>();
    var seenTiles = new HashSet<(int X, int Y, int Layer, int Resolution)>();
    var duplicateTiles = 0;
    var scanned = 0;
    foreach (var path in packages)
    {
        scanned++;
        try
        {
            var package = provider.LoadPackage(Path.ChangeExtension(path, null));
            foreach (var export in package.GetExports())
                if (export is URecastNavMeshDataChunk or ARecastNavMesh { RecastNavMeshImpl: not null })
                {
                    var (version, tileData) = export is URecastNavMeshDataChunk chunk ? (chunk.NavMeshVersion, chunk.Tiles)
                        : (((ARecastNavMesh)export).NavMeshVersion, ((ARecastNavMesh)export).RecastNavMeshImpl!.DetourMeshTiles ?? []);
                    versions.Add(version.ToString());
                    foreach (var data in tileData)
                        if (data is { IsValid: true, Tile: { } tile })
                        {
                            var h = tile.Header;
                            if (!seenTiles.Add((h.X, h.Y, h.Layer, h.Resolution))) { duplicateTiles++; continue; }
                            var added = AddTile(tile, polys);
                            tiles.Add(new { cell = path, h.X, h.Y, h.Layer, h.Resolution, polys = added, h.walkableHeight, h.walkableRadius, h.walkableClimb });
                        }
                }
        }
        catch (Exception error)
        {
            failures.Add(new { path, error = error.GetType().Name, error.Message });
        }
    }
    if (polys.Count == 0) return $"no navigation mesh ({packages.Count} packages, {failures.Count} failed)";

    // Cross-tile portals: border edges whose segments overlap (in XY, close in Z) with an opposite border edge.
    var borders = new Dictionary<(long, long), List<(int Poly, int Edge)>>();
    for (var p = 0; p < polys.Count; p++)
        for (var e = 0; e < polys[p].Verts.Length; e++)
            if (polys[p].External[e])
            {
                var (a, b) = polys[p].Edge(e);
                foreach (var key in Cells(a, b)) (borders.TryGetValue(key, out var list) ? list : borders[key] = []).Add((p, e));
            }
    var crossLinks = 0;
    var linked = new HashSet<(int, int, int, int)>();
    foreach (var bucket in borders.Values)
        for (var i = 0; i < bucket.Count; i++)
            for (var j = i + 1; j < bucket.Count; j++)
            {
                var (p1, e1) = bucket[i];
                var (p2, e2) = bucket[j];
                if (p1 == p2 || polys[p1].Tile == polys[p2].Tile || !linked.Add((Math.Min(p1, p2), p1 < p2 ? e1 : e2, Math.Max(p1, p2), p1 < p2 ? e2 : e1))) continue;
                if (Overlap(polys[p1].Edge(e1), polys[p2].Edge(e2)) is { } portal)
                {
                    polys[p1].Links.Add(new Link(p2, portal.A, portal.B));
                    polys[p2].Links.Add(new Link(p1, portal.B, portal.A));
                    crossLinks++;
                }
            }

    Directory.CreateDirectory(outputDir);
    using (var stream = new FileStream(Path.Combine(outputDir, "nav.bin"), FileMode.CreateNew))
    using (var w = new BinaryWriter(stream))
    {
        // "AIONNAV1", version 1, poly count; per poly: area, vert count, verts (xyz float32, Unreal space),
        // link count, links (neighbour index, portal A xyz, portal B xyz), detail triangle count, triangles (3 x xyz).
        w.Write("AIONNAV1"u8);
        w.Write(1);
        w.Write(polys.Count);
        foreach (var p in polys)
        {
            w.Write(p.Area);
            w.Write((byte)p.Verts.Length);
            foreach (var v in p.Verts) Write(w, v);
            w.Write((ushort)p.Links.Count);
            foreach (var link in p.Links) { w.Write(link.To); Write(w, link.A); Write(w, link.B); }
            w.Write((ushort)p.Detail.Count);
            foreach (var tri in p.Detail) { Write(w, tri.A); Write(w, tri.B); Write(w, tri.C); }
        }
    }
    var all = polys.SelectMany(p => p.Verts).ToList();
    var report = new
    {
        map = mapDir, packagesScanned = packages.Count, versions, tileCount = tiles.Count, duplicateTiles,
        polygons = polys.Count, internalLinks = polys.Sum(p => p.Links.Count) - 2 * crossLinks, crossTileLinks = crossLinks,
        unlinkedBorderEdges = polys.Sum(p => p.External.Count(x => x)) - 2 * crossLinks,
        areas = polys.GroupBy(p => p.Area).ToDictionary(g => g.Key.ToString(), g => g.Count()),
        bounds = new { minX = all.Min(v => v.X), maxX = all.Max(v => v.X), minY = all.Min(v => v.Y), maxY = all.Max(v => v.Y), minZ = all.Min(v => v.Z), maxZ = all.Max(v => v.Z) },
        tiles, failures,
        limitation = "Ground polygons only (off-mesh connections and segments skipped). Cross-tile links matched by edge overlap.",
    };
    File.WriteAllText(Path.Combine(outputDir, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return $"{tiles.Count} tiles ({duplicateTiles} duplicates), {polys.Count} polygons, {crossLinks} cross-tile links, versions {string.Join(",", versions)}, {failures.Count} failed packages";
}

static (double X, double Y, double Z) ToUnreal(FVector r) => (-r.X, -r.Z, r.Y);

static void Write(BinaryWriter w, (double X, double Y, double Z) v) { w.Write((float)v.X); w.Write((float)v.Y); w.Write((float)v.Z); }

static int AddTile(DetourMeshTile tile, List<Poly> polys)
{
    var tileIndex = polys.Count == 0 ? 0 : polys[^1].Tile + 1;
    var first = polys.Count;
    var count = 0;
    for (var i = 0; i < tile.Polys.Length; i++)
    {
        var poly = tile.Polys[i];
        if (poly.AreaAndType >> 6 != 0) { polys.Add(Poly.Skipped(tileIndex)); continue; }   // off-mesh connection
        var verts = Enumerable.Range(0, poly.VertCount).Select(k => ToUnreal(tile.Vertices[poly.Verts[k]])).ToArray();
        var external = Enumerable.Range(0, poly.VertCount).Select(k => (poly.Neis[k] & 0x8000) != 0).ToArray();
        var p = new Poly(tileIndex, (byte)(poly.AreaAndType & 0x3f), verts, external);
        for (var k = 0; k < poly.VertCount; k++)
            if (poly.Neis[k] is > 0 and < 0x8000)
                p.Links.Add(new Link(first + poly.Neis[k] - 1, verts[k], verts[(k + 1) % poly.VertCount]));
        if (i < tile.DetailMeshes.Length)
        {
            var detail = tile.DetailMeshes[i];
            (double, double, double) Vertex(byte index) => index < poly.VertCount ? verts[index]
                : ToUnreal(tile.DetailVertices[detail.VertBase + index - poly.VertCount]);
            for (var t = 0; t < detail.TriCount; t++)
            {
                var tri = tile.DetailTris[detail.TriBase + t];
                p.Detail.Add(new Tri(Vertex(tri[0]), Vertex(tri[1]), Vertex(tri[2])));
            }
        }
        polys.Add(p);
        count++;
    }
    return count;
}

static IEnumerable<(long, long)> Cells((double X, double Y, double Z) a, (double X, double Y, double Z) b)
{
    const double BorderCell = 400;   // border-edge hashing cell
    var keys = new HashSet<(long, long)>();
    var steps = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y)) / BorderCell));
    for (var s = 0; s <= steps; s++)
    {
        var t = (double)s / steps;
        keys.Add(((long)Math.Floor((a.X + (b.X - a.X) * t) / BorderCell), (long)Math.Floor((a.Y + (b.Y - a.Y) * t) / BorderCell)));
    }
    return keys;
}

// Two border edges form a portal when they lie on the same line (opposite directions), overlap, and are close in height.
static ((double X, double Y, double Z) A, (double X, double Y, double Z) B)? Overlap(
    ((double X, double Y, double Z) A, (double X, double Y, double Z) B) e1, ((double X, double Y, double Z) A, (double X, double Y, double Z) B) e2)
{
    double dx = e1.B.X - e1.A.X, dy = e1.B.Y - e1.A.Y;
    var length = Math.Sqrt(dx * dx + dy * dy);
    if (length < 1) return null;
    double ux = dx / length, uy = dy / length;
    double Off((double X, double Y, double Z) p) => Math.Abs((p.X - e1.A.X) * uy - (p.Y - e1.A.Y) * ux);
    if (Off(e2.A) > 2 || Off(e2.B) > 2) return null;
    double Along((double X, double Y, double Z) p) => (p.X - e1.A.X) * ux + (p.Y - e1.A.Y) * uy;
    double s1 = Math.Max(0, Math.Min(Along(e2.A), Along(e2.B))), s2 = Math.Min(length, Math.Max(Along(e2.A), Along(e2.B)));
    if (s2 - s1 < 1) return null;
    (double X, double Y, double Z) At(double s) => (e1.A.X + ux * s, e1.A.Y + uy * s, e1.A.Z + (e1.B.Z - e1.A.Z) * s / length);
    double Z2(double s) { var a = Along(e2.A); var b = Along(e2.B); var t = Math.Abs(b - a) < 1e-6 ? 0 : (s - a) / (b - a); return e2.A.Z + (e2.B.Z - e2.A.Z) * t; }
    if (Math.Abs(At(s1).Z - Z2(s1)) > 100 || Math.Abs(At(s2).Z - Z2(s2)) > 100) return null;   // different floors
    return (At(s1), At(s2));
}

sealed record MapEntry(int Id, string Name, string Dir);
sealed record Link(int To, (double X, double Y, double Z) A, (double X, double Y, double Z) B);
sealed record Tri((double X, double Y, double Z) A, (double X, double Y, double Z) B, (double X, double Y, double Z) C);
sealed class Poly(int tile, byte area, (double X, double Y, double Z)[] verts, bool[] external)
{
    public int Tile { get; } = tile;
    public byte Area { get; } = area;
    public (double X, double Y, double Z)[] Verts { get; } = verts;
    public bool[] External { get; } = external;
    public List<Link> Links { get; } = [];
    public List<Tri> Detail { get; } = [];
    public ((double X, double Y, double Z) A, (double X, double Y, double Z) B) Edge(int e) => (Verts[e], Verts[(e + 1) % Verts.Length]);
    public static Poly Skipped(int tile) => new(tile, 255, [], []);
}
