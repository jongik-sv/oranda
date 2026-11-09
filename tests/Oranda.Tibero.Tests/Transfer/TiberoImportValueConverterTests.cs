using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Tibero.Transfer;

namespace Oranda.Tibero.Tests.Transfer;

/// <summary>Tibero 가져오기 변환기(TB2): 6.2.15 사전이 내는 열 타입 이름 → 분류, 그 분류별 SQL 리터럴. 서버 없이 돈다.</summary>
public class TiberoImportValueConverterTests
{
    private static readonly TiberoImportValueConverter Converter = new();

    private static ColumnInfo Col(string type) => new("C", 1, type);

    [Theory]
    [InlineData("VARCHAR", ColumnDataKind.Text)]
    [InlineData("NVARCHAR", ColumnDataKind.Text)]
    [InlineData("CHAR", ColumnDataKind.Text)]
    [InlineData("NCHAR", ColumnDataKind.Text)]
    [InlineData("LONG", ColumnDataKind.Text)]
    [InlineData("NUMBER", ColumnDataKind.Number)]
    [InlineData("BINARY_FLOAT", ColumnDataKind.BinaryFloat)]
    [InlineData("BINARY_DOUBLE", ColumnDataKind.BinaryFloat)]
    [InlineData("DATE", ColumnDataKind.Date)]
    [InlineData("TIMESTAMP(6)", ColumnDataKind.Timestamp)]
    [InlineData("TIMESTAMP(9)", ColumnDataKind.Timestamp)]
    [InlineData("TIMESTAMP(6) WITH TIME ZONE", ColumnDataKind.TimestampTz)]
    [InlineData("TIMESTAMP(9) WITH LOCAL TIME ZONE", ColumnDataKind.TimestampTz)]
    [InlineData("INTERVAL YEAR(9) TO MONTH", ColumnDataKind.IntervalYearMonth)]
    [InlineData("INTERVAL DAY(3) TO SECOND(0)", ColumnDataKind.IntervalDaySecond)]
    [InlineData("CLOB", ColumnDataKind.Clob)]
    [InlineData("NCLOB", ColumnDataKind.Clob)]
    [InlineData("BLOB", ColumnDataKind.Blob)]
    [InlineData("RAW", ColumnDataKind.Raw)]
    [InlineData("LONG RAW", ColumnDataKind.Raw)]
    [InlineData("ROWID", ColumnDataKind.RowId)]
    [InlineData("BOOLEAN", ColumnDataKind.Other)] // 이 서버에는 BOOLEAN 열·리터럴이 없다
    [InlineData("XMLTYPE", ColumnDataKind.Other)]
    public void Dictionary_type_names_map_to_kinds(string dataType, ColumnDataKind expected) =>
        Assert.Equal(expected, Converter.KindOf(dataType));

    [Theory]
    [InlineData("NUMBER", "12.5", "12.5")]
    [InlineData("NUMBER", "+3", "3")]
    [InlineData("NUMBER", "1.5E3", "1.5E3")]
    [InlineData("NUMBER", "", "NULL")]
    [InlineData("VARCHAR", "O'Neil", "'O''Neil'")]
    [InlineData("DATE", "2026-03-05 14:30:15", "TO_DATE('2026-03-05 14:30:15', 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("TIMESTAMP(6)", "2026-03-05 14:30:15.25", "TO_TIMESTAMP('2026-03-05 14:30:15.250000', 'YYYY-MM-DD HH24:MI:SS.FF6')")]
    [InlineData("RAW", "0xCAFE", "HEXTORAW('CAFE')")]
    [InlineData("BLOB", "00FF10", "TO_BLOB(HEXTORAW('00FF10'))")]
    [InlineData("CLOB", "abc", "'abc'")]
    [InlineData("INTERVAL YEAR(9) TO MONTH", "1-2", "INTERVAL '1-2' YEAR TO MONTH")]
    [InlineData("INTERVAL DAY(3) TO SECOND(0)", "3 04:05:06", "INTERVAL '3 04:05:06' DAY TO SECOND")]
    public void Literals_use_the_shared_tibero_checked_syntax(string type, string input, string expected) =>
        Assert.Equal(expected, Converter.ToLiteral(Col(type), input));

    [Fact]
    public void Time_zone_timestamps_use_an_explicit_format_and_offset()
    {
        var literal = Converter.ToLiteral(Col("TIMESTAMP(6) WITH TIME ZONE"), new DateTimeOffset(2026, 3, 5, 14, 30, 15, TimeSpan.FromHours(9)));
        Assert.Equal("TO_TIMESTAMP_TZ('2026-03-05 14:30:15.000000 +09:00', 'YYYY-MM-DD HH24:MI:SS.FF6 TZH:TZM')", literal);
    }

    [Theory]
    [InlineData("NUMBER", "abc")]
    [InlineData("DATE", "내일")]
    [InlineData("RAW", "XYZ")]
    public void Values_that_do_not_fit_throw_and_preview_reports_the_reason(string type, string input)
    {
        Assert.Throws<FormatException>(() => Converter.ToLiteral(Col(type), input));
        Assert.False(Converter.TryConvert(Col(type), input, out var literal, out var reason));
        Assert.Equal(string.Empty, literal);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void Blob_up_to_2000_bytes_is_one_literal_and_larger_values_are_rejected_with_the_reason()
    {
        // 6.2.15: TO_BLOB(HEXTORAW) 는 2000바이트까지이고 BLOB 끼리 || 가 안 된다(ODP-11003) — 이어 붙인 리터럴을 만들지 않는다.
        var ok = Converter.ToLiteral(Col("BLOB"), new string('A', 4000));
        Assert.Equal("TO_BLOB(HEXTORAW('" + new string('A', 4000) + "'))", ok);

        var tooLarge = new string('A', 4002);
        var ex = Assert.Throws<FormatException>(() => Converter.ToLiteral(Col("BLOB"), tooLarge));
        Assert.Contains("2000", ex.Message, StringComparison.Ordinal);
        Assert.False(Converter.TryConvert(Col("BLOB"), tooLarge, out _, out var reason));
        Assert.Contains("2000", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Long_clob_text_is_split_into_chained_to_clob_pieces()
    {
        var literal = Converter.ToLiteral(Col("CLOB"), new string('가', 4000));

        Assert.StartsWith("TO_CLOB('", literal, StringComparison.Ordinal);
        Assert.True(literal.Split("||").Length >= 4);
    }
}
