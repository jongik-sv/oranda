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
    [InlineData(ColumnDataKind.BinaryFloat, 132, "Raw")]
    [InlineData(ColumnDataKind.BinaryFloat, 133, "Raw")]
    [InlineData(ColumnDataKind.Text, 109, "Clr")]
    [InlineData(ColumnDataKind.Raw, 110, "Clr")]
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

    [Theory]
    [InlineData("3FEFFFFF", -2.25f)] // R1x 실측(win144): 음수는 모든 비트가 뒤집혀 저장된다
    [InlineData("C0100000", 2.25f)]
    [InlineData("FF7FFFFF", float.MaxValue)]
    [InlineData("00800000", -float.MaxValue)]
    [InlineData("80000000", 0f)]
    [InlineData("725DBD9F", -1E-30f)] // 이하 4줄도 win144 #85 실측 바이트
    [InlineData("F149F2CA", 1E30f)]
    [InlineData("407FFFFF", -1f)]
    [InlineData("C2F6E979", 123.456f)]
    [InlineData("FF800000", float.PositiveInfinity)] // 경계(실측 아님: 서버가 글자 'Inf' 를 거부) — 같은 비트 규칙
    [InlineData("007FFFFF", float.NegativeInfinity)]
    public void Binary_float_raw_bytes_are_decoded_to_a_single(string hex, float expected)
    {
        var value = Assert.IsType<float>(TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex(hex)));

        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("3FFDFFFFFFFFFFFF", -2.25d)]
    [InlineData("C002000000000000", 2.25d)]
    [InlineData("8000000000000000", 0d)]
    [InlineData("FFEFFFFFFFFFFFFF", double.MaxValue)]
    [InlineData("0010000000000000", -double.MaxValue)]
    [InlineData("464BB7B401143D5F", -1E-30d)] // 이하 4줄 win144 #85 실측 바이트
    [InlineData("C6293E5939A08CEA", 1E30d)]
    [InlineData("400FFFFFFFFFFFFF", -1d)]
    [InlineData("C05EDD2F1A9FBE77", 123.456d)]
    public void Binary_double_raw_bytes_are_decoded_to_a_double(string hex, double expected)
    {
        var value = Assert.IsType<double>(TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex(hex)));

        Assert.Equal(expected, value);
    }

    [Fact]
    public void A_stored_negative_zero_decodes_to_negative_zero_bits()
    {
        // 서버는 -0.0 을 +0(80000000)으로 저장했다(#85). 7FFFFFFF 가 오면 규칙대로 -0.0.
        var value = Assert.IsType<float>(TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex("7FFFFFFF")));

        Assert.Equal(unchecked((int)0x80000000), BitConverter.SingleToInt32Bits(value));
    }

    [Fact]
    public void Binary_float_and_double_not_a_number_is_kept()
    {
        Assert.True(float.IsNaN(Assert.IsType<float>(TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex("FFC00000")))));
        Assert.True(double.IsNaN(Assert.IsType<double>(TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex("FFF8000000000000")))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("3FEFFF")]
    [InlineData("3FEFFFFFFF")]
    [InlineData("3FEFFFFFFFFFFF")]
    public void Binary_float_raw_bytes_of_another_length_become_hex_text(string hex)
    {
        Assert.Equal("0x" + hex, TiberoCellConverter.FromRaw(ColumnDataKind.BinaryFloat, Hex(hex)));
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
