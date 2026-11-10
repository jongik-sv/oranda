using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Tibero.Execution;

namespace Oranda.Tibero.Tests.Execution;

// 소유: XC4. Tibero 코덱의 교차 복사 이식 값 멤버(IsReadable·ToPortable·FromPortable)와 교차 쓰기 바인드(CrossBindText). 드라이버 없음.
public class TiberoBulkPortableTests
{
    private static readonly TiberoBulkValueCodec Codec = TiberoBulkValueCodec.Instance;

    private static TiberoBulkNumber N(string text) => TiberoBulkNumber.Parse(text);

    // ---- IsReadable ----

    [Theory]
    [InlineData("NUMBER(10,2)")]
    [InlineData("FLOAT")]
    [InlineData("VARCHAR2(10)")]
    [InlineData("VARCHAR")]
    [InlineData("CHAR(3)")]
    [InlineData("NVARCHAR2(5)")]
    [InlineData("NCHAR(2)")]
    [InlineData("CLOB")]
    [InlineData("NCLOB")]
    [InlineData("BLOB")]
    [InlineData("RAW(8)")]
    [InlineData("DATE")]
    [InlineData("TIMESTAMP(9)")]
    [InlineData("BINARY_FLOAT")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE")]
    [InlineData("INTERVAL YEAR(2) TO MONTH")]
    [InlineData("INTERVAL DAY(2) TO SECOND(6)")]
    [InlineData("LONG")]
    [InlineData("LONG RAW")]
    public void Readable_types(string serverType)
    {
        Assert.True(Codec.IsReadable(serverType));
    }

    [Theory]
    [InlineData("TIMESTAMP(6) WITH LOCAL TIME ZONE")]
    [InlineData("XMLTYPE")]
    [InlineData("ROWID")]
    public void Local_time_zone_and_unknown_types_are_not_readable(string serverType)
    {
        Assert.False(Codec.IsReadable(serverType));
        Assert.Throws<NotSupportedException>(() => Codec.ToPortable(N("1"), serverType));
        Assert.Throws<NotSupportedException>(() => Codec.FromPortable("x", serverType));
    }

    [Theory]
    [InlineData("BINARY_FLOAT")]
    [InlineData("BINARY_DOUBLE")]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE")]
    [InlineData("LONG")]
    [InlineData("LONG RAW")]
    public void Same_dbms_write_support_is_unchanged(string serverType)
    {
        Assert.False(Codec.IsSupported(serverType)); // 읽기만 되고 같은 DBMS 쓰기는 꺼진 채
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.ToBindText("x", serverType));
        Assert.Throws<NotSupportedException>(() => TiberoBulkValueCodec.BindExpression("C1", serverType));
    }

    // ---- ToPortable / FromPortable 왕복 ----

    [Fact]
    public void Number_round_trips_without_exponent()
    {
        var portable = Codec.ToPortable(N("-12345678901234567890.0000001"), "NUMBER(38,7)");
        Assert.Equal(PortableNumber.FromText("-12345678901234567890.0000001"), portable);
        Assert.Equal(N("-12345678901234567890.0000001"), Codec.FromPortable(portable, "NUMBER"));
        Assert.Equal(N("0"), Codec.FromPortable(default(PortableNumber), "NUMBER"));
    }

    [Fact]
    public void Exponent_text_is_expanded_to_fixed_point()
    {
        var big = (PortableNumber)Codec.ToPortable(N("100000000000000000000"), "NUMBER")!;
        Assert.Equal("100000000000000000000", big.Text);
        Assert.Equal(N("0.0000001"), Codec.FromPortable(PortableNumber.FromText("1E-7"), "NUMBER"));
        Assert.Equal(N("1500"), Codec.FromPortable(PortableNumber.FromText("1.5E+3"), "NUMBER"));
    }

