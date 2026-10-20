using Oranda.App.Features.GridEdit;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.GridEdit;

/// <summary>
/// 편집 세션의 「원래 값」(GRID-04, PRD 6.5.1-3·6): UPDATE·DELETE 의 WHERE 키, 낙관적 잠금 원래 값, 「원래 값과 같으면 변경 지움」은
/// 그리드에 보이는 바꾼 값이 아니라 질의 결과의 원래 값을 쓴다. 세션은 실행 화면과 같은 읽기 함수로 그리드 모델에 붙인다.
/// </summary>
public sealed partial class GridEditTests
{
    /// <summary>실행 화면·객체 상세 「데이터」 탭이 편집 세션에 넘기는 원래 값 읽기와 같은 것.</summary>
    private static Func<long, int, object?> SessionReader(ResultGridModel m) => GridEditBinding.OriginalValues(m);

    /// <summary>EMP 3열 결과를 그리드 모델에 올리고 실행 화면처럼 편집 세션을 붙인다(바꾼 값이 그리드 덮어쓰기에 보인다).</summary>
    private async Task<(ResultGridModel Grid, GridEditSession Session)> EditGridAsync(IResultSet? source = null, ResultPageCacheOptions? cache = null,
                                                                                     int pageSize = ResultGridModel.DefaultPageSize)
    {
        var m = new ResultGridModel(source ?? new MemoryResult(EmpCols, Data), cache, pageSize);
        await m.LoadFirstPageAsync();
        var meta = new OneTableMetadata(new Oranda.Core.Metadata.TableInfo(Emp, [new Oranda.Core.Metadata.ColumnInfo("EMPNO", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = [new Oranda.Core.Metadata.ConstraintInfo("PK_EMP", Oranda.Core.Metadata.ConstraintKind.PrimaryKey, ["EMPNO"])],
        });
        var stmt = new SqlStatement("SELECT empno, ename, sal FROM emp", default, StatementKind.Query) { Verb = "SELECT" };
        var s = await GridEditSession.OpenAsync(new RowEditor(_exec), meta, new TestSession(false), stmt, m.Columns,
            SessionReader(m), () => m.LoadedRowCount, target: null, default);
        GridEditBinding.Attach(m, s);
        return (m, s);
    }

    private static object? Bind(IReadOnlyDictionary<string, BindValue> binds, string name) => binds[name].Value;

    [Fact]
    public async Task Editing_a_key_column_updates_the_row_found_by_its_original_key()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;

        Assert.True(s.SetCell(0, 0, 9999m)); // EMPNO 7369 → 9999

        var change = Assert.Single(s.BuildChanges());
        Assert.Equal(RowChangeKind.Update, change.Kind);
        Assert.Equal(7369m, change.Key["EMPNO"]);
        Assert.Equal(9999m, change.NewValues["EMPNO"]);
        var preview = s.BuildPreview();
        Assert.Contains("UPDATE SCOTT.EMP SET EMPNO = :v1 WHERE EMPNO = :k1;", preview, StringComparison.Ordinal);
        Assert.Contains(":v1 = 9999, :k1 = 7369", preview, StringComparison.Ordinal);

        var result = await s.ApplyAsync(commit: false, default);

        Assert.Equal(1, result.AppliedCount);
        var dml = Assert.Single(_exec.Calls, c => c.Statement.Kind == StatementKind.Dml);
        Assert.Equal(7369m, Bind(dml.Binds, "k1"));
        Assert.Equal(9999m, Bind(dml.Binds, "v1"));
    }

    [Fact]
    public async Task Optimistic_lock_compares_against_the_values_before_the_edit()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;
        s.OptimisticLock = true;

        // 가짜 테이블: 다른 세션이 바꾸지 않았다(7369 행은 그대로 SMITH·800). WHERE 의 원래 값이 맞아야 1행이 바뀐다.
        _exec.RowsForBinds = (stmt, binds) =>
            Equals(Bind(binds, "k1"), 7369m) && Equals(Bind(binds, "o1"), "SMITH") && Equals(Bind(binds, "o2"), 800m) ? 1 : 0;
        Assert.True(s.SetCell(0, 2, 900m)); // SAL 800 → 900

        var change = Assert.Single(s.BuildChanges());
        Assert.Equal("SMITH", change.OriginalValues["ENAME"]);
        Assert.Equal(800m, change.OriginalValues["SAL"]);
        Assert.Contains("UPDATE SCOTT.EMP SET SAL = :v1 WHERE EMPNO = :k1 AND ENAME = :o1 AND SAL = :o2;", s.BuildPreview(), StringComparison.Ordinal);

        var result = await s.ApplyAsync(commit: false, default);

        Assert.Empty(result.Failures); // 바꾼 값(900)을 원래 값으로 보내면 0행 → 「다른 세션이 바꿨다」는 거짓 충돌
        Assert.Equal(1, result.AppliedCount);
        Assert.False(s.IsDirty);
    }

    [Fact]
    public async Task Editing_a_cell_back_to_its_original_value_drops_the_change()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;

        Assert.True(s.SetCell(0, 2, 900m));
        Assert.True(s.IsDirty);
        Assert.True(s.SetCell(0, 2, 800m)); // 원래 값으로 되돌렸다

