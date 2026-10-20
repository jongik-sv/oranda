using Oranda.App.Features.Flashback;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Catalog;
using Oranda.Core.Connection;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>TSK-13-05 플래시백: 대상 판정(메뉴를 눌렀을 때의 안내 분기)·되돌리기 계획·차이 칸 강조·환경 안내.</summary>
public sealed class FlashbackTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static ResultColumn[] Cols(params (string Name, ColumnDataKind Kind)[] cols) =>
        [.. cols.Select((c, i) => new ResultColumn(i, c.Name, c.Kind.ToString(), c.Kind))];

    private static readonly ResultColumn[] EmpCols = Cols(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number), ("MEMO", ColumnDataKind.Clob));

    private static EditCapability Editable(params int[] keys) =>
        new(true) { Target = Emp, KeyColumnOrdinals = keys, EditableColumnOrdinals = [0, 1, 2, 3] };

    private static FlashbackTarget Target(EditCapability cap, params string?[] names) =>
        FlashbackTarget.TryCreate(cap, names, EmpCols, out _)!;

    // ---------- 대상 판정 ----------

    [Fact]
    public void Target_pk_excludes_lob_and_marks_editable_columns()
    {
        var t = Target(Editable(0), "EMPNO", "ENAME", "SAL", "MEMO");
        Assert.Equal(["EMPNO", "ENAME", "SAL"], t.Names);
        Assert.Equal([0], t.KeyPositions);
        Assert.Equal([1, 2], t.EditablePositions);
        Assert.Equal([0, 1, 2], t.Ordinals);
    }

    [Fact]
    public void Target_composite_pk_and_rowid_key()
    {
        var composite = Target(Editable(0, 1), "EMPNO", "ENAME", "SAL", "MEMO");
        Assert.Equal(["EMPNO", "ENAME"], composite.KeyNames);

        var cols = Cols(("ROWID", ColumnDataKind.RowId), ("ENAME", ColumnDataKind.Text));
        var cap = new EditCapability(true) { Target = Emp, KeyColumnOrdinals = [0], EditableColumnOrdinals = [1] };
        var t = FlashbackTarget.TryCreate(cap, ["ROWID", "ENAME"], cols, out _)!;
        Assert.Equal(["ROWID"], t.KeyNames);
    }

    [Fact]
    public void Target_unavailable_reasons_per_case()
    {
        Assert.Null(FlashbackTarget.TryCreate(null, null, EmpCols, out var noEdit));
        Assert.Equal(FlashbackTarget.NeedsEditMode, noEdit);

        var multi = new EditCapability(false, EditBlockReason.MultipleTables);
        Assert.Null(FlashbackTarget.TryCreate(multi, null, EmpCols, out var many));
        Assert.Equal(FlashbackTarget.MultipleTables, many);

        var nokey = new EditCapability(false, EditBlockReason.NoKeyColumns);
        Assert.Null(FlashbackTarget.TryCreate(nokey, null, EmpCols, out var why));
        Assert.Contains("키", why, StringComparison.Ordinal);
    }

    // ---------- 되돌리기 계획 ----------

    [Fact]
    public void PlanRestore_pairs_by_key_and_sets_only_changed_editable_columns()
    {
        var t = Target(Editable(0), "EMPNO", "ENAME", "SAL", "MEMO");
        object?[][] past = [[7499m, "ALLEN", 1600m], [7369m, "SMITH", 800m]]; // 순서가 달라도 키로 짝짓는다
        (long, object?[])[] now = [(0, [7369m, "SMITH", 900m]), (1, [7499m, "ALLEN", 1600m]), (2, [7788m, "SCOTT", 3000m])];
        var plan = FlashbackPlanner.PlanRestore(t, now, past);
        var change = Assert.Single(plan.Changes);
        Assert.Equal(0, change.RowIndex);
        Assert.Equal(7369m, change.Key["EMPNO"]);
        Assert.Equal(800m, change.NewValues["SAL"]);
        Assert.Single(change.NewValues);
        Assert.Equal(1, plan.Unchanged);
        Assert.Equal(1, plan.MissingThen);
    }

    [Fact]
    public void PlanRestore_turns_past_null_into_null_value()
    {
        var t = Target(Editable(0), "EMPNO", "ENAME", "SAL", "MEMO");
        var plan = FlashbackPlanner.PlanRestore(t, [(0, [1m, "A", 5m])], [[1m, "A", DBNull.Value]]);
        Assert.Null(Assert.Single(plan.Changes).NewValues["SAL"]);
    }

    // ---------- 차이 칸 강조 ----------

    [Fact]
    public async Task Overlay_marks_cells_that_differ_from_now_regardless_of_row_order()
    {
        var cols = Cols(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text), ("SAL", ColumnDataKind.Number));
        var model = new ResultGridModel(new Mem(cols, [[7499m, "ALLEN", 1600m], [7369m, "SMITH", 800m]]));
        await model.LoadFirstPageAsync();
        var overlay = new FlashbackOverlay(model, [0], [[7369m, "SMITH", 900m], [7499m, "ALLEN", 1600m]]);
        Assert.False(overlay.IsEdited(0, 2));
        Assert.Equal(GridRowMark.None, overlay.RowMark(0));
        Assert.True(overlay.IsEdited(1, 2));
        Assert.False(overlay.IsEdited(1, 1));
        Assert.Equal(GridRowMark.Modified, overlay.RowMark(1));
        Assert.False(overlay.HasPendingChanges);
    }

    // ---------- 환경 안내 ----------

    [Fact]
    public async Task Environment_unknown_when_catalog_has_no_variant()
    {
        var value = await FlashbackEnvironment.ReadUndoRetentionAsync(new NoVariantCatalog(), Server, _ => Task.FromResult<object?>(900m));
        Assert.Null(value);
        Assert.Contains("알 수 없음", FlashbackEnvironment.Describe(value), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_unknown_on_ora_00942_and_known_value_described()
    {
        var denied = await FlashbackEnvironment.ReadUndoRetentionAsync(new FixedCatalog(), Server,
            _ => throw new InvalidOperationException("ORA-00942: table or view does not exist"));
        Assert.Null(denied);
        var ok = await FlashbackEnvironment.ReadUndoRetentionAsync(new FixedCatalog(), Server, _ => Task.FromResult<object?>("900"));
        Assert.Contains("900초", FlashbackEnvironment.Describe(ok), StringComparison.Ordinal);
        Assert.Contains("15분", FlashbackEnvironment.Describe(ok), StringComparison.Ordinal);
    }

    [Fact]
    public void LastDdl_value_converts_from_driver_types()
    {
        var t = new DateTime(2026, 10, 20, 9, 30, 0);
        Assert.Equal(t, FlashbackRunner.ToDateTime(t));
        Assert.Equal(t, FlashbackRunner.ToDateTime("2026-10-20 09:30:00"));
        Assert.Null(FlashbackRunner.ToDateTime(DBNull.Value));
    }

    private static readonly ServerInfo Server = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
        new ServerCapabilities(false, false, false, false, false));

    private sealed class NoVariantCatalog : ISqlCatalog
    {
        public CatalogQuery Get(string id, ServerInfo server) => throw new CatalogQueryNotFoundException(id, 11);
    }

    private sealed class FixedCatalog : ISqlCatalog
    {
        public CatalogQuery Get(string id, ServerInfo server) => new(null!, "SELECT 1 FROM dual");
    }

    private sealed class Mem(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * 500;
            var count = Math.Clamp(rows.Length - first, 0, 500);
            var cols = Enumerable.Range(0, columns.Length)
                .Select(c => (IReadOnlyList<object?>)rows.Skip(first).Take(count).Select(r => r[c]).ToList()).ToList();
            return Task.FromResult(new ResultPage(pageIndex, first, count, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
