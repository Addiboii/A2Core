using System.Text.Json;

namespace Aion.World;

/// <summary>
/// The Daevanion boards from the client's own tables (tools/daevanion_data.py → tools/data/daevanion.json): each class's boards
/// (DaevanionBoard: level, the crystal type its nodes cost), their nodes (DaevanionNode: a 15 x 15 grid, id = board * 10000 +
/// (row - 1) * 15 + col; Start, Stat and SkillLevel nodes with cost, level and refund Kina), the most points a type holds
/// (DaevanionMaxCapacity) and the DaevanionCrystal points of each level (Exp DaevanionPointMap).
/// </summary>
public sealed class DaevanionData
{
    public sealed record Board(int Id, string Class, int NeedLevel, int Order, int PointType);

    /// <summary>A node: Start, Stat (stats by EStat name) or SkillLevel (skill id and levels).</summary>
    public sealed record Node(int Id, string Type, int Cost, int NeedLevel, int ResetGold, IReadOnlyList<(string Stat, int Value)> Stats, int SkillId, int SkillLevels)
    {
        public int Board => Id / 10_000;
        public int Row => (Id % 10_000 - 1) / GridSize;
        public int Col => (Id % 10_000 - 1) % GridSize;
    }

    public const int GridSize = 15;
    public const int PointTypes = 5;   // EDaevanionPointType 1-5 (DaevanionCrystal .. Season03Crystal01)

    readonly Dictionary<int, Board> boards;
    readonly Dictionary<int, Node> nodes;
    readonly Dictionary<int, int> max;
    readonly SortedDictionary<int, int> levelPoints;

    DaevanionData(Dictionary<int, Board> boards, Dictionary<int, Node> nodes, Dictionary<int, int> max, SortedDictionary<int, int> levelPoints) =>
        (this.boards, this.nodes, this.max, this.levelPoints) = (boards, nodes, max, levelPoints);

    static readonly Lazy<DaevanionData> embedded = new(() =>
    {
        using var stream = typeof(DaevanionData).Assembly.GetManifestResourceStream("daevanion.json")
            ?? throw new InvalidOperationException("Missing embedded game data daevanion.json");
        return Parse(JsonDocument.Parse(stream));
    });

    public static DaevanionData Embedded => embedded.Value;

    public static DaevanionData Parse(JsonDocument document)
    {
        var root = document.RootElement;
        var boards = root.GetProperty("boards").EnumerateArray().Select(b => new Board(b.GetProperty("id").GetInt32(), b.GetProperty("class").GetString()!,
            b.GetProperty("level").GetInt32(), b.GetProperty("order").GetInt32(), b.GetProperty("point_type").GetInt32())).ToDictionary(b => b.Id);
        var nodes = new Dictionary<int, Node>();
        foreach (var n in root.GetProperty("nodes").EnumerateArray())
        {
            var id = n[0].GetInt32();
            var type = n[1].GetString()!;
            var effects = n[5].EnumerateArray().ToList();
            var stats = type == "Stat" ? effects.Select(e => (e[0].GetString()!, e[1].GetInt32())).ToList() : [];
            foreach (var (stat, _) in stats)
                if (StatIds.Of(stat) is null) throw new InvalidDataException($"daevanion.json: node {id} has the unknown stat {stat}");
            var (skill, levels) = type == "SkillLevel" ? (effects[0][0].GetInt32(), effects[0][1].GetInt32()) : (0, 0);
            nodes[id] = new Node(id, type, n[2].GetInt32(), n[3].GetInt32(), n[4].GetInt32(), stats, skill, levels);
        }
        var max = root.GetProperty("max").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32());
        var levelPoints = new SortedDictionary<int, int>(root.GetProperty("level_points").EnumerateObject().ToDictionary(p => int.Parse(p.Name), p => p.Value.GetInt32()));
        return new DaevanionData(boards, nodes, max, levelPoints);
    }

    public Board? BoardOf(int id) => boards.GetValueOrDefault(id);
    public Node? NodeOf(int id) => nodes.GetValueOrDefault(id);
    public IEnumerable<Node> NodesOf(int board) => nodes.Values.Where(n => n.Board == board);
    public IEnumerable<Board> Boards => boards.Values;

    /// <summary>The most points of a type a character can hold (DaevanionMaxCapacity; 0 for an unknown type).</summary>
    public int MaxPoints(int type) => max.GetValueOrDefault(type);

    /// <summary>The DaevanionCrystal points a level gives in total (Exp DaevanionPointMap: 4 at 12, 4 more per level).</summary>
    public int LevelPoints(int level) => levelPoints.LastOrDefault(p => p.Key <= level).Value;

    /// <summary>The nodes next to a node (up, down, left, right on the board's grid) that exist.</summary>
    public IEnumerable<int> Neighbours(int id)
    {
        var board = id / 10_000;
        var cell = id % 10_000 - 1;
        var (row, col) = (cell / GridSize, cell % GridSize);
        foreach (var (r, c) in new[] { (row - 1, col), (row + 1, col), (row, col - 1), (row, col + 1) })
            if (r is >= 0 and < GridSize && c is >= 0 and < GridSize && nodes.ContainsKey(board * 10_000 + r * GridSize + c + 1))
                yield return board * 10_000 + r * GridSize + c + 1;
    }
}
