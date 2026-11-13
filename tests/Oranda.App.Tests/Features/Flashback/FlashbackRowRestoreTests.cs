using Oranda.App.Features.Flashback;
using Oranda.App.Features.PointCompare;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Language.Editing;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.Flashback;

// 소유: fb-w1 FB-02. 「이 행 상태로 되돌리기」: 플래시백 결과 행을 목표 값으로 화면 3 을 연다(시점을 읽지 않음, 미리 보기·반영은 화면 3 그대로).
public class FlashbackRowRestoreTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static readonly RollbackTable Table = new(Emp,
        [new RollbackColumn("EMPNO", "NUMBER", RollbackValueKind.Number), new RollbackColumn("SAL", "NUMBER", RollbackValueKind.Number)], ["EMPNO"]);

    private static PointCompareViewModel Vm(NowDb db, decimal empno, decimal sal) =>
        new(db, "dev-example", Emp, [new PointOption("고른 바꾼 행(U)", PointOptionKind.ResultRows)], [[empno]])
        {
            KeyColumns = ["EMPNO"],
            Targets = [new CompareTargetRow(new Dictionary<string, object?> { ["EMPNO"] = empno, ["SAL"] = sal })],
        };

    [Fact]
    public async Task Existing_row_is_updated_to_the_chosen_values_without_reading_a_past_point()
    {
        var db = new NowDb();
        var vm = Vm(db, 7369m, 800m);

        await vm.LoadAsync();

        Assert.Equal(string.Empty, vm.ErrorText);
        var update = Assert.Single(vm.Script!.Statements);
        Assert.Equal("UPDATE SCOTT.EMP SET SAL = 800 WHERE EMPNO = 7369 AND SAL = 900", update.Sql);
        Assert.StartsWith("-- 목표 값: 고른 바꾼 행(U)", vm.DmlText, StringComparison.Ordinal);
        Assert.StartsWith("이 행 상태로 되돌리기 · ", vm.Title, StringComparison.Ordinal);
        Assert.Equal("범위: " + PointCompareLoader.RangeTargets, vm.RangeText);
        Assert.Single(db.Queries); // 지금 값만 읽는다(AS OF·VERSIONS·COUNT 없음)
        Assert.DoesNotContain(" AS OF ", db.Queries[0], StringComparison.Ordinal);

        var applied = await vm.ApplyAsync();
        Assert.Equal(1, applied!.Applied);
        Assert.Equal(update.Sql, Assert.Single(applied.Executed).Sql); // 반대 문장 기록(RecordDerived)에 넘어간다
    }

    [Fact]
    public async Task Deleted_row_is_inserted_back_and_same_values_need_nothing()
    {
        var gone = Vm(new NowDb(), 7499m, 1600m); // 지금 없는 행(D 의 마지막 모습)
        await gone.LoadAsync();
        Assert.Equal("INSERT INTO SCOTT.EMP (EMPNO, SAL) VALUES (7499, 1600)", Assert.Single(gone.Script!.Statements).Sql);
        Assert.Contains("되살릴 행 1 · INSERT", gone.CountsText, StringComparison.Ordinal);

        var same = Vm(new NowDb(), 7369m, 900m);
        await same.LoadAsync();
        Assert.Empty(same.Script!.Statements);
        Assert.False(same.CanApply);
        Assert.Equal("바꿀 것 없음: 지금 값이 고른 행과 같습니다.", same.StatusText);
    }

    [Theory]
    [InlineData("SELECT * FROM emp VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE", "D", "고른 지운 행(D)의 마지막 모습")]
    [InlineData("SELECT * FROM emp VERSIONS BETWEEN SCN MINVALUE AND MAXVALUE", "U", "고른 바꾼 행(U)")]
    [InlineData("SELECT * FROM emp AS OF SCN 123 e", null, "고른 행 · AS OF SCN 123")]
    public void Label_names_the_chosen_version(string sql, string? op, string label)
        => Assert.Equal(label, FlashbackRowRestore.Label(FlashbackResultAnalyzer.Analyze(sql), op));

    [Theory]
    [InlineData(FlashbackResultAnalyzer.NotFlashback)]
    [InlineData(FlashbackResultAnalyzer.MultipleTables)]
    [InlineData(FlashbackResultAnalyzer.Remote)]
    [InlineData(FlashbackResultAnalyzer.Period)]
    [InlineData("바꿀 것 없음: 지금 값이 고른 행과 같습니다.")]
    public void Condition_notes_have_english(string korean)
        => Assert.True(Oranda.App.Features.Localization.LiteralTranslator.Table.ContainsKey(korean), "literals-en 없음: " + korean);

    /// <summary>지금: 7369 = 900 만 있다.</summary>
    private sealed class NowDb : IPointCompareDb
    {
        public List<string> Queries { get; } = [];

        public Dbms Dbms => Dbms.Oracle;

        public IRollbackDialect Dialect { get; } = new FakeRollbackDialect();

        public Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<RollbackTable?>(Table);

        public Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct)
        {
            Queries.Add(query.Sql);
            IReadOnlyList<object?[]> rows = query.Sql.Contains("7369", StringComparison.Ordinal) ? [["7369", "900", "R1"]] : [];
            return Task.FromResult(rows);
        }

        public Task<long?> CurrentPointAsync(CancellationToken ct) => Task.FromResult<long?>(1000);

        public Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct) => throw new InvalidOperationException("시점을 읽지 않는다");

        public Task<long?> UndoRetentionAsync(CancellationToken ct) => throw new InvalidOperationException("undo 를 묻지 않는다");

        public Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct) => throw new InvalidOperationException("DDL 시각을 묻지 않는다");

        public Task<long> ExecuteAsync(string sql, CancellationToken ct) => Task.FromResult(1L);
    }
}
