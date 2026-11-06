using Oracle.ManagedDataAccess.Types;
using Oranda.Core.Execution;
using Oranda.Oracle.Transfer.Copy;

namespace Oranda.Oracle.Tests.Transfer.Copy;

// 소유: 데이터 복사 Task 9. 실패 행 키 값 ↔ 기록 글자. 「실패 행만 다시 넣기」가 이 글자로 원본을 다시 읽는다.
public class FailedRowKeysTests
{
    [Fact]
    public void Key_text_round_trips()
    {
        var number = OracleDecimal.Parse("12345678901234567890.5");
        var numberText = FailedRowKeys.ToText(number, "NUMBER");
        Assert.Equal("12345678901234567890.5", numberText);
        Assert.Equal(number, FailedRowKeys.FromText(numberText, "NUMBER"));

        var date = new OracleDate(new DateTime(2026, 3, 5, 14, 30, 15));
        var dateText = FailedRowKeys.ToText(date, "DATE");
        Assert.Equal("2026-03-05T14:30:15", dateText);
        Assert.Equal(date, FailedRowKeys.FromText(dateText, "DATE"));

        var ts = new OracleTimeStamp(new DateTime(2026, 3, 5, 14, 30, 15).AddTicks(1_234_560));
        var tsText = FailedRowKeys.ToText(ts, "TIMESTAMP(6)");
        Assert.Equal("2026-03-05T14:30:15.1234560", tsText);
        Assert.Equal(ts, FailedRowKeys.FromText(tsText, "TIMESTAMP(6)"));

        byte[] raw = [0x00, 0xAB, 0x10, 0xFF, 0x7E, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B];
        var rawText = FailedRowKeys.ToText(raw, "RAW");
        Assert.Equal("00AB10FF7E0102030405060708090A0B", rawText);
        Assert.Equal(raw, (byte[])FailedRowKeys.FromText(rawText, "RAW")!);

        Assert.Equal("A1  ", FailedRowKeys.ToText("A1  ", "CHAR"));
        Assert.Null(FailedRowKeys.ToText(null, "NUMBER"));
        Assert.Null(FailedRowKeys.FromText(null, "DATE"));
    }

    [Fact]
    public void Number_text_has_no_exponent_or_grouping()
    {
        var big = OracleDecimal.Parse("12345678901234567890123456789012345678");
        var bigText = FailedRowKeys.ToText(big, "NUMBER(38)");
        Assert.Equal("12345678901234567890123456789012345678", bigText);
        Assert.Equal(big, FailedRowKeys.FromText(bigText, "NUMBER(38)"));

        var tiny = OracleDecimal.Parse("0.00000000000000000000000000000000012345");
        var tinyText = FailedRowKeys.ToText(tiny, "NUMBER");
        Assert.DoesNotContain("E", tinyText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tiny, FailedRowKeys.FromText(tinyText, "NUMBER"));

        Assert.Equal("-1234.5", FailedRowKeys.ToText(OracleDecimal.Parse("-1234.5"), "NUMBER"));
        Assert.Equal("12340000000000000000000000000000000000000", FailedRowKeys.ExpandExponent("1.234E+40"));
        Assert.Equal("0.0001234", FailedRowKeys.ExpandExponent("1.234E-04"));
        Assert.Equal("-12", FailedRowKeys.ExpandExponent("-1.2E+01"));
    }

    [Fact]
    public void Timestamp_with_time_zone_keeps_offset()
    {
        var tz = new OracleTimeStampTZ(new DateTime(2026, 3, 5, 14, 30, 15), "+09:00");
        var text = FailedRowKeys.ToText(tz, "TIMESTAMP(6) WITH TIME ZONE");
        Assert.Equal("2026-03-05T14:30:15.0000000+09:00", text);
        var back = (OracleTimeStampTZ)FailedRowKeys.FromText(text, "TIMESTAMP(6) WITH TIME ZONE")!;
        Assert.Equal(tz.Value, back.Value);
        Assert.Equal(tz.GetTimeZoneOffset(), back.GetTimeZoneOffset());
    }

    [Fact]
    public void Timestamp_nanoseconds_survive_round_trip()
    {
        // TIMESTAMP(9): 100ns 아래 자리가 있으면 9자리로 쓴다(7자리로 자르면 다시 읽을 때 키가 맞지 않는다).
        var ts = new OracleTimeStamp(2026, 3, 5, 14, 30, 15, 123_456_789);
        var text = FailedRowKeys.ToText(ts, "TIMESTAMP(9)");
        Assert.Equal("2026-03-05T14:30:15.123456789", text);
        Assert.Equal(ts, FailedRowKeys.FromText(text, "TIMESTAMP(9)"));

        var tz = new OracleTimeStampTZ(2026, 3, 5, 14, 30, 15, 123_456_789, "+09:00");
        var tzText = FailedRowKeys.ToText(tz, "TIMESTAMP(9) WITH TIME ZONE");
        Assert.Equal("2026-03-05T14:30:15.123456789+09:00", tzText);
        var back = (OracleTimeStampTZ)FailedRowKeys.FromText(tzText, "TIMESTAMP(9) WITH TIME ZONE")!;
        Assert.Equal(tz.Nanosecond, back.Nanosecond);
        Assert.Equal(tz.GetTimeZoneOffset(), back.GetTimeZoneOffset());
    }

    [Theory]
    [InlineData("NUMBER(10)", BindDataType.Number)]
    [InlineData("DATE", BindDataType.Date)]
    [InlineData("TIMESTAMP(6)", BindDataType.Timestamp)]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE", BindDataType.TimestampLtz)]
    [InlineData("RAW", BindDataType.Raw)]
    [InlineData("CHAR", BindDataType.FixedChar)]
    [InlineData("VARCHAR2", BindDataType.Varchar2)]
    public void Bind_type_follows_key_column_type(string serverType, BindDataType expected)
    {
        Assert.Equal(expected, FailedRowKeys.BindTypeOf(serverType));
    }

    [Fact]
    public void Lob_and_interval_keys_are_not_supported()
    {
        Assert.Throws<NotSupportedException>(() => FailedRowKeys.ToText("x", "CLOB"));
        Assert.Throws<NotSupportedException>(() => FailedRowKeys.BindTypeOf("INTERVAL DAY(2) TO SECOND(6)"));
    }
}
