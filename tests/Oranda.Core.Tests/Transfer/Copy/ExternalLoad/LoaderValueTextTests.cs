using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Core.Transfer.Copy.ExternalLoad;

namespace Oranda.Core.Tests.Transfer.Copy.ExternalLoad;

// 소유: XL1. 이식 값 → 외부 도구 필드 글자(문화권 무관, 실패 글에 값 없음).
public class LoaderValueTextTests
{
    private static string Format(object? value, string type)
    {
        Assert.True(LoaderValueText.TryFormat(value, "C", type, out var text, out var error), error);
        return text!;
    }

    private static string Fail(object? value, string type)
    {
        Assert.False(LoaderValueText.TryFormat(value, "C", type, out var text, out var error));
        Assert.Null(text);
        return error!;
    }

    [Theory]
    [InlineData("NUMBER(10,2)", LoaderFieldKind.Number, 10)]
    [InlineData("FLOAT(126)", LoaderFieldKind.Number, 126)]
    [InlineData("VARCHAR2(20 CHAR)", LoaderFieldKind.Text, 20)]
    [InlineData("nvarchar2(20)", LoaderFieldKind.Text, 20)]
    [InlineData("CHAR(10)", LoaderFieldKind.Text, 10)]
    [InlineData("NCHAR(2)", LoaderFieldKind.Text, 2)]
    [InlineData("DATE", LoaderFieldKind.Date, null)]
    [InlineData("TIMESTAMP(6)", LoaderFieldKind.Timestamp, 6)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", LoaderFieldKind.TimestampTz, 6)]
    [InlineData("INTERVAL YEAR(2) TO MONTH", LoaderFieldKind.IntervalYm, 2)]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)", LoaderFieldKind.IntervalDs, 2)]
    [InlineData("RAW(16)", LoaderFieldKind.Raw, 16)]
    [InlineData("BINARY_FLOAT", LoaderFieldKind.BinaryFloat, null)]
    [InlineData("BINARY_DOUBLE", LoaderFieldKind.BinaryDouble, null)]
    [InlineData("CLOB", LoaderFieldKind.Clob, null)]
    [InlineData("NCLOB", LoaderFieldKind.Clob, null)]
    [InlineData("BLOB", LoaderFieldKind.Blob, null)]
    public void Target_type_names_map_to_field_kinds(string type, LoaderFieldKind kind, int? length)
    {
        Assert.True(LoaderFieldType.TryParse(type, out var parsed));
        Assert.Equal(kind, parsed.Kind);
        Assert.Equal(length, parsed.Length);
        Assert.True(LoaderValueText.Supports(type));
    }

    [Theory]
    [InlineData("LONG")]
    [InlineData("LONG RAW")]
    [InlineData("XMLTYPE")]
    [InlineData("SYS.XMLTYPE")]
    [InlineData("ROWID")]
    [InlineData("UROWID(4000)")]
    [InlineData("BFILE")]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE")]
    [InlineData("MDSYS.SDO_GEOMETRY")]
    [InlineData("")]
    [InlineData(null)]
    public void Unsupported_types_send_the_table_back_to_the_standard_method(string? type)
    {
        Assert.False(LoaderValueText.Supports(type));
    }

    [Fact]
    public void Null_is_null_text_for_every_type()
    {
        Assert.True(LoaderValueText.TryFormat(null, "C", "NUMBER", out var text, out var error));
        Assert.Null(text);
        Assert.Null(error);
        Assert.True(LoaderValueText.TryFormat(DBNull.Value, "C", "BLOB", out text, out _));
        Assert.Null(text);
    }

    [Fact]
    public void Numbers_are_fixed_point_text()
    {
        Assert.Equal("-123.45", Format(PortableNumber.FromText("-1.2345E2"), "NUMBER(10,2)"));
        Assert.Equal("1.1", Format(new PortableBinaryFloat(1.1f), "NUMBER"));
        Assert.Equal("0.1", Format(new PortableBinaryDouble(0.1), "FLOAT(126)"));
    }

