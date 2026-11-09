using System.Data;
using System.Data.Common;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Results;
using Oranda.Tibero.Tests.Driver;

namespace Oranda.Tibero.Tests.Results;

[Collection("FakeDriver")]
public class TiberoValuesTests
{
    private const string TypesNs = "Tibero.DataAccess.Types.";

    private static TiberoDriver Driver => FakeDriverHost.Driver;

    private static byte[] Hex(string hex) => Convert.FromHexString(hex);

    private static DbDataReader ReaderOf(object value, Type type)
    {
        var table = new DataTable();
        table.Columns.Add("c", type);
        table.Rows.Add(value);
        var reader = FakeDriverHost.NewReader(table);
        Assert.True(reader.Read());
        return reader;
    }

    private static object? Cell(object value, Type type, ColumnDataKind kind, int providerType, int preview = 4000)
    {
        using var reader = ReaderOf(value, type);
        return TiberoValues.ReadCell(Driver, reader, 0, kind, providerType, preview);
    }

    [Fact]
    public void Number_decimal_column_is_decoded_from_raw_bytes()
    {
        Assert.Equal(1m, Cell(Hex("02C181"), typeof(byte[]), ColumnDataKind.Number, TiberoColumnTypes.PtDecimal));
        Assert.Equal(1.5m, Cell(Hex("03C181B2"), typeof(byte[]), ColumnDataKind.Number, TiberoColumnTypes.PtDecimal));
    }

    [Fact]
    public void Native_numeric_column_uses_the_clr_value()
    {
        Assert.Equal(5, Cell(5, typeof(int), ColumnDataKind.Number, TiberoColumnTypes.PtInt32));
    }

    [Fact]
    public void Timestamp_and_timestamp_tz_keep_precision_and_offset()
    {
        var stamp = Hex("787C010203040500075BCD15");
        var tz = Hex("787C010208220500000000005F1E000245");

        Assert.Equal(TiberoWire.DecodeTimestamp(stamp), Cell(stamp, typeof(byte[]), ColumnDataKind.Timestamp, TiberoColumnTypes.PtTimeStamp));
        Assert.Equal(TiberoWire.DecodeTimestampTz(tz), Cell(tz, typeof(byte[]), ColumnDataKind.TimestampTz, TiberoColumnTypes.PtTimeStampTZ));
    }

    [Fact]
    public void Unreadable_raw_bytes_become_hex_text_instead_of_failing_the_read()
    {
        var value = Cell(Hex("3C64010100000000"), typeof(byte[]), ColumnDataKind.Timestamp, TiberoColumnTypes.PtTimeStamp);

        Assert.Equal("0x3C64010100000000", value);
    }

    [Fact]
    public void Local_time_zone_timestamp_reads_the_driver_date_time()
    {
        var when = new DateTime(2026, 5, 6, 7, 8, 9);

        Assert.Equal(when, Cell(when, typeof(DateTime), ColumnDataKind.Timestamp, TiberoColumnTypes.PtTimeStampLTZ));
    }

    [Fact]
    public void Text_rowid_date_and_signed_raw_are_plain_values()
    {
        Assert.Equal("안녕", Cell("안녕", typeof(string), ColumnDataKind.Text, TiberoColumnTypes.PtVarchar2));
        Assert.Equal("AAAAtYAAEAAAAfQAAA", Cell("AAAAtYAAEAAAAfQAAA", typeof(string), ColumnDataKind.RowId, TiberoColumnTypes.PtVarchar2));
        var date = new DateTime(2026, 1, 2);
        Assert.Equal(date, Cell(date, typeof(DateTime), ColumnDataKind.Date, TiberoColumnTypes.PtDate));
        Assert.Equal(new byte[] { 255, 1 }, Cell(new sbyte[] { -1, 1 }, typeof(sbyte[]), ColumnDataKind.Raw, TiberoColumnTypes.PtRaw));
    }

    [Fact]
    public void Null_cell_is_null_for_every_source()
    {
        Assert.Null(Cell(DBNull.Value, typeof(byte[]), ColumnDataKind.Number, TiberoColumnTypes.PtDecimal));
        Assert.Null(Cell(DBNull.Value, typeof(string), ColumnDataKind.Clob, TiberoColumnTypes.PtClob));
        Assert.Null(Cell(DBNull.Value, typeof(DateTime), ColumnDataKind.Timestamp, TiberoColumnTypes.PtTimeStampLTZ));
    }

    [Fact]
    public void Driver_objects_never_end_up_in_a_cell()
    {
        var other = FakeDriverHost.New(TypesNs + "TiberoString", "x");

        var value = Cell(other, typeof(object), ColumnDataKind.Other, TiberoColumnTypes.PtXmlType);

        Assert.IsType<string>(value);
    }

    [Fact]
    public void Clob_cell_has_preview_length_and_a_locator()
    {
        var lob = Assert.IsType<LobValue>(Cell("가나다abc", typeof(string), ColumnDataKind.Clob, TiberoColumnTypes.PtClob, preview: 4));

        Assert.Equal(LobKind.Clob, lob.Kind);
        Assert.Equal(6, lob.Length);
        Assert.Equal("가나다a", lob.TextPreview);
        Assert.True(lob.IsTruncated);
        using var locator = Assert.IsType<TiberoLob>(lob.Locator);
        Assert.Equal(12, locator.Length);
    }

