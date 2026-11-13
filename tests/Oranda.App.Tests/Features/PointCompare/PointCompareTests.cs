using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.PointCompare;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Core.Rollback.Compare;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.PointCompare;

// 소유: RB-32. 화면 3(설계 8장): 불러오기 범위·오류 안내, 표·충돌 체크·만들어진 DML, 반영(커밋 안 함).
public class PointCompareTests
{
    private static readonly DbObjectRef Products = new("SAMPLE", "PRODUCTS", "TABLE");

    private static readonly RollbackTable Table = new(Products,
        [
            new RollbackColumn("PRODUCT_ID", "NUMBER", RollbackValueKind.Number),
            new RollbackColumn("PRODUCT_NAME", "VARCHAR2", RollbackValueKind.Text),
            new RollbackColumn("UNIT_PRICE", "NUMBER", RollbackValueKind.Number),
        ],
        ["PRODUCT_ID"]);

    private static readonly PointOption Group321 = new("기준 SCN 100", PointOptionKind.Group)
    {
        BasePoint = 100, BaseAt = DateTimeOffset.Now.AddMinutes(-11), CommitPoint = 200, CommitAt = DateTimeOffset.Now.AddMinutes(-10),
        TransactionId = "10.27.4670", GroupId = 321, Tables = [Products],
    };

