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

    [Theory]
    [InlineData("BinaryFloat", TiberoColumnTypes.PtBinaryFloat)]
    [InlineData("BinaryDouble", TiberoColumnTypes.PtBinaryDouble)]
    [InlineData("BINARY_FLOAT", TiberoColumnTypes.PtBinaryFloat)]
    [InlineData("Long", TiberoColumnTypes.PtLong)]
    [InlineData("LongRaw", TiberoColumnTypes.PtLongRaw)]
    [InlineData("LONG RAW", TiberoColumnTypes.PtLongRaw)]
    [InlineData("Decimal", TiberoColumnTypes.PtDecimal)]
    [InlineData("Int64", TiberoColumnTypes.PtInt64)]
    [InlineData("Varchar2", TiberoColumnTypes.PtVarchar2)]
    [InlineData("TimeStampLTZ", TiberoColumnTypes.PtTimeStampLTZ)]
    [InlineData("TIMESTAMP(6)", TiberoColumnTypes.PtTimeStamp)]
    [InlineData("TIMESTAMP WITH TIME ZONE", TiberoColumnTypes.PtTimeStampTZ)]
    [InlineData("IntervalYM", TiberoColumnTypes.PtIntervalYM)]
    [InlineData("Raw", TiberoColumnTypes.PtRaw)]
    [InlineData("NClob", TiberoColumnTypes.PtNClob)]
    public void Data_type_name_maps_to_the_same_provider_type_the_schema_table_gives(string name, int providerType)
    {
        Assert.Equal((providerType, false), TiberoColumns.ProviderTypeOfName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Byte[]")]
    [InlineData("SomethingNew")]
    public void Unknown_data_type_name_maps_to_nothing(string? name)
    {
        Assert.Null(TiberoColumns.ProviderTypeOfName(name));
    }

    [Fact]
    public void Rowid_data_type_name_marks_a_rowid_column_without_the_schema_table()
    {
        var columns = TiberoColumns.Read(null, 1, _ => "R", _ => "RowID", _ => typeof(string));

        Assert.Equal(ColumnDataKind.RowId, columns[0].Kind);
        Assert.Equal("ROWID", columns[0].DbTypeName);
    }

    [Fact]
    public void Without_a_schema_table_the_data_type_names_pick_the_special_reads()
    {
        string[] names = ["BinaryFloat", "BinaryDouble", "Long", "LongRaw", "Varchar2", "Decimal"];
        var reader = new NamedTypeReader(names, [names.Select(_ => (object?)null).ToArray()], names);

        var (columns, providerTypes) = TiberoValues.Describe(reader);

        Assert.Equal([TiberoColumnTypes.PtBinaryFloat, TiberoColumnTypes.PtBinaryDouble, TiberoColumnTypes.PtLong, TiberoColumnTypes.PtLongRaw,
                      TiberoColumnTypes.PtVarchar2, TiberoColumnTypes.PtDecimal], providerTypes);
        Assert.Equal([TiberoCellSource.Raw, TiberoCellSource.Raw, TiberoCellSource.Clr, TiberoCellSource.Clr, TiberoCellSource.String, TiberoCellSource.Raw],
                     columns.Select((c, i) => TiberoCellConverter.SourceFor(c.Kind, providerTypes[i])));
    }

    [Fact]
    public void Schema_table_wins_over_the_data_type_name_and_the_name_fills_a_missing_provider_type()
    {
        var schema = Schema(("A", 107, 22, 38, -128, true, false));
        schema.Rows.Add("B", 5, (short)0, (short)0, DBNull.Value, true, false, "T", "B");

        var columns = TiberoColumns.Read(schema, 2, i => "C" + i, _ => "Long", _ => typeof(string));

        Assert.Equal(ColumnDataKind.Number, columns[0].Kind); // 표가 정본
        Assert.Equal("LONG", columns[1].DbTypeName); // ProviderType 이 빈 행은 이름으로
    }

    [Fact]
    public void Unknown_name_and_non_text_field_type_is_read_with_get_value_as_unknown()
    {
        var reader = new NamedTypeReader(["X", "Y"], [[null, null]], ["SomethingNew", "SomethingNew"], [typeof(DateTime), typeof(string)]);

        var (columns, providerTypes) = TiberoValues.Describe(reader);

        Assert.Equal([TiberoColumns.UnknownProviderType, TiberoColumnTypes.PtVarchar2], providerTypes);
        Assert.Equal(ColumnDataKind.Other, columns[0].Kind);
        Assert.Equal(TiberoCellSource.Clr, TiberoCellConverter.SourceFor(columns[0].Kind, providerTypes[0]));
    }

    [Fact]
    public void Data_type_name_that_throws_leaves_the_column_text_as_before()
    {
        var reader = new NamedTypeReader(["X"], [[null]], null, null, throwOnTypeInfo: true);

        var (columns, providerTypes) = TiberoValues.Describe(reader);

        Assert.Equal(ColumnDataKind.Text, columns[0].Kind);
        Assert.Equal([TiberoColumnTypes.PtVarchar2], providerTypes);
    }

    /// <summary>스키마 표를 못 읽고(NotSupported) <c>GetDataTypeName</c>·<c>GetFieldType</c> 만 답하는 리더.</summary>
    private sealed class NamedTypeReader(string[] names, object?[][] rows, string[]? typeNames, Type[]? fieldTypes = null, bool throwOnTypeInfo = false)
        : FakeDataReader(names, rows)
    {
        public override DataTable? GetSchemaTable() => throw new NotSupportedException();

        public override string GetDataTypeName(int ordinal) =>
            throwOnTypeInfo ? throw new NotSupportedException() : typeNames![ordinal];

        public override Type GetFieldType(int ordinal) =>
            throwOnTypeInfo ? throw new NotSupportedException() : fieldTypes?[ordinal] ?? typeof(object);
    }

    private sealed class FakeDbException : DbException
    {
    }
}
