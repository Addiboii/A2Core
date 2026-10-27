using System.Collections.Concurrent;

namespace Aion.World;

/// <summary>
/// A map's ground height grid (tools/TerrainProbe: the landscape heightmaps of every World
/// Partition cell, placed with each component's world transform). Landscape only: buildings,
/// rocks and bridges are not in it. Checked against the official recording: NPC movement
/// heights sat on this grid (median +1.9 units, 1805 of 1860 within 30).
/// </summary>
public sealed class Terrain
{
    readonly double originX, originY, stepX, stepY;
    readonly int width, height;
    readonly float[] heights;

    Terrain(double originX, double originY, double stepX, double stepY, int width, int height, float[] heights)
    {
        (this.originX, this.originY, this.stepX, this.stepY, this.width, this.height, this.heights) =
            (originX, originY, stepX, stepY, width, height, heights);
    }

    /// <summary>TerrainProbe's terrain.bin: "AIONHGT1", version 1, origin/step doubles, width/height, float32 rows (NaN: none).</summary>
    public static Terrain Read(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual("AIONHGT1"u8) || reader.ReadInt32() != 1)
            throw new InvalidDataException("Not a version 1 terrain grid");
        double originX = reader.ReadDouble(), originY = reader.ReadDouble(), stepX = reader.ReadDouble(), stepY = reader.ReadDouble();
        int width = reader.ReadInt32(), height = reader.ReadInt32();
        if (width < 2 || height < 2 || stepX <= 0 || stepY <= 0 || (long)width * height > 64_000_000)
            throw new InvalidDataException("Invalid terrain grid size");
        var heights = new float[width * height];
        var bytes = reader.ReadBytes(heights.Length * 4);
        if (bytes.Length != heights.Length * 4) throw new InvalidDataException("Truncated terrain grid");
        Buffer.BlockCopy(bytes, 0, heights, 0, bytes.Length);
        return new Terrain(originX, originY, stepX, stepY, width, height, heights);
    }

    /// <summary>Ground height at (x, y), bilinear between grid points; null outside the grid or where it has no data.</summary>
    public double? GroundAt(double x, double y)
    {
        var fx = (x - originX) / stepX;
        var fy = (y - originY) / stepY;
        if (!(fx >= 0 && fy >= 0 && fx <= width - 1 && fy <= height - 1)) return null;
        int ix = Math.Min((int)fx, width - 2), iy = Math.Min((int)fy, height - 2);
        double tx = fx - ix, ty = fy - iy;
        double a = heights[iy * width + ix], b = heights[iy * width + ix + 1];
        double c = heights[(iy + 1) * width + ix], d = heights[(iy + 1) * width + ix + 1];
        var value = (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        return double.IsNaN(value) ? null : value;
    }
}

/// <summary>Terrain grids by map, loaded on first use and shared by every session: &lt;directory&gt;/&lt;map id&gt;/terrain.bin.</summary>
public sealed class TerrainStore(string? directory)
{
    readonly ConcurrentDictionary<int, Lazy<Task<Terrain?>>> maps = new();

    public string? Directory => directory;

    /// <summary>
    /// The map's grid, loaded once on a worker thread (a world map's grid is large; loading it on a
    /// game loop stalled the client for seconds). Null when there is none.
    /// </summary>
    public Task<Terrain?> LoadAsync(int mapId) => directory is null ? Task.FromResult<Terrain?>(null)
        : maps.GetOrAdd(mapId, id => new Lazy<Task<Terrain?>>(() => Task.Run(() =>
        {
            var path = Path.Combine(directory, id.ToString(System.Globalization.CultureInfo.InvariantCulture), "terrain.bin");
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return (Terrain?)Terrain.Read(stream);
        }))).Value;

    /// <summary>The map's grid, waiting for it (tools and tests; game loops use <see cref="LoadAsync"/>).</summary>
    public Terrain? For(int mapId) => LoadAsync(mapId).GetAwaiter().GetResult();

    /// <summary>The configured directory, else research/local/terrain found above <paramref name="start"/> (development checkout).</summary>
    public static string? Locate(string? configured, string start)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured);
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "research", "local", "terrain");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }
}
