using CUE4Parse.GameTypes.Aion2.Objects;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;

namespace AionClientEditor.Editing;

public sealed class DataTableColumnDef
{
    public required string Header { get; init; }
    public required string PropertyName { get; init; }
    public int ArrayIndex { get; init; }
}

public sealed class DataTableGridCell
{
    public required string Display { get; init; }
    public FPropertyTag? Tag { get; init; }
    public PropertyEditKind EditKind { get; init; }
}

public sealed class DataTableGridRow
{
    public required string RowKey { get; init; }
    public required FStructFallback Struct { get; init; }
    public required IReadOnlyList<DataTableGridCell> Cells { get; init; }
}

public sealed class DataTableGridModel
{
    public IReadOnlyList<DataTableColumnDef> Columns { get; }
    public IReadOnlyList<DataTableGridRow> Rows { get; }

    DataTableGridModel(IReadOnlyList<DataTableColumnDef> columns, IReadOnlyList<DataTableGridRow> rows)
    {
        Columns = columns;
        Rows = rows;
    }

    public static DataTableGridModel From(FAion2DataTableFile table)
    {
        if (table.Properties.FirstOrDefault()?.Tag is not ArrayProperty { Value: { } array })
            throw new InvalidOperationException("Data table has no row array");

        var structs = array.Properties
            .Select(p => p is StructProperty { Value.StructType: FStructFallback fb } ? fb : null)
            .Where(fb => fb is not null)
            .Cast<FStructFallback>()
            .ToList();

        var propertyColumns = new SortedDictionary<(string Name, int Index), DataTableColumnDef>();
        foreach (var row in structs)
        {
            foreach (var tag in row.Properties)
            {
                var key = (tag.Name.Text, tag.ArrayIndex);
                if (!propertyColumns.ContainsKey(key))
                {
                    propertyColumns[key] = new DataTableColumnDef
                    {
                        PropertyName = tag.Name.Text,
                        ArrayIndex = tag.ArrayIndex,
                        Header = tag.ArrayIndex > 0 ? $"{tag.Name.Text}[{tag.ArrayIndex}]" : tag.Name.Text,
                    };
                }
            }
        }

        var columns = new List<DataTableColumnDef> { new() { Header = "RowId", PropertyName = "", ArrayIndex = 0 } };
        columns.AddRange(propertyColumns.Values);

        var rows = new List<DataTableGridRow>();
        for (var i = 0; i < structs.Count; i++)
        {
            var rowStruct = structs[i];
            var cells = new List<DataTableGridCell>
            {
                new() { Display = ResolveRowKey(rowStruct, i, table.Ids), Tag = null, EditKind = PropertyEditKind.ReadOnly },
            };

            foreach (var col in columns.Skip(1))
            {
                var tag = rowStruct.Properties.FirstOrDefault(p =>
                    p.Name.Text == col.PropertyName && p.ArrayIndex == col.ArrayIndex);
                if (tag is null)
                {
                    cells.Add(new DataTableGridCell { Display = "", Tag = null, EditKind = PropertyEditKind.ReadOnly });
                    continue;
                }

                cells.Add(new DataTableGridCell
                {
                    Display = PropertyValueFormat.FormatCell(tag),
                    Tag = tag,
                    EditKind = PropertyValueFormat.EditKindOf(tag),
                });
            }

            rows.Add(new DataTableGridRow { RowKey = cells[0].Display, Struct = rowStruct, Cells = cells });
        }

        return new DataTableGridModel(columns, rows);
    }

    static string ResolveRowKey(FStructFallback row, int index, string[] ids)
    {
        if (index < ids.Length && !string.IsNullOrWhiteSpace(ids[index]))
            return ids[index];

        foreach (var name in new[] { "Id", "ID", "Name", "Key", "Code" })
        {
            var tag = row.Properties.FirstOrDefault(p =>
                p.Name.Text.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tag is null) continue;
            var text = PropertyValueFormat.FormatCell(tag);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        return $"#{index}";
    }
}
