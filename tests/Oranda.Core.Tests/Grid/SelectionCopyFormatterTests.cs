#pragma warning disable CA1861 // 시험 입력용 상수 배열
using Oranda.Core.Grid;

namespace Oranda.Core.Tests.Grid;

// 소유: TSK-06-03. 복사 형식·타입 표시.
public class SelectionCopyFormatterTests
{
    private static readonly ResultColumn Id = new(0, "ID", "NUMBER", ColumnDataKind.Number);
    private static readonly ResultColumn Name = new(1, "NAME", "VARCHAR2", ColumnDataKind.Text);
    private static readonly ResultColumn Hired = new(2, "hired at", "DATE", ColumnDataKind.Date);

    private static object?[][] Rows(params object?[][] rows) => rows;

    [Fact]
    public void Tsv_with_headers_quotes_special_cells_and_blanks_null()
    {
        var text = SelectionCopyFormatter.Format(
            CopyFormat.Tsv,
            [Id, Name],
            Rows([1m, "a\tb"], [2m, null], [3m, "say \"hi\"\nnow"]),
            new CopyOptions { IncludeHeaders = true });

        Assert.Equal("ID\tNAME\n1\t\"a\tb\"\n2\t\n3\t\"say \"\"hi\"\"\nnow\"", text);
    }

    [Fact]
    public void Csv_quotes_commas_and_uses_crlf()
    {
        var text = SelectionCopyFormatter.Format(CopyFormat.Csv, [Id, Name], Rows([1m, "x,y"], [2m, "z"]), new CopyOptions { IncludeHeaders = true });
        Assert.Equal("ID,NAME\r\n1,\"x,y\"\r\n2,z", text);
    }

    [Fact]
    public void Insert_quotes_strings_dates_null_and_identifiers()
    {
        var d = new DateTime(2026, 10, 19, 13, 5, 9);
        var text = SelectionCopyFormatter.Format(
            CopyFormat.Insert,
            [Id, Name, Hired],
            Rows([1m, "O'Neil", d], [2m, null, null]),
            new CopyOptions { TableName = "HR.EMP" });

        Assert.Equal(
            "INSERT INTO HR.EMP (ID, NAME, \"hired at\") VALUES (1, 'O''Neil', TO_DATE('2026-10-19 13:05:09', 'YYYY-MM-DD HH24:MI:SS'));\n" +
            "INSERT INTO HR.EMP (ID, NAME, \"hired at\") VALUES (2, NULL, NULL);",
            text);
    }

    [Fact]
    public void InList_skips_null_and_dedupes()
    {
        var text = SelectionCopyFormatter.Format(CopyFormat.InList, [Name], Rows(["a"], [null], ["b"], ["a"]));
        Assert.Equal("('a', 'b')", text);

        var nums = SelectionCopyFormatter.Format(CopyFormat.InList, [Id], Rows([1m], [2.50m]));
        Assert.Equal("(1, 2.50)", nums);
    }

    [Fact]
    public void Literals_cover_timestamp_interval_raw_boolean_and_lobs()
    {
        var ts = new ResultColumn(0, "T", "TIMESTAMP", ColumnDataKind.Timestamp);
        Assert.Equal("TO_TIMESTAMP('2026-01-02 03:04:05.123456', 'YYYY-MM-DD HH24:MI:SS.FF6')",
            CellValueFormatter.ToSqlLiteral(ts, new DateTime(2026, 1, 2, 3, 4, 5).AddTicks(1234560)));
        var raw = new ResultColumn(0, "R", "RAW", ColumnDataKind.Raw);
        Assert.Equal("HEXTORAW('0AFF')", CellValueFormatter.ToSqlLiteral(raw, new byte[] { 0x0A, 0xFF }));
        Assert.Equal("NULL", CellValueFormatter.ToSqlLiteral(raw, Array.Empty<byte>()));
        var ds = new ResultColumn(0, "I", "INTERVAL DAY TO SECOND", ColumnDataKind.IntervalDaySecond);
        Assert.Equal("INTERVAL '-01 02:03:04.000000' DAY TO SECOND", CellValueFormatter.ToSqlLiteral(ds, -new TimeSpan(1, 2, 3, 4)));
        var ym = new ResultColumn(0, "I", "INTERVAL YEAR TO MONTH", ColumnDataKind.IntervalYearMonth);
        Assert.Equal("INTERVAL '14-02' YEAR TO MONTH", CellValueFormatter.ToSqlLiteral(ym, 14L * 12 + 2));
        var clob = new ResultColumn(0, "C", "CLOB", ColumnDataKind.Clob);
        Assert.Equal("TO_CLOB('it''s')", CellValueFormatter.ToSqlLiteral(clob, new LobValue(LobKind.Clob, 4) { TextPreview = "it's" }));
        Assert.StartsWith("NULL", CellValueFormatter.ToSqlLiteral(clob, new LobValue(LobKind.Clob, 99999) { TextPreview = "x", IsTruncated = true }), StringComparison.Ordinal);
        Assert.Equal("TRUE", CellValueFormatter.ToSqlLiteral(new ResultColumn(0, "B", "BOOLEAN", ColumnDataKind.Boolean), true));
        Assert.Equal("TO_VECTOR('[1, 2.5]')", CellValueFormatter.ToSqlLiteral(new ResultColumn(0, "V", "VECTOR", ColumnDataKind.Vector), new[] { 1f, 2.5f }));
    }

