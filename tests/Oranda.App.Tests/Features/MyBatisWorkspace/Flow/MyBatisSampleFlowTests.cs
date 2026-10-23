using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Conversion;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.LogPaste;
using Oranda.App.Features.MyBatisWorkspace.Expanded;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Shell.Docking;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Oranda.Platform.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Flow;

/// <summary>
/// TSK-04-01 I1: 시안 매퍼(EmpMapper 표본)로 MyBatis 작업 공간 흐름을 처음부터 끝까지 잇는다. DB 는 쓰지 않는다(구문 분석만).
/// 흐름 하나가 시험 하나: A 열기→탭·분할→고침→바인드·재계산→시나리오, B 조합 검사→자동 고침→저장, C 밖에서 고침→충돌,
/// D 일반 작업 공간→MyBatis 바꾸기→로그 붙여 넣기→매퍼 폴더 열기. 각 흐름에 정상·경계·오류·되돌림을 둔다.
/// 앱 조립(ShellHarness)이 만든 실제 훅·계산 허브·시나리오 저장소·편집기 공장을 쓰고, 저장 확인 창만 응답기로 바꾼다.
/// </summary>
public sealed class MyBatisSampleFlowTests
{
    private const string Update = "updateEmpSal";
    private const string List = "selectEmpList";
    private const string Picked = "selectEmp";
    private const string StatementsTool = "oranda.panel.mybatisStatements";
    private const string ExpandedTool = "oranda.panel.mybatisExpanded";
    private const string SqlEditorDoc = "oranda.panel.sqlEditor";

    private static BindVariableRow RowOf(SqlEditorViewModel tab, string name) => tab.Binds.Rows.Single(r => r.Name == name);

