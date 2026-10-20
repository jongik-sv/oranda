using Oranda.Core.Metadata;
using Oranda.Oracle.Schema;

namespace Oranda.Oracle.Tests.Schema;

// 소유: TSK-13-01. 열 순서에 민감한 소스 줄 후보 판정(한 줄 단위).
public class ColumnReorderSourceScanTests
{
    private static readonly DbObjectRef Table = new("SCOTT", "EMP", "TABLE");

    [Theory]
    [InlineData("  SELECT * FROM emp WHERE id = 1;", ColumnReorderRisk.SelectStar)]
    [InlineData("select e.* from scott.emp e", ColumnReorderRisk.SelectStar)]
    [InlineData("SELECT DISTINCT * FROM \"EMP\"", ColumnReorderRisk.SelectStar)]
    [InlineData("  SELECT *", ColumnReorderRisk.SelectStar)] // FROM 이 다음 줄이면 알 수 없어 후보로 둔다
    [InlineData("INSERT INTO emp VALUES (1, 'a');", ColumnReorderRisk.InsertWithoutColumns)]
    [InlineData("insert into scott.emp values(1)", ColumnReorderRisk.InsertWithoutColumns)]
    [InlineData("INSERT INTO emp SELECT * FROM emp_bak;", ColumnReorderRisk.InsertWithoutColumns)]
    public void Classify_flags_order_sensitive_lines(string line, ColumnReorderRisk expected)
        => Assert.Equal(expected, ColumnReorderSourceScan.Classify(line, Table));

    [Theory]
    [InlineData("SELECT COUNT(*) INTO n FROM emp;")]
    [InlineData("SELECT * FROM dept;")] // 다른 표
    [InlineData("INSERT INTO emp (id, name) VALUES (1, 'a');")]
    [InlineData("INSERT INTO hr.emp VALUES (1)")] // 다른 소유자
    [InlineData("INSERT INTO emp_log VALUES (1)")]
    [InlineData("-- SELECT * FROM emp")]
    [InlineData("x := 1; /* SELECT * FROM emp */")]
    [InlineData("")]
    public void Classify_ignores_safe_or_unrelated_lines(string line)
        => Assert.Null(ColumnReorderSourceScan.Classify(line, Table));

    [Fact]
    public void Filter_keeps_only_candidates_with_trimmed_text()
    {
        var pkg = new DbObjectRef("SCOTT", "PKG", "PACKAGE BODY");
        var hits = ColumnReorderSourceScan.Filter(
        [
            new SourceSearchHit(pkg, 10, "    SELECT * FROM emp;"),
            new SourceSearchHit(pkg, 11, "    SELECT id FROM emp;"),
            new SourceSearchHit(pkg, 20, "INSERT INTO emp VALUES (1);"),
        ], Table);

        Assert.Equal([10, 20], hits.Select(h => h.Line));
        Assert.Equal(ColumnReorderRisk.SelectStar, hits[0].Risk);
        Assert.Equal("SELECT * FROM emp;", hits[0].Text);
    }
}
