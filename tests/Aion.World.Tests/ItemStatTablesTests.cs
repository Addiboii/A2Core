using System.Text.Json;
using Aion.World;
using Xunit;

namespace Aion.World.Tests;

public sealed class ItemStatTablesTests
{
    [Fact]
    public void Parses_sub_stats_by_name_and_wire_id()
    {
        var tables = ItemStatTables.Embedded;
        var byName = JsonDocument.Parse("""{"sub_stats":[["CombatSpeed",660]]}""").RootElement;
        var byWire = JsonDocument.Parse("""{"sub_stats":[[282,660]]}""").RootElement;
        var empty = JsonDocument.Parse("{}").RootElement;
        Assert.Equal(660, tables.SumInstance(byName, empty, "CombatSpeed"));
        Assert.Equal(660, tables.SumInstance(byWire, empty, "CombatSpeed"));
    }
}