    [Fact]
    public void Blob_cell_has_binary_preview()
    {
        var lob = Assert.IsType<LobValue>(Cell(new byte[] { 1, 2, 3, 4, 5 }, typeof(byte[]), ColumnDataKind.Blob, TiberoColumnTypes.PtBlob, preview: 3));

        Assert.Equal(LobKind.Blob, lob.Kind);
        Assert.Equal(5, lob.Length);
        Assert.Equal(new byte[] { 1, 2, 3 }, lob.BinaryPreview.ToArray());
        Assert.True(lob.IsTruncated);
        (lob.Locator as IDisposable)?.Dispose();
    }

    [Fact]
    public void Cell_reader_uses_the_provider_type_of_each_column()
    {
        var table = new DataTable();
        table.Columns.Add("a", typeof(byte[]));
        table.Columns.Add("b", typeof(string));
        table.Rows.Add(Hex("02C181"), "x");
        using var reader = FakeDriverHost.NewReader(table);
        Assert.True(reader.Read());
        var columns = new ResultColumn[]
        {
            new(0, "A", "NUMBER", ColumnDataKind.Number),
            new(1, "B", "VARCHAR2", ColumnDataKind.Text),
        };
        var read = TiberoValues.CreateCellReader(Driver, [TiberoColumnTypes.PtDecimal, TiberoColumnTypes.PtVarchar2]);

        Assert.Equal(1m, read(reader, 0, columns[0]));
        Assert.Equal("x", read(reader, 1, columns[1]));
    }

    // ---- OUT 값 ----

    private static object? Out(object? providerValue, BindValue? bind = null) =>
        TiberoValues.FromOut(Driver.ConvertProviderValue(providerValue), bind ?? new BindValue(BindDataType.Varchar2, null, BindDirection.Out));

    [Fact]
    public void Out_null_values_are_null()
    {
        Assert.Null(Out(DBNull.Value));
        Assert.Null(Out(FakeDriverHost.Static(TypesNs + "TiberoString", "Null")));
        Assert.Null(Out(FakeDriverHost.Static(TypesNs + "TiberoDecimal", "Null")));
    }

    [Fact]
    public void Out_number_keeps_precision()
    {
        Assert.Equal(12.5m, Out(FakeDriverHost.New(TypesNs + "TiberoDecimal", 12.5m)));
        Assert.Equal("1.5E+30", TiberoValues.FromDecimalText("1.5E+30"));
        Assert.Equal("1234567890123456789012345678901234567.5", TiberoValues.FromDecimalText("1234567890123456789012345678901234567.5"));
        Assert.Equal(1234567890123456789012345678m, TiberoValues.FromDecimalText("1234567890123456789012345678"));
    }

    [Fact]
    public void Out_string_date_and_binary_are_plain()
    {
        Assert.Equal("안녕", Out(FakeDriverHost.New(TypesNs + "TiberoString", "안녕")));
        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), Out(FakeDriverHost.New(TypesNs + "TiberoDate", new DateTime(2026, 1, 2, 3, 4, 5))));
        Assert.Equal(new byte[] { 1, 2 }, Out(FakeDriverHost.New(TypesNs + "TiberoBinary", new byte[] { 1, 2 })));
    }

    [Fact]
    public void Out_timestamp_adds_nanoseconds_as_ticks()
    {
        var stamp = FakeDriverHost.New(TypesNs + "TiberoTimeStamp", new DateTime(2026, 1, 2, 3, 4, 5), 123_456_789);

        var value = Assert.IsType<DateTime>(Out(stamp));

        Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1_234_567), value);
    }

    [Fact]
    public void Out_boolean_text_is_turned_back_into_a_boolean_only_for_boolean_binds()
    {
        var text = FakeDriverHost.New(TypesNs + "TiberoString", "1");

        Assert.Equal(true, Out(text, new BindValue(BindDataType.Boolean, null, BindDirection.Out)));
        Assert.Equal("1", Out(text, new BindValue(BindDataType.Varchar2, null, BindDirection.Out)));
    }

    [Fact]
    public void Out_clob_and_blob_are_read_completely_and_disposed()
    {
        var clob = (Stream)FakeDriverHost.New(TypesNs + "TiberoClob", new string('가', 9000), false);
        var blob = FakeDriverHost.New(TypesNs + "TiberoBlob", new byte[10000]);

        Assert.Equal(new string('가', 9000), Out(clob));
        Assert.Equal(10000, Assert.IsType<byte[]>(Out(blob)).Length);
    }

    [Fact]
    public void Out_ref_cursor_is_the_reader()
    {
        var table = new DataTable();
        table.Columns.Add("c", typeof(int));
        var cursor = FakeDriverHost.New(TypesNs + "TiberoRefCursor", table);

        using var reader = Assert.IsAssignableFrom<DbDataReader>(Out(cursor));

        Assert.Equal(1, reader.FieldCount);
    }

    [Fact]
    public void Out_clr_signed_raw_and_unknown_driver_values_are_plain()
    {
        Assert.Equal(new byte[] { 255 }, Out(new sbyte[] { -1 }));
        Assert.Equal(5, Out(5));
        Assert.IsType<string>(Out(FakeDriverHost.New(TypesNs + "TiberoTimeStampTZ", new DateTime(2026, 1, 1), "+09:00")));
    }
}
