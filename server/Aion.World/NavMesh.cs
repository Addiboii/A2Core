using System.Collections.Concurrent;
using Aion.Protocol.Native;

namespace Aion.World;

/// <summary>
/// A map's walkable navigation mesh (tools/NavProbe: the client's Recast/Detour tiles in Unreal
/// space, neighbours linked inside and across tiles). Checked against the official data: 272 of
/// 274 Poeta spawns and 1811 of 1879 recorded official NPC positions stand on it within 60.
/// Finds the floor under a point (detail triangles: ground, bridges, building floors) and
/// paths around obstacles (A* over polygons, then the funnel algorithm).
/// </summary>
public sealed class NavMesh
{
    const double Cell = 1000;
    readonly int[] vertStart, linkStart, triStart;
    readonly float[] verts, portals, tris;
    readonly int[] linkTo;
    readonly Dictionary<long, int[]> grid;

    public int PolygonCount => vertStart.Length - 1;

    NavMesh(int[] vertStart, float[] verts, int[] linkStart, int[] linkTo, float[] portals, int[] triStart, float[] tris)
    {
        (this.vertStart, this.verts, this.linkStart, this.linkTo, this.portals, this.triStart, this.tris) =
            (vertStart, verts, linkStart, linkTo, portals, triStart, tris);
        var cells = new Dictionary<long, List<int>>();
        for (var p = 0; p < PolygonCount; p++)
        {
            if (vertStart[p + 1] == vertStart[p]) continue;
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            for (var v = vertStart[p]; v < vertStart[p + 1]; v++)
            {
                minX = Math.Min(minX, verts[v * 3]); maxX = Math.Max(maxX, verts[v * 3]);
                minY = Math.Min(minY, verts[v * 3 + 1]); maxY = Math.Max(maxY, verts[v * 3 + 1]);
            }
            for (var cx = (long)Math.Floor(minX / Cell); cx <= (long)Math.Floor(maxX / Cell); cx++)
                for (var cy = (long)Math.Floor(minY / Cell); cy <= (long)Math.Floor(maxY / Cell); cy++)
                    (cells.TryGetValue(Key(cx, cy), out var list) ? list : cells[Key(cx, cy)] = []).Add(p);
        }
        grid = cells.ToDictionary(c => c.Key, c => c.Value.ToArray());
    }

    static long Key(long cx, long cy) => (cx << 32) ^ (cy & 0xffffffff);

    /// <summary>NavProbe's nav.bin ("AIONNAV1", version 1): polygons with vertices, links (neighbour + portal) and detail triangles.</summary>
    public static NavMesh Read(Stream stream)
    {
        using var r = new BinaryReader(stream);
        if (!r.ReadBytes(8).AsSpan().SequenceEqual("AIONNAV1"u8) || r.ReadInt32() != 1) throw new InvalidDataException("Not a version 1 navigation mesh");
        var count = r.ReadInt32();
        if (count is < 0 or > 10_000_000) throw new InvalidDataException("Invalid polygon count");
        var vertStart = new int[count + 1];
        var linkStart = new int[count + 1];
        var triStart = new int[count + 1];
        List<float> verts = [], portals = [], tris = [];
        List<int> linkTo = [];
        for (var p = 0; p < count; p++)
        {
            vertStart[p] = verts.Count / 3; linkStart[p] = linkTo.Count; triStart[p] = tris.Count / 9;
            r.ReadByte();   // area
            var vc = r.ReadByte();
            for (var i = 0; i < vc * 3; i++) verts.Add(r.ReadSingle());
            var lc = r.ReadUInt16();
            for (var i = 0; i < lc; i++)
            {
                var to = r.ReadInt32();
                if (to < 0 || to >= count) throw new InvalidDataException("Link outside the mesh");
                linkTo.Add(to);
                for (var k = 0; k < 6; k++) portals.Add(r.ReadSingle());
            }
            var tc = r.ReadUInt16();
            for (var i = 0; i < tc * 9; i++) tris.Add(r.ReadSingle());
        }
        vertStart[count] = verts.Count / 3; linkStart[count] = linkTo.Count; triStart[count] = tris.Count / 9;
        return new NavMesh(vertStart, [.. verts], linkStart, [.. linkTo], [.. portals], triStart, [.. tris]);
    }

