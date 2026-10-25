using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Language.Editing;

namespace Oranda.Language.Tests.Editing;

// 소유: TSK-13-05. 플래시백 질의 생성·차이 칸 계산·오류 안내.
public class FlashbackTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    [Fact]
    public void AsOf_single_pk_binds_keys_and_minutes()
    {
        var q = FlashbackQueryBuilder.BuildAsOf(Emp, ["EMPNO", "ENAME"], ["EMPNO"], [[7369m], [7499m]], 10);
        Assert.Equal("SELECT EMPNO, ENAME FROM SCOTT.EMP AS OF TIMESTAMP (SYSTIMESTAMP - NUMTODSINTERVAL(:mins, 'MINUTE')) WHERE EMPNO IN (:k1_1, :k2_1)",
            q.Statement.Text);
        Assert.Equal(10m, q.Binds["mins"].Value);
        Assert.Equal(7499m, q.Binds["k2_1"].Value);
        Assert.Equal(["mins", "k1_1", "k2_1"], q.Statement.BindNames);
    }

    [Fact]
    public void AsOf_composite_pk_uses_tuple_in()
    {
        var q = FlashbackQueryBuilder.BuildAsOf(Emp, ["A", "B", "C"], ["A", "B"], [["x", 1m], ["y", 2m]], 120);
        Assert.Contains("WHERE (A, B) IN ((:k1_1, :k1_2), (:k2_1, :k2_2))", q.Statement.Text);
        Assert.Equal(120m, q.Binds["mins"].Value); // 100분 넘는 값도 바인드라 INTERVAL 정밀도 문제가 없다
    }

    [Fact]
    public void AsOf_rowid_key_is_not_quoted()
    {
        var q = FlashbackQueryBuilder.BuildAsOf(Emp, ["ROWID", "ENAME"], ["ROWID"], [["AAAB"]], 5);
        Assert.StartsWith("SELECT ROWID, ENAME FROM", q.Statement.Text, StringComparison.Ordinal);
        Assert.EndsWith("WHERE ROWID IN (:k1_1)", q.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AsOf_quotes_lowercase_and_reserved_names()
    {
        var q = FlashbackQueryBuilder.BuildAsOf(new DbObjectRef("my user", "t$x", "TABLE"), ["Id", "ORDER"], ["Id"], [[1m]], 1);
        Assert.Contains("\"my user\".\"t$x\"", q.Statement.Text, StringComparison.Ordinal);
        Assert.Contains("SELECT \"Id\", \"ORDER\"", q.Statement.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AsOf_chunks_large_key_lists_with_or()
    {
        var rows = Enumerable.Range(1, 1000).Select(i => (IReadOnlyList<object?>)[(decimal)i]).ToList();
        var q = FlashbackQueryBuilder.BuildAsOf(Emp, ["ID"], ["ID"], rows, 10);
        Assert.Contains(") OR ID IN (", q.Statement.Text, StringComparison.Ordinal);
        Assert.Equal(1001, q.Binds.Count);
    }

    [Fact]
    public void AsOf_rejects_null_key_and_bad_minutes()
    {
        Assert.Throws<ArgumentException>(() => FlashbackQueryBuilder.BuildAsOf(Emp, ["ID"], ["ID"], [[null]], 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => FlashbackQueryBuilder.BuildAsOf(Emp, ["ID"], ["ID"], [[1m]], 0));
    }

    [Fact]
    public void Restore_builds_update_by_key_with_binds()
    {
        var dml = FlashbackQueryBuilder.BuildRestore(Emp, 3, new Dictionary<string, object?> { ["EMPNO"] = 7369m },
            new Dictionary<string, object?> { ["SAL"] = 800m });
        Assert.NotNull(dml);
        Assert.Equal("UPDATE SCOTT.EMP SET SAL = :v1 WHERE EMPNO = :k1", dml.Statement.Text);
        Assert.Equal(800m, dml.Binds["v1"].Value);
    }

    [Fact]
    public void Restore_with_rowid_key_uses_rid_bind()
    {
        var dml = FlashbackQueryBuilder.BuildRestore(Emp, 0, new Dictionary<string, object?> { ["ROWID"] = "AAAB" },
            new Dictionary<string, object?> { ["ENAME"] = "SMITH" });
        Assert.Equal("UPDATE SCOTT.EMP SET ENAME = :v1 WHERE ROWID = :rid", dml!.Statement.Text);
    }

    [Fact]
    public void Restore_without_values_is_null()
    {
        Assert.Null(FlashbackQueryBuilder.BuildRestore(Emp, 0, new Dictionary<string, object?> { ["EMPNO"] = 1m }, new Dictionary<string, object?>()));
    }

    [Fact]
    public void Diff_finds_changed_columns_and_treats_numbers_by_value()
    {
        var changed = FlashbackDiff.ChangedColumns([7369m, "SMITH", 900m, null], [7369, "SMITH", 800m, DBNull.Value]);
        Assert.Equal([2], changed);
        Assert.True(FlashbackDiff.SameValue(1m, 1.0));
        Assert.False(FlashbackDiff.SameValue(null, ""));
    }

    [Fact]
    public void KeyText_pairs_rows_regardless_of_number_type()
    {
        Assert.Equal(FlashbackDiff.KeyText([7369m, "A"]), FlashbackDiff.KeyText([7369, "A"]));
        Assert.NotEqual(FlashbackDiff.KeyText([1m, "A"]), FlashbackDiff.KeyText([2m, "A"]));
    }

    [Theory]
    [InlineData("ORA-01555: snapshot too old", "undo_retention")]
    [InlineData("ORA-08180: no snapshot found based on specified time", "SCN")]
    [InlineData("ORA-01466: unable to read data - table definition has changed", "표 정의")]
    [InlineData("ORA-01031: insufficient privileges", "FLASHBACK 권한")]
    [InlineData("ORA-41900: FLASHBACK 권한이 \"MDM_USER\".\"TB_CODE\"에 누락되었습니다", "GRANT FLASHBACK ON <소유자.표> TO <접속 사용자>")]
    public void Advisor_explains_each_error(string message, string expected)
    {
        Assert.Contains(expected, FlashbackErrorAdvisor.Explain(message), StringComparison.Ordinal);
    }

    [Fact]
    public void Advisor_ignores_unrelated_errors()
    {
        Assert.Null(FlashbackErrorAdvisor.Explain("ORA-01722: invalid number"));
        Assert.Null(FlashbackErrorAdvisor.Explain(null));
    }

    [Fact]
    public void KeyText_distinguishes_raw_and_subsecond_timestamps()
    {
        Assert.NotEqual(FlashbackDiff.KeyText([new byte[] { 1, 2 }]), FlashbackDiff.KeyText([new byte[] { 1, 3 }]));
        var t = new DateTime(2026, 10, 20, 1, 2, 3, DateTimeKind.Unspecified);
        Assert.False(FlashbackDiff.SameValue(t, t.AddMilliseconds(5)));
        Assert.False(FlashbackDiff.SameValue(double.NaN, 1.0));
    }

    [Fact]
    public void Advisor_finds_known_code_in_wrapped_error()
    {
        Assert.Contains("undo_retention", FlashbackErrorAdvisor.Explain("ORA-12801: parallel query server failed\nORA-01555: snapshot too old"), StringComparison.Ordinal);
        Assert.Contains("ORA-30052", FlashbackErrorAdvisor.Explain("ORA-30052: invalid lower limit snapshot expression"), StringComparison.Ordinal);
    }

    [Fact]
    public void Definition_changed_is_recognised_by_ora_code()
    {
        Assert.True(FlashbackErrorAdvisor.IsDefinitionChanged("ORA-01466: unable to read data"));
        Assert.False(FlashbackErrorAdvisor.IsDefinitionChanged("ORA-01555"));
    }
}
