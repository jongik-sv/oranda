using System.IO.Compression;
using System.Text.RegularExpressions;
using Oranda.Core.Grid;
using Oranda.Core.Transfer;
using Oranda.Oracle.Transfer;

namespace Oranda.Oracle.Tests.Transfer;

/// <summary>「Excel 로 내보내기」가 쓰는 xlsx 쓰기: 파일을 zip 으로 풀어 시트·스타일 XML 을 직접 본다.</summary>
public sealed class XlsxWriterTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "이름", "VARCHAR2", ColumnDataKind.Text),
        new(2, "HIRED", "DATE", ColumnDataKind.Date),
    ];

    private static (Dictionary<string, string> Parts, IList<string> Warnings) Write(ExportOptions options, IReadOnlyList<ResultColumn> columns, IEnumerable<object?[]> rows)
    {
        using var ms = new MemoryStream();
        IList<string> warnings;
        using (var writer = TransferWriters.Create(options, ms))
        {
            writer.Begin(columns);
            foreach (var r in rows)
            {
                writer.WriteRow(r);
            }

            writer.Finish();
            warnings = writer.Warnings;
        }

        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        return (zip.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        }), warnings);
    }

    private static ExportOptions Xlsx(string? sheet = null) => new(TransferFormat.Xlsx) { SheetName = sheet };

    private static object?[] Row(object? id, object? name, object? hired) => [id, name, hired];

    [Fact]
    public void Header_is_bold_shaded_frozen_and_filterable()
    {
        var (parts, _) = Write(Xlsx(), Cols, [Row(1m, "가", new DateTime(2024, 1, 2)), Row(2m, "나", null), Row(3m, "다", null)]);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        var styles = parts["xl/styles.xml"];

        // 머리글 칸은 스타일 3(굵은 글씨 + 옅은 배경).
        Assert.Matches(new Regex("<c r=\"A1\" s=\"3\" t=\"inlineStr\">"), sheet);
        Assert.Contains("<b/>", styles, StringComparison.Ordinal);
        // 첫 xf 는 cellStyleXfs 의 것이라 cellXfs 의 3번 스타일은 인덱스 4.
        var xfs = Regex.Matches(styles, "<xf [^>]*/>").Select(m => m.Value).ToList();
        Assert.Contains("fontId=\"1\"", xfs[4], StringComparison.Ordinal);
        Assert.Contains("fillId=\"2\"", xfs[4], StringComparison.Ordinal);
        Assert.Contains("patternType=\"solid\"", styles, StringComparison.Ordinal);

        Assert.Contains("ySplit=\"1\"", sheet, StringComparison.Ordinal);
        Assert.Contains("state=\"frozen\"", sheet, StringComparison.Ordinal);
        Assert.Contains("<autoFilter ref=\"A1:C4\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_header_there_is_no_freeze_or_filter()
    {
        var (parts, _) = Write(new ExportOptions(TransferFormat.Xlsx) { IncludeHeader = false }, Cols, [Row(1m, "a", null)]);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.DoesNotContain("<pane", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("<autoFilter", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A1\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Column_widths_follow_content_and_stop_at_sixty()
    {
        var (parts, _) = Write(Xlsx(), Cols, [Row(1m, new string('x', 200), null), Row(22m, "가나다라마바사아자차", null)]);
        var widths = Regex.Matches(parts["xl/worksheets/sheet1.xml"], "<col min=\"(\\d+)\" max=\"\\1\" width=\"([\\d.]+)\" customWidth=\"1\" />")
            .Select(m => double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(widths.Length == 3, parts["xl/worksheets/sheet1.xml"][..600]);
        Assert.Equal(60, widths[1]);          // 200자 글은 최대 60 에서 멈춘다.
        Assert.True(widths[0] >= 8 && widths[0] < 20);
        Assert.True(widths[2] >= 8);          // 값이 없어도 머리글 폭(+필터 단추)만큼은 확보
    }

    [Fact]
    public void Wide_characters_count_as_two_columns_in_header_width()
    {
        var cols = new ResultColumn[] { new(0, "가나다라마바사아", "VARCHAR2", ColumnDataKind.Text), new(1, "abcdefgh", "VARCHAR2", ColumnDataKind.Text) };
        var (parts, _) = Write(Xlsx(), cols, [["a", "b"]]);
        var widths = Regex.Matches(parts["xl/worksheets/sheet1.xml"], "width=\"([\\d.]+)\"").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.True(widths[0] > widths[1]);
    }

    [Fact]
    public void Numbers_are_numeric_cells_dates_use_date_format_and_nulls_are_empty()
    {
        var (parts, _) = Write(Xlsx(), Cols, [Row(12.5m, "x", new DateTime(2024, 3, 4, 5, 6, 7)), Row(null, null, null)]);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Matches(new Regex("<c r=\"A2\"><v>12.5</v></c>"), sheet);          // t 속성 없음 = 숫자 셀
        Assert.True(Regex.IsMatch(sheet, "<c r=\"C2\" s=\"1\"><v>45355\\.2125"), sheet);    // 날짜 직렬값 + 날짜 서식
        Assert.Contains("numFmtId=\"164\"", parts["xl/styles.xml"], StringComparison.Ordinal);
        Assert.Contains("<row r=\"3\" />", sheet, StringComparison.Ordinal);
        Assert.DoesNotContain("r=\"A3\"", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Timestamp_uses_fraction_format()
    {
        var cols = new ResultColumn[] { new(0, "T", "TIMESTAMP", ColumnDataKind.Timestamp) };
        var (parts, _) = Write(Xlsx(), cols, [[new DateTime(2024, 3, 4, 5, 6, 7, 123)]]);
        Assert.Contains("<c r=\"A2\" s=\"2\">", parts["xl/worksheets/sheet1.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void Numbers_over_fifteen_digits_become_text_cells()
    {
        var (parts, _) = Write(Xlsx(), Cols, [Row(1234567890123456m, "x", null), Row(123456789012345m, "y", null)]);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Contains("<c r=\"A2\" t=\"inlineStr\"><is><t xml:space=\"preserve\">1234567890123456</t></is></c>", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A3\"><v>123456789012345</v></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_over_cell_limit_is_cut_with_ellipsis_and_warned()
    {
        var (parts, warnings) = Write(Xlsx(), Cols, [Row(1m, new string('가', 40000), null)]);
        var m = Regex.Match(parts["xl/worksheets/sheet1.xml"], "<c r=\"B2\"[^>]*><is><t[^>]*>([^<]*)</t>");
        Assert.True(m.Success);
        Assert.Equal(32767, m.Groups[1].Value.Length);
        Assert.EndsWith("…", m.Groups[1].Value, StringComparison.Ordinal);
        Assert.Single(warnings);
    }

    [Fact]
    public void Cut_does_not_split_a_surrogate_pair()
    {
        var text = new string('a', 32765) + "😀😀";
        var (parts, _) = Write(Xlsx(), Cols, [Row(1m, text, null)]);
        var m = Regex.Match(parts["xl/worksheets/sheet1.xml"], "<c r=\"B2\"[^>]*><is><t[^>]*>([^<]*)</t>");
        var cell = m.Groups[1].Value;
        Assert.EndsWith("…", cell, StringComparison.Ordinal);
        Assert.True(cell.Length <= 32767);
        Assert.False(char.IsHighSurrogate(cell[^2]));
    }

    [Theory]
    [InlineData("EMP", 1, "EMP")]
    [InlineData("", 1, "Sheet1")]
    [InlineData(null, 2, "Sheet2")]
    [InlineData("a:b/c\\d?e*f[g]h", 1, "a_b_c_d_e_f_g_h")]
    [InlineData("'quoted'", 1, "quoted")]
    [InlineData("history", 1, "history_")]
    [InlineData("EMP", 3, "EMP_3")]
    public void Sheet_names_are_cleaned(string? name, int index, string expected) =>
        Assert.Equal(expected, TransferWriters.XlsxSheetName(name, index));

    [Fact]
    public void Sheet_name_is_cut_to_thirty_one_characters_including_suffix()
    {
        var name = new string('A', 50);
        Assert.Equal(31, TransferWriters.XlsxSheetName(name, 1).Length);
        var second = TransferWriters.XlsxSheetName(name, 2);
        Assert.Equal(31, second.Length);
        Assert.EndsWith("_2", second, StringComparison.Ordinal);
    }

    [Fact]
    public void Workbook_uses_sheet_name_and_splits_sheets_with_distinct_names()
    {
        var options = new ExportOptions(TransferFormat.Xlsx) { SheetName = "EMP", MaxRowsPerSheet = 2 };
        var (parts, _) = Write(options, Cols, Enumerable.Range(1, 5).Select(i => Row((decimal)i, "x", null)));
        var wb = parts["xl/workbook.xml"];
        Assert.Contains("name=\"EMP\"", wb, StringComparison.Ordinal);
        Assert.Contains("name=\"EMP_2\"", wb, StringComparison.Ordinal);
        Assert.Contains("name=\"EMP_3\"", wb, StringComparison.Ordinal);
        Assert.True(parts.ContainsKey("xl/worksheets/sheet3.xml"));
        // 시트마다 머리글·고정·필터가 따로 붙는다.
        Assert.Contains("<autoFilter ref=\"A1:C3\"", parts["xl/worksheets/sheet1.xml"], StringComparison.Ordinal);
        Assert.Contains("<autoFilter ref=\"A1:C2\"", parts["xl/worksheets/sheet3.xml"], StringComparison.Ordinal);
    }

    [Fact]
    public void More_rows_than_the_width_sample_are_all_written()
    {
        var (parts, _) = Write(Xlsx(), Cols, Enumerable.Range(1, 2500).Select(i => Row((decimal)i, "x" + i, null)));
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Equal(2501, Regex.Count(sheet, "<row "));
        Assert.Contains("<autoFilter ref=\"A1:C2501\"", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A2501\"><v>2500</v></c>", sheet, StringComparison.Ordinal);
    }

    [Fact]
    public void Written_file_is_read_back_by_the_import_reader_with_new_cols_filter_and_styles()
    {
        var hired = new DateTime(2024, 3, 4, 5, 6, 7);
        using var ms = new MemoryStream();
        using (var writer = TransferWriters.Create(Xlsx("EMP"), ms))
        {
            writer.Begin(Cols);
            writer.WriteRow(Row(1m, "가나다", hired));
            writer.WriteRow(Row(2m, null, null));
            writer.WriteRow(Row(1234567890123456m, new string('x', 40000), hired));
            writer.Finish();
        }

        ms.Position = 0;
        var rows = TransferFileReader.ReadRows(TransferFormat.Xlsx, ms).ToList();
        Assert.Equal(4, rows.Count); // 머리글 + 3행
        Assert.Equal(["ID", "이름", "HIRED"], rows[0].Cast<string>());
        Assert.Equal("1", rows[1][0]);
        Assert.Equal("가나다", rows[1][1]);
        Assert.Equal(hired, rows[1][2]);
        Assert.True(rows[2].Length < 2 || rows[2][1] is null); // 빈 셀은 리더가 뒤쪽을 생략할 수 있다
        Assert.Equal("1234567890123456", rows[3][0]); // 15자리 넘는 숫자는 글자 그대로
        Assert.Equal(32767, ((string)rows[3][1]!).Length);
        Assert.EndsWith("…", (string)rows[3][1]!, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_result_still_makes_a_valid_sheet_with_header_only()
    {
        var (parts, _) = Write(Xlsx(), Cols, []);
        var sheet = parts["xl/worksheets/sheet1.xml"];
        Assert.Contains("<autoFilter ref=\"A1:C1\"", sheet, StringComparison.Ordinal);
        Assert.Contains("<c r=\"A1\"", sheet, StringComparison.Ordinal);
    }
}
