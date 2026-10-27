using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Aion.World;

/// <summary>
/// Keys bucketed by their 2D position in square cells: "what is near here" visits the cells around a point
/// instead of every NPC or object on a map (the levelling maps have ~16000 NPCs and ~3500 objects).
/// Not thread-safe by itself: grids of a shared map are used under its turn.
/// </summary>
public sealed class SpatialGrid(double cellSize = SpatialGrid.DefaultCellSize)
{
    public const double DefaultCellSize = 4000;
    readonly Dictionary<(int, int), List<int>> cells = [];

    public int Count { get; private set; }

    (int, int) Cell(double x, double y) => ((int)Math.Floor(x / cellSize), (int)Math.Floor(y / cellSize));

    public void Add(int key, double x, double y)
    {
        var cell = Cell(x, y);
        if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = [];
        list.Add(key);
        Count++;
    }

    /// <summary>Keys placed within the square of <paramref name="radius"/> around (x, y) (a superset of the circle; callers check distance).</summary>
    public IEnumerable<int> Near(double x, double y, double radius)
    {
        var (x0, y0) = Cell(x - radius, y - radius);
        var (x1, y1) = Cell(x + radius, y + radius);
        for (var cx = x0; cx <= x1; cx++)
            for (var cy = y0; cy <= y1; cy++)
                if (cells.TryGetValue((cx, cy), out var list))
                    foreach (var key in list) yield return key;
    }

    /// <summary>
    /// NPCs placed by where they stood when the grid was built (their spawn points: targets are only ever added,
    /// and the grid is rebuilt when the count changes). An NPC strays at most <see cref="NpcMargin"/> from it
    /// (leash, then the walk back; patrol routes reach up to ~50 m from their spawn), so a query widened by that finds
    /// every NPC near a point.
    /// </summary>
    public const double NpcMargin = NpcAi.LeashRange + 3000;
    static readonly ConditionalWeakTable<Dictionary<int, Target>, SpatialGrid> npcGrids = new();

    public static SpatialGrid Npcs(Dictionary<int, Target> targets)
    {
        if (npcGrids.TryGetValue(targets, out var grid) && grid.Count == targets.Count) return grid;
        grid = new SpatialGrid();
        foreach (var t in targets.Values) grid.Add(t.Key, t.Position.X, t.Position.Y);
        npcGrids.AddOrUpdate(targets, grid);
        return grid;
    }

    static readonly ConcurrentDictionary<int, SpatialGrid> objectGrids = new();

    /// <summary>A map's quest objects and gather nodes by index (they never move).</summary>
    public static SpatialGrid Objects(int mapId) => objectGrids.GetOrAdd(mapId, id =>
    {
        var grid = new SpatialGrid();
        var objects = WorldObjects.ObjectsOn(id);
        for (var i = 0; i < objects.Count; i++) grid.Add(i, objects[i].Position[0], objects[i].Position[1]);
        return grid;
    });
}