    [Fact]
    public void Binary_values_become_number_text_with_round_trip_digits()
    {
        Assert.Equal(new PortableBinaryFloat(1.5f), Codec.ToPortable(1.5f, "BINARY_FLOAT"));
        Assert.Equal(new PortableBinaryDouble(0.1d), Codec.ToPortable(0.1d, "BINARY_DOUBLE"));

        Assert.Equal(N("0.1"), Codec.FromPortable(new PortableBinaryDouble(0.1d), "NUMBER"));
        Assert.Equal(N("0.1"), Codec.FromPortable(new PortableBinaryFloat(0.1f), "NUMBER")); // float 는 float 의 R 글자
        Assert.Equal(N("0.0000001"), Codec.FromPortable(new PortableBinaryDouble(1e-7d), "NUMBER"));
        Assert.Equal("0.1", TiberoBulkValueCodec.CrossBindText(Codec.FromPortable(new PortableBinaryDouble(0.1d), "NUMBER"), "NUMBER"));
    }

    [Fact]
    public void Nan_and_infinity_fail_the_row_when_written_as_number()
    {
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryDouble(double.NaN), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryDouble(double.PositiveInfinity), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryFloat(float.NegativeInfinity), "NUMBER"));
        Assert.Throws<PortableConversionException>(() => TiberoBulkValueCodec.CrossBindText(double.NaN, "BINARY_DOUBLE"));
        // 읽기는 NaN 도 이식 값으로 낸다(대상이 BINARY_* 면 담을 수 있다).
        Assert.Equal(new PortableBinaryDouble(double.NaN), Codec.ToPortable(double.NaN, "BINARY_DOUBLE"));
    }

    [Theory]
    [InlineData(1e200)]
    [InlineData(1e126)]
    [InlineData(1e-200)]
    [InlineData(-1e-131)]
    public void Numbers_outside_the_tibero_number_range_fail_the_row(double value)
    {
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableBinaryDouble(value), "NUMBER"));
    }

    [Theory]
    [InlineData(1e125)]
    [InlineData(1e-130)]
    [InlineData(0.0)]
    [InlineData(-123.456)]
    public void Numbers_inside_the_tibero_number_range_pass(double value)
    {
        Assert.IsType<TiberoBulkNumber>(Codec.FromPortable(new PortableBinaryDouble(value), "NUMBER"));
    }

    [Fact]
    public void Cross_write_turns_on_time_zone_only_and_leaves_same_dbms_support_unchanged()
    {
        Assert.True(Codec.IsCrossWritable("TIMESTAMP WITH TIME ZONE"));
        Assert.False(Codec.IsSupported("TIMESTAMP WITH TIME ZONE"));
        Assert.True(Codec.IsCrossWritable("NUMBER"));
        Assert.False(Codec.IsCrossWritable("TIMESTAMP WITH LOCAL TIME ZONE"));
        Assert.False(Codec.IsCrossWritable("BINARY_DOUBLE"));
    }

    [Theory]
    [InlineData("-00:30", "2026-11-10 09:30:00.500000000 +00:00")]
    [InlineData("-00:01", "2026-11-10 09:01:00.500000000 +00:00")]
    public void A_negative_offset_with_zero_hours_is_moved_to_the_same_instant_at_plus_zero(string offset, string expected)
    {
        // XC5 실측: 서버가 시 0 인 음수 오프셋의 부호를 잃는다 → 같은 시각을 +00:00 으로 옮겨 쓴다.
        var portable = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 11, 10, 9, 0, 0), 500_000_000), offset, null);
        var tz = Assert.IsType<TiberoBulkTimestampTz>(Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal(expected, TiberoBulkValueCodec.CrossBindText(tz, "TIMESTAMP WITH TIME ZONE"));
    }

    [Theory]
    [InlineData("-01:00")]
    [InlineData("-03:30")]
    public void A_negative_offset_with_hours_keeps_its_offset(string offset)
    {
        var portable = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 11, 10, 9, 0, 0), 0), offset, null);
        var tz = Assert.IsType<TiberoBulkTimestampTz>(Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal("2026-11-10 09:00:00.000000000 " + offset, TiberoBulkValueCodec.CrossBindText(tz, "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Binary_bind_type_is_written_as_number_text()
    {
        Assert.Equal("1.5", TiberoBulkValueCodec.CrossBindText(1.5f, "BINARY_FLOAT"));
        Assert.Equal("-2.25", TiberoBulkValueCodec.CrossBindText(-2.25d, "BINARY_DOUBLE"));
        Assert.Null(TiberoBulkValueCodec.CrossBindText(null, "BINARY_DOUBLE"));
        Assert.Equal(":C1", TiberoBulkValueCodec.CrossBindExpression("C1", "BINARY_DOUBLE"));
    }

    [Fact]
    public void Text_and_binary_values_pass_through()
    {
        Assert.Equal("가나 ", Codec.ToPortable("가나 ", "CHAR(3)"));
        Assert.Equal("long text", Codec.ToPortable("long text", "LONG"));
        Assert.Equal("clob", Codec.FromPortable("clob", "CLOB"));
        Assert.Equal("clob", Codec.FromPortable("clob", "NCLOB"));
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Same(bytes, Codec.ToPortable(bytes, "RAW(3)"));
        Assert.Same(bytes, Codec.ToPortable(bytes, "BLOB"));
        Assert.Same(bytes, Codec.ToPortable(bytes, "LONG RAW"));
        Assert.Same(bytes, Codec.FromPortable(bytes, "BLOB"));
        Assert.Same(bytes, Codec.FromPortable(bytes, "RAW"));
        Assert.Null(Codec.ToPortable(null, "VARCHAR2"));
        Assert.Null(Codec.ToPortable(DBNull.Value, "NUMBER"));
        Assert.Null(Codec.FromPortable(null, "NUMBER"));
    }

    [Fact]
    public void Date_and_timestamp_keep_nanoseconds()
    {
        var date = new DateTime(2026, 3, 5, 14, 30, 15);
        Assert.Equal(date, Codec.ToPortable(date, "DATE"));
        Assert.Equal(date, Codec.FromPortable(date, "DATE"));

        var stamp = new TiberoBulkTimestamp(date, 123_456_789);
        var portable = Codec.ToPortable(stamp, "TIMESTAMP(9)");
        Assert.Equal(new PortableTimestamp(date, 123_456_789), portable);
        Assert.Equal(stamp, Codec.FromPortable(portable, "TIMESTAMP"));
        Assert.Equal("2026-03-05 14:30:15.123456789", TiberoBulkValueCodec.ToBindText(Codec.FromPortable(portable, "TIMESTAMP"), "TIMESTAMP"));
    }

    [Fact]
    public void Interval_round_trips_including_negative_values()
    {
        Assert.Equal(new PortableIntervalYm(-131), Codec.ToPortable(new TiberoBulkIntervalYm(-131), "INTERVAL YEAR TO MONTH"));
        Assert.Equal(new TiberoBulkIntervalYm(-131), Codec.FromPortable(new PortableIntervalYm(-131), "INTERVAL YEAR TO MONTH"));

        var ds = new TiberoBulkIntervalDs(-5, -86399, -999_999_999);
        var portable = Codec.ToPortable(ds, "INTERVAL DAY(9) TO SECOND(9)");
        Assert.Equal(new PortableIntervalDs(-((Int128)5 * 86_400_000_000_000 + (Int128)86_399_999_999_999)), portable);
        var back = Codec.FromPortable(portable, "INTERVAL DAY TO SECOND");
        Assert.Equal(ds, back);
        Assert.Equal("-5 23:59:59.999999999", TiberoBulkValueCodec.ToBindText(back, "INTERVAL DAY TO SECOND"));
        Assert.Equal(new TiberoBulkIntervalDs(0, 0, 0), Codec.FromPortable(new PortableIntervalDs(0), "INTERVAL DAY TO SECOND"));
    }

    // ---- TIMESTAMP WITH TIME ZONE ----

    [Fact]
    public void Time_zone_value_reads_as_wall_clock_with_offset()
    {
        var tz = new TiberoBulkTimestampTz(new DateTime(2026, 7, 1, 3, 0, 0), 123_456_789, TimeSpan.FromHours(9));
        var portable = (PortableTimestampTz)Codec.ToPortable(tz, "TIMESTAMP(9) WITH TIME ZONE")!;
        Assert.Equal(new DateTime(2026, 7, 1, 12, 0, 0), portable.Local.Seconds);
        Assert.Equal(123_456_789, portable.Local.Nanos);
        Assert.Equal("+09:00", portable.Offset);
        Assert.Null(portable.Region);
    }

    [Fact]
    public void Time_zone_offset_round_trips_to_bind_text_with_negative_minutes()
    {
        var tz = new TiberoBulkTimestampTz(new DateTime(2026, 7, 1, 15, 30, 0), 5, new TimeSpan(-3, -30, 0));
        var portable = Codec.ToPortable(tz, "TIMESTAMP WITH TIME ZONE");
        Assert.Equal("-03:30", ((PortableTimestampTz)portable!).Offset);

        var written = Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE");
        Assert.Equal(tz, written);
        Assert.Equal("2026-07-01 12:00:00.000000005 -03:30", TiberoBulkValueCodec.CrossBindText(written, "TIMESTAMP WITH TIME ZONE"));
        Assert.Null(TiberoBulkValueCodec.CrossBindText(null, "TIMESTAMP WITH TIME ZONE"));
        Assert.Equal(
            "TO_TIMESTAMP_TZ(:C1, 'YYYY-MM-DD HH24:MI:SS.FF9 TZH:TZM')",
            TiberoBulkValueCodec.CrossBindExpression("C1", "TIMESTAMP WITH TIME ZONE"));
    }

    [Theory]
    [InlineData("Asia/Seoul", 2026, 7, 1, 12, "+09:00")]
    [InlineData("America/New_York", 2026, 7, 1, 12, "-04:00")] // 여름
    [InlineData("America/New_York", 2026, 1, 15, 12, "-05:00")] // 겨울
    public void Region_becomes_the_offset_of_that_wall_clock_time(string region, int y, int mo, int d, int h, string expected)
    {
        var local = new DateTime(y, mo, d, h, 0, 0);
        var portable = new PortableTimestampTz(new PortableTimestamp(local, 0), null, region);

        var written = (TiberoBulkTimestampTz)Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE")!;
        Assert.Equal(expected, PortableTimestampTz.FormatOffset((int)written.Offset.TotalMinutes));
        Assert.Equal(local, written.Utc + written.Offset);
        Assert.EndsWith(expected, TiberoBulkValueCodec.CrossBindText(written, "TIMESTAMP WITH TIME ZONE"), StringComparison.Ordinal);
    }

    [Fact]
    public void Known_offset_wins_over_region()
    {
        var portable = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 11, 1, 1, 30, 0), 0), "-04:00", "America/New_York");
        var written = (TiberoBulkTimestampTz)Codec.FromPortable(portable, "TIMESTAMP WITH TIME ZONE")!; // 모호한 벽시계 시각이어도 오프셋이 정한다
        Assert.Equal(TimeSpan.FromHours(-4), written.Offset);
    }

    [Fact]
    public void Unknown_region_and_unreal_wall_clock_times_fail_the_row()
    {
        var unknown = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 7, 1), 0), null, "Mars/Olympus_Mons");
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(unknown, "TIMESTAMP WITH TIME ZONE"));

        var gap = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 3, 8, 2, 30, 0), 0), null, "America/New_York");
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(gap, "TIMESTAMP WITH TIME ZONE"));

        var ambiguous = new PortableTimestampTz(new PortableTimestamp(new DateTime(2026, 11, 1, 1, 30, 0), 0), null, "America/New_York");
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(ambiguous, "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Time_zone_at_the_calendar_edge_fails_the_row()
    {
        var edge = new PortableTimestampTz(new PortableTimestamp(new DateTime(1, 1, 1, 0, 0, 0), 0), "+09:00", null);
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(edge, "TIMESTAMP WITH TIME ZONE")); // UTC 가 기원전
    }

    // ---- 형식 불일치 ----

    [Fact]
    public void Mismatched_native_values_are_invalid_cast()
    {
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable("1", "NUMBER"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable(1.5d, "BINARY_FLOAT"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable(new byte[1], "VARCHAR2"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable("2026", "DATE"));
        Assert.Throws<InvalidCastException>(() => Codec.ToPortable(new TiberoBulkTimestamp(new DateTime(2026, 1, 1), 0), "TIMESTAMP WITH TIME ZONE"));
    }

    [Fact]
    public void Mismatched_portable_values_fail_the_row()
    {
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable("1", "NUMBER"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(PortableNumber.FromText("1"), "VARCHAR2"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new PortableTimestamp(new DateTime(2026, 1, 1), 0), "DATE"));
        Assert.Throws<PortableConversionException>(() => Codec.FromPortable(new byte[1], "CLOB"));
        Assert.Throws<NotSupportedException>(() => Codec.FromPortable("x", "LONG"));
    }
}
