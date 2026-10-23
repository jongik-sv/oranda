using System.IO.Compression;
using System.Xml.Linq;
using Oranda.Core.Transfer;
using Oranda.Oracle.Transfer;

namespace Oranda.Oracle.Tests.Transfer;

// 소유: TSK-13-06. 테이블 정의서 xlsx(목차 + 표마다 시트)·Markdown·HTML 내용 단위 시험.
public class TableSpecWriterTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly TableSpecWriter Writer = new();

    private static readonly TableSpecDocument Doc = new("SCOTT 테이블 정의서", new DateTimeOffset(2026, 10, 20, 9, 30, 0, TimeSpan.Zero),
    [
        new TableSpecTable("SCOTT", "EMP", "사원 | 급여",
        [
            new TableSpecColumn(1, "EMPNO", "NUMBER(4)", false, true, null, null, "사번"),
            new TableSpecColumn(2, "ENAME", "VARCHAR2(10)", true, false, null, "'NONE'", null),
            new TableSpecColumn(3, "DEPTNO", "NUMBER(2)", true, false, "DEPT.DEPTNO", null, "부서 <번호>"),
        ],
        [new TableSpecIndex("PK_EMP", true, ["EMPNO"]), new TableSpecIndex("IDX_EMP_DEPT", false, ["DEPTNO", "ENAME"])]),
        new TableSpecTable("SCOTT", "A/B:C?", null, [new TableSpecColumn(1, "ID", "NUMBER", true, false, null, null, null)], []),
    ]);

    private static async Task<byte[]> WriteAsync(TableSpecFormat format)
    {
        using var ms = new MemoryStream();
        await Writer.WriteAsync(Doc, format, ms, default);
        return ms.ToArray();
    }

    private static XDocument Part(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        return XDocument.Load(s);
    }

    private static string[][] Rows(XDocument sheet) =>
        [.. sheet.Descendants(Ns + "row").Select(r => r.Elements(Ns + "c").Select(c => string.Concat(c.Descendants(Ns + "t").Select(t => t.Value))).ToArray())];

    [Fact]
    public async Task Xlsx_has_a_toc_sheet_and_one_sheet_per_table_with_safe_unique_names()
    {
        using var zip = new ZipArchive(new MemoryStream(await WriteAsync(TableSpecFormat.Xlsx)));

        var workbook = Part(zip, "xl/workbook.xml");
        var names = workbook.Descendants(Ns + "sheet").Select(s => (string?)s.Attribute("name")).ToArray();
        Assert.Equal(["목차", "EMP", "A_B_C_"], names);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        Assert.Null(zip.GetEntry("xl/worksheets/sheet4.xml"));
    }

    [Fact]
    public async Task Xlsx_toc_lists_every_table_and_links_to_its_sheet()
    {
        using var zip = new ZipArchive(new MemoryStream(await WriteAsync(TableSpecFormat.Xlsx)));

        var toc = Part(zip, "xl/worksheets/sheet1.xml");
        var rows = Rows(toc);
        Assert.Equal("SCOTT 테이블 정의서", rows[0][0]);
        Assert.Equal(["순번", "표", "주석"], rows[3]);
        Assert.Equal(["1", "SCOTT.EMP", "사원 | 급여"], rows[4]);
        Assert.Equal(["2", "SCOTT.A/B:C?", string.Empty], rows[5]);
        var links = toc.Descendants(Ns + "hyperlink").Select(h => ((string?)h.Attribute("ref"), (string?)h.Attribute("location"))).ToArray();
        Assert.Equal([("B5", "'EMP'!A1"), ("B6", "'A_B_C_'!A1")], links);
    }

    [Fact]
    public async Task Xlsx_table_sheet_has_columns_then_indexes()
    {
        using var zip = new ZipArchive(new MemoryStream(await WriteAsync(TableSpecFormat.Xlsx)));

        var rows = Rows(Part(zip, "xl/worksheets/sheet2.xml"));
        Assert.Equal("SCOTT.EMP", rows[0][0]);
        Assert.Equal(["순번", "열 이름", "타입", "NULL", "PK", "FK", "기본값", "주석"], rows[3]);
        Assert.Equal(["1", "EMPNO", "NUMBER(4)", "N", "Y", string.Empty, string.Empty, "사번"], rows[4]);
        Assert.Equal(["2", "ENAME", "VARCHAR2(10)", "Y", string.Empty, string.Empty, "'NONE'", string.Empty], rows[5]);
        Assert.Equal("DEPT.DEPTNO", rows[6][5]);
        Assert.Equal(["인덱스", "고유", "열"], rows[8]);
        Assert.Equal(["PK_EMP", "Y", "EMPNO"], rows[9]);
        Assert.Equal(["IDX_EMP_DEPT", string.Empty, "DEPTNO, ENAME"], rows[10]);
    }

    [Fact]
    public void Sheet_names_are_at_most_31_chars_and_unique_ignoring_case()
    {
        var longName = new string('X', 40);
        var doc = new TableSpecDocument("t", DateTimeOffset.UnixEpoch,
        [
            new("S", longName, null, [], []),
            new("S", longName.ToLowerInvariant(), null, [], []),
            new("S", "목차", null, [], []),
        ]);

        var names = TableSpecXlsx.SheetNames(doc);

        Assert.All(names, n => Assert.True(n.Length <= 31));
        Assert.Equal(names.Length, names.Select(n => n.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("목차", names[0]);
    }

    [Fact]
    public async Task Markdown_has_toc_anchors_tables_and_escapes_pipes()
    {
        var md = System.Text.Encoding.UTF8.GetString(await WriteAsync(TableSpecFormat.Markdown));

        Assert.StartsWith("# SCOTT 테이블 정의서\n", md, StringComparison.Ordinal);
        Assert.Contains("1. [SCOTT.EMP](#scottemp) — 사원 \\| 급여\n", md, StringComparison.Ordinal);
        Assert.Contains("## SCOTT.EMP\n", md, StringComparison.Ordinal);
        Assert.Contains("| 순번 | 열 이름 | 타입 | NULL | PK | FK | 기본값 | 주석 |\n| --- | --- | --- | --- | --- | --- | --- | --- |\n", md, StringComparison.Ordinal);
        Assert.Contains("| 1 | EMPNO | NUMBER(4) | N | Y |  |  | 사번 |\n", md, StringComparison.Ordinal);
        Assert.Contains("| 3 | DEPTNO | NUMBER(2) | Y |  | DEPT.DEPTNO |  | 부서 &lt;번호&gt; |\n", md, StringComparison.Ordinal);
        Assert.Contains("| IDX\\_EMP\\_DEPT |  | DEPTNO, ENAME |\n", md, StringComparison.Ordinal);
        Assert.DoesNotContain("**인덱스**\n\n| 인덱스 | 고유 | 열 |\n| --- | --- | --- |\n\n", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_escapes_text_and_links_the_toc()
    {
        var html = System.Text.Encoding.UTF8.GetString(await WriteAsync(TableSpecFormat.Html));

        Assert.Contains("<meta charset=\"utf-8\">", html, StringComparison.Ordinal);
        Assert.Contains("<li><a href=\"#t1\">SCOTT.EMP</a> <span class=\"muted\">— 사원 | 급여</span></li>", html, StringComparison.Ordinal);
        Assert.Contains("<h2 id=\"t2\">SCOTT.A/B:C?</h2>", html, StringComparison.Ordinal);
        Assert.Contains("<td>부서 &lt;번호&gt;</td>", html, StringComparison.Ordinal);
        Assert.Contains("<td>PK_EMP</td><td>Y</td><td>EMPNO</td>", html, StringComparison.Ordinal);
        Assert.Equal(1, html.Split("<h3>인덱스</h3>").Length - 1); // 인덱스가 없는 표에는 인덱스 표를 만들지 않는다
    }

    [Fact]
    public void Sheet_names_avoid_the_reserved_history_name_and_edge_quotes()
    {
        var doc = new TableSpecDocument("t", DateTimeOffset.UnixEpoch,
        [
            new("S", "HISTORY", null, [], []),
            new("S", new string('A', 30) + "'B", null, [], []),
        ]);

        var names = TableSpecXlsx.SheetNames(doc);

        Assert.NotEqual("history", names[1], StringComparer.OrdinalIgnoreCase);
        Assert.False(names[2].EndsWith('\'') || names[2].StartsWith('\''));
    }

    [Fact]
    public async Task Markdown_escapes_inline_markup_characters()
    {
        var doc = new TableSpecDocument("t", DateTimeOffset.UnixEpoch,
            [new("S", "T", "a_b *c* `d` [e] & List<String>", [], [])]);
        using var ms = new MemoryStream();
        await Writer.WriteAsync(doc, TableSpecFormat.Markdown, ms, default);

        Assert.Contains("a\\_b \\*c\\* \\`d\\` \\[e\\] &amp; List&lt;String&gt;", System.Text.Encoding.UTF8.GetString(ms.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TableSpecFormat.Xlsx, ".xlsx")]
    [InlineData(TableSpecFormat.Markdown, ".md")]
    [InlineData(TableSpecFormat.Html, ".html")]
    public void Extensions_follow_the_format(TableSpecFormat format, string extension) => Assert.Equal(extension, Writer.Extension(format));
}
