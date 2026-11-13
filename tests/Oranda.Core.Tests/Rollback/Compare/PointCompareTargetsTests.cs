using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Testing.Rollback;

namespace Oranda.Core.Tests.Rollback.Compare;

// 소유: fb-w1 FB-02. 목표 값 = 결과 행(플래시백 결과)일 때의 분류: 지금 있으면 UPDATE, 없으면 INSERT, 같으면 할 일 없음.
public class PointCompareTargetsTests
{
    private static readonly RollbackTable Emp = new(
        new DbObjectRef("SCOTT", "EMP", "TABLE"),
        [
            new RollbackColumn("EMPNO", "NUMBER", RollbackValueKind.Number),
            new RollbackColumn("ENAME", "VARCHAR2", RollbackValueKind.Text),
            new RollbackColumn("SAL", "NUMBER", RollbackValueKind.Number),
            new RollbackColumn("HIRED", "DATE", RollbackValueKind.Date),
        ],
        ["EMPNO"]);

    private static CompareTargetRow T(params (string Name, object? Value)[] values) => new(values.ToDictionary(v => v.Name, v => v.Value));

    /// <summary>지금 행은 읽는 식(고정 형식 글)으로 읽은 모양이다: 숫자·날짜도 글(SYYYY 는 앞에 공백).</summary>
    private static CompareSnapshotRow Now(string empno, string ename, string sal, string hired) => new([empno, ename, sal, hired]);

    private static readonly DateTime Hired = new(1980, 12, 17);

    [Fact]
    public void Existing_row_with_other_values_becomes_an_update_of_the_changed_columns_only()
    {
        var plan = PointCompareTargets.Plan(Emp, [T(("EMPNO", 7369m), ("ENAME", "SMITH"), ("SAL", 800.0d), ("HIRED", Hired))],
            [Now("7369", "SMITH", "900", " 1980-12-17 00:00:00")], out var notes);

        var row = Assert.Single(plan.Rows);
        Assert.Equal(CompareKind.Revert, row.Kind);
        Assert.Equal([2], row.ChangedColumns); // 날짜 글의 앞 공백·숫자 모양 차이는 차이가 아니다
        Assert.Empty(notes);
        Assert.Empty(plan.Warnings); // 커밋 시점 안내는 결과 행 비교에 맞지 않는다

        var sql = Assert.Single(PointComparePlanner.BuildDml(plan, new FakeRollbackDialect(), quote: n => n).Statements).Sql;
        Assert.Equal("UPDATE SCOTT.EMP SET SAL = 800 WHERE EMPNO = 7369 AND SAL = 900", sql);
    }

    [Fact]
    public void Missing_row_becomes_an_insert_and_same_values_need_nothing()
    {
        var targets = new[]
        {
            T(("EMPNO", 7499m), ("ENAME", "ALLEN"), ("SAL", 1600m), ("HIRED", Hired)), // 지운 행(D)의 마지막 모습
            T(("EMPNO", 7369m), ("ENAME", "SMITH"), ("SAL", 800m), ("HIRED", Hired)),
        };
        var plan = PointCompareTargets.Plan(Emp, targets, [Now("7369", "SMITH", "800", "1980-12-17 00:00:00")], out _);

        Assert.Equal(CompareKind.Restore, plan.Rows.Single(r => r.Key[0] is 7499m).Kind);
        Assert.Equal(CompareKind.Same, plan.Rows.Single(r => r.Key[0] is 7369m).Kind);
        var sql = Assert.Single(PointComparePlanner.BuildDml(plan, new FakeRollbackDialect(), quote: n => n).Statements).Sql;
        Assert.Equal("INSERT INTO SCOTT.EMP (EMPNO, ENAME, SAL, HIRED) VALUES (7499, 'ALLEN', 1600, TO_DATE('1980-12-17 00:00:00', 'SYYYY-MM-DD HH24:MI:SS'))", sql);
    }

    [Fact]
    public void Columns_missing_from_the_result_keep_their_value_and_block_an_insert()
    {
        var plan = PointCompareTargets.Plan(Emp, [T(("EMPNO", 7369m), ("SAL", 800m)), T(("EMPNO", 7499m), ("SAL", 1600m))],
            [Now("7369", "SMITH", "900", "1980-12-17 00:00:00")], out var notes);

        var row = Assert.Single(plan.Rows); // 7499 는 INSERT 해야 하지만 ENAME·HIRED 가 없어 만들지 않는다
        Assert.Equal([2], row.ChangedColumns);
        Assert.Contains("ENAME, HIRED", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Fact]
    public void Long_numbers_stay_text_and_local_time_zone_values_are_not_reverted()
    {
        const string third = ".3333333333333333333333333333333333333333";
        Assert.Equal(third, PointCompareTargets.Normalize(RollbackValueKind.Number, third)); // decimal 은 28자리에서 반올림한다

        var table = new RollbackTable(new DbObjectRef("SCOTT", "T", "TABLE"),
            [new RollbackColumn("ID", "NUMBER", RollbackValueKind.Number), new RollbackColumn("AT", "TIMESTAMP WITH LOCAL TIME ZONE", RollbackValueKind.TimestampLtz)], ["ID"]);
        var plan = PointCompareTargets.Plan(table, [T(("ID", 1m), ("AT", new DateTime(2026, 11, 13)))], [new CompareSnapshotRow(["1", "2026-11-12 00:00:00.000000000"])], out var notes);

        Assert.Equal(CompareKind.Same, Assert.Single(plan.Rows).Kind); // 세션 시간대를 몰라 그리드 값으로 쓰지 않는다
        Assert.Contains("TIMESTAMP WITH LOCAL TIME ZONE", Assert.Single(notes), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RollbackValueKind.Number, ".5", 0.5)]
    [InlineData(RollbackValueKind.Timestamp, " 2026-11-13 09:05:07.123456700", "2026-11-13 09:05:07.123456700")]
    [InlineData(RollbackValueKind.IntervalDaySecond, "+000000001 02:03:04.500000", "+1 02:03:04.5000000")]
    [InlineData(RollbackValueKind.IntervalYearMonth, "+000000002-03", "+2-03")]
    [InlineData(RollbackValueKind.Raw, "0aff", "0AFF")]
    public void Read_text_and_grid_values_meet_in_one_shape(RollbackValueKind kind, object read, object expected)
    {
        var want = expected is double d ? (decimal)d : expected;
        Assert.Equal(want, PointCompareTargets.Normalize(kind, read));
    }

    [Fact]
    public void Grid_values_take_the_same_shape_as_read_text()
    {
        Assert.Equal("2026-11-13 09:05:07.123456700", PointCompareTargets.Normalize(RollbackValueKind.Timestamp, new DateTime(2026, 11, 13, 9, 5, 7).AddTicks(1234567)));
        Assert.Equal("+1 02:03:04.5000000", PointCompareTargets.Normalize(RollbackValueKind.IntervalDaySecond, new TimeSpan(1, 2, 3, 4, 500)));
        Assert.Equal("0AFF", PointCompareTargets.Normalize(RollbackValueKind.Raw, new byte[] { 0x0a, 0xff }));
        Assert.Null(PointCompareTargets.Normalize(RollbackValueKind.Text, DBNull.Value));
    }
}
