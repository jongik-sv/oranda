using Oranda.Core.Grid;
using Xunit.Abstractions;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Completion;
using Oranda.App.Features.FormatterProfiles;
using Oranda.App.Features.GridEdit;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Features.Execution;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.Formatting;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;
using Oranda.Core.Storage;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-11-01 개발자 흐름 시나리오(접속 이후): S2 작성(자동완성)→포맷→실행, S3 그리드 편집, S4 이력 다시 실행.
/// 각 시나리오는 앱 명령·서비스를 그대로 거치고 실제 Oracle(상주 컨테이너 26ai)로 끝에서 끝까지 이어 단정한다.
/// ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다(「돌렸다」 로 세지 않는다).
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class DeveloperFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static string NewTable() => "T_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Squash(string sql) => string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NoSpace(string sql) => string.Concat(sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ------------------------------------------------------------------------------------------
    // S2. 작성(자동완성) → 포맷 → 실행
    // ------------------------------------------------------------------------------------------

    [Fact]
    public Task S2_1_Completion_after_alias_dot_lists_table_columns_from_the_live_server() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, name varchar2(50), \"이름\" varchar2(50), amt number(10,2))");
        try
        {
            var meta = rig.Container.GetRequiredService<IMetadataProvider>();
            var provider = rig.Container.GetRequiredService<ICompletionProvider>();
            var controller = new CompletionController
            {
                ProviderResolver = () => provider,
                MetadataResolver = () => meta,
                SessionResolver = _ => rig.Session,
            };

            // 정상: 별칭 뒤 「.」 → 그 표의 열(한글 따옴표 열 포함) 목록 창이 뜬다.
            var text = $"select e. from {t} e";
            var (window, view, _) = Show(text, 9);
            var area = view.TextEditorControl.TextArea;
            // 메타데이터는 처음엔 150 ms 예산 안에 다 못 읽을 수 있다(IsPartial). 백그라운드 로드가 끝날 때까지 최대 10초 다시 시도하고, 그래도 안 열리면 결함으로 본다.
            var opened = false;
            for (var attempt = 0; attempt < 20 && !opened; attempt++)
            {
                opened = await controller.ShowCompletionAsync(area, null, automatic: true);
                if (!opened)
                {
                    await Task.Delay(500);
                }
            }

            Assert.True(opened, $"후보 {controller.LastResult?.Items.Count}개, partial={controller.LastResult?.IsPartial}");
            var labels = controller.LastResult!.Items.Select(i => i.Label).ToList();
            Assert.Contains("ID", labels);
            Assert.Contains("NAME", labels);
            Assert.Contains("AMT", labels);
            Assert.Contains(labels, l => l.Contains("이름", StringComparison.Ordinal));
            Assert.NotNull(controller.CurrentWindow);
            controller.CloseCompletion();

            // 경계: 접두어를 쓰면 같은 위치에서 걸러 낼 수 있다(결과에 접두어 일치 항목이 있다), 빈 문장·FROM 뒤는 표 이름.
            var item = controller.LastResult.Items.First(i => i.Label == "AMT");
            var applied = text[..controller.LastResult.ReplaceRange.Start] + item.InsertText + text[(controller.LastResult.ReplaceRange.Start + controller.LastResult.ReplaceRange.Length)..];
            Assert.Contains("e.AMT", applied.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);

            var tableText = $"select * from {t[..5]}";
            var (w2, v2, _) = Show(tableText, tableText.Length);
            var tableOpened = await controller.ShowCompletionAsync(v2.TextEditorControl.TextArea, null, automatic: false);
            Assert.True(tableOpened, $"표 이름 후보 {controller.LastResult?.Items.Count}개");
            Assert.Contains(controller.LastResult!.Items, i => string.Equals(i.Label, t, StringComparison.OrdinalIgnoreCase));
            controller.CloseCompletion();

            // 오류: 모르는 별칭은 열 목록을 내지 않고, 접속 없음(null 세션)이어도 예외 없이 끝난다.
            var (w3, v3, _) = Show($"select zz. from {t} e", 10);
            await controller.ShowCompletionAsync(v3.TextEditorControl.TextArea, null, automatic: true);
            Assert.DoesNotContain(controller.LastResult?.Items.Select(i => i.Label) ?? [], l => l == "AMT");
            var noSession = new CompletionController { ProviderResolver = () => provider, MetadataResolver = () => meta, SessionResolver = _ => null };
            var (w4, v4, _) = Show("select e. from emp e", 9);
            Assert.False(await noSession.ShowCompletionAsync(v4.TextEditorControl.TextArea, null, automatic: true));
            foreach (var w in new[] { window, w2, w3, w4 })
            {
                w.Close();
            }

            output.WriteLine("S2-1 OK: 별칭 열 자동완성(한글 열 포함)·표 이름·모르는 별칭·세션 없음");
        }
        finally
        {
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text, int caret)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        view.TextEditorControl.CaretOffset = caret;
        view.TextEditorControl.TextArea.Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    [Fact]
    public Task S2_2_Format_then_run_gives_the_same_rows_as_the_unformatted_text() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, name varchar2(50), amt number(10,2));\n" +
            $"insert into {t} values (1, 'it''s 100% \\ 한글', 12.50);\ninsert into {t} values (2, null, null);\ninsert into {t} values (3, '', 0);\ncommit;", "oranda.exec.runScript");
        try
        {
            var formatHandler = rig.Container.Create(typeof(FormatterProfilesCommandHandler));
            var runFormat = (string id) => ((ICommandHandler)formatHandler).ExecuteAsync(new CommandContext(id, FakeContextKeys.AllTrue), default);

            // 작성: 흐트러진 SQL(주석 포함) → 포맷 명령.
            var messy = $"select   id,name ,  amt   /* 금액 */ from {t}\n where   id>0   and ( name is not null or amt is null )  order by   id -- 끝";
            var baseline = await rig.RunSqlAsync(messy);
            var baselineRows = Assert.Single(baseline.Tabs).Pane!.Model!;
            await baselineRows.LoadFirstPageAsync();

            var tab = rig.Host.NewDocument(messy);
            tab.Select(0, 0);
            await runFormat("oranda.format.document");

            // 정상: 글이 바뀌었고, 줄이 나뉘었고, 낱말 순서·주석은 그대로.
            Assert.NotEqual(messy, tab.Text);
            Assert.Contains('\n', tab.Text);
            Assert.Equal(NoSpace(messy), NoSpace(tab.Text), ignoreCase: true);
            Assert.Contains("/* 금액 */", tab.Text, StringComparison.Ordinal);
            Assert.Contains("-- 끝", tab.Text, StringComparison.Ordinal);

            // 경계: 두 번 포맷해도 같다(멱등), 이미 포맷된 문서는 그대로.
            var once = tab.Text;
            await runFormat("oranda.format.document");
            Assert.Equal(once, tab.Text);

            // 실행: 포맷한 글을 같은 편집기에서 실행하면 원래 글과 같은 행이 나온다.
            tab.Select(0, 0);
            await rig.Run("oranda.exec.runStatement");
            var formatted = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
            var model = formatted.Pane!.Model!;
            await model.LoadFirstPageAsync();
            Assert.Equal(baselineRows.LoadedRowCount, model.LoadedRowCount);
            for (var r = 0; r < model.LoadedRowCount; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    Assert.Equal(baselineRows.GetCellText(r, c), model.GetCellText(r, c));
                }
            }

            Assert.Equal(2, model.LoadedRowCount); // id 3 은 name 이 ''(=NULL), amt 0 이라 조건에서 빠진다
            Assert.Equal("it's 100% \\ 한글", model.GetCellText(0, 1)); // 특수문자·한글 왕복
            Assert.Equal("(null)", model.GetCellText(1, 1)); // NULL 칸 표시

            // 되돌림: 실행 취소 한 번으로 원문이 돌아온다.
            Assert.True(tab.CanUndo);
            tab.Document.UndoStack.Undo();
            Assert.Equal(messy, tab.Text);

            // 오류 경로 1: 읽기 전용 문서는 포맷하지 않는다.
            var ro = rig.Host.NewDocument(messy);
            ro.IsReadOnly = true;
            await runFormat("oranda.format.document");
            Assert.Equal(messy, ro.Text);

            // 오류 경로 2: 문법이 깨진 글은 앱이 죽지 않고, 결과가 있으면 낱말이 그대로여야 한다(원문 유지 또는 안전한 포맷).
            var broken = "selct  *   frm   ( where";
            var bt = rig.Host.NewDocument(broken);
            await runFormat("oranda.format.document");
            Assert.Equal(NoSpace(broken), NoSpace(bt.Text), ignoreCase: true);

            // 오류 경로 3: 포맷한 글이라도 틀린 SQL 은 서버 오류 탭으로 나오고, 다음 실행은 정상이다.
            var bad = await rig.RunSqlAsync($"select * from {t}_NOPE");
            var err = Assert.Single(bad.Tabs);
            Assert.True(err.IsError);
            Assert.Contains("ORA-00942", err.Message, StringComparison.Ordinal);
            var after = await rig.RunSqlAsync($"select count(*) from {t}");
            Assert.Equal("3", await FlowRig.CellAsync(Assert.Single(after.Tabs)));
            output.WriteLine("S2-2 OK: 포맷(멱등·주석 보존·되돌리기)→실행 결과 동일, 읽기 전용·문법 오류·서버 오류 경로");
        }
        finally
        {
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    [Fact]
    public Task S2_3_Statement_scope_format_touches_only_the_current_statement() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var formatHandler = (ICommandHandler)rig.Container.Create(typeof(FormatterProfilesCommandHandler));

        var first = "select   1   from   dual";
        var second = "select    2    from   dual";
        var tab = rig.Host.NewDocument(first + ";\n\n" + second + ";");
        tab.Select(first.Length + 4, 0); // 두 번째 문장 안
        await formatHandler.ExecuteAsync(new CommandContext("oranda.format.statement", FakeContextKeys.AllTrue), default);

        Assert.StartsWith(first + ";", tab.Text, StringComparison.Ordinal); // 첫 문장은 손대지 않는다
        Assert.NotEqual(first + ";\n\n" + second + ";", tab.Text);
        Assert.Contains("select", tab.Text[(first.Length + 1)..], StringComparison.OrdinalIgnoreCase);

        // 경계: 문장이 없는 곳(빈 문서)은 아무 일도 없다.
        var empty = rig.Host.NewDocument(string.Empty);
        await formatHandler.ExecuteAsync(new CommandContext("oranda.format.statement", FakeContextKeys.AllTrue), default);
        Assert.Equal(string.Empty, empty.Text);

        // 실행: 포맷된 두 번째 문장이 그대로 실행된다.
        rig.Host.Active = tab;
        tab.Select(tab.Text.Length - 3, 0);
        await rig.Run("oranda.exec.runStatement");
        Assert.Equal("2", await FlowRig.CellAsync(Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs)));
        output.WriteLine("S2-3 OK: 현재 문장만 포맷, 빈 문서, 실행");
    });

    [Fact]
    public Task S2_4_Run_results_cover_null_dml_rollback_and_error_recovery() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, v varchar2(4000))");
        try
        {
            // DML 은 미커밋 상태가 되고, 다른 세션에는 보이지 않는다. 롤백하면 사라진다(되돌림).
            await rig.RunSqlAsync($"insert into {t} values (1, '미커밋')");
            await HeadlessHost.WaitUntil(() => rig.State.TxnPending);
            Assert.Equal("0", db.Text($"select count(*) from {t}"));
            await rig.Run("oranda.txn.rollback");
            Assert.False(rig.State.TxnPending);
            var empty = await rig.RunSqlAsync($"select count(*) from {t}");
            Assert.Equal("0", await FlowRig.CellAsync(Assert.Single(empty.Tabs)));

            // 커밋하면 다른 세션에서 보인다.
            await rig.RunSqlAsync($"insert into {t} values (2, '확정')");
            await rig.Run("oranda.txn.commit");
            Assert.Equal("확정", db.Text($"select v from {t} where id = 2"));

            // 경계: 제약 위반(PK 중복)은 오류 탭, 이전 데이터는 그대로, 세션은 계속 쓸 수 있다.
            var dup = await rig.RunSqlAsync($"insert into {t} values (2, '중복')");
            Assert.True(Assert.Single(dup.Tabs).IsError);
            Assert.Contains("ORA-00001", Assert.Single(dup.Tabs).Message, StringComparison.Ordinal);
            Assert.Equal("확정", db.Text($"select v from {t} where id = 2"));

            // 경계: 0행 SELECT, 긴 문자열(4000자), 여러 문장 스크립트.
            var none = await rig.RunSqlAsync($"select * from {t} where id = -1");
            Assert.Equal(0, Assert.Single(none.Tabs).Pane!.Model!.LoadedRowCount);
            var longText = new string('가', 1000);
            await rig.RunSqlAsync($"insert into {t} values (3, '{longText}')");
            await rig.Run("oranda.txn.commit");
            var read = await rig.RunSqlAsync($"select v from {t} where id = 3");
            Assert.Equal(longText, await FlowRig.CellAsync(Assert.Single(read.Tabs)));
            output.WriteLine("S2-4 OK: 미커밋·롤백·커밋·PK 위반·0행·긴 한글");
        }
        finally
        {
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    // ------------------------------------------------------------------------------------------
    // S3. 실행 결과 → 그리드 편집 (PK·ROWID 규칙)
    // ------------------------------------------------------------------------------------------

    private static async Task RunGrid(string id, long? row = null) =>
        await new GridEditCommandHandler().ExecuteAsync(new CommandContext(id, FakeContextKeys.AllTrue, row), default);

    [Fact]
    public Task S3_1_Edit_result_preview_decline_apply_commit_and_other_session_sees_it() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, name varchar2(50), amt number(10,2));\n" +
            $"insert into {t} values (1, 'A', 10);\ninsert into {t} values (2, 'B', 20);\ninsert into {t} values (3, 'C', 30);\ncommit;", "oranda.exec.runScript");
        try
        {
            var tab = rig.Host.NewDocument($"select id, name, amt from {t} order by id");
            tab.Select(0, 0);
            await RunGrid("oranda.exec.runForEdit");
            var item = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
            var s = item.EditSession!;
            Assert.True(s.CanEdit, s.Capability.Message);
            await item.Pane!.Model!.LoadFirstPageAsync();

            // 수정·추가·삭제를 한꺼번에: 한글·NULL 값 포함.
            Assert.True(s.SetCell(0, 1, "수정됨"));
            Assert.True(s.SetCell(1, 2, null));
            var added = s.AddRow();
            Assert.True(s.SetCell(added, 0, 4m));
            Assert.True(s.SetCell(added, 1, "새 행 'q'"));
            s.DeleteRow(2);
            Assert.True(s.IsDirty);
            var preview = s.BuildPreview();
            Assert.Contains("UPDATE", preview, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("INSERT", preview, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DELETE", preview, StringComparison.OrdinalIgnoreCase);

            // 오류/되돌림: 미리보기에서 거절하면 서버는 바뀌지 않고 변경 표시는 남는다.
            rig.Dialogs.PreviewAnswer = false;
            await RunGrid("oranda.grid.edit.apply");
            Assert.True(s.IsDirty);
            Assert.Equal("A|10", db.Text($"select name || '|' || amt from {t} where id = 1"));
            Assert.Equal("3", db.Text($"select count(*) from {t}"));

            // 「행 되돌리기」 로 한 행만 원복하면 미리보기에서 그 행이 빠진다.
            await RunGrid("oranda.grid.edit.resetRow", 0);
            Assert.DoesNotContain("수정됨", s.BuildPreview(), StringComparison.Ordinal);
            Assert.True(s.SetCell(0, 1, "수정됨"));

            // 정상: 승인 + 커밋 → 다른 세션에서 결과를 본다.
            rig.Dialogs.PreviewAnswer = true;
            await RunGrid("oranda.grid.edit.applyAndCommit");
            Assert.False(s.IsDirty);
            Assert.Equal("수정됨", db.Text($"select name from {t} where id = 1"));
            Assert.Null(db.Scalar($"select amt from {t} where id = 2"));
            Assert.Equal("새 행 'q'", db.Text($"select name from {t} where id = 4"));
            Assert.Equal("0", db.Text($"select count(*) from {t} where id = 3"));

            // 반영만(커밋 안 함): 다른 세션에는 안 보이고, 커밋 명령 뒤에 보인다.
            Assert.True(s.SetCell(1, 1, "미커밋 B"));
            await RunGrid("oranda.grid.edit.apply");
            Assert.Equal("B", db.Text($"select name from {t} where id = 2"));
            await HeadlessHost.WaitUntil(() => rig.Session.HasPendingTransaction);
            await rig.Run("oranda.txn.commit");
            Assert.Equal("미커밋 B", db.Text($"select name from {t} where id = 2"));
            output.WriteLine("S3-1 OK: F9 편집 모드·수정/추가/삭제·거절 되돌림·행 되돌리기·커밋 후 타 세션 확인");
        }
        finally
        {
            GridEditCommands.Active = null;
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    [Fact]
    public Task S3_2_Constraint_failure_marks_the_row_and_optimistic_lock_detects_other_session_changes() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, name varchar2(20));\ninsert into {t} values (1, 'A');\ninsert into {t} values (2, 'B');\ncommit;", "oranda.exec.runScript");
        try
        {
            var tab = rig.Host.NewDocument($"select id, name from {t} order by id");
            tab.Select(0, 0);
            await RunGrid("oranda.exec.runForEdit");
            var s = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs).EditSession!;
            await Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs).Pane!.Model!.LoadFirstPageAsync();

            // 오류: PK 중복 INSERT 와 너무 긴 값(VARCHAR2(10))은 행 단위로 실패 표시, 서버는 그대로.
            var dupRow = s.AddRow();
            s.SetCell(dupRow, 0, 1m);
            s.SetCell(dupRow, 1, "중복");
            rig.Dialogs.PreviewAnswer = true;
            await RunGrid("oranda.grid.edit.apply");
            Assert.Equal(GridEditRowState.Failed, s.GetRowState(dupRow));
            Assert.Contains("ORA-00001", s.GetRowFailure(dupRow), StringComparison.Ordinal);
            Assert.Equal("2", db.Text($"select count(*) from {t}"));
            s.ResetAll();
            Assert.False(s.IsDirty);

            var longRow = s.AddRow();
            s.SetCell(longRow, 0, 9m);
            s.SetCell(longRow, 1, "열글자를훌쩍넘는아주긴이름입니다");
            await RunGrid("oranda.grid.edit.apply");
            Assert.Equal(GridEditRowState.Failed, s.GetRowState(longRow));
            Assert.Equal("0", db.Text($"select count(*) from {t} where id = 9"));
            s.ResetAll();

            // 낙관적 잠금: 다른 세션이 먼저 바꾼 행은 덮어쓰지 않고 실패로 알린다.
            s.OptimisticLock = true;
            db.Exec($"update {t} set name = '남이먼저' where id = 1");
            s.SetCell(0, 1, "내가나중");
            await RunGrid("oranda.grid.edit.apply");
            Assert.Equal(GridEditRowState.Failed, s.GetRowState(0));
            Assert.Equal("남이먼저", db.Text($"select name from {t} where id = 1"));

            // 되돌림: 실패 뒤에도 세션은 정상 — 다시 불러 편집·반영이 된다.
            s.ResetAll();
            var fresh = await rig.RunSqlAsync($"select name from {t} where id = 1");
            Assert.Equal("남이먼저", await FlowRig.CellAsync(Assert.Single(fresh.Tabs)));
            output.WriteLine("S3-2 OK: PK 중복·길이 초과 행 실패 표시, 낙관적 잠금 충돌, 이후 정상");
        }
        finally
        {
            GridEditCommands.Active = null;
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    [Fact]
    public Task S3_3_Keyless_result_is_blocked_then_editable_after_adding_rowid_and_read_only_profile_blocks() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t}_PK (id number primary key, v number);\ninsert into {t}_PK values (1, 1);\ncreate table {t} (code varchar2(10), qty number);\ninsert into {t} values ('a', 1);\ninsert into {t} values ('b', 2);\ncommit;", "oranda.exec.runScript");
        try
        {
            var tab = rig.Host.NewDocument($"select code, qty from {t} order by code");
            tab.Select(0, 0);
            await RunGrid("oranda.exec.runForEdit");
            var first = Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs);
            var s = first.EditSession!;

            // PK 없는 표: 편집 불가 + 이유 + 「ROWID 추가 후 다시 실행」 제안.
            Assert.False(s.CanEdit);
            Assert.Equal(EditBlockReason.NoKeyColumns, s.Capability.Reason);
            Assert.True(s.Capability.CanAddRowId);
            Assert.False(s.SetCell(0, 1, 5m));

            // 제안대로 ROWID 를 더해 다시 실행하면 편집되고, 서버에 반영된다.
            GridEditCommands.Active = s;
            await RunGrid("oranda.grid.edit.addRowidAndRerun");
            var again = ((ResultTabsViewModel)tab.ResultPane!).Tabs.Last();
            var s2 = again.EditSession!;
            Assert.True(s2.CanEdit, s2.Capability.Message);
            await again.Pane!.Model!.LoadFirstPageAsync();
            Assert.True(s2.SetCell(0, 1, 99m));
            rig.Dialogs.PreviewAnswer = true;
            await RunGrid("oranda.grid.edit.applyAndCommit");
            Assert.Equal("99", db.Text($"select qty from {t} where code = 'a'"));
            Assert.Equal("2", db.Text($"select qty from {t} where code = 'b'"));

            // 읽기 전용 접속: PK 가 있는 표도 편집할 수 없고 이유가 「읽기 전용」 이다.
            await Task.Delay(2500); // 읽기 전용 트랜잭션은 방금 만든 표를 못 읽는다(ORA-01466, 서버 규칙)
            await rig.ConnectAsync(rig.Profile(readOnly: true));
            var rtab = rig.Host.NewDocument($"select id, v from {t}_PK");
            rtab.Select(0, 0);
            await GridEditCommands.Rerun!(null, default);
            var roTabs = ((ResultTabsViewModel?)rtab.ResultPane)?.Tabs.Select(x => x.Title + ":" + x.Message + ":" + (x.EditSession is null ? "noedit" : "edit")).ToList();
            var ro = Assert.Single(((ResultTabsViewModel)rtab.ResultPane!).Tabs).EditSession;
            Assert.True(ro is not null, "읽기 전용 접속 F9 결과: " + string.Join(" | ", roTabs ?? []) + " busy=" + rig.State.Busy + " ro=" + rig.State.ReadOnly + " logs=" + string.Join(" / ", rig.Controller.Log.Messages.TakeLast(3).Select(m => m.Text)));
            Assert.False(ro!.CanEdit);
            Assert.Equal(EditBlockReason.ReadOnlyProfile, ro.Capability.Reason);
            Assert.False(ro.SetCell(0, 1, 5m));
            Assert.Equal("1", db.Text($"select v from {t}_PK"));
            output.WriteLine("S3-3 OK: PK 없음 차단→ROWID 다시 실행→반영, 읽기 전용 접속");
        }
        finally
        {
            GridEditCommands.Active = null;
            await rig.ConnectAsync();
            await rig.RunSqlAsync($"drop table {t} purge");
            await rig.RunSqlAsync($"drop table {t}_PK purge");
        }
    });

    [Fact]
    public Task S3_4_Edit_mode_on_dual_synonym_and_view_queries_explains_instead_of_throwing() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.ConnectAsync();
        await rig.RunSqlAsync($"create table {t} (id number primary key, v number);\ninsert into {t} values (1, 1);\ncreate view {t}_V as select id, v from {t};\ncommit;", "oranda.exec.runScript");
        try
        {
            // F9(편집 모드로 실행)를 편집할 수 없는 대상(DUAL·공용 동의어 뷰·사용자 뷰)에 눌러도 예외 없이 「편집 불가」 로 설명해야 한다.
            var failures = new List<string>();
            foreach (var sql in new[] { "select dummy from dual", "select owner, table_name from all_tables", $"select id, v from {t}_V" })
            {
                var tab = rig.Host.NewDocument(sql);
                tab.Select(0, 0);
                try
                {
                    await GridEditCommands.Rerun!(null, default);
                    var item = ((ResultTabsViewModel)tab.ResultPane!).Tabs.Last();
                    if (item.EditSession is { CanEdit: true } && !sql.Contains("_V", StringComparison.Ordinal))
                    {
                        failures.Add($"{sql}: 편집 가능으로 열림");
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"{sql}: {ex.GetType().Name} {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0, string.Join(" || ", failures));

            // 이후에도 앱은 정상: 같은 세션에서 일반 실행이 된다.
            var after = await rig.RunSqlAsync("select 1 from dual");
            Assert.Equal("1", await FlowRig.CellAsync(Assert.Single(after.Tabs)));
            output.WriteLine("S3-4 OK");
        }
        finally
        {
            GridEditCommands.Active = null;
            await rig.RunSqlAsync($"drop view {t}_V");
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    // ------------------------------------------------------------------------------------------
    // S7. 파일 열기 → 실행 → 수정 저장 → 작업 공간 복원 (b143610 작업 공간·파일 동작)
    // ------------------------------------------------------------------------------------------

    [Fact]
    public Task S7_1_Open_script_file_run_edit_save_and_restore_workspace_keeps_text_and_file_binding() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var dir = Path.Combine(Path.GetTempPath(), "oranda-itest-s7-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            // 정상: BOM 있는 UTF-8 / BOM 없는 UTF-8 한글 파일을 열고 실행한다.
            var bom = Path.Combine(dir, "한글bom.sql");
            var plain = Path.Combine(dir, "plain.sql");
            await File.WriteAllTextAsync(bom, "select '가나다' as k from dual", new System.Text.UTF8Encoding(true));
            await File.WriteAllTextAsync(plain, "select '라마바' as k from dual", new System.Text.UTF8Encoding(false));
            var tab = (await rig.Host.OpenFileAsync(bom, default))!;
            var tab2 = (await rig.Host.OpenFileAsync(plain, default))!;
            Assert.False(tab.IsDirty);
            Assert.Same(tab, await rig.Host.OpenFileAsync(bom, default)); // 같은 파일을 다시 열면 그 탭으로
            rig.Host.Active = tab;
            tab.Select(0, 0);
            await rig.Run("oranda.exec.runStatement");
            Assert.Equal("가나다", await FlowRig.CellAsync(Assert.Single(((ResultTabsViewModel)tab.ResultPane!).Tabs)));
            rig.Host.Active = tab2;
            tab2.Select(0, 0);
            await rig.Run("oranda.exec.runStatement");
            Assert.Equal("라마바", await FlowRig.CellAsync(Assert.Single(((ResultTabsViewModel)tab2.ResultPane!).Tabs)));

            // 수정 → 저장: 파일에 반영되고 변경 표시가 사라진다.
            tab.LoadText("select '수정됨' as k from dual", dirty: true);
            Assert.True(tab.IsDirty);
            Assert.True(await rig.Host.SaveAsync(tab, default));
            Assert.False(tab.IsDirty);
            Assert.Contains("수정됨", await File.ReadAllTextAsync(bom), StringComparison.Ordinal);

            // 작업 공간 저장 → 복원: 탭 수·글·파일 연결·활성 탭이 같다.
            tab2.LoadText("select 'unsaved draft' from dual", dirty: true);
            var snapshot = rig.Host.BuildWorkspace();
            using var other = new SqlEditorHost();
            other.Restore(snapshot);
            Assert.Equal(2, other.Tabs.Count);
            Assert.Contains(other.Tabs, t => t.FilePath == Path.GetFullPath(bom) && t.Text.Contains("수정됨", StringComparison.Ordinal));
            Assert.Contains(other.Tabs, t => t.FilePath == Path.GetFullPath(plain) && t.Text.Contains("unsaved draft", StringComparison.Ordinal)); // 저장 안 한 글 보존
            Assert.Equal(tab2.TabId, other.Active!.TabId);

            // 오류: 파일이 없어졌을 때 복원은 예외 없이 열리고(빈 문서·변경됨), 없는 파일 열기는 오류를 던진다.
            File.Delete(plain);
            var snap2 = new SqlEditorHost();
            snap2.Restore(rig.Host.BuildWorkspace());
            Assert.Equal(2, snap2.Tabs.Count);
            snap2.Dispose();
            await Assert.ThrowsAnyAsync<IOException>(() => rig.Host.OpenFileAsync(Path.Combine(dir, "none.sql"), default));
            output.WriteLine("S7-1 OK: 한글 파일 열기·실행·저장·작업 공간 복원(저장 안 한 글 포함)·파일 없어짐");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    });

    // ------------------------------------------------------------------------------------------
    // S4. 실행 이력 → 검색 → 다시 실행
    // ------------------------------------------------------------------------------------------

    [Fact]
    public Task S4_1_History_records_runs_and_failures_search_filter_rerun_delete_clear() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        var t = NewTable();
        await rig.StartHistoryRecorderAsync();
        var profile = rig.Profile(name: "이력 시험 접속");
        await rig.ConnectAsync(profile);
        await rig.RunSqlAsync($"create table {t} (id number primary key, v varchar2(4000))");
        try
        {
            await rig.RunSqlAsync($"insert into {t} values (1, '한글 이력 abc')");
            await rig.Run("oranda.txn.commit");
            await rig.RunSqlAsync($"select * from {t}_MISSING");
            await rig.RunSqlAsync($"select v from {t} where id = 1");

            // 기록: 성공 3 + 실패 1(오류 코드·접속 이름 포함), 접속 이름·원문 보존.
            var all = await rig.WaitHistoryAsync(r => r.Count(e => e.SqlText.Contains(t, StringComparison.Ordinal)) >= 4);
            var mine = all.Where(e => e.SqlText.Contains(t, StringComparison.Ordinal)).ToList();
            var failed = Assert.Single(mine, e => e.Status == HistoryStatus.Failed);
            Assert.Contains("942", failed.ErrorCode + failed.ErrorMessage, StringComparison.Ordinal);
            Assert.All(mine, e => Assert.Equal("이력 시험 접속", e.ProfileName));
            Assert.Contains(mine, e => e.SqlText.Contains("한글 이력 abc", StringComparison.Ordinal) && e.Status == HistoryStatus.Succeeded);

            // 화면: 이 접속만 / 검색 / 상태 필터.
            using var vm = new HistoryViewModel(rig.Container);
            await vm.ReloadAsync();
            Assert.True(vm.Items.Count >= 4);
            vm.SearchText = "한글 이력";
            await vm.ReloadAsync();
            Assert.Single(vm.Items);
            vm.SearchText = string.Empty;
            vm.StatusFilter = vm.StatusFilters.First(f => f.Status == HistoryStatus.Failed);
            await vm.ReloadAsync();
            Assert.Contains(vm.Items, i => i.FullSql.Contains("_MISSING", StringComparison.Ordinal));
            Assert.All(vm.Items, i => Assert.Equal("실패", i.StatusText));
            vm.StatusFilter = vm.StatusFilters[0];
            vm.SearchText = "절대없는검색어_zzz";
            await vm.ReloadAsync();
            Assert.Empty(vm.Items); // 경계: 결과 0건

            // 다시 실행: 성공했던 INSERT 를 다시 보내면 (PK 중복으로) 실패로 보고되고, 새 이력이 한 건 더 남는다.
            vm.SearchText = "insert into " + t;
            await vm.ReloadAsync();
            var insert = Assert.Single(vm.Items);
            var before = (await rig.WaitHistoryAsync(_ => true)).Count;
            await vm.RerunAsync(insert);
            Assert.Contains("못했다", vm.StatusMessage, StringComparison.Ordinal);
            await rig.WaitHistoryAsync(r => r.Count > before);

            // 다시 실행(정상): SELECT 는 성공 메시지, 새 이력이 남는다.
            vm.SearchText = "select v from " + t;
            await vm.ReloadAsync();
            before = (await rig.WaitHistoryAsync(_ => true)).Count;
            await vm.RerunAsync(Assert.Single(vm.Items));
            Assert.Contains("다시 실행했다", vm.StatusMessage, StringComparison.Ordinal);
            await rig.WaitHistoryAsync(r => r.Count > before);

            // 지우기 / 비우기.
            vm.SearchText = "_MISSING";
            await vm.ReloadAsync();
            var one = Assert.Single(vm.Items);
            await vm.DeleteAsync([one]);
            vm.SearchText = string.Empty;
            await vm.ReloadAsync();
            Assert.DoesNotContain(vm.Items, i => i.Id == one.Id);
            await vm.ClearAsync(currentConnectionOnly: true);
            Assert.Empty(vm.Items);

            // 접속이 없으면 다시 실행은 하지 않고 이유를 알린다(되돌림 경로).
            await rig.Service.DisconnectAsync(default);
            var vm2 = new HistoryViewModel(rig.Container);
            await vm2.RerunAsync(new HistoryItem(new HistoryEntry { Id = 1, SqlText = "select 1 from dual", ProfileId = Guid.NewGuid() }));
            Assert.Contains("못했다", vm2.StatusMessage, StringComparison.Ordinal);
            vm2.Dispose();
            output.WriteLine("S4-1 OK: 이력 기록(성공·실패·접속 이름)·검색·상태 필터·0건·다시 실행(성공·실패)·삭제·비우기·접속 없음");
        }
        finally
        {
            await rig.ConnectAsync();
            await rig.RunSqlAsync($"drop table {t} purge");
        }
    });

    [Fact]
    public Task S4_2_History_text_is_redacted_and_history_off_or_skip_history_records_nothing() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.StartHistoryRecorderAsync();
        await rig.ConnectAsync();

        // 정상: 한글·긴 SQL 도 원문 그대로. 경계: 비밀번호가 들어 있는 DDL 은 가려서 남는다.
        await rig.RunSqlAsync("select '긴문장' as k from dual where 1 = 1 -- " + new string('x', 3000));
        var tag = "ORA_IT_PW_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await rig.RunSqlAsync($"create user {tag} identified by \"Sup3rSecret#x\"");
        await rig.RunSqlAsync($"drop user {tag}");
        var rows = await rig.WaitHistoryAsync(r => r.Any(e => e.SqlText.Contains(tag, StringComparison.Ordinal)));
        var createUser = rows.First(e => e.SqlText.Contains("create user", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Sup3rSecret#x", createUser.SqlText, StringComparison.Ordinal);
        output.WriteLine("S4-2 OK: 비밀번호 포함 DDL 이력에서 가림 (create user 는 권한 부족 오류여도 기록)");
    });
}