    [Fact]
    public async Task Loader_uses_versions_keys_and_classifies_three_points()
    {
        var db = SampleDb();
        var load = await PointCompareLoader.LoadAsync(db, new CompareRequest(Products, 100) { CommitPoint = 200, CommitAt = DateTimeOffset.Now.AddMinutes(-11), TransactionId = "10.27.4670" }, DateTimeOffset.Now, default);

        Assert.Null(load.Error);
        Assert.Equal(PointCompareLoader.RangeVersions, load.Range);
        Assert.Contains(db.Queries, q => q.Contains("HEXTORAW(:xid)", StringComparison.Ordinal));
        var plan = load.Plan!;
        Assert.Equal((1, 1, 1, 1), (plan.Count(CompareKind.Revert), plan.Count(CompareKind.Conflict), plan.Count(CompareKind.Restore), plan.Count(CompareKind.Remove)));
        Assert.Contains(load.Notes, n => n.Contains("undo_retention 900초", StringComparison.Ordinal) && n.Contains("ORA-01555", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Loader_refuses_future_point_and_explains_tibero_snapshot_too_old()
    {
        var future = SampleDb();
        future.Current = 150;
        var f = await PointCompareLoader.LoadAsync(future, new CompareRequest(Products, 100) { CommitPoint = 200 }, DateTimeOffset.Now, default);
        Assert.Contains("보다 큰 시점", f.Error, StringComparison.Ordinal);

        var tibero = SampleDb(new FakeRollbackDialect { Dbms = Dbms.Tibero, PointName = "TSN" });
        tibero.FailAsOf = new DbError("TBR-21003", "snapshot too old") { Kind = DbErrorKind.SnapshotTooOld };
        var t = await PointCompareLoader.LoadAsync(tibero, new CompareRequest(Products, 100) { Keys = [["1001"]] }, DateTimeOffset.Now, default);
        Assert.True(t.SnapshotTooOld);
        Assert.Contains("그 시점 뒤 테이블 정의가 바뀌었습니다", t.Error, StringComparison.Ordinal);
    }

    [Fact]
    public Task Window_shows_rows_and_conflict_box_adds_conflict_dml_then_apply_counts_zero_rows() => HeadlessHost.Run(async () =>
    {
        var db = SampleDb();
        db.Affected = sql => sql.StartsWith("DELETE", StringComparison.Ordinal) ? 0 : 1;
        CompareApplyResult? applied = null;
        var vm = new PointCompareViewModel(db, "dev-example", Products, [Group321])
        {
            Applied = (r, _) =>
            {
                applied = r;
                return Task.CompletedTask;
            },
        };
        var window = new PointCompareWindow { DataContext = vm };
        window.Show();
        await vm.LoadAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("이 실행 전과 비교 · SAMPLE.PRODUCTS", window.Title);
        Assert.Contains(vm.Rows, r => r.Kind == "되돌림" && r.Column == "UNIT_PRICE" && r.Base == "25900" && r.Now == "28490");
        Assert.Contains(vm.Rows, r => r.IsConflict);
        Assert.DoesNotContain("-- 충돌", vm.DmlText, StringComparison.Ordinal);
        Assert.Equal(3, vm.Script!.Statements.Count);

        window.FindControl<CheckBox>("ConflictBox")!.IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("-- 충돌", vm.DmlText, StringComparison.Ordinal);
        Assert.Contains("충돌 1 · 포함", vm.CountsText, StringComparison.Ordinal);

        window.FindControl<Button>("ApplyButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => applied is not null);
        Assert.Equal(4, db.Executed.Count); // 되살림 → 되돌림·충돌 → 지움
        Assert.StartsWith("INSERT", db.Executed[0], StringComparison.Ordinal);
        Assert.StartsWith("DELETE", db.Executed[^1], StringComparison.Ordinal);
        Assert.Equal((3, 1), (applied!.Applied, applied.ZeroRows));
        Assert.Equal(3, applied.Executed.Count); // 반대 문장은 1행을 바꾼 문장만(RD-8)
        Assert.Contains("커밋 안 함", vm.StatusText, StringComparison.Ordinal);
        window.Close();
    });

    [Fact]
    public async Task Custom_point_accepts_number_and_rejects_bad_text()
    {
        var db = SampleDb();
        var custom = new PointOption("직접", PointOptionKind.Custom);
        var vm = new PointCompareViewModel(db, "dev-example", Products, [custom]) { CustomText = "어제" };
        await vm.LoadAsync();
        Assert.Contains("yyyy-MM-dd HH:mm:ss", vm.ErrorText, StringComparison.Ordinal);

        vm.CustomText = "100";
        await vm.LoadAsync();
        Assert.Equal(string.Empty, vm.ErrorText);
        Assert.Contains(PointComparePlanner.NoCommitPointWarning, vm.Notes, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Insert_and_update_statements_narrow_by_where_and_inserted_key()
    {
        var db = SampleDb(new FakeRollbackDialect { Dbms = Dbms.Tibero, PointName = "TSN" }); // VERSIONS 없음 → 2번 방법
        db.WhereKeys.Add(["1001"]);
        var analyzer = new Oranda.Language.Editing.DmlShapeAnalyzer();
        RollbackScriptStatement St(string text, params RollbackBind[] binds) => new(1, DateTimeOffset.Now, text, binds, [], [], false, null, 1);
        var scopes = new[]
        {
            PointCompareLoader.ScopeOf(St("UPDATE products SET unit_price = :p WHERE product_id = :id", new RollbackBind("p", BindDataType.Number, "1"), new RollbackBind("id", BindDataType.Number, "1001")), analyzer),
            PointCompareLoader.ScopeOf(St("INSERT INTO products (product_id, product_name, unit_price) VALUES (2001, 'N', 1)"), analyzer),
        };
        Assert.Equal(["id"], scopes[0].Binds.Keys); // 쓰지 않은 바인드는 넘기지 않는다

        var load = await PointCompareLoader.LoadAsync(db, new CompareRequest(Products, 100) { CommitPoint = 200, Statements = scopes }, DateTimeOffset.Now, default);
        Assert.Equal(PointCompareLoader.RangeWhere, load.Range);
        Assert.Contains(db.Queries, q => q.Contains("WHERE PRODUCT_ID = 2001", StringComparison.Ordinal)); // 넣은 행의 키 조건
        Assert.DoesNotContain(db.Queries, q => q.Contains("COUNT(*)", StringComparison.Ordinal));
    }

    [Fact]
    public Task Metadata_failure_shows_error_instead_of_throwing() => HeadlessHost.Run(async () =>
    {
        var db = SampleDb();
        db.TableFails = true;
        var vm = new PointCompareViewModel(db, "dev-example", Products, [Group321]);
        var window = new PointCompareWindow { DataContext = vm };
        window.Show();
        window.FindControl<Button>("CompareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await HeadlessHost.WaitUntil(() => vm.ErrorText.Length > 0);
        Assert.Contains("ORA-04043", vm.ErrorText, StringComparison.Ordinal);
        Assert.False(vm.CanApply);
        window.Close();
    });

    /// <summary>시안 화면 3 의 축소판: 1001 되돌림, 1007 충돌, 1040 되살림, 2001 지움.</summary>
    private static FakeCompareDb SampleDb(FakeRollbackDialect? dialect = null) => new(dialect ?? new FakeRollbackDialect())
    {
        Base = [Row(1001, "A", 25900), Row(1007, "B", 19000), Row(1040, "보리차", 4300)],
        Commit = [Row(1001, "A", 28490), Row(1007, "B", 20900), Row(2001, "N", 1)],
        NowRows = [Row(1001, "A", 28490), Row(1007, "B", 21500), Row(2001, "N", 1)],
        VersionKeys = [["1001"], ["1007"], ["1040"], ["2001"]],
    };

    private static object?[] Row(int id, string name, int price) => [id.ToString(System.Globalization.CultureInfo.InvariantCulture), name, price.ToString(System.Globalization.CultureInfo.InvariantCulture), "AAA" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    private sealed class FakeCompareDb(FakeRollbackDialect dialect) : IPointCompareDb
    {
        public List<object?[]> Base { get; init; } = [];
        public List<object?[]> Commit { get; init; } = [];
        public List<object?[]> NowRows { get; init; } = [];
        public List<object?[]> VersionKeys { get; init; } = [];
        public long Current { get; set; } = 1000;
        public DbError? FailAsOf { get; set; }
        public Func<string, long> Affected { get; set; } = _ => 1;
        public List<string> Queries { get; } = [];
        public List<string> Executed { get; } = [];

        public Dbms Dbms => dialect.Dbms;

        public IRollbackDialect Dialect => dialect;

        public bool TableFails { get; set; }

        public List<object?[]> WhereKeys { get; init; } = [];

        public Task<RollbackTable?> TableAsync(DbObjectRef table, CancellationToken ct)
            => TableFails ? throw new InvalidOperationException("ORA-04043: object does not exist") : Task.FromResult<RollbackTable?>(Table);

        public Task<IReadOnlyList<object?[]>> QueryAsync(CompareQuery query, CancellationToken ct)
        {
            var sql = query.Sql;
            Queries.Add(sql);
            if (FailAsOf is { } e && sql.Contains(" AS OF ", StringComparison.Ordinal))
            {
                throw new PointCompareDbException(e.Message, e);
            }

            IReadOnlyList<object?[]> rows = sql switch
            {
                _ when sql.Contains("VERSIONS BETWEEN", StringComparison.Ordinal) => VersionKeys,
                _ when sql.Contains(" AS K0 FROM", StringComparison.Ordinal) => WhereKeys,
                _ when sql.Contains("COUNT(*)", StringComparison.Ordinal) => [[3m]],
                _ when sql.Contains(" 100 t", StringComparison.Ordinal) => Base,
                _ when sql.Contains(" 200 t", StringComparison.Ordinal) => Commit,
                _ => NowRows,
            };
            return Task.FromResult(rows);
        }

        public Task<long?> CurrentPointAsync(CancellationToken ct) => Task.FromResult<long?>(Current);

        public Task<long?> PointMinutesAgoAsync(decimal minutesAgo, CancellationToken ct) => Task.FromResult<long?>(100);

        public Task<long?> UndoRetentionAsync(CancellationToken ct) => Task.FromResult<long?>(900);

        public Task<DateTimeOffset?> LastDdlAsync(DbObjectRef table, CancellationToken ct) => Task.FromResult<DateTimeOffset?>(null);

        public Task<long> ExecuteAsync(string sql, CancellationToken ct)
        {
            Executed.Add(sql);
            return Task.FromResult(Affected(sql));
        }
    }
}
