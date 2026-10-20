using Oranda.App.Features.ColumnReorder;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Oracle.Schema;

namespace Oranda.App.Tests.Features.ColumnReorder;

/// <summary>TSK-13-01 열 순서 바꾸기 ViewModel 시험: 순서 바꾸기·미리보기·실행·막는 경우·경고·방식 ② 스크립트.</summary>
public class ColumnReorderViewModelTests
{
    private static readonly DbObjectRef Emp = ReorderMeta.Emp;

    private static async Task<(ColumnReorderViewModel Vm, ReorderMeta Meta, ReorderProvider Reorder, ReorderExec Exec)> OpenAsync(
        ReorderSession? session = null, Action<ReorderMeta, ReorderProvider>? setup = null)
    {
        var meta = new ReorderMeta();
        var reorder = new ReorderProvider();
        setup?.Invoke(meta, reorder);
        var exec = new ReorderExec();
        var vm = new ColumnReorderViewModel(meta, reorder, exec);
        await vm.LoadAsync(session ?? new ReorderSession(), Emp);
        return (vm, meta, reorder, exec);
    }

    [Fact]
    public async Task Load_lists_visible_columns_in_position_order_with_type_and_null()
    {
        var (vm, _, _, _) = await OpenAsync(setup: (m, _) =>
        {
            m.Columns.Add(new ColumnInfo("HID", 5, "NUMBER") { IsInvisible = true });
            m.Columns.Reverse();
        });

        Assert.Equal(["ID", "NAME", "HIRED", "SAL"], vm.Items.Select(i => i.Name));
        Assert.Equal("NUMBER(10)", vm.Items[0].TypeText);
        Assert.Equal("NOT NULL", vm.Items[0].NullableText);
        Assert.Equal("VARCHAR2(30)", vm.Items[1].TypeText);
        Assert.Equal("NUMBER(8,2)", vm.Items[3].TypeText);
        Assert.False(vm.IsChanged);
        Assert.False(vm.CanPreview);
    }

    [Fact]
    public async Task Move_buttons_and_drop_change_the_order_and_stop_at_the_edges()
    {
        var (vm, _, _, _) = await OpenAsync();

        Assert.False(vm.MoveUp(vm.Items[0]));
        Assert.True(vm.MoveDown(vm.Items[0]));
        Assert.Equal(["NAME", "ID", "HIRED", "SAL"], vm.Items.Select(i => i.Name));
        Assert.True(vm.MoveTo(vm.Items[3], 0));
        Assert.Equal(["SAL", "NAME", "ID", "HIRED"], vm.Items.Select(i => i.Name));
        Assert.False(vm.MoveDown(vm.Items[3]));
        Assert.True(vm.IsChanged);
        Assert.True(vm.CanPreview);
    }

    [Fact]
    public async Task Preview_for_invisible_visible_starts_at_the_first_changed_position()
    {
        var (vm, _, _, _) = await OpenAsync();
        vm.MoveTo(vm.Items[2], 1); // ID, HIRED, NAME, SAL

        Assert.Equal(string.Empty, vm.PreviewText); // 미리보기를 누르기 전에는 비어 있다
        await vm.ShowPreviewAsync();

        Assert.Equal(
            "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"HIRED\" INVISIBLE);\n"
            + "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"HIRED\" VISIBLE);\n"
            + "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"NAME\" INVISIBLE);\n"
            + "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"NAME\" VISIBLE);\n"
            + "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"SAL\" INVISIBLE);\n"
            + "ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"SAL\" VISIBLE);",
            vm.PreviewText);
    }

