using System.Data;
using System.Data.Common;
using Oranda.Core.Grid;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

public class TiberoColumnsTests
{
    private static readonly string[] Names = ["ID", "NAME", "CREATED"];

    private static DataTable Schema(params (string Name, int ProviderType, int Size, int Precision, int Scale, bool Nullable, bool RowId)[] cols)
    {
        var t = new DataTable();
        t.Columns.Add("ColumnName", typeof(string));
        t.Columns.Add("ColumnSize", typeof(int));
        t.Columns.Add("NumericPrecision", typeof(short));
        t.Columns.Add("NumericScale", typeof(short));
        t.Columns.Add("ProviderType", typeof(int));
        t.Columns.Add("AllowDBNull", typeof(bool));
        t.Columns.Add("IsRowID", typeof(bool));
        t.Columns.Add("BaseTableName", typeof(string));
        t.Columns.Add("BaseColumnName", typeof(string));
        foreach (var c in cols)
        {
            t.Rows.Add(c.Name, c.Size, (short)c.Precision, (short)c.Scale, c.ProviderType, c.Nullable, c.RowId, "SOME_TABLE", c.Name);
        }

        return t;
    }

    [Fact]
    public void Reads_kind_size_and_nullability()
    {
        var schema = Schema(("ID", 107, 22, 38, -128, false, false), ("NAME", 126, 30, 0, 0, true, false), ("CREATED", 123, 12, 6, 6, true, false));
        var columns = TiberoColumns.Read(schema, 3, i => Names[i]);

        Assert.Equal([ColumnDataKind.Number, ColumnDataKind.Text, ColumnDataKind.Timestamp], columns.Select(c => c.Kind));
        Assert.Equal(["NUMBER", "VARCHAR2", "TIMESTAMP"], columns.Select(c => c.DbTypeName));
        Assert.Equal(30, columns[1].Size);
        Assert.False(columns[0].IsNullable);
        Assert.True(columns[1].IsNullable);
        Assert.Equal(6, columns[2].Scale);
    }

    [Fact]
    public void Unspecified_number_precision_and_scale_are_null()
    {
        var columns = TiberoColumns.Read(Schema(("N", 107, 22, 38, -128, true, false)), 1, _ => "N");
        Assert.Equal(38, columns[0].Precision);
        Assert.Null(columns[0].Scale);
    }

    [Fact]
    public void Rowid_column_is_recognized_by_schema_flag()
    {
        var columns = TiberoColumns.Read(Schema(("ROWID", 126, 12, 0, 0, true, true), ("X", 126, 5, 0, 0, true, false)), 2, i => i == 0 ? "ROWID" : "X");
        Assert.Equal(ColumnDataKind.RowId, columns[0].Kind);
        Assert.Equal("ROWID", columns[0].DbTypeName);
        Assert.Equal(ColumnDataKind.Text, columns[1].Kind);
    }

    [Fact]
    public void Base_table_info_is_not_trusted()
    {
        var columns = TiberoColumns.Read(Schema(("A", 126, 5, 0, 0, true, false)), 1, _ => "A");
        Assert.Null(columns[0].BaseTable);
        Assert.Null(columns[0].BaseOwner);
        Assert.Null(columns[0].BaseColumn);
    }

    [Fact]
    public void Missing_schema_falls_back_to_text_columns()
    {
        var columns = TiberoColumns.Read(null, 2, i => "C" + i);
        Assert.All(columns, c => Assert.Equal(ColumnDataKind.Text, c.Kind));
        Assert.Equal(["C0", "C1"], columns.Select(c => c.Name));
    }

    [Fact]
    public void Schema_failure_is_swallowed()
    {
        Assert.Null(TiberoColumns.SchemaOrNull(() => throw new InvalidOperationException("x")));
        Assert.Null(TiberoColumns.SchemaOrNull(() => throw new FakeDbException()));
        Assert.Throws<ArgumentException>(() => TiberoColumns.SchemaOrNull(() => throw new ArgumentException("bug")));
    }

    [Fact]
    public void Provider_type_of_returns_null_without_schema()
    {
        Assert.Null(TiberoColumns.ProviderTypeOf(null, 0));
        Assert.Equal(113, TiberoColumns.ProviderTypeOf(Schema(("N", 113, 8, 19, 0, true, false)), 0));
    }

    private sealed class FakeDbException : DbException
    {
    }
}