    [Fact]
    public void Long_clob_literal_is_split_in_chunks()
    {
        var clob = new ResultColumn(0, "C", "CLOB", ColumnDataKind.Clob);
        var lit = CellValueFormatter.ToSqlLiteral(clob, new LobValue(LobKind.Clob, 9000) { TextPreview = new string('a', 9000) });
        Assert.Equal(3, lit.Split(" || ").Length);
    }

    [Fact]
    public void Format_displays_each_type()
    {
        string F(ResultColumn c, object? v, GridDisplayOptions? o = null) => CellValueFormatter.Format(c, v, o);
        var num = new ResultColumn(0, "N", "NUMBER", ColumnDataKind.Number);
        var thousands = new GridDisplayOptions { ThousandsSeparator = true };

        Assert.Equal("(null)", F(num, null));
        Assert.Equal("-", F(num, null, new GridDisplayOptions { NullText = "-" }));
        Assert.Equal("12345678901234567890.123456789", F(num, "12345678901234567890.123456789"));
        Assert.Equal("1,234,567.891", F(num, 1234567.891m, thousands));
        Assert.Equal("-1,000", F(num, "-1000", thousands));
        Assert.Equal("1E+30", F(num, "1E+30", thousands));
        Assert.Equal("0.1", F(num, 0.1d));
        Assert.Equal("2026-10-19 01:02:03", F(new ResultColumn(0, "D", "DATE", ColumnDataKind.Date), new DateTime(2026, 10, 19, 1, 2, 3)));
        Assert.Equal("19/10/2026", F(new ResultColumn(0, "D", "DATE", ColumnDataKind.Date), new DateTime(2026, 10, 19), new GridDisplayOptions { DateFormat = CellValueFormatter.NlsToDotNetFormat("DD/MM/YYYY") }));
        Assert.Equal("2026-10-19 01:02:03.500000 +09:00", F(new ResultColumn(0, "T", "TIMESTAMP WITH TIME ZONE", ColumnDataKind.TimestampTz), new DateTimeOffset(2026, 10, 19, 1, 2, 3, 500, TimeSpan.FromHours(9))));
        Assert.Equal("+01 02:03:04.000000", F(new ResultColumn(0, "I", "I", ColumnDataKind.IntervalDaySecond), new TimeSpan(1, 2, 3, 4)));
        Assert.Equal("+14-02", F(new ResultColumn(0, "I", "I", ColumnDataKind.IntervalYearMonth), 14L * 12 + 2));
        Assert.Equal("0AFF", F(new ResultColumn(0, "R", "RAW", ColumnDataKind.Raw), new byte[] { 0x0A, 0xFF }));
        var b = new ResultColumn(0, "B", "BOOLEAN", ColumnDataKind.Boolean);
        Assert.Equal("✓", F(b, true));
        Assert.Equal("FALSE", F(b, false, GridDisplayOptions.Plain));
        Assert.Equal("{\"a\":[1,2]}", F(new ResultColumn(0, "J", "JSON", ColumnDataKind.Json), "{ \"a\": [1, 2] }"));
        Assert.Contains('\n', F(new ResultColumn(0, "J", "JSON", ColumnDataKind.Json), "{\"a\":1}", new GridDisplayOptions { PrettyJson = true, SingleLine = false }));
        var vec = new ResultColumn(0, "V", "VECTOR", ColumnDataKind.Vector);
        Assert.Equal("VECTOR(3, FLOAT32)", F(vec, new[] { 1f, 2f, 3f }));
        Assert.Equal("[1, 2, 3]", F(vec, new[] { 1f, 2f, 3f }, GridDisplayOptions.Plain));
        Assert.Equal("VECTOR(2, FLOAT64)", F(vec, new[] { 1d, 2d }));
        Assert.Equal("a↵b c", F(new ResultColumn(0, "T", "VARCHAR2", ColumnDataKind.Text), "a\nb\tc"));
        Assert.Equal("abc…", F(new ResultColumn(0, "C", "CLOB", ColumnDataKind.Clob), new LobValue(LobKind.Clob, 9) { TextPreview = "abc", IsTruncated = true }));
        Assert.Equal("(BLOB 1,000 B) 0102…", F(new ResultColumn(0, "B", "BLOB", ColumnDataKind.Blob), new LobValue(LobKind.Blob, 1000) { BinaryPreview = new byte[] { 1, 2 }, IsTruncated = true }));
    }

    [Theory]
    [InlineData("YYYY-MM-DD HH24:MI:SS", "yyyy-MM-dd HH:mm:ss")]
    [InlineData("DD-MON-RR", "dd-MMM-yy")]
    [InlineData("YYYY-MM-DD HH24:MI:SS.FF3", "yyyy-MM-dd HH:mm:ss.fff")]
    [InlineData("YYYY\"년\" MM\"월\"", "yyyy'년' MM'월'")]
    public void Nls_format_converts_to_dotnet(string nls, string expected)
    {
        Assert.Equal(expected.Replace("-", "-", StringComparison.Ordinal), CellValueFormatter.NlsToDotNetFormat(nls).Replace("\\:", ":").Replace("\\.", ".").Replace("\\/", "/"));
    }
}
