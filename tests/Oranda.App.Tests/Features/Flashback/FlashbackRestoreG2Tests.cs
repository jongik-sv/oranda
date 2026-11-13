using Oranda.App.Features.PointCompare;
using Oranda.App.Features.Rollback;
using Oranda.App.Shell.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.Flashback;

// 소유: RB-33. 그리드 「이전 값으로 되돌리기」가 화면 3 을 고른 행의 키 범위로 연다(설계 8.5 G2).
public class FlashbackRestoreG2Tests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static readonly RollbackTable Table = new(Emp,
        [new RollbackColumn("EMPNO", "NUMBER", RollbackValueKind.Number), new RollbackColumn("SAL", "NUMBER", RollbackValueKind.Number)], ["EMPNO"]);

    [Fact]
    public async Task Selected_keys_give_revert_only_and_count_rows_missing_then()
    {
        var db = new KeyDb();
        var minutes = new PointOption("10분 전", PointOptionKind.MinutesAgo) { Minutes = 10 };
        var group = new PointOption("커밋 묶음 #7 실행 직전", PointOptionKind.Group) { BasePoint = 100, CommitPoint = 200, GroupId = 7, Tables = [Emp] };
        var vm = new PointCompareViewModel(db, "dev-example", Emp, [minutes, group], [[7369m], [7499m], [9999m]]) { KeyColumns = ["EMPNO"] };

        await vm.LoadAsync();
        Assert.True(vm.UsesKeys);
        Assert.False(vm.CanWidenToGroup); // 「N분 전」에는 묶음이 없다
        var update = Assert.Single(vm.Script!.Statements);
        Assert.Equal("UPDATE SCOTT.EMP SET SAL = 800 WHERE EMPNO = 7369 AND SAL = 900", update.Sql);
        Assert.Equal((1, 1), (vm.MissingThen, vm.Unchanged)); // 9999 는 그때 없던 행, 7499 는 같은 값
        Assert.Contains("건너뜀: 그때 없던 행 1, 같은 값 1", vm.CountsText, StringComparison.Ordinal);
        Assert.Contains("WHERE t.EMPNO IN (7369, 7499, 9999)", db.Queries[^1], StringComparison.Ordinal);

        vm.SelectedPoint = group;
        Assert.True(vm.CanWidenToGroup);
        await vm.WidenToGroupAsync();
        Assert.False(vm.UsesKeys);
        Assert.Contains(vm.Script!.Statements, s => s.Sql.StartsWith("DELETE FROM SCOTT.EMP WHERE EMPNO = 9999", StringComparison.Ordinal)); // 묶음 전체 범위면 지움도 나온다
    }

    [Fact]
    public void Grid_key_values_become_fixed_format_text_for_literals()
    {
        var t = new RollbackTable(Emp,
            [new RollbackColumn("ID", "NUMBER", RollbackValueKind.Number), new RollbackColumn("EFF", "DATE", RollbackValueKind.Date)], ["ID", "EFF"]);
        var key = PointCompareLoader.KeyTexts(t, [3.0d, new DateTime(2026, 11, 13, 9, 5, 7)]);
        Assert.Equal([3m, "2026-11-13 09:05:07"], key);
        var lit = new FakeRollbackDialect().Literal(t.Columns[1], key[1], out _);
        Assert.Equal("TO_DATE('2026-11-13 09:05:07', 'SYYYY-MM-DD HH24:MI:SS')", lit);
    }

    [Fact]
    public async Task Launcher_explains_when_the_connection_is_not_open()
    {
        var why = await PointCompareLauncher.OpenAsync(new ServiceContainer(), new PointCompareArgs(Guid.NewGuid(), null, Emp, ["EMPNO"], [[1m]], 10), null, default);
        Assert.Contains("접속이 열려 있지 않습니다", why, StringComparison.Ordinal);
    }

    /// <summary>기준(SCN 100): 7369 = 800, 7499 = 1600. 커밋 직후·지금: 7369 = 900, 7499 = 1600, 9999 = 1.</summary>
    private sealed class KeyDb : IPointCompareDb
    {
        public List<string> Queries { get; } = [];

        public Dbms Dbms => Dbms.Oracle;

        public IRollbackDialect Dialect { get; } = new FakeRollbackDialect();

        public Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<RollbackTable?>(Table);

        public Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct)
        {
            Queries.Add(query.Sql);
            IReadOnlyList<object?[]> rows = query.Sql switch
            {
                var s when s.Contains("COUNT(*)", StringComparison.Ordinal) => [[3m]],
                var s when s.Contains("VERSIONS", StringComparison.Ordinal) => [],
                var s when s.Contains(" AS OF SCN 100 ", StringComparison.Ordinal) => [["7369", "800", "R1"], ["7499", "1600", "R2"]],
                _ => [["7369", "900", "R1"], ["7499", "1600", "R2"], ["9999", "1", "R3"]], // 커밋 직후(SCN 200)와 지금
            };
            return Task.FromResult(rows);
        }

        public Task<long?> CurrentPointAsync(CancellationToken ct) => Task.FromResult<long?>(1000);

        public Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct) => Task.FromResult<long?>(100);

        public Task<long?> UndoRetentionAsync(CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task<long> ExecuteAsync(string sql, CancellationToken ct) => Task.FromResult(1L);
    }
}
