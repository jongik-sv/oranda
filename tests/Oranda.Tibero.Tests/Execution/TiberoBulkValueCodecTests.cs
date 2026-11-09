using Oranda.Core.Execution;
using Oranda.Core.Transfer.Copy;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

// 소유: TB1 커밋 3. Tibero 대량 값 코덱(TB1 설계 2장, 시험 4.3 의 코덱 줄). 드라이버 없음.
public class TiberoBulkValueCodecTests
{
    private static readonly TiberoBulkValueCodec Codec = TiberoBulkValueCodec.Instance;

    private static TiberoBulkNumber N(string text) => TiberoBulkNumber.Parse(text);

    // ---- 형식표 ----

    [Theory]
    [InlineData("NUMBER(38,10)", BulkKind.Other, BindDataType.Number)]
    [InlineData("FLOAT", BulkKind.Other, BindDataType.Number)]
    [InlineData("VARCHAR2(10 CHAR)", BulkKind.Text, BindDataType.Varchar2)]
    [InlineData("VARCHAR", BulkKind.Text, BindDataType.Varchar2)]
    [InlineData("CHAR(5)", BulkKind.Text, BindDataType.FixedChar)]
    [InlineData("NVARCHAR2(20)", BulkKind.Text, BindDataType.NVarchar2)]
    [InlineData("NCHAR", BulkKind.Text, BindDataType.NVarchar2)]
    [InlineData("DATE", BulkKind.Other, BindDataType.Date)]
    [InlineData("TIMESTAMP(6)", BulkKind.Other, BindDataType.Timestamp)]
    [InlineData("RAW(16)", BulkKind.Other, BindDataType.Raw)]
    public void Supported_key_types(string serverType, BulkKind kind, BindDataType bind)
    {
        Assert.True(Codec.IsSupported(serverType));
        Assert.Equal(kind, Codec.KindOf(serverType));
        Assert.Equal(bind, Codec.BindTypeOf(serverType));
    }

    [Theory]
    [InlineData("CLOB", BulkKind.Clob)]
    [InlineData("NCLOB", BulkKind.Clob)]
    [InlineData("BLOB", BulkKind.Blob)]
    public void Lobs_are_supported_but_not_keys(string serverType, BulkKind kind)
    {
        Assert.True(Codec.IsSupported(serverType));
        Assert.Equal(kind, Codec.KindOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.BindTypeOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.ToKeyText("x", serverType));
        Assert.Throws<NotSupportedException>(() => Codec.FromKeyText("x", serverType));
    }