    private static void Edit(SqlEditorViewModel tab, string from, string to)
    {
        var at = tab.Document.Text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, from);
        tab.Document.Replace(at, from.Length, to);
    }

    private static void Click(Control button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        (TopLevel.GetTopLevel(button) as Window)?.UpdateLayout();
    }

    // ---- A. 열기 → 탭 열기(분할) → 고치기 → 바인드 → 재계산 → 시나리오 --------------------------------------

    [Fact]
    public Task A_open_tab_split_edit_bind_values_recompute_and_scenarios() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var docking = rig.Get<DockWorkspace>();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var original = File.ReadAllBytes(path);
        var (ws, session) = await rig.OpenAsync(path);

        // 열기: 문장 수만큼 항목, 탭은 고른 문장만(아직 문장 탭 없음), 「매퍼 쿼리」 창이 보임
        Assert.Equal(6, session.VisibleEntries.Count(e => e.IsStatement));
        Assert.Equal(SqlWorkspaceKind.MyBatis, ws.DocumentKind);
        var statements = new MyBatisStatementsViewModel { Host = rig.Host, Text = new ShellText() };
        var statementsView = new MyBatisStatementsView { DataContext = statements };
        var listWindow = new Window { Width = 340, Height = 560, Content = statementsView };
        listWindow.Show();
        MyBatisFlowRig.Pump();
        Assert.Equal(session.Entries.Count, statements.Groups.Sum(g => g.Children.Count));
        Assert.NotEmpty(statements.Rows);
        MyBatisFlowRig.Render(listWindow, "sample-statements-grid", 340, 560);
        listWindow.Close();

        var mapperDoc = docking.OpenDocument(SqlEditorDoc, ws)!;
        docking.Activate(mapperDoc);
        MyBatisFlowRig.Pump();
        Assert.Contains(StatementsTool, docking.VisibleToolIds);

        // 경계: 탭 없이 목록에서 고르기만 하면 펼친 SQL 은 어디에도 없다(도구 창 목록에 없고 열린 문장 탭도 없음)
        var tabsBefore = ws.Tabs.Count;
        session.SelectStatement(Picked);
        rig.Hub.Refresh();
        MyBatisFlowRig.Pump();
        Assert.Equal(tabsBefore, ws.Tabs.Count);
        Assert.Null(session.Find(Picked)!.Tab);
        Assert.DoesNotContain(ExpandedTool, docking.VisibleToolIds);

        // 탭 열기 → 펼친 SQL 이 탭 안 분할에 생긴다(세로 기본), 가로 전환, 다시 세로
        var tab = session.OpenStatement(Update)!;
        Assert.Same(tab, ws.ActiveTab);
        var (view, window) = rig.ShowEditor(tab, session);
        var vm = view.Expanded!;
        await HeadlessHost.WaitUntil(() => vm.Sql.Length > 0);
        Assert.False(view.Split.IsHorizontal);
        Assert.True(view.Split.IsExpandedShown);
        Assert.Equal(Update, vm.Eval!.StatementId);
        Click(view.Split.HorizontalButton);
        Assert.True(view.Split.IsHorizontal);
        MyBatisFlowRig.Render(window, "sample-split-horizontal", 1200, 640);
        Click(view.Split.VerticalButton);
        Assert.False(view.Split.IsHorizontal);
        MyBatisFlowRig.Render(window, "sample-split-vertical", 1200, 640);

        // 고치기: 수정 표시(탭 더럽힘·항목 수정·제목 별표) — 아직 파일은 그대로
        Edit(tab, "WHERE EMPNO", "-- 확인용 주석\n WHERE EMPNO");
        Assert.True(tab.IsDirty);
        Assert.True(session.Find(Update)!.IsModified);
        Assert.True(session.IsDirty);
        Assert.StartsWith("* ", ws.DisplayTitle, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(path));

        // 바인드 변수: 행 3개(SAL·COMM·EMPNO). 값이 없으면 if 블록이 꺼져 :sal 이 펼친 SQL 에 없다
        var eval = rig.Hub.ForTab(tab)!;
        rig.Hub.Refresh();
        Assert.Equal(["COMM", "EMPNO", "SAL"], tab.Binds.Rows.Select(r => r.Name).Order());
        Assert.DoesNotContain(":sal", vm.Sql, StringComparison.Ordinal);

        // 자료형 서식: 숫자 칸 「1,500」 → 칸을 떠나면 「1500」, 못 읽는 값은 오류 표시 + 그대로
        var sal = RowOf(tab, "SAL");
        sal.Type = BindDataType.Number;
        sal.Text = "1,500";
        sal.Commit();
        Assert.Equal("1500", sal.Text);
        var bad = RowOf(tab, "COMM");
        bad.Type = BindDataType.Number;
        bad.Text = "열두개";
        bad.Commit();
        Assert.Equal("열두개", bad.Text);
        Assert.True(bad.HasValueError);
        bad.Text = string.Empty; // 되돌림: 빈 칸 = NULL 이라 오류 없음
        bad.Commit();
        Assert.False(bad.HasValueError);

        // 재계산(디바운스 뒤): SAL 이 켜지고 펼친 SQL 에 :sal 이 나타난다
        RowOf(tab, "EMPNO").Text = "7499";
        eval.Flush();
        await HeadlessHost.WaitUntil(() => vm.Sql.Contains(":sal", StringComparison.Ordinal));
        Assert.Equal(eval.Result!.Sql, vm.Sql);
        Assert.Contains(":empNo", vm.Sql, StringComparison.Ordinal);

        // 블록 복사·붙여 넣기: 복사 글(변수·값·자료형·방향)을 값을 지운 뒤 그대로 붙이면 값·자료형이 돌아온다
        var tsv = BindTsv.Format(tab.Binds.Rows, withHeader: true);
        Assert.StartsWith("변수\t값\t자료형\t방향\n", tsv, StringComparison.Ordinal);
        sal.Text = string.Empty;
        sal.Type = BindDataType.Varchar2;
        RowOf(tab, "EMPNO").Text = string.Empty;
        var (applied, skipped) = BindTsv.Apply(tab.Binds, tsv);
        Assert.Equal((3, 0), (applied, skipped));
        Assert.Equal(("1500", BindDataType.Number), (sal.Text, sal.Type));
        Assert.Equal("7499", RowOf(tab, "EMPNO").Text);
        // 경계: 모르는 변수 이름은 건너뛰고, 빈 글은 아무것도 안 바꾼다
        Assert.Equal((1, 1), BindTsv.Apply(tab.Binds, "SAL\t1600\nNOPE\t1\n"));
        Assert.Equal((0, 0), BindTsv.Apply(tab.Binds, "\n\n"));
        Assert.Equal("1600", sal.Text);
        sal.Text = "1500";
        eval.Flush();

        // 재계산 뒤 펼친 SQL 이 다시 맞다(값 바꾸기 → 다른 :sal 값)
        eval.Flush();
        await HeadlessHost.WaitUntil(() => eval.Result is not null && vm.Sql == eval.Result.Sql);
        MyBatisFlowRig.Render(window, "sample-binds-recomputed", 1200, 640);
        var bindVm = new Oranda.App.Features.Execution.BindVariablesViewModel(new Oranda.Core.Events.EventBus());
        bindVm.Store.Ensure(tab.Binds.Rows.Select(r => r.Name));
        BindTsv.Apply(bindVm.Store, BindTsv.Format(tab.Binds.Rows));
        MyBatisFlowRig.RenderControl(new Oranda.App.Features.Execution.BindVariablesView { DataContext = bindVm }, "sample-bind-pane", 520, 220);

        // 시나리오: 만들기(지금 값 저장) → 값을 바꿔 두 번째 만들기 → 첫 번째 고르면 값이 돌아온다
        await eval.EnsureLoadedAsync();
        var first = await eval.AddScenarioAsync("정상 급여");
        Assert.NotNull(first);
        sal.Text = "2500";
        eval.Flush();
        var second = await eval.AddScenarioAsync("큰 급여");
        Assert.NotNull(second);
        Assert.Equal(2, eval.Scenarios.Count);
        eval.SelectScenario(first);
        MyBatisFlowRig.Pump();
        Assert.Equal("1500", sal.Text);
        Assert.Equal(first!.Id, eval.SelectedScenario!.Id);
        eval.SelectScenario(second);
        Assert.Equal("2500", sal.Text);

        // 오류: 이름이 빈 시나리오는 만들지 않는다 / 되돌림: 지우면 목록에서 빠진다
        await Assert.ThrowsAnyAsync<ArgumentException>(() => eval.AddScenarioAsync("  "));
        await eval.DeleteScenarioAsync(second!);
        Assert.Single(eval.Scenarios);
        Assert.Equal(original, File.ReadAllBytes(path)); // 여기까지 파일은 그대로(저장 전)
        window.Close();
    });

    // ---- B. 조합 검사 → 자동 고침 → 저장(바뀌는 부분, git diff 비교) → 되돌림 -------------------------------

    [Fact]
    public Task B_combo_check_auto_fix_and_save_changes_only_the_edited_statement_lines() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var backup = rig.Write("EmpMapper.orig.xml", MapperSamples.EmpMapper, "orig");
        var (ws, session) = await rig.OpenAsync(path);
        var tab = session.OpenStatement(Update)!;
        var baseline = session.Document.Find(Update)!;
        var firstLine = MyBatisFlowRig.LineOf(MapperSamples.EmpMapper, baseline.ElementRange.Start);
        var lastLine = MyBatisFlowRig.LineOf(MapperSamples.EmpMapper, baseline.ElementRange.End - 1);

        // 조합 검사(구문 분석만): 조건 2개 → 조합 4개, 오류 2개(WHERE 앞 쉼표 · SET 뒤 비어 있음), 실행 계획은 쓰지 않는다
        var panel = new MyBatisCombosViewModel(rig.Harness.Services) { PostToUi = a => a() };
        await panel.CheckAsync(session, Update);
        Assert.Equal(4, panel.Rows.Count);
        Assert.Equal((2, 2), (panel.PassCount, panel.ErrorCount));
        Assert.Equal(0, panel.ModeIndex); // 구문 분석만
        MyBatisFlowRig.RenderControl(new MyBatisCombosView { DataContext = panel }, "sample-combo-errors", 1000, 420);
        var fix = Assert.Single(panel.Fixes);
        Assert.Equal("<set> 으로 고치기", fix.ActionText);

        // 자동 고침: 미리보기를 보고 적용 → 오류 0, 문장 원문에 <set>, 수정 표시
        await panel.PreviewAsync(fix);
        Assert.True(panel.IsPreviewing);
        await panel.ApplyFixCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("<set>", session.ElementOf(Update)!.Text, StringComparison.Ordinal);
        Assert.Equal((4, 0), (panel.PassCount, panel.ErrorCount));
        Assert.True(session.Find(Update)!.IsModified);
        Assert.Contains("<set>", tab.Text, StringComparison.Ordinal);

        // 같은 흐름에서 다른 문장은 건드리지 않았다
        Assert.False(session.Find(List)!.IsModified);

        // 저장 1(되돌림): 「바뀌는 부분」 창에서 취소 → 파일 그대로, 고침 남음
        rig.Presenter.Answer = r =>
        {
            var diffWindow = new SaveDiffWindow(new SaveDiffViewModel(r));
            diffWindow.Show();
            MyBatisFlowRig.Pump();
            MyBatisFlowRig.Render(diffWindow, "sample-save-diff", 900, 760);
            diffWindow.Close();
            return null;
        };
        Assert.False(await rig.Save.SaveAsync(ws, default));
        Assert.Equal(File.ReadAllBytes(backup), File.ReadAllBytes(path));
        Assert.True(tab.IsDirty);
        var request = Assert.Single(rig.Presenter.Requests);
        Assert.Equal("EmpMapper.xml", request.FileName);
        var item = Assert.Single(request.Items);
        Assert.Equal(Update, item.CurrentId);
        Assert.True(item.Added > 0);
        Assert.Equal(session.Entries.Count(e => !e.IsModified), request.UnchangedCount);
        Assert.True(request.Check(request.Items).CanWrite);

        // 저장 2(오류): 읽기 전용 파일 → 쓰지 못함을 알리고 고침이 그대로 남는다
        rig.Presenter.Answer = r => new SaveDiffResult(r.Items, r.ShowEveryTime);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        Assert.False(await rig.Save.SaveAsync(ws, default));
        Assert.True(tab.IsDirty);
        Assert.Equal(File.ReadAllBytes(backup), File.ReadAllBytes(path));
        File.SetAttributes(path, FileAttributes.Normal);

        // 저장 3(정상): 저장 → 수정 표시가 사라지고, git diff 로 보면 고친 문장 줄 범위 안에서만 바뀐다
        Assert.True(await rig.Save.SaveAsync(ws, default));
        Assert.False(tab.IsDirty);
        Assert.False(session.IsDirty);
        var diff = MyBatisFlowRig.GitDiff(backup, path);
        Assert.NotEmpty(diff.Hunks);
        Assert.All(diff.Hunks, h => Assert.InRange(h.Start, firstLine - 1, lastLine));
        Assert.True(diff.Added > 0);
        var reread = new Oranda.Language.MyBatis.MapperParser().Parse(File.ReadAllBytes(path));
        Assert.Contains("<set>", reread.Find(Update)!.Text, StringComparison.Ordinal);
        Assert.Equal(MapperSamples.EmpMapper.Replace(baseline.Text, reread.Find(Update)!.Text, StringComparison.Ordinal), File.ReadAllText(path));

        // 경계: 저장한 뒤 다시 검사하면 오류 0, 빠른 고침 제안 없음
        await panel.CheckAsync(session, Update);
        Assert.Equal((4, 0), (panel.PassCount, panel.ErrorCount));
        Assert.Empty(panel.Fixes);
    });

    // ---- C. 밖에서 같은 파일 고침 → 충돌 처리 --------------------------------------------------------------

    [Fact]
    public Task C_an_outside_edit_merges_when_other_statements_change_and_asks_when_the_same_statement_changes() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper);
        var (ws, session) = await rig.OpenAsync(path);
        var watch = rig.Save.Watch;
        var tab = session.OpenStatement("countEmp")!;
        var other = session.OpenStatement("deleteEmp")!;

        // 내가 countEmp 를 고쳤고, 밖(IDE)에서 deleteEmp 를 고쳤다 → 합쳐진다(안 고친 탭은 파일 것으로, 내 것은 남김)
        Edit(tab, "COUNT(*)", "COUNT(EMPNO)");
        File.WriteAllText(path, MapperSamples.EmpMapper.Replace("DELETE FROM EMP WHERE", "DELETE FROM EMP E WHERE", StringComparison.Ordinal));
        Assert.True(watch.CheckNow(ws));
        Assert.Equal(MapperBannerKind.Merged, watch.StateOf(ws)!.Banner);
        Assert.Contains("COUNT(EMPNO)", tab.Text, StringComparison.Ordinal);
        Assert.Contains("DELETE FROM EMP E WHERE", other.Text, StringComparison.Ordinal);
        Assert.True(tab.IsDirty);

        // 이번엔 밖에서 같은 countEmp 를 다르게 고쳤다 → 충돌(띠), 저장은 막힌다
        File.WriteAllText(path, MapperSamples.EmpMapper.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        Assert.False(watch.CheckNow(ws));
        var state = watch.StateOf(ws)!;
        Assert.Equal(MapperBannerKind.Conflict, state.Banner);
        Assert.Equal(["countEmp"], state.Conflicts.Select(c => c.Key));
        var conflict = Assert.Single(state.Conflicts);
        Assert.Contains("COUNT(1)", conflict.DiskText, StringComparison.Ordinal);
        Assert.Contains("COUNT(EMPNO)", conflict.MineText, StringComparison.Ordinal);

        // 오류/경계: 파일이 사라졌다 → 「없음」 띠, 고침은 남는다. 다시 만들면(기준으로 돌아오면) 띠가 지워진다
        File.Delete(path);
        Assert.False(watch.CheckNow(ws));
        Assert.Equal(MapperBannerKind.Missing, state.Banner);
        Assert.True(tab.IsDirty);
        File.WriteAllText(path, MapperSamples.EmpMapper.Replace("COUNT(*)", "COUNT(1)", StringComparison.Ordinal));
        Assert.False(watch.CheckNow(ws));
        Assert.Equal(MapperBannerKind.Conflict, state.Banner);

        // 되돌림: 「내 것으로」 → 내 고침이 남고 저장이 되며, 파일에 내 글이 들어간다
        watch.ResolveAll(ws, MapperMergeChoice.Mine);
        Assert.Contains("COUNT(EMPNO)", tab.Text, StringComparison.Ordinal);
        rig.Presenter.Answer = r => new SaveDiffResult(r.Items, r.ShowEveryTime);
        Assert.True(await rig.Save.SaveAsync(ws, default));
        Assert.Contains("COUNT(EMPNO)", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(session.IsDirty);

        // 다시 한 번: 충돌 뒤 「파일 것으로」 → 내 탭 글이 파일 글로 바뀌고 수정 표시가 사라진다
        Edit(tab, "COUNT(EMPNO)", "COUNT(DEPTNO)");
        File.WriteAllText(path, File.ReadAllText(path).Replace("COUNT(EMPNO)", "COUNT(ENAME)", StringComparison.Ordinal));
        Assert.False(watch.CheckNow(ws));
        watch.ResolveAll(ws, MapperMergeChoice.Disk);
        Assert.Contains("COUNT(ENAME)", session.OpenStatement("countEmp")!.Text, StringComparison.Ordinal);
        Assert.False(session.IsDirty);
    });

    // ---- D. 일반 작업 공간 → MyBatis 바꾸기 → 로그 붙여 넣기로 문장 찾기 ------------------------------------

    private const string Logger = "com.acme.hr.EmpMapper";

    private const string SelectEmpListSql =
        "SELECT E.EMPNO, E.ENAME, E.JOB, E.SAL, D.DNAME FROM EMP E JOIN DEPT D ON D.DEPTNO = E.DEPTNO "
        + "WHERE E.EMPNO = ? AND E.ENAME LIKE '%' || ? || '%' AND E.DEPTNO IN ( ? , ? ) ORDER BY E.ENAME";

    private static string LogPair(string logger, string sql, string parameters) =>
        $"14:05:01.123 [main] DEBUG {logger} - ==>  Preparing: {sql}\n14:05:01.130 [main] DEBUG {logger} - ==> Parameters: {parameters}\n";

    private static Func<ConvertDialogViewModel, Task<ConvertDialogResult?>> Accept(ConvertTargetKind target, string path) => async vm =>
    {
        vm.Target = target;
        vm.Path = path;
        vm.Namespace = "demo.FlowMapper";
        await vm.ExistingReadTask;
        foreach (var line in vm.Lines.Where(l => l.IdError is not null))
        {
            line.Id += "New";
        }

        return vm.TryBuildResult();
    };

    [Fact]
    public Task D_plain_workspace_converts_to_a_mapper_then_a_pasted_log_finds_the_statement() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var host = rig.Host;
        var plain = host.EnsureWorkspace();
        var select = host.NewDocument("SELECT * FROM EMP WHERE DEPTNO = :deptNo");
        select.Title = "사원 목록";
        select.Binds.Add("DEPTNO").Type = BindDataType.Number;
        select.Binds.Find("DEPTNO")!.Text = "10";
        var update = host.NewDocument("UPDATE EMP SET SAL = :sal WHERE EMPNO = :empNo");
        update.Title = "급여 바꾸기";
        var before = plain.Tabs.Select(t => (t.Title, t.Text)).ToList();
        var store = rig.Hub.Store!;
        var target = Path.Combine(rig.Root, "conv", "FlowMapper.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var writer = new ConvertWriter(new MapperFileWriter(), store);

        // MyBatis 바꾸기(새 매퍼 만들기): 파일이 써지고 매퍼 작업 공간으로 열리며 원래 작업 공간은 그대로
        await MyBatisConvertCommands.RunAsync(host, plain, Accept(ConvertTargetKind.NewFile, target), writer, null, null, default);
        Assert.Empty(rig.Dialogs.Messages);
        var doc = new MapperParser().Parse(await File.ReadAllBytesAsync(target));
        Assert.Equal("demo.FlowMapper", doc.MapperNamespace);
        Assert.Equal([MyBatisElementKind.Select, MyBatisElementKind.Update], doc.Elements.Select(e => e.Kind));
        Assert.Equal(before, plain.Tabs.Select(t => (t.Title, t.Text)).ToList());
        Assert.Null(plain.MyBatis);
        var converted = host.Workspaces.Single(w => string.Equals(w.SourcePath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase));
        var convertedSession = Assert.IsType<MyBatisWorkspaceSession>(converted.MyBatis);
        Assert.Contains(await store.ListAsync(Path.GetFullPath(target), "demo.FlowMapper", doc.Elements[0].Id!, default), s => s.Values["deptNo"].Text == "10");

        // 오류/경계: 저장 안 한 고침이 있는 열린 매퍼에 덧붙이기는 막고 파일은 그대로
        convertedSession.OpenStatement(doc.Elements[0].Id!)!.Document.Insert(0, "-- 고침\n");
        var bytes = await File.ReadAllBytesAsync(target);
        await MyBatisConvertCommands.RunAsync(host, plain, Accept(ConvertTargetKind.ExistingMapper, target), writer, null, null, default);
        Assert.Single(rig.Dialogs.Messages);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(target));

        // 로그 붙여 넣기: 시안 매퍼를 열고 selectEmpList 로그를 붙이면 문장 탭이 열리고 시나리오가 저장된다
        var path = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper, "log");
        var (_, emp) = await rig.OpenAsync(path);
        var flow = new MyBatisLogPasteFlow(host, rig.Hub.Evaluator!, store, rig.Dialogs, () => new DateTimeOffset(2026, 10, 23, 14, 5, 0, TimeSpan.Zero));
        var result = await flow.RunAsync(LogPair(Logger + "." + List, SelectEmpListSql, "7499(Integer), AL(String), 10(Integer), 30(Integer)"), emp, default);
        var opened = Assert.Single(result!.Opened);
        Assert.Equal((Path.GetFullPath(path), List, "로그 14:05"), (Path.GetFullPath(opened.MapperPath), opened.StatementId, opened.ScenarioName));
        Assert.Equal(List, emp.SelectedStatementId);
        Assert.NotNull(emp.Find(List)!.Tab);
        var scenario = (await store.ListAsync(Path.GetFullPath(path), emp.MapperNamespace, List, default)).Single(s => s.Name == "로그 14:05");
        Assert.Equal("7499", scenario.Values["empNo"].Text);
        Assert.Equal(["10", "30"], scenario.Values["deptIds"].Items);
        Assert.Equal(0, result.Fallback);

        // 경계: Preparing 이 없는 글은 아무 일도 하지 않고 null
        Assert.Null(await flow.RunAsync("그냥 메모입니다\n두 번째 줄", emp, default));
        // 오류: 어느 문장과도 안 맞는 SQL 은 일반 SQL 탭 붙여 넣기로 가고 매퍼는 그대로
        var odd = await flow.RunAsync(LogPair(Logger + ".nope", "SELECT COUNT(*) FROM NO_SUCH_TABLE WHERE X = ? AND Y = ?", "1(Integer), 2(Integer)"), emp, default);
        Assert.Equal(1, odd!.Fallback);
        Assert.Empty(odd.Opened);
        Assert.False(emp.IsDirty);
    });

    // ---- E. 매퍼 폴더 열기 ------------------------------------------------------------------------------

    [Fact]
    public Task E_opening_a_mapper_folder_lists_files_and_opens_a_statement_from_the_tree() => Ui.Run(async () =>
    {
        using var rig = MyBatisFlowRig.Create();
        var hr = rig.Write("EmpMapper.xml", MapperSamples.EmpMapper, "hr");
        rig.Write("DeptMapper.xml", "<mapper namespace=\"com.acme.hr.DeptMapper\"><select id=\"selectDept\">SELECT 1 FROM DUAL</select></mapper>", "hr");
        rig.Write("notMapper.xml", "<beans/>", "cfg");
        var folder = rig.Get<MapperFolderService>();

        // 오류: 없는 폴더는 열리지 않고 상태가 그대로
        Assert.False(await folder.OpenAsync(Path.Combine(rig.Root, "nowhere"), default));
        Assert.False(folder.IsOpen);

        // 정상: 폴더 훑기 → 매퍼 2개, 매퍼가 아닌 xml 은 거름
        Assert.True(await folder.OpenAsync(rig.Root, default));
        Assert.Equal(2, folder.Index!.Files.Count);
        Assert.Equal(1, folder.Index.SkippedCount);
        using var tree = new MapperFolderTreeViewModel(folder) { Host = rig.Host, Text = new ShellText() };
        var emp = tree.Roots.Single(n => n.Name == "hr").Children.Single(n => n.Name == "EmpMapper.xml");
        await tree.OpenNodeAsync(emp.Children.Single(n => n.Name == Update));
        MyBatisFlowRig.Pump();
        var session = (MyBatisWorkspaceSession)rig.Host.ActiveWorkspace!.MyBatis!;
        Assert.Equal((Update, Path.GetFullPath(hr)), (session.SelectedStatementId, Path.GetFullPath(session.MapperPath)));

        // 되돌림: 폴더를 닫으면 트리가 비고 열린 매퍼는 그대로
        folder.Close();
        MyBatisFlowRig.Pump();
        Assert.False(folder.IsOpen);
        Assert.Empty(tree.Roots);
        Assert.Same(session, rig.Host.ActiveWorkspace!.MyBatis);
    });
}
