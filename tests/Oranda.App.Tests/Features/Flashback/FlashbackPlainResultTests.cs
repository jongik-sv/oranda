using Oranda.App.Features.Flashback;
using Oranda.App.Features.GridEdit;
using Oranda.App.Tests.Features.Execution;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Editing;

namespace Oranda.App.Tests.Features.Flashback;

/// <summary>TSK-13-33: 일반 실행 결과(편집 모드 아님)의 플래시백 대상 판정과 「변경 추적하기」 명령 합침.</summary>
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class FlashbackPlainResultTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static ResultColumn[] Cols(params (string Name, ColumnDataKind Kind)[] cols) =>
        [.. cols.Select((c, i) => new ResultColumn(i, c.Name, c.Kind.ToString(), c.Kind))];

    private static readonly ResultColumn[] EmpCols = Cols(("EMPNO", ColumnDataKind.Number), ("ENAME", ColumnDataKind.Text));

    private static SqlStatement Select(string sql) => new(sql, default, StatementKind.Query) { Verb = "SELECT" };

    private static async Task<(FlashbackTarget? Target, string Reason)> PlainAsync(string sql, ResultColumn[]? cols = null, bool withPk = true)
    {
        using var rig = new Rig();
        var table = new TableInfo(Emp, [new ColumnInfo("EMPNO", 1, "NUMBER") { IsNullable = false }])
        {
            Constraints = withPk ? [new ConstraintInfo("PK_EMP", ConstraintKind.PrimaryKey, ["EMPNO"])] : [],
        };
        return await FlashbackCommandHandler.PlainTargetAsync(new RowEditor(rig.Exec), new OneTableMetadata(table), rig.Session, Select(sql), cols ?? EmpCols,
            CancellationToken.None);
    }

    [Fact]
    public async Task Plain_single_table_result_with_pk_column_gives_a_target()
    {
        var (target, reason) = await PlainAsync("SELECT empno, ename FROM emp");

        Assert.NotNull(target);
        Assert.Equal(string.Empty, reason);
        Assert.Equal(Emp, target.Table);
        Assert.Equal(["EMPNO"], target.KeyNames);
        Assert.Equal(["EMPNO", "ENAME"], target.Names);
    }

    [Fact]
    public async Task Plain_result_without_pk_column_explains_how_to_get_one()
    {
        var (missing, why) = await PlainAsync("SELECT ename FROM emp", Cols(("ENAME", ColumnDataKind.Text)));
        Assert.Null(missing);
        Assert.Equal(FlashbackTarget.NeedsKeyColumns, why);
        Assert.Contains("PK", why, StringComparison.Ordinal);

        var (nokey, why2) = await PlainAsync("SELECT empno, ename FROM emp", withPk: false);
        Assert.Null(nokey);
        Assert.Equal(FlashbackTarget.NeedsKeyColumns, why2);
    }

    [Fact]
    public async Task Plain_multi_table_result_is_refused()
    {
        var (target, why) = await PlainAsync("SELECT e.empno, d.dname FROM emp e JOIN dept d ON d.deptno = e.deptno",
            Cols(("EMPNO", ColumnDataKind.Number), ("DNAME", ColumnDataKind.Text)));

        Assert.Null(target);
        Assert.Equal(FlashbackTarget.MultipleTables, why);
    }

    [Fact]
    public void Edit_mode_capability_still_decides_as_before_and_restore_needs_edit_mode()
    {
        var cap = new EditCapability(true) { Target = Emp, KeyColumnOrdinals = [0], EditableColumnOrdinals = [1] };
        var target = FlashbackTarget.TryCreate(cap, ["EMPNO", "ENAME"], EmpCols, out var reason);

        Assert.NotNull(target);
        Assert.Equal(string.Empty, reason);
        Assert.Contains("편집 모드", FlashbackTarget.RestoreNeedsEditMode, StringComparison.Ordinal);

        // 편집 모드 결과(ROWID 키 없음)의 키 없음 안내는 기존 문구 그대로
        Assert.Contains("PK·ROWID", FlashbackTarget.Unavailable(new EditCapability(false, EditBlockReason.NoKeyColumns)), StringComparison.Ordinal);
    }

    [Fact]
    public void Track_command_is_gone_and_history_remains()
    {
        var registry = ShellTestHelpers.CreateCommandRegistry();

        Assert.Null(registry.Find("oranda.grid.flashback.track"));
        Assert.NotNull(registry.Find("oranda.grid.flashback.history"));
        Assert.NotNull(registry.Find("oranda.grid.flashback.asOf"));
        Assert.NotNull(registry.Find("oranda.grid.flashback.restore"));
    }

    private sealed class OneTableMetadata(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table with { Table = t });

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