    [Theory]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("BINARY_FLOAT")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE")]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE")]
    [InlineData("LONG")]
    [InlineData("LONG RAW")]
    [InlineData("XMLTYPE")]
    [InlineData("BFILE")]
    public void Off_and_unknown_types_are_not_supported(string serverType)
    {
        Assert.False(Codec.IsSupported(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.KindOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.BindTypeOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.ToKeyText("x", serverType));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.BindExpression("C1", serverType));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.ToBindText("x", serverType));
    }

    // ---- 키 글자 (Oracle FailedRowKeys 와 같은 형식) ----

    [Fact]
    public void Key_text_round_trips()
    {
        var ts7 = new TiberoBulkTimestamp(new DateTime(2026, 3, 5, 14, 30, 15), 123_456_700);
        Assert.Equal("2026-03-05T14:30:15.1234567", Codec.ToKeyText(ts7, "TIMESTAMP(7)"));
        Assert.Equal(ts7, Codec.FromKeyText("2026-03-05T14:30:15.1234567", "TIMESTAMP(7)"));

        var ts9 = new TiberoBulkTimestamp(new DateTime(2026, 3, 5, 14, 30, 15), 123_456_789);
        Assert.Equal("2026-03-05T14:30:15.123456789", Codec.ToKeyText(ts9, "TIMESTAMP(9)"));
        Assert.Equal(ts9, Codec.FromKeyText("2026-03-05T14:30:15.123456789", "TIMESTAMP(9)"));
        Assert.Equal(new TiberoBulkTimestamp(new DateTime(2026, 3, 5, 14, 30, 15), 0), Codec.FromKeyText("2026-03-05T14:30:15", "TIMESTAMP"));
        Assert.Throws<FormatException>(() => Codec.FromKeyText("2026-03-05T14:30:15.123", "TIMESTAMP"));

        Assert.Equal("2026-03-05T14:30:15", Codec.ToKeyText(new DateTime(2026, 3, 5, 14, 30, 15), "DATE"));
        Assert.Equal(new DateTime(2026, 3, 5, 14, 30, 15), Codec.FromKeyText("2026-03-05T14:30:15", "DATE"));

        Assert.Equal("00FF10", Codec.ToKeyText(new byte[] { 0x00, 0xFF, 0x10 }, "RAW(3)"));
        Assert.Equal(new byte[] { 0x00, 0xFF, 0x10 }, (byte[]?)Codec.FromKeyText("00ff10", "RAW(3)"));

        var big = "12345678901234567890123456789012345678";
        Assert.Equal(big, Codec.ToKeyText(N(big), "NUMBER"));
        Assert.Equal(N(big), Codec.FromKeyText(big, "NUMBER"));
        Assert.Equal(N("-0.000001"), Codec.FromKeyText("-0.0000010", "NUMBER")); // 정규화
        Assert.Equal("1.5", Codec.ToKeyText(1.50m, "NUMBER"));
        Assert.Equal("42", Codec.ToKeyText(42L, "NUMBER(19)"));
        Assert.Throws<FormatException>(() => Codec.FromKeyText("1E5", "NUMBER"));

        Assert.Equal("AB ", Codec.ToKeyText("AB ", "CHAR(3)")); // CHAR 끝 공백 그대로
        Assert.Equal("가", Codec.FromKeyText("가", "NVARCHAR2(10)"));
    }

    [Fact]
    public void Key_text_null_and_unexpected_values()
    {
        Assert.Null(Codec.ToKeyText(null, "NUMBER"));
        Assert.Null(Codec.ToKeyText(DBNull.Value, "DATE"));
        Assert.Null(Codec.ToKeyText(null, "CLOB")); // NULL 은 형식보다 먼저(Oracle 같음)
        Assert.Null(Codec.FromKeyText(null, "TIMESTAMP"));
        Assert.Throws<InvalidOperationException>(() => Codec.ToKeyText("12", "NUMBER"));
        Assert.Throws<InvalidOperationException>(() => Codec.ToKeyText(1.5d, "NUMBER"));
        Assert.Throws<InvalidOperationException>(() => Codec.ToKeyText("x", "DATE"));
        Assert.Throws<InvalidOperationException>(() => Codec.ToKeyText(5, "VARCHAR2"));
    }

    // ---- 숫자 결과 ----

    [Fact]
    public void ToInt64_truncates_and_clamps()
    {
        Assert.Null(Codec.ToInt64(null));
        Assert.Null(Codec.ToInt64(DBNull.Value));
        Assert.Equal(12, Codec.ToInt64(N("12.9")));
        Assert.Equal(-12, Codec.ToInt64(N("-12.9")));
        Assert.Equal(0, Codec.ToInt64(N("-0.5")));
        Assert.Equal(long.MaxValue, Codec.ToInt64(N("9223372036854775807")));
        Assert.Equal(long.MaxValue, Codec.ToInt64(N("9223372036854775808")));
        Assert.Equal(long.MinValue, Codec.ToInt64(N("-123456789012345678901234567890")));
        Assert.Equal(long.MaxValue, Codec.ToInt64(N("1" + new string('0', 125))));
        Assert.Equal(900, Codec.ToInt64("900")); // V$ 값은 글자
        Assert.Equal(12, Codec.ToInt64("12.7"));
        Assert.Equal(long.MaxValue, Codec.ToInt64("123456789012345678901234567890")); // decimal 범위 밖 글자도 끝값(리뷰 TB1 1~3)
        Assert.Equal(long.MinValue, Codec.ToInt64("-123456789012345678901234567890.5"));
        Assert.Equal(2, Codec.ToInt64(2.7d)); // 버림, 반올림 아님
        Assert.Equal(-2, Codec.ToInt64(-2.7f));
        Assert.Equal(long.MaxValue, Codec.ToInt64(1e30));
        Assert.Equal(5, Codec.ToInt64(5));
        Assert.Null(Codec.ToInt64(new object()));
    }

    [Fact]
    public void ToInt64Exact_overflows_and_rejects_non_numbers()
    {
        Assert.Equal(9223372036854775807, Codec.ToInt64Exact(N("9223372036854775807")));
        Assert.Equal(-7, Codec.ToInt64Exact(N("-7.9")));
        Assert.Equal(0, Codec.ToInt64Exact(N("0.5")));
        Assert.Throws<OverflowException>(() => Codec.ToInt64Exact(N("9223372036854775808")));
        Assert.Throws<OverflowException>(() => Codec.ToInt64Exact(N("-123456789012345678901234567890")));
        Assert.Throws<InvalidCastException>(() => Codec.ToInt64Exact("12"));
        Assert.Throws<InvalidCastException>(() => Codec.ToInt64Exact(12m));
        Assert.Throws<InvalidCastException>(() => Codec.ToInt64Exact(null));
    }

    [Fact]
    public void Number_text_zero_and_text()
    {
        Assert.Equal("12.5", Codec.NumberText(N("12.50")));
        Assert.Null(Codec.NumberText("12")); // 문자 MAX 값을 숫자로 보지 않는다
        Assert.Null(Codec.NumberText(12m));
        Assert.Null(Codec.NumberText(null));

        Assert.True(Codec.IsZero(N("0")));
        Assert.True(Codec.IsZero(N("-0.000")));
        Assert.True(Codec.IsZero(TiberoBulkNumber.FromBytes(TiberoBulkValuesTests.Bytes("01 80"))));
        Assert.False(Codec.IsZero(N("0.1")));
        Assert.False(Codec.IsZero(0m));
        Assert.False(Codec.IsZero("0"));
        Assert.False(Codec.IsZero(null));

        Assert.Null(Codec.ToText(null));
        Assert.Null(Codec.ToText(DBNull.Value));
        Assert.Equal("SCOTT", Codec.ToText("SCOTT"));
        Assert.Equal("-0.0000001", Codec.ToText(TiberoBulkNumber.FromBytes(TiberoBulkValuesTests.Bytes("03 42 76 F0"))));
        Assert.Equal("2024-01-02 03:04:05.000000007", Codec.ToText(new TiberoBulkTimestamp(new DateTime(2024, 1, 2, 3, 4, 5), 7)));
        Assert.Equal("1.5", Codec.ToText(1.5d)); // invariant
    }

    // ---- 바인드 글 ----

    [Fact]
    public void Bind_expression_wraps_date_timestamp_and_raw()
    {
        Assert.Equal("TO_DATE(:C1, 'YYYY-MM-DD HH24:MI:SS')", TiberoBulkValueCodec.BindExpression("C1", "DATE"));
        Assert.Equal("TO_TIMESTAMP(:C2, 'YYYY-MM-DD HH24:MI:SS.FF9')", TiberoBulkValueCodec.BindExpression("C2", "TIMESTAMP(9)"));
        Assert.Equal("HEXTORAW(:C3)", TiberoBulkValueCodec.BindExpression("C3", "RAW(2000)"));
        Assert.Equal(":C4", TiberoBulkValueCodec.BindExpression("C4", "NUMBER(38,10)"));
        Assert.Equal(":C5", TiberoBulkValueCodec.BindExpression("C5", "VARCHAR2(10 CHAR)"));
        Assert.Equal(":C6", TiberoBulkValueCodec.BindExpression("C6", "CLOB"));
        Assert.Equal(":C7", TiberoBulkValueCodec.BindExpression("C7", "BLOB"));
    }

    [Fact]
    public void Bind_text_per_type()
    {
        Assert.Equal("0001-01-01 00:00:00", TiberoBulkValueCodec.ToBindText(new DateTime(1, 1, 1), "DATE"));
        Assert.Equal("9999-12-31 23:59:59", TiberoBulkValueCodec.ToBindText(new DateTime(9999, 12, 31, 23, 59, 59), "DATE"));
        Assert.Equal("2024-01-02 03:04:05.123456789",
            TiberoBulkValueCodec.ToBindText(new TiberoBulkTimestamp(new DateTime(2024, 1, 2, 3, 4, 5), 123_456_789), "TIMESTAMP(9)"));
        Assert.Equal("2024-01-02 03:04:05.000000000",
            TiberoBulkValueCodec.ToBindText(new TiberoBulkTimestamp(new DateTime(2024, 1, 2, 3, 4, 5), 0), "TIMESTAMP"));
        Assert.Equal("2024-01-02 03:04:05.123456700",
            TiberoBulkValueCodec.ToBindText(new DateTime(2024, 1, 2, 3, 4, 5).AddTicks(1_234_567), "TIMESTAMP(7)"));
        Assert.Equal("00FF10", TiberoBulkValueCodec.ToBindText(new byte[] { 0x00, 0xFF, 0x10 }, "RAW(3)"));
        Assert.Equal(string.Empty, TiberoBulkValueCodec.ToBindText(Array.Empty<byte>(), "RAW(3)")); // 빈 글자 = 서버 NULL

        Assert.Equal("1" + new string('0', 125), TiberoBulkValueCodec.ToBindText(TiberoBulkNumber.FromBytes(TiberoBulkValuesTests.Bytes("02 FF 8A")), "NUMBER"));
        Assert.Equal("-12.5", TiberoBulkValueCodec.ToBindText(-12.50m, "NUMBER"));
        Assert.Equal("-9223372036854775808", TiberoBulkValueCodec.ToBindText(long.MinValue, "NUMBER(19)"));
        Assert.Equal("7", TiberoBulkValueCodec.ToBindText(7, "FLOAT"));

        Assert.Equal("한글 ", TiberoBulkValueCodec.ToBindText("한글 ", "NCHAR(3)"));
        Assert.Equal(new string('x', 5001), TiberoBulkValueCodec.ToBindText(new string('x', 5001), "CLOB"));

        Assert.Null(TiberoBulkValueCodec.ToBindText(null, "DATE"));
        Assert.Null(TiberoBulkValueCodec.ToBindText(DBNull.Value, "NUMBER"));
    }

    // ---- INTERVAL (TB1 커밋 9: win144 IT 실측 뒤 켬) ----

    [Theory]
    [InlineData("INTERVAL YEAR TO MONTH")]
    [InlineData("INTERVAL YEAR(9) TO MONTH")]
    [InlineData("INTERVAL DAY TO SECOND")]
    [InlineData("INTERVAL DAY(9) TO SECOND(9)")]
    public void Interval_types_are_supported_but_not_keys(string serverType)
    {
        Assert.True(Codec.IsSupported(serverType));
        Assert.Equal(BulkKind.Other, Codec.KindOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.BindTypeOf(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.ToKeyText(new TiberoBulkIntervalYm(1), serverType));
        Assert.Throws<NotSupportedException>(() => Codec.FromKeyText("+1-2", serverType));
    }

    [Theory]
    [InlineData("BINARY_FLOAT")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("TIMESTAMP(9) WITH TIME ZONE")]
    [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE")]
    [InlineData("LONG")]
    [InlineData("LONG RAW")]
    public void Other_disabled_types_stay_off(string serverType)
    {
        Assert.False(Codec.IsSupported(serverType));
    }

    [Fact]
    public void Interval_bind_expression_wraps_the_conversion_function()
    {
        Assert.Equal("TO_YMINTERVAL(:C1)", TiberoBulkValueCodec.BindExpression("C1", "INTERVAL YEAR(9) TO MONTH"));
        Assert.Equal("TO_DSINTERVAL(:C2)", TiberoBulkValueCodec.BindExpression("C2", "INTERVAL DAY(9) TO SECOND(9)"));
    }

    [Fact]
    public void Interval_year_to_month_bind_text()
    {
        const string Type = "INTERVAL YEAR(9) TO MONTH";
        Assert.Equal("+1-2", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(14), Type));
        Assert.Equal("-10-11", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(-131), Type));
        Assert.Equal("+0-0", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(0), Type));
        Assert.Equal("-0-5", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(-5), Type));
        Assert.Equal("+999999999-11", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(11_999_999_999), Type));
        Assert.Null(TiberoBulkValueCodec.ToBindText(null, Type));
        Assert.Null(TiberoBulkValueCodec.ToBindText(DBNull.Value, Type));
    }

    [Fact]
    public void Interval_day_to_second_bind_text()
    {
        const string Type = "INTERVAL DAY(9) TO SECOND(9)";
        Assert.Equal("+5 04:03:02.123456789", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(5, 4 * 3600 + 3 * 60 + 2, 123_456_789), Type));
        Assert.Equal("+0 00:00:00.000000000", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(0, 0, 0), Type));
        Assert.Equal("-1 00:00:01.000000001", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(-1, -1, -1), Type));
        Assert.Equal("-5 23:59:59.999999999", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(-5, -86399, -999_999_999), Type));
        Assert.Equal("+999999999 23:59:59.999999999", TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(999_999_999, 86399, 999_999_999), Type));
        Assert.Null(TiberoBulkValueCodec.ToBindText(null, Type));
    }

    [Fact]
    public void Interval_day_to_second_accepts_time_span_with_tick_precision()
    {
        const string Type = "INTERVAL DAY TO SECOND";
        Assert.Equal("+1 02:03:04.000000000", TiberoBulkValueCodec.ToBindText(new TimeSpan(1, 2, 3, 4), Type));
        Assert.Equal("+0 00:00:00.000000100", TiberoBulkValueCodec.ToBindText(TimeSpan.FromTicks(1), Type));
        Assert.Equal("-1 00:00:01.500000000", TiberoBulkValueCodec.ToBindText(-(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1.5)), Type));
    }

    [Fact]
    public void Interval_bind_text_round_trips_reader_records()
    {
        // 리더가 만드는 값(원시 바이트 → 레코드)이 글자로 가서 같은 값으로 읽히는 지점: 부호는 전체에 하나, 부분은 모두 같은 부호.
        var ym = TiberoBulkIntervalYm.FromBytes(TiberoBulkValuesTests.Bytes("3F FF FF F6 00")); // -10 년 -11 개월
        Assert.Equal(-131, ym.Months);
        Assert.Equal("-10-11", TiberoBulkValueCodec.ToBindText(ym, "INTERVAL YEAR(9) TO MONTH"));
        Assert.Equal("-10-11", Codec.ToText(ym));

        var ds = TiberoBulkIntervalDs.FromBytes(TiberoBulkValuesTests.Bytes("3F FF FF FF 3F FF FF FF 3F FF FF FF")); // -1일 -1초 -1ns
        Assert.Equal(new TiberoBulkIntervalDs(-1, -1, -1), ds);
        Assert.Equal("-1 00:00:01.000000001", TiberoBulkValueCodec.ToBindText(ds, "INTERVAL DAY(9) TO SECOND(9)"));
        Assert.Equal("-1 00:00:01.000000001", Codec.ToText(ds));

        var big = TiberoBulkIntervalDs.FromBytes(TiberoBulkValuesTests.Bytes("3F FF FF FB 3F FE AE 81 04 65 36 01"));
        Assert.Equal("-5 23:59:59.999999999", TiberoBulkValueCodec.ToBindText(big, "INTERVAL DAY TO SECOND"));
    }

    [Fact]
    public void Interval_text_for_logs()
    {
        Assert.Equal("+1-2", Codec.ToText(new TiberoBulkIntervalYm(14)));
        Assert.Equal("+5 04:03:02.123456789", Codec.ToText(new TiberoBulkIntervalDs(5, 14582, 123_456_789)));
    }

    [Fact]
    public void Interval_bind_text_rejects_other_value_types()
    {
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText("+1-2", "INTERVAL YEAR TO MONTH"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(14L, "INTERVAL YEAR TO MONTH"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalDs(1, 0, 0), "INTERVAL YEAR TO MONTH"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(TimeSpan.FromDays(1), "INTERVAL YEAR TO MONTH"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText("+1 00:00:00", "INTERVAL DAY TO SECOND"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(new TiberoBulkIntervalYm(1), "INTERVAL DAY TO SECOND"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(new TiberoBulkTimestamp(new DateTime(2024, 1, 1), 0), "INTERVAL DAY TO SECOND"));
    }

    [Theory]
    [InlineData("NUMBER")]
    [InlineData("DATE")]
    [InlineData("TIMESTAMP(6)")]
    [InlineData("RAW(16)")]
    [InlineData("VARCHAR2(10)")]
    [InlineData("CLOB")]
    [InlineData("INTERVAL YEAR TO MONTH")]
    [InlineData("INTERVAL DAY TO SECOND")]
    public void Bind_text_rejects_values_of_another_type(string serverType)
    {
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(new object(), serverType));
    }

    [Fact]
    public void Bind_text_mismatches_are_invalid_cast()
    {
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText("12", "NUMBER")); // 글자는 문자 열만
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(1.5d, "NUMBER"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText("2024-01-01", "DATE"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(new TiberoBulkTimestamp(new DateTime(2024, 1, 1), 0), "DATE"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText("00FF", "RAW"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindText(5, "VARCHAR2"));
    }

    [Fact]
    public void Blob_binds_bytes_not_text()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Same(bytes, TiberoBulkValueCodec.ToBindBytes(bytes, "BLOB"));
        Assert.Null(TiberoBulkValueCodec.ToBindBytes(null, "BLOB"));
        Assert.Null(TiberoBulkValueCodec.ToBindBytes(DBNull.Value, "BLOB"));
        Assert.Throws<InvalidCastException>(() => TiberoBulkValueCodec.ToBindBytes("01", "BLOB"));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.ToBindText(bytes, "BLOB"));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.ToBindBytes(bytes, "RAW(3)"));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.ToBindBytes(bytes, "LONG RAW")); // 꺼짐
    }
}