    [Fact]
    public void Nan_into_number_fails_without_the_value()
    {
        var error = Fail(new PortableBinaryDouble(double.NaN), "NUMBER");
        Assert.Contains("C", error, StringComparison.Ordinal);
        Assert.Contains("NUMBER", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Dates_and_timestamps_use_the_masks()
    {
        Assert.Equal("2026-11-10 21:14:03", Format(new DateTime(2026, 11, 10, 21, 14, 3), "DATE"));
        Assert.Equal("0001-01-01 00:00:00", Format(DateTime.MinValue, "DATE"));
        Assert.Equal("9999-12-31 23:59:59", Format(new DateTime(9999, 12, 31, 23, 59, 59), "DATE"));
        Assert.Equal(
            "2026-11-10 21:14:03.000000007",
            Format(new PortableTimestamp(new DateTime(2026, 11, 10, 21, 14, 3), 7), "TIMESTAMP(9)"));
        Assert.Equal("YYYY-MM-DD HH24:MI:SS.FF9", LoaderValueText.TimestampMask);
    }

    [Fact]
    public void Time_zone_values_carry_region_or_offset()
    {
        var local = new PortableTimestamp(new DateTime(2026, 1, 2, 3, 4, 5), 123_456_789);
        Assert.Equal("2026-01-02 03:04:05.123456789 +09:00", Format(new PortableTimestampTz(local, "+9:00", null), "TIMESTAMP(6) WITH TIME ZONE"));
        Assert.Equal("2026-01-02 03:04:05.123456789 Asia/Seoul", Format(new PortableTimestampTz(local, "+09:00", "Asia/Seoul"), "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal("2026-01-02 03:04:05.123456789 -03:30", Format(new PortableTimestampTz(local, "-03:30", null), "TIMESTAMP WITH TIME ZONE"));
    }

    [Theory]
    [InlineData(27L, "+02-03")]
    [InlineData(-23L, "-01-11")]
    [InlineData(0L, "+00-00")]
    [InlineData(12L * 123, "+123-00")]
    public void Year_month_intervals_are_oracle_literals(long months, string expected)
    {
        Assert.Equal(expected, Format(new PortableIntervalYm(months), "INTERVAL YEAR(3) TO MONTH"));
    }

    [Fact]
    public void Day_second_intervals_are_oracle_literals()
    {
        Int128 nanos = ((((1L * 24) + 2) * 60 + 3) * 60 + 4) * 1_000_000_000L + 123_456_789;
        Assert.Equal("+01 02:03:04.123456789", Format(new PortableIntervalDs(nanos), "INTERVAL DAY(2) TO SECOND(6)"));
        Assert.Equal("-01 02:03:04.123456789", Format(new PortableIntervalDs(-nanos), "INTERVAL DAY(2) TO SECOND(9)"));
        Assert.Equal("+00 00:00:00.000000000", Format(new PortableIntervalDs(0), "INTERVAL DAY TO SECOND"));
    }

    [Fact]
    public void Raw_is_uppercase_hex()
    {
        Assert.Equal("00FF10AB", Format(new byte[] { 0x00, 0xFF, 0x10, 0xAB }, "RAW(16)"));
        Assert.Equal(string.Empty, Format(Array.Empty<byte>(), "RAW(16)"));
    }

    [Fact]
    public void Binary_floats_round_trip_with_special_values()
    {
        Assert.Equal("NaN", Format(new PortableBinaryFloat(float.NaN), "BINARY_FLOAT"));
        Assert.Equal("Inf", Format(new PortableBinaryFloat(float.PositiveInfinity), "BINARY_FLOAT"));
        Assert.Equal("-Inf", Format(new PortableBinaryDouble(double.NegativeInfinity), "BINARY_DOUBLE"));
        Assert.Equal(0.1, double.Parse(Format(new PortableBinaryDouble(0.1), "BINARY_DOUBLE"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1.1f, float.Parse(Format(new PortableBinaryFloat(1.1f), "BINARY_FLOAT"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("1E+38", Format(new PortableBinaryFloat(1e38f), "BINARY_FLOAT"));
    }

    [Fact]
    public void Formatting_ignores_the_current_culture()
    {
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", Format(new PortableBinaryDouble(1.5), "BINARY_DOUBLE"));
            Assert.Equal("2026-11-10 21:14:03", Format(new DateTime(2026, 11, 10, 21, 14, 3), "DATE"));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void Text_and_clob_pass_through()
    {
        Assert.Equal("a\"b\r\nc", Format("a\"b\r\nc", "VARCHAR2(20)"));
        Assert.Equal("긴 글", Format("긴 글", "NCLOB"));
    }

    [Fact]
    public void A_value_of_the_wrong_kind_fails_naming_column_and_type_but_not_the_value()
    {
        var error = Fail("secret-123", "NUMBER(10)");
        Assert.Contains("C", error, StringComparison.Ordinal);
        Assert.Contains("NUMBER(10)", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-123", error, StringComparison.Ordinal);

        Fail(new byte[] { 1 }, "BLOB");
        Fail("x", "LONG");
    }
}
