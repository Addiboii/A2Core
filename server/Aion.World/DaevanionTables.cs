using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aion.World;

/// <summary>Daevanion boards/nodes (tools/data/daevanion.json, tools/daevanion_data.py).</summary>
public sealed class DaevanionTables
{
    public sealed record Node(int BoardId, int Row, int Col, int Grade, int Type, int Cost, IReadOnlyList<(int Wire, int Value)> Stats, int SkillId);

    public sealed record Board(string ClassKey, int Slot, string God, int NeedLevel, int PointType);

    sealed record BoardJson([property: JsonPropertyName("class")] string Class, [property: JsonPropertyName("slot")] int Slot,
        [property: JsonPropertyName("god")] string God, [property: JsonPropertyName("level")] int Level, [property: JsonPropertyName("point")] int Point);

    sealed record File([property: JsonPropertyName("boards")] Dictionary<string, BoardJson> Boards,
        [property: JsonPropertyName("nodes")] Dictionary<string, JsonElement> Nodes,
        [property: JsonPropertyName("creation_slot")] Dictionary<string, int> CreationSlot,
        [property: JsonPropertyName("item_points")] Dictionary<string, ItemPointJson> ItemPoints,
        [property: JsonPropertyName("grid")] JsonElement Grid);

    public sealed record ItemPointJson([property: JsonPropertyName("point")] int PointType, [property: JsonPropertyName("amount")] int Amount);

    readonly Dictionary<int, Node> nodes;
    readonly Dictionary<int, Board> boards;
    readonly Dictionary<int, int> creationSlot;
    readonly Dictionary<int, (int PointType, int Amount)> itemPoints;
    readonly Dictionary<int, List<int>> nodesByBoard = [];
    readonly (int Row, int Col) origin;

    DaevanionTables(File file)
    {
        boards = file.Boards.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p =>
            new Board(p.Value.Class, p.Value.Slot, p.Value.God, p.Value.Level, p.Value.Point));
        creationSlot = file.CreationSlot.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => p.Value);
        itemPoints = file.ItemPoints.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p => (p.Value.PointType, p.Value.Amount));
        origin = (file.Grid.GetProperty("origin").GetProperty("r").GetInt32(), file.Grid.GetProperty("origin").GetProperty("c").GetInt32());
        nodes = file.Nodes.ToDictionary(p => int.Parse(p.Key, CultureInfo.InvariantCulture), p =>
        {
            var n = p.Value;
            var stats = new List<(int Wire, int Value)>();
            if (n.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.Array)
                foreach (var s in statsEl.EnumerateArray())
                    stats.Add((s[0].GetInt32(), s[1].GetInt32()));
            var grade = n.GetProperty("g").GetInt32();
            var cost = n.TryGetProperty("cost", out var costEl) ? costEl.GetInt32() : (grade == 0 ? 0 : DefaultCost(grade));
            var skill = n.TryGetProperty("skill", out var skillEl) ? skillEl.GetInt32() : 0;
            return new Node(n.GetProperty("b").GetInt32(), n.GetProperty("r").GetInt32(), n.GetProperty("c").GetInt32(), grade,
                n.GetProperty("t").GetInt32(), cost, stats, skill);
        });
        foreach (var (id, node) in nodes)
        {
            if (!nodesByBoard.TryGetValue(node.BoardId, out var list)) nodesByBoard[node.BoardId] = list = [];
            list.Add(id);
        }
    }

    static int DefaultCost(int grade) => grade switch { 11 => 1, 21 => 2, 31 => 3, 41 => 4, _ => 0 };

    static readonly Lazy<DaevanionTables> embedded = new(() =>
    {
        using var stream = typeof(DaevanionTables).Assembly.GetManifestResourceStream("daevanion.json")
            ?? throw new InvalidOperationException("Missing embedded game data daevanion.json");
        return new DaevanionTables(JsonSerializer.Deserialize<File>(stream) ?? throw new InvalidOperationException("Empty daevanion.json"));
    });

    public static DaevanionTables Embedded => embedded.Value;

    public int BoardCount => boards.Count;
    public int NodeCount => nodes.Count;

    public Node? NodeOf(int nodeId) => nodes.GetValueOrDefault(nodeId);
    public Board? BoardOf(int boardId) => boards.GetValueOrDefault(boardId);
    public IReadOnlyList<int> NodesOfBoard(int boardId) => nodesByBoard.GetValueOrDefault(boardId) ?? [];
    public int ClassSlot(int creationId) => creationSlot.GetValueOrDefault(creationId);
    public (int PointType, int Amount)? ItemGrant(int templateId) => itemPoints.TryGetValue(templateId, out var grant) ? grant : null;
    public (int Row, int Col) Origin => origin;

    /// <summary>Board ids for a creation profile (five gods).</summary>
    public IEnumerable<int> BoardsForCreation(int creationId)
    {
        var slot = ClassSlot(creationId);
        if (slot == 0) yield break;
        foreach (var id in boards.Keys.Where(b => boards[b].Slot == slot).OrderBy(b => b))
            yield return id;
    }

    public int StartNodeId(int boardId)
    {
        var (r, c) = origin;
        return int.Parse($"{boardId}{(r - 1) * 15 + c:0000}", CultureInfo.InvariantCulture);
    }

    public bool IsNeighbor(Node a, Node b) => a.BoardId == b.BoardId && Math.Abs(a.Row - b.Row) + Math.Abs(a.Col - b.Col) == 1;
}
