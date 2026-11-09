using Oranda.Core.Grid;
using Oranda.Tibero.Results;

namespace Oranda.Tibero.Tests.Results;

public class TiberoCellConverterTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Theory]
    [InlineData(ColumnDataKind.Number, 107, "Raw")]
    [InlineData(ColumnDataKind.Number, 113, "Clr")]
    [InlineData(ColumnDataKind.Number, 108, "Clr")]
    [InlineData(ColumnDataKind.Timestamp, 123, "Raw")]
    [InlineData(ColumnDataKind.Timestamp, 124, "LtzDateTime")]
    [InlineData(ColumnDataKind.TimestampTz, 125, "Raw")]
    [InlineData(ColumnDataKind.IntervalYearMonth, 115, "Raw")]
    [InlineData(ColumnDataKind.IntervalDaySecond, 114, "Raw")]
    [InlineData(ColumnDataKind.Text, 126, "String")]
    [InlineData(ColumnDataKind.RowId, 126, "String")]
    [InlineData(ColumnDataKind.Clob, 105, "Clob")]
    [InlineData(ColumnDataKind.Blob, 102, "Blob")]
    [InlineData(ColumnDataKind.Date, 106, "Clr")]
    [InlineData(ColumnDataKind.Raw, 120, "Clr")]
    [InlineData(ColumnDataKind.BinaryFloat, 132, "Clr")]
    public void Source_follows_kind_and_provider_type(ColumnDataKind kind, int providerType, string expected)
    {
        Assert.Equal(expected, TiberoCellConverter.SourceFor(kind, providerType).ToString());
    }

    [Fact]
    public void Raw_number_timestamp_and_intervals_are_decoded()
    {
        Assert.Equal(1.5m, TiberoCellConverter.FromRaw(ColumnDataKind.Number, Hex("03C181B2")));
        Assert.Equal(new DateTime(2024, 1, 2, 3, 4, 5), TiberoCellConverter.FromRaw(ColumnDataKind.Timestamp, Hex("787C010203040500 00000000")));
        Assert.Equal(14L, TiberoCellConverter.FromRaw(ColumnDataKind.IntervalYearMonth, Hex("400000010D")));
        Assert.Equal(new TimeSpan(100, 0, 0, 0), TiberoCellConverter.FromRaw(ColumnDataKind.IntervalDaySecond, Hex("40000064 40000000 40000000")));
        var tz = Assert.IsType<DateTimeOffset>(TiberoCellConverter.FromRaw(ColumnDataKind.TimestampTz, Hex("787C010112040500000000006D0000012D")));
        Assert.Equal(TimeSpan.FromHours(9), tz.Offset);
    }

    [Fact]
    public void Undecodable_raw_bytes_become_hex_text_instead_of_throwing()
    {
        Assert.Equal("0x6465", TiberoCellConverter.FromRaw(ColumnDataKind.Timestamp, Hex("6465")));
        Assert.Equal("0x3C64010100000000", TiberoCellConverter.FromRaw(ColumnDataKind.Timestamp, Hex("3C64010100000000")));
        Assert.Equal("0x05C181", TiberoCellConverter.FromRaw(ColumnDataKind.Number, Hex("05C181")));
    }

    [Fact]
    public void Raw_for_a_non_raw_kind_is_a_programming_error()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TiberoCellConverter.FromRaw(ColumnDataKind.Text, Hex("00")));
    }

    [Fact]
    public void Clr_values_are_normalized()
    {
        Assert.Null(TiberoCellConverter.FromClr(null));
        Assert.Null(TiberoCellConverter.FromClr(DBNull.Value));
        Assert.Equal(new byte[] { 0xDE, 0xAD }, TiberoCellConverter.FromClr(new sbyte[] { -34, -83 }));
        Assert.Equal(5L, TiberoCellConverter.FromClr(5L));
        Assert.Equal("x", TiberoCellConverter.FromClr("x"));
    }

    [Fact]
    public void Text_and_int_conversions_use_invariant_culture()
    {
        Assert.Equal("1.5", TiberoCellConverter.ToText(1.5m));
        Assert.Null(TiberoCellConverter.ToText(null));
        Assert.Equal(7, TiberoCellConverter.ToInt("7"));
        Assert.Equal(7, TiberoCellConverter.ToInt(7.0m));
        Assert.Equal(0, TiberoCellConverter.ToInt(null));
    }
}