        Assert.False(s.IsDirty);
        Assert.Equal(GridEditRowState.Unchanged, s.GetRowState(0));
        Assert.Empty(s.BuildChanges());
    }

    [Fact]
    public async Task Deleting_an_edited_row_uses_its_original_key()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;

        Assert.True(s.SetCell(1, 0, 8888m)); // EMPNO 7499 → 8888 로 고친 뒤
        s.DeleteRow(1);                      // 그 행을 지운다

        var change = Assert.Single(s.BuildChanges());
        Assert.Equal(RowChangeKind.Delete, change.Kind);
        Assert.Equal(7499m, change.Key["EMPNO"]);
        Assert.Contains("DELETE FROM SCOTT.EMP WHERE EMPNO = :k1;", s.BuildPreview(), StringComparison.Ordinal);
        Assert.Contains(":k1 = 7499", s.BuildPreview(), StringComparison.Ordinal);

        await s.ApplyAsync(commit: false, default);
        var dml = Assert.Single(_exec.Calls, c => c.Statement.Kind == StatementKind.Dml);
        Assert.Equal(7499m, Bind(dml.Binds, "k1"));
    }

    [Fact]
    public async Task New_rows_have_no_original_values_and_insert_what_was_typed()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;

        var added = s.AddRow();
        Assert.True(s.SetCell(added, 0, 9000m));
        Assert.True(s.SetCell(added, 1, "NEW"));

        var change = Assert.Single(s.BuildChanges());
        Assert.Equal(RowChangeKind.Insert, change.Kind);
        Assert.Empty(change.Key);
        Assert.Empty(change.OriginalValues);
        Assert.Null(s.ReadCell(added, 0)); // 새 행은 원본이 없다
        Assert.False(m.TryGetBaseValue(m.RowCount - 1, 0, out _));
        Assert.Contains("INSERT INTO SCOTT.EMP (EMPNO, ENAME) VALUES (:v1, :v2);", s.BuildPreview(), StringComparison.Ordinal);
        Assert.Contains(":v1 = 9000, :v2 = 'NEW'", s.BuildPreview(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Visible_values_still_show_the_edit_while_the_session_reads_originals()
    {
        var (m, s) = await EditGridAsync();
        await using var scope = m;
        string? clip = null;
        m.ClipboardWriter = t =>
        {
            clip = t;
            return Task.CompletedTask;
        };

        s.SetCell(0, 0, 9999m);

        Assert.True(m.TryGetValue(0, 0, out var shown) && Equals(shown, 9999m)); // 그리기·복사·찾기는 바꾼 값
        Assert.Equal("9999", m.GetCellText(0, 0));
        Assert.True(m.TryGetBaseValue(0, 0, out var original) && Equals(original, 7369m));
        Assert.Equal(7369m, s.ReadCell(0, 0));
        m.SelectCell(new GridCell(0, 0));
        await m.CopyAsync(CopyFormat.Tsv);
        Assert.Equal("9999", clip);
        Assert.True(await m.FindNextAsync("SMITH"));
    }

    [Fact]
    public async Task Keys_survive_the_edited_page_leaving_memory()
    {
        // 한 페이지만 메모리에 두는 캐시: 고친 행의 페이지가 디스크로 내려가도 WHERE 키와 원래 값은 고칠 때의 원래 값이다.
        var rows = Enumerable.Range(0, 120).Select(i => new object?[] { 7000m + i, "E" + i, 1000m + i }).ToArray();
        var (m, s) = await EditGridAsync(new PagedResult(EmpCols, rows, pageSize: 10), new ResultPageCacheOptions { MaxMemoryBytes = 1 }, pageSize: 10);
        await using var scope = m;
        s.OptimisticLock = true;
        Assert.True(s.SetCell(0, 2, 1.5m));
        s.DeleteRow(1);

        await m.FetchAllAsync(); // 0 쪽 페이지가 메모리에서 밀려난다
        Assert.False(m.TryGetBaseValue(0, 0, out _));

        var changes = s.BuildChanges();
        Assert.Equal(7001m, Assert.Single(changes, c => c.Kind == RowChangeKind.Delete).Key["EMPNO"]);
        var update = Assert.Single(changes, c => c.Kind == RowChangeKind.Update);
        Assert.Equal(7000m, update.Key["EMPNO"]);
        Assert.Equal(1000m, update.OriginalValues["SAL"]);
        Assert.Contains(":k1 = 7000", s.BuildPreview(), StringComparison.Ordinal);
    }

    /// <summary>페이지 크기만큼 나눠 돌려주는 메모리 결과(캐시가 페이지를 내려보낼 수 있게).</summary>
    private sealed class PagedResult(ResultColumn[] columns, object?[][] rows, int pageSize) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete { get; private set; }

        public long? KnownRowCount => IsComplete ? rows.Length : null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * pageSize;
            var n = Math.Clamp(rows.Length - first, 0, pageSize);
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add([.. rows.Skip(first).Take(n).Select(r => r[c])]);
            }

            IsComplete |= first + n >= rows.Length;
            return Task.FromResult(new ResultPage(pageIndex, first, n, cols) { IsLast = first + n >= rows.Length });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