    /// <summary>Height of polygon p's detail surface at (x, y), or null when (x, y) is outside it.</summary>
    double? SurfaceZ(int p, double x, double y)
    {
        for (var t = triStart[p]; t < triStart[p + 1]; t++)
        {
            var o = t * 9;
            double ax = tris[o], ay = tris[o + 1], az = tris[o + 2], bx = tris[o + 3], by = tris[o + 4], bz = tris[o + 5], cx = tris[o + 6], cy = tris[o + 7], cz = tris[o + 8];
            var d = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy);
            if (Math.Abs(d) < 1e-9) continue;
            var l1 = ((by - cy) * (x - cx) + (cx - bx) * (y - cy)) / d;
            var l2 = ((cy - ay) * (x - cx) + (ax - cx) * (y - cy)) / d;
            var l3 = 1 - l1 - l2;
            if (l1 >= -1e-4 && l2 >= -1e-4 && l3 >= -1e-4) return l1 * az + l2 * bz + l3 * cz;
        }
        return null;
    }

    /// <summary>The walkable floor under (x, y) closest to z (within maxDz), as (polygon, floor height).</summary>
    public (int Poly, double Z)? Locate(double x, double y, double z, double maxDz = 300)
    {
        if (!grid.TryGetValue(Key((long)Math.Floor(x / Cell), (long)Math.Floor(y / Cell)), out var candidates)) return null;
        (int, double)? best = null;
        var bestDz = maxDz;
        foreach (var p in candidates)
            if (SurfaceZ(p, x, y) is { } floor && Math.Abs(floor - z) <= bestDz)
            {
                bestDz = Math.Abs(floor - z);
                best = (p, floor);
            }
        return best;
    }

    /// <summary>Floor height under (x, y) near z, or null off the mesh.</summary>
    public double? FloorAt(double x, double y, double z, double maxDz = 300) => Locate(x, y, z, maxDz)?.Z;

    (double X, double Y) Centroid(int p)
    {
        double x = 0, y = 0;
        var n = vertStart[p + 1] - vertStart[p];
        for (var v = vertStart[p]; v < vertStart[p + 1]; v++) { x += verts[v * 3]; y += verts[v * 3 + 1]; }
        return (x / n, y / n);
    }

    /// <summary>
    /// Waypoints from <paramref name="from"/> to <paramref name="to"/> on the mesh (excluding the
    /// start), or null when either end is off the mesh or no path exists within maxNodes polygons.
    /// </summary>
    public List<Vector3d>? FindPath(Vector3d from, Vector3d to, int maxNodes = 4096)
    {
        if (Locate(from.X, from.Y, from.Z) is not { } start || Locate(to.X, to.Y, to.Z) is not { } goal) return null;
        var end = new Vector3d(to.X, to.Y, goal.Z);
        if (start.Poly == goal.Poly) return [end];
        // A* over polygons; a node's position is where the path enters it (the portal midpoint).
        var cost = new Dictionary<int, double> { [start.Poly] = 0 };
        var entry = new Dictionary<int, (double X, double Y, double Z)> { [start.Poly] = (from.X, from.Y, start.Z) };
        var parent = new Dictionary<int, (int Poly, int Link)>();
        var closed = new HashSet<int>();
        var open = new PriorityQueue<int, double>();
        open.Enqueue(start.Poly, 0);
        var found = false;
        while (open.TryDequeue(out var current, out _))
        {
            if (current == goal.Poly) { found = true; break; }
            if (!closed.Add(current)) continue;
            if (closed.Count > maxNodes) return null;
            var at = entry[current];
            for (var l = linkStart[current]; l < linkStart[current + 1]; l++)
            {
                var next = linkTo[l];
                if (closed.Contains(next)) continue;
                var o = l * 6;
                var mid = ((portals[o] + portals[o + 3]) / 2.0, (portals[o + 1] + portals[o + 4]) / 2.0, (portals[o + 2] + portals[o + 5]) / 2.0);
                var g = cost[current] + Dist(at, mid);
                if (g >= cost.GetValueOrDefault(next, double.MaxValue)) continue;
                cost[next] = g; entry[next] = mid; parent[next] = (current, l);
                open.Enqueue(next, g + Dist(mid, (end.X, end.Y, end.Z)));
            }
        }
        if (!found) return null;
        // Portals along the corridor, oriented left/right of the direction of travel.
        var chain = new List<(int Poly, int Link)>();
        for (var p = goal.Poly; p != start.Poly; p = parent[p].Poly) chain.Add(parent[p]);
        chain.Reverse();
        var lefts = new List<(double X, double Y, double Z)> { (from.X, from.Y, start.Z) };
        var rights = new List<(double X, double Y, double Z)> { (from.X, from.Y, start.Z) };
        foreach (var (poly, link) in chain)
        {
            var o = link * 6;
            (double X, double Y, double Z) a = (portals[o], portals[o + 1], portals[o + 2]), b = (portals[o + 3], portals[o + 4], portals[o + 5]);
            var c = Centroid(poly);
            var n = Centroid(linkTo[link]);
            var cross = (n.X - c.X) * (a.Y - c.Y) - (n.Y - c.Y) * (a.X - c.X);
            if (cross > 0) { lefts.Add(a); rights.Add(b); } else { lefts.Add(b); rights.Add(a); }
        }
        lefts.Add((end.X, end.Y, end.Z)); rights.Add((end.X, end.Y, end.Z));
        return Funnel(lefts, rights);
    }

    static double Dist((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z));

    /// <summary>Signed area: positive when c is left of a→b (Y towards +Y, X towards +X).</summary>
    static double Area((double X, double Y, double Z) a, (double X, double Y, double Z) b, (double X, double Y, double Z) c) =>
        (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static bool Same((double X, double Y, double Z) a, (double X, double Y, double Z) b) =>
        Math.Abs(a.X - b.X) < 1e-3 && Math.Abs(a.Y - b.Y) < 1e-3;

    /// <summary>The "simple stupid funnel" string pulling over the portal corridor (start and end are degenerate portals).</summary>
    static List<Vector3d> Funnel(List<(double X, double Y, double Z)> lefts, List<(double X, double Y, double Z)> rights)
    {
        var path = new List<Vector3d>();
        var apex = lefts[0];
        var left = lefts[0];
        var right = rights[0];
        int apexIndex = 0, leftIndex = 0, rightIndex = 0;
        for (var i = 1; i < lefts.Count; i++)
        {
            var l = lefts[i];
            var r = rights[i];
            // Narrow the right side.
            if (Area(apex, right, r) >= 0)
            {
                if (Same(apex, right) || Area(apex, left, r) < 0) { right = r; rightIndex = i; }
                else
                {
                    path.Add(new Vector3d(left.X, left.Y, left.Z));
                    apex = left; apexIndex = leftIndex; right = apex; rightIndex = apexIndex; i = apexIndex;
                    continue;
                }
            }
            // Narrow the left side.
            if (Area(apex, left, l) <= 0)
            {
                if (Same(apex, left) || Area(apex, right, l) > 0) { left = l; leftIndex = i; }
                else
                {
                    path.Add(new Vector3d(right.X, right.Y, right.Z));
                    apex = right; apexIndex = rightIndex; left = apex; leftIndex = apexIndex; i = apexIndex;
                    continue;
                }
            }
        }
        var last = lefts[^1];
        if (path.Count == 0 || !Same((path[^1].X, path[^1].Y, path[^1].Z), last)) path.Add(new Vector3d(last.X, last.Y, last.Z));
        return path;
    }
}

/// <summary>Navigation meshes by map, loaded on first use and shared: &lt;directory&gt;/&lt;map id&gt;/nav.bin.</summary>
public sealed class NavMeshStore(string? directory)
{
    readonly ConcurrentDictionary<int, Lazy<Task<NavMesh?>>> maps = new();

    /// <summary>The map's mesh, loaded once on a worker thread (world maps have over a million polygons). Null when there is none.</summary>
    public Task<NavMesh?> LoadAsync(int mapId) => directory is null ? Task.FromResult<NavMesh?>(null)
        : maps.GetOrAdd(mapId, id => new Lazy<Task<NavMesh?>>(() => Task.Run(() =>
        {
            var path = Path.Combine(directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture), "nav.bin");
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return (NavMesh?)NavMesh.Read(stream);
        }))).Value;

    /// <summary>The map's mesh, waiting for it (tools and tests; game loops use <see cref="LoadAsync"/>).</summary>
    public NavMesh? For(int mapId) => LoadAsync(mapId).GetAwaiter().GetResult();

    /// <summary>The configured directory, else research/local/navmesh found above <paramref name="start"/>.</summary>
    public static string? Locate(string? configured, string start)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "research", "local", "navmesh");
            if (Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