    [Fact]
    public async Task Run_needs_a_preview_then_executes_in_order_and_reloads()
    {
        var (vm, meta, _, exec) = await OpenAsync();
        var invalidated = 0;
        var applied = 0;
        vm.InvalidateCache = (_, _, _) =>
        {
            invalidated++;
            // 서버에서 순서가 바뀐 것처럼 다음 읽기에 새 순서를 돌려준다.
            meta.Columns = [.. vm.Items.Select((it, i) => meta.Columns.Single(c => c.Name == it.Name) with { Position = i + 1 })];
            return Task.CompletedTask;
        };
        vm.Applied += (_, _) => applied++;
        vm.MoveTo(vm.Items[1], 0); // NAME, ID, HIRED, SAL

        Assert.False(vm.CanRun);
        Assert.False(await vm.RunAsync());
        Assert.Empty(exec.Ran);

        await vm.ShowPreviewAsync();
        Assert.True(vm.CanRun);
        Assert.True(await vm.RunAsync());

        Assert.Equal(8, exec.Ran.Count);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"NAME\" INVISIBLE)", exec.Ran[0]);
        Assert.Equal("ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"SAL\" VISIBLE)", exec.Ran[^1]);
        Assert.Equal(1, invalidated);
        Assert.Equal(1, applied);
        Assert.Equal(["NAME", "ID", "HIRED", "SAL"], vm.Items.Select(i => i.Name));
        Assert.False(vm.IsChanged); // 새 순서가 처음 순서가 된다
        Assert.False(vm.ShowPreview);
    }

    [Fact]
    public async Task Production_runs_all_statements_as_one_block_so_the_gate_confirms_once()
    {
        var (vm, _, _, exec) = await OpenAsync(new ReorderSession(production: true));
        vm.MoveTo(vm.Items[1], 0); // NAME, ID, HIRED, SAL
        await vm.ShowPreviewAsync();

        Assert.True(await vm.RunAsync());

        var block = Assert.Single(exec.Ran);
        Assert.StartsWith("-- 열 순서 바꾸기 SCOTT.EMP: ALTER TABLE 8문장", block, StringComparison.Ordinal);
        Assert.Contains("BEGIN\n  EXECUTE IMMEDIATE 'ALTER TABLE \"SCOTT\".\"EMP\" MODIFY (\"NAME\" INVISIBLE)';", block.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.EndsWith("END;", block, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Development_keeps_statement_by_statement_execution()
    {
        var (vm, _, _, exec) = await OpenAsync(new ReorderSession(production: false));
        vm.MoveTo(vm.Items[1], 0);
        await vm.ShowPreviewAsync();

        await vm.RunAsync();

        Assert.Equal(8, exec.Ran.Count);
    }

    [Theory]
    [InlineData(ExecutionStatus.Failed)]
    [InlineData(ExecutionStatus.Denied)]
    public async Task Run_stops_at_the_first_failed_statement_and_does_not_report_applied(ExecutionStatus status)
    {
        var (vm, _, _, exec) = await OpenAsync();
        exec.FailAt = 2;
        exec.FailStatus = status;
        var applied = 0;
        vm.Applied += (_, _) => applied++;
        vm.MoveTo(vm.Items[1], 0);
        await vm.ShowPreviewAsync();

        Assert.False(await vm.RunAsync());

        Assert.Equal(3, exec.Ran.Count);
        Assert.Equal(0, applied);
        Assert.Contains("2/8", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBusy);
    }

    [Theory]
    [InlineData(true, false, null, "외부 표")]
    [InlineData(false, true, null, "임시 표")]
    [InlineData(false, false, "CL", "클러스터 표(CL)")]
    public async Task Unsupported_table_kinds_are_blocked_with_a_reason(bool external, bool temporary, string? cluster, string reason)
    {
        var (vm, _, _, _) = await OpenAsync(setup: (_, r) => r.Traits = new ColumnReorderTraits(external, temporary, cluster));
        vm.MoveTo(vm.Items[1], 0);

        Assert.True(vm.HasBlockReasons);
        Assert.Contains(vm.BlockReasons, b => b.Contains(reason, StringComparison.Ordinal));
        Assert.False(vm.CanPreview);
        await vm.ShowPreviewAsync();
        Assert.Equal(string.Empty, vm.PreviewText);
        Assert.False(vm.CanRun);
    }

    [Fact]
    public async Task Originally_invisible_columns_block_method_one_but_not_the_script()
    {
        var (vm, _, _, _) = await OpenAsync(setup: (m, _) => m.Columns.Add(new ColumnInfo("HID", 9, "NUMBER") { IsInvisible = true }));
        vm.MoveTo(vm.Items[1], 0);

        Assert.Contains(vm.BlockReasons, b => b.Contains("INVISIBLE 인 열", StringComparison.Ordinal) && b.Contains("HID", StringComparison.Ordinal));
        Assert.False(vm.CanPreview);

        vm.Method = ColumnReorderMethod.Rebuild;
        Assert.False(vm.HasBlockReasons);
        Assert.True(vm.CanOpenScript);
    }

    [Fact]
    public async Task Server_before_12c_hides_method_one_and_starts_with_the_script()
    {
        var (vm, _, _, _) = await OpenAsync(new ReorderSession("11.2.0.4.0", "Enterprise"));

        Assert.False(vm.CanUseInvisible);
        Assert.Equal(ColumnReorderMethod.Rebuild, vm.Method);
        Assert.True(vm.IsRebuildMethod);
        Assert.False(vm.IsInvisibleMethod);
    }

    [Fact]
    public async Task Missing_catalog_for_this_server_version_disables_the_feature_without_throwing()
    {
        var (vm, _, reorder, _) = await OpenAsync(new ReorderSession("11.2.0.4.0"), (_, r) => r.TraitsNotFound = true);

        Assert.True(vm.IsUnsupported);
        Assert.Contains("쓸 수 없다", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.CanPreview);
        Assert.Equal(0, reorder.ScanCalls);
    }

    [Fact]
    public async Task Rebuild_script_on_enterprise_uses_dbms_redefinition_and_opens_in_a_new_tab()
    {
        var (vm, _, _, exec) = await OpenAsync(new ReorderSession("19.0.0.0", "Enterprise"));
        (string Title, string Text)? opened = null;
        vm.OpenEditor = (title, text) => opened = (title, text);
        vm.Method = ColumnReorderMethod.Rebuild;
        vm.MoveTo(vm.Items[3], 0); // SAL, ID, NAME, HIRED

        Assert.False(vm.CanRun); // ② 는 실행 단추가 없다
        Assert.True(await vm.OpenScriptAsync());

        Assert.NotNull(opened);
        Assert.Equal("EMP 열 순서", opened!.Value.Title);
        var script = opened.Value.Text;
        Assert.Contains("검토 뒤 실행", script, StringComparison.Ordinal);
        Assert.Contains("AS SELECT \"SAL\", \"ID\", \"NAME\", \"HIRED\" FROM", script, StringComparison.Ordinal);
        Assert.Contains("CONS_USE_PK", script, StringComparison.Ordinal);
        Assert.Contains("DBMS_REDEFINITION.FINISH_REDEF_TABLE", script, StringComparison.Ordinal);
        Assert.Contains("-- CREATE UNIQUE INDEX \"SCOTT\".\"EMP_PK\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("CREATE TABLE \"SCOTT\".\"EMP\" (ID", script, StringComparison.Ordinal); // 표 DDL 은 빠지고 종속 DDL 만
        Assert.Empty(exec.Ran);
    }

    [Fact]
    public async Task Rebuild_script_on_standard_uses_ctas_and_rename_with_dependent_ddl()
    {
        var (vm, _, _, _) = await OpenAsync(new ReorderSession("19.0.0.0", "Standard"));
        string? text = null;
        vm.OpenEditor = (_, t) => text = t;
        vm.Method = ColumnReorderMethod.Rebuild;
        vm.MoveTo(vm.Items[3], 0);

        await vm.OpenScriptAsync();

        Assert.Contains("CREATE TABLE \"SCOTT\".\"EMP$NEW\" AS SELECT", text, StringComparison.Ordinal);
        Assert.Contains("RENAME TO \"EMP$OLD\"", text, StringComparison.Ordinal);
        Assert.Contains("\nCREATE UNIQUE INDEX \"SCOTT\".\"EMP_PK\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("DBMS_REDEFINITION", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Warnings_list_only_order_sensitive_lines_and_state_the_line_limit()
    {
        var pkg = new DbObjectRef("SCOTT", "PKG", "PACKAGE BODY");
        var (vm, _, reorder, _) = await OpenAsync(setup: (_, r) => r.Lines =
        [
            new SourceSearchHit(pkg, 10, "  SELECT * FROM emp;"),
            new SourceSearchHit(pkg, 11, "  SELECT id FROM emp;"),
            new SourceSearchHit(pkg, 30, "  INSERT INTO emp VALUES (1, 'a', SYSDATE, 1);"),
        ]);
        await HeadlessWait(() => vm.HasWarnings);

        Assert.Equal(1, reorder.ScanCalls);
        Assert.Equal(2, vm.Warnings.Count);
        Assert.Contains("SCOTT.PKG (PACKAGE BODY) 줄 10", vm.Warnings[0].Text, StringComparison.Ordinal);
        Assert.Contains("SELECT *", vm.Warnings[0].Text, StringComparison.Ordinal);
        Assert.Contains("INSERT(열 목록 없음)", vm.Warnings[1].Text, StringComparison.Ordinal);
        Assert.Contains("한 줄 단위", vm.WarningNote, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failing_source_scan_keeps_the_dialog_usable()
    {
        var (vm, _, _, _) = await OpenAsync(setup: (_, r) => r.ScanFails = new InvalidOperationException("ORA-00942: table or view does not exist"));
        await HeadlessWait(() => vm.WarningNote.Length > 0);

        Assert.Contains("소스 경고를 찾지 못했다", vm.WarningNote, StringComparison.Ordinal);
        Assert.False(vm.IsUnsupported);
        vm.MoveTo(vm.Items[1], 0);
        Assert.True(vm.CanPreview);
    }

    private static async Task HeadlessWait(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "조건이 시간 안에 참이 되지 않았다.");
    }
}
