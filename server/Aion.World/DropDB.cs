using System.Globalization;
using System.Xml.Linq;

namespace Aion.World;

/// <summary>
/// Extra boss drops by source (gamedata/drops/*.xml, tools/drop_xml.py; aion2hub.com rates, UNOFFICIAL): a source with a dungeon
/// gives its final boss these drops, each rolled at its percent, on top of the client reward tables (LootTables). Pity
/// ("Ceiling") rows and sources without a dungeon are kept in the file for staff but not rolled. Loaded once at start-up.
/// </summary>
public sealed class DropDB
{
    public sealed record Drop(int ItemId, double Percent);
    public sealed record Source(string Name, int Dungeon, IReadOnlyList<Drop> Drops, int Pity);

    readonly Dictionary<int, Source> byDungeon;
    public IReadOnlyList<Source> Sources { get; }
    DropDB(IReadOnlyList<Source> sources)
    {
        Sources = sources;
        byDungeon = sources.Where(s => s.Dungeon > 0).GroupBy(s => s.Dungeon).ToDictionary(g => g.Key, g => g.First());
    }

    static readonly Lazy<DropDB> embedded = new(() =>
    {
        using var stream = typeof(DropDB).Assembly.GetManifestResourceStream("drops/aion2hub.xml")
            ?? throw new InvalidOperationException("Missing embedded game data drops/aion2hub.xml");
        return Parse(XDocument.Load(stream));
    });

    public static DropDB Embedded => embedded.Value;

    public static DropDB Parse(XDocument document)
    {
        var sources = new List<Source>();
        foreach (var s in document.Root?.Elements("source") ?? [])
        {
            var name = (string?)s.Attribute("name") ?? throw new InvalidDataException("drops: a source without a name");
            var dungeon = (string?)s.Attribute("dungeon") is { } d ? int.Parse(d, CultureInfo.InvariantCulture) : 0;
            var drops = new List<Drop>();
            var pity = 0;
            foreach (var e in s.Elements("drop"))
            {
                var item = int.Parse((string)e.Attribute("item")!, CultureInfo.InvariantCulture);
                if ((string?)e.Attribute("rate") is not { } text) { pity++; continue; }
                var percent = double.Parse(text, CultureInfo.InvariantCulture);
                if (percent is < 0 or > 100) throw new InvalidDataException($"drops: {name} item {item} rate {percent} is not 0-100");
                drops.Add(new Drop(item, percent));
            }
            sources.Add(new Source(name, dungeon, drops, pity));
        }
        return new DropDB(sources);
    }

    public Source? OfDungeon(int dungeonId) => byDungeon.GetValueOrDefault(dungeonId);

    /// <summary>The source's drops for one kill: each item at its percent, one of each.</summary>
    public List<(int ItemId, long Count)> Roll(int dungeonId, Random rng) =>
        OfDungeon(dungeonId) is not { } source ? [] : source.Drops.Where(d => rng.NextDouble() * 100 < d.Percent).Select(d => (d.ItemId, 1L)).ToList();
}
