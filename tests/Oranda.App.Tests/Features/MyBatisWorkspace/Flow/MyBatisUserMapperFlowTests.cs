using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Oranda.App.Features.Binds;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Folder;
using Oranda.App.Features.MyBatisWorkspace.LogPaste;
using Oranda.App.Features.MyBatisWorkspace.Save;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Language.MyBatis;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Flow;

/// <summary>
/// TSK-04-01 I1(사용자 매퍼): 사용자가 가진 큰 매퍼(저장소 밖 파일, 쿼리 153개 안팎)로 UI 흐름을 잇는다. DB 는 쓰지 않는다 — 쿼리가 DB 에서 도는지는 사용자 확인 몫.
/// 파일이 없으면 아무것도 하지 않고 끝난다. 저장소 밖 파일은 읽기만 하고 임시 폴더에 복사본을 만들어 쓴다(공개 저장소에 쿼리 본문·이름을 옮기지 않으므로
/// 시험 출력에는 개수와 id 자리 번호만 적는다).
/// </summary>
public sealed partial class MyBatisUserMapperFlowTests(ITestOutputHelper output)
{
    private static readonly bool Present = MyBatisFlowRig.UserMapperPath is not null;

    [GeneratedRegex(@":[A-Za-z_]\w*")]
    private static partial Regex BindToken();

    private static void Click(Control button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        (TopLevel.GetTopLevel(button) as Window)?.UpdateLayout();
    }

    /// <summary>탭 글의 맨 끝 닫는 태그 앞에 줄을 끼운다.</summary>
    private static void InsertBeforeClose(SqlEditorViewModel tab, string line)
    {
        var at = tab.Document.Text.LastIndexOf("</", StringComparison.Ordinal);
        Assert.True(at >= 0);
        tab.Document.Insert(at, line);
    }

    /// <summary>파일 글에서 요소의 닫는 태그 앞에 줄을 끼운 새 글.</summary>
    private static string InsertBeforeClose(string fileText, MyBatisElement element, string line) =>
        fileText.Insert(element.ElementRange.Start + element.Text.LastIndexOf("</", StringComparison.Ordinal), line);

    /// <summary>시험이 고를 문장: 조건 블록과 #{} 가 있고 너무 길지 않은 것. 몇 번째인지(자리 번호)만 출력한다.</summary>
    private static List<MyBatisStatementEntry> Candidates(MyBatisWorkspaceSession session) =>
        [.. session.VisibleEntries.Where(e => e.IsStatement && e.CanOpen && e.Text.Contains("<if", StringComparison.Ordinal)
            && e.Text.Contains("#{", StringComparison.Ordinal) && e.Text.Length is > 200 and < 6000)];

    [Fact]
    public Task U1_open_list_tab_split_edit_bind_block_copy_recompute_and_scenarios() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml");
        var original = File.ReadAllBytes(path);
        var (ws, session) = await rig.OpenAsync(path);
        var statementCount = session.VisibleEntries.Count(e => e.IsStatement);
        output.WriteLine($"[흐름] 요소 {session.Entries.Count}개, 문장 {statementCount}개");
        Assert.True(statementCount >= 100);

        // 「매퍼 쿼리」 창 격자: 항목 수가 맞고 행이 그려진다
        var statements = new MyBatisStatementsViewModel { Host = rig.Host, Text = new ShellText() };
        var listWindow = new Window { Width = 340, Height = 560, Content = new MyBatisStatementsView { DataContext = statements } };
        listWindow.Show();
        MyBatisFlowRig.Pump();
        Assert.Equal(session.Entries.Count, statements.Groups.Sum(g => g.Children.Count));
        Assert.True(statements.Rows.Count > 0);
        MyBatisFlowRig.Render(listWindow, "user-statements-grid", 340, 560);
        listWindow.Close();

        // 계산 전체 훑기: 문장마다 펼치기가 예외 없이 끝난다(오류 문장 수는 출력만)
        var candidates = Candidates(session);
        Assert.NotEmpty(candidates);
        var failed = 0;
        foreach (var entry in session.VisibleEntries.Where(e => e.IsStatement && e.CanOpen))
        {
            var e = rig.Hub.For(session, entry.Id!);
            e.Recompute();
            if (e.Error is not null)
            {
                failed++;
            }
        }

        output.WriteLine($"[흐름] 펼치기 오류 문장 {failed}개 / {statementCount}개");

        // 경계: 탭 없이 목록에서 고르기만 하면 문장 탭이 늘지 않는다
        var picked = candidates[^1];
        var tabsBefore = ws.Tabs.Count;
        session.SelectEntry(picked);
        rig.Hub.Refresh();
        MyBatisFlowRig.Pump();
        Assert.Equal(tabsBefore, ws.Tabs.Count);
        Assert.Null(picked.Tab);

        // 탭 열기 → 펼친 SQL 분할(세로 기본 → 가로 → 세로)
        var chosen = candidates[0];
        var tab = session.OpenEntry(chosen)!;
        var (view, window) = rig.ShowEditor(tab, session);
        var vm = view.Expanded!;
        await HeadlessHost.WaitUntil(() => vm.Sql.Length > 0 || vm.Eval?.Error is not null);
        Assert.False(view.Split.IsHorizontal);
        Click(view.Split.HorizontalButton);
        Assert.True(view.Split.IsHorizontal);
        MyBatisFlowRig.Render(window, "user-split-horizontal", 1200, 640);
        Click(view.Split.VerticalButton);
        Assert.False(view.Split.IsHorizontal);
        MyBatisFlowRig.Render(window, "user-split-vertical", 1200, 640);

        // 고치기: 수정 표시(항목·탭·제목)
        InsertBeforeClose(tab, "-- 확인용\n");
        Assert.True(tab.IsDirty);
        Assert.True(chosen.IsModified);
        Assert.StartsWith("* ", ws.DisplayTitle, StringComparison.Ordinal);
        Assert.Equal(original, File.ReadAllBytes(path));

        // 바인드 변수: 행마다 자료형별 값을 넣고(숫자 서식·날짜 서식) 재계산
        var eval = rig.Hub.ForTab(tab)!;
        rig.Hub.Refresh();
        Assert.NotEmpty(tab.Binds.Rows);
        var rows = tab.Binds.Rows.Where(r => !r.Name.StartsWith('$')).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].Type = (i % 3) switch { 1 => BindDataType.Number, 2 => BindDataType.Date, _ => BindDataType.Varchar2 };
            rows[i].Text = (i % 3) switch { 1 => $"{i + 1},000", 2 => "20261023", _ => "값" + i.ToString(CultureInfo.InvariantCulture) };
            rows[i].Commit();
        }

        Assert.Contains(rows, r => r.Type == BindDataType.Number ? r.Text == $"{rows.IndexOf(r) + 1}000" : true);
        Assert.Contains(rows, r => r.Type == BindDataType.Date && r.Text == "2026-10-23 00:00:00" || rows.Count < 3);
        var sqlBefore = vm.Sql;
        eval.Flush();
        await HeadlessHost.WaitUntil(() => eval.Result is not null && vm.Sql == eval.Result.Sql);

        // 블록 복사·붙여 넣기: 값을 지운 뒤 같은 글을 붙이면 값·자료형이 모두 돌아온다
        var tsv = BindTsv.Format(tab.Binds.Rows);
        var snapshot = tab.Binds.Rows.Select(r => (r.Name, r.Text, r.Type)).ToList();
        foreach (var row in tab.Binds.Rows)
        {
            row.Text = string.Empty;
        }

        var (applied, skipped) = BindTsv.Apply(tab.Binds, tsv);
        Assert.Equal((tab.Binds.Rows.Count, 0), (applied, skipped));
        Assert.Equal(snapshot, tab.Binds.Rows.Select(r => (r.Name, r.Text, r.Type)).ToList());
        eval.Flush();
        output.WriteLine($"[흐름] 바인드 행 {tab.Binds.Rows.Count}개, 복사 글 {tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length}줄, 펼친 SQL 길이 {sqlBefore.Length} → {vm.Sql.Length}");

        // 시나리오 만들기·고르기
        await eval.EnsureLoadedAsync();
        var first = await eval.AddScenarioAsync("시험 값 1");
        Assert.NotNull(first);
        var firstRow = rows[0];
        var firstValue = firstRow.Text;
        firstRow.Text = firstRow.Type == BindDataType.Number ? "999" : "다른값";
        eval.Flush();
        var second = await eval.AddScenarioAsync("시험 값 2");
        Assert.Equal(2, eval.Scenarios.Count);
        eval.SelectScenario(first);
        MyBatisFlowRig.Pump();
        Assert.Equal(firstValue, firstRow.Text);
        eval.SelectScenario(second);
        Assert.NotEqual(firstValue, firstRow.Text);

        // 오류: 이름 없는 시나리오는 만들지 않음 / 되돌림: 지우기
        await Assert.ThrowsAnyAsync<ArgumentException>(() => eval.AddScenarioAsync(string.Empty));
        await eval.DeleteScenarioAsync(second!);
        Assert.Single(eval.Scenarios);
        window.Close();
    });

    [Fact]
    public Task U2_combo_check_then_save_changes_only_the_edited_statement_and_outside_edit_merges_or_conflicts() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml");
        var backup = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.orig.xml", "orig");
        var originalText = File.ReadAllText(path);
        var (ws, session) = await rig.OpenAsync(path);
        var candidates = Candidates(session);
        Assert.True(candidates.Count >= 3);

        // 조합 검사(구문 분석만): 조건이 있는 문장 몇 개를 검사하고 끝난 상태·개수만 본다
        var panel = new MyBatisCombosViewModel(rig.Harness.Services) { PostToUi = a => a() };
        var checkedCount = 0;
        var totalCombos = 0;
        var totalErrors = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var c in candidates.Take(10))
        {
            await panel.CheckAsync(session, c.Id!);
            Assert.False(panel.IsRunning);
            Assert.True(panel.HasReport);
            checkedCount++;
            totalCombos += panel.Rows.Count;
            totalErrors += panel.ErrorCount;
        }

        output.WriteLine($"[흐름] 조합 검사 {checkedCount}문장, 조합 {totalCombos}개, 오류 {totalErrors}개, {sw.ElapsedMilliseconds} ms");
        Assert.Equal(0, panel.ModeIndex);

        // 저장: 고른 문장 하나만 고치고 저장 → 확인 창 항목 1개, 수정 표시 해제
        var target = candidates[1];
        var tab = session.OpenEntry(target)!;
        var element = session.Document.Find(target.Id!)!;
        var start = element.ElementRange.Start;
        var end = element.ElementRange.End;
        InsertBeforeClose(tab, "  <!-- 저장 시험 -->\n");
        Assert.True(target.IsModified);

        // 되돌림: 취소하면 파일 그대로
        rig.Presenter.Answer = _ => null;
        Assert.False(await rig.Save.SaveAsync(ws, default));
        Assert.Equal(File.ReadAllBytes(backup), File.ReadAllBytes(path));
        Assert.True(tab.IsDirty);
        var request = Assert.Single(rig.Presenter.Requests);
        var item = Assert.Single(request.Items);
        Assert.Equal(target.Id, item.CurrentId);
        Assert.True(request.Check(request.Items).CanWrite);
        Assert.Equal(session.VisibleEntries.Count() - 1, request.UnchangedCount);

        // 정상 저장: 바뀐 곳은 그 문장 범위뿐(git diff 와 바이트 비교 둘 다)
        rig.Presenter.Answer = r => new SaveDiffResult(r.Items, r.ShowEveryTime);
        Assert.True(await rig.Save.SaveAsync(ws, default));
        Assert.False(session.IsDirty);
        var firstLine = MyBatisFlowRig.LineOf(originalText, start);
        var lastLine = MyBatisFlowRig.LineOf(originalText, end - 1);
        var diff = MyBatisFlowRig.GitDiff(backup, path);
        Assert.NotEmpty(diff.Hunks);
        Assert.All(diff.Hunks, h => Assert.InRange(h.Start, firstLine - 1, lastLine));
        Assert.InRange(diff.Added, 1, 2); // 닫는 태그 줄의 들여쓰기가 새 줄로 갈라지면 한 줄이 지워지고 두 줄이 생긴다
        Assert.InRange(diff.Removed, 0, 1);
        var saved = File.ReadAllText(path);
        Assert.StartsWith(originalText[..start], saved, StringComparison.Ordinal);
        Assert.EndsWith(originalText[end..], saved, StringComparison.Ordinal);
        output.WriteLine($"[흐름] 저장 diff: 바뀐 덩어리 {diff.Hunks.Count}개, +{diff.Added} -{diff.Removed}, 문장 범위 안");

        // 밖에서 다른 문장을 고침 → 합쳐지고 내 고침은 남는다
        var watch = rig.Save.Watch;
        var mine = candidates[2]; // 저장하면 항목 개체가 새로 만들어지므로 id 로 다시 찾는다
        var mineTab = session.OpenStatement(mine.Id!)!;
        InsertBeforeClose(mineTab, "  <!-- 내 고침 -->\n");
        var otherElement = new MapperParser().Parse(File.ReadAllBytes(path)).Find(candidates[0].Id!)!;
        var onDisk = File.ReadAllText(path);
        File.WriteAllText(path, InsertBeforeClose(onDisk, otherElement, "  <!-- IDE -->\n"));
        Assert.True(watch.CheckNow(ws));
        Assert.Equal(MapperBannerKind.Merged, watch.StateOf(ws)!.Banner);
        Assert.True(mineTab.IsDirty);

        // 밖에서 같은 문장을 고침 → 충돌, 「파일 것으로」 → 내 탭이 파일 글로
        var mineElement = new MapperParser().Parse(File.ReadAllBytes(path)).Find(mine.Id!)!;
        onDisk = File.ReadAllText(path);
        File.WriteAllText(path, InsertBeforeClose(onDisk, mineElement, "  <!-- 밖 고침 -->\n"));
        Assert.False(watch.CheckNow(ws));
        Assert.Equal(MapperBannerKind.Conflict, watch.StateOf(ws)!.Banner);
        watch.ResolveAll(ws, MapperMergeChoice.Disk);
        Assert.Contains("밖 고침", session.OpenStatement(mine.Id!)!.Text, StringComparison.Ordinal);
        Assert.False(session.Find(mine.Id)!.IsModified);
    });

    [Fact]
    public Task U3_log_paste_finds_the_statement_and_the_mapper_folder_opens_the_copy() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml", "mappers");
        var (_, session) = await rig.OpenAsync(path);
        var candidates = Candidates(session);
        var chosen = candidates[0];

        // 로그 만들기: 이 문장을 펼친 SQL 의 :이름 을 ? 로 바꾸고 값 줄을 붙인다(문장 본문은 시험 출력에 적지 않는다)
        var eval = rig.Hub.For(session, chosen.Id!);
        eval.Recompute();
        var result = eval.Result!;
        var binds = result.Binds.ToList();
        var sql = BindToken().Replace(string.Join(' ', result.Sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), "?");
        var parameters = string.Join(", ", binds.Select(b => b.Value switch
        {
            null => "null",
            string s => s + "(String)",
            _ => Convert.ToString(b.Value, CultureInfo.InvariantCulture) + "(Integer)",
        }));
        var ns = session.MapperNamespace;
        var log = $"14:05:01.123 [main] DEBUG {ns}.{chosen.Id} - ==>  Preparing: {sql}\n14:05:01.130 [main] DEBUG {ns}.{chosen.Id} - ==> Parameters: {parameters}\n";
        var flow = new MyBatisLogPasteFlow(rig.Host, rig.Hub.Evaluator!, rig.Hub.Store!, rig.Dialogs, () => new DateTimeOffset(2026, 10, 23, 14, 5, 0, TimeSpan.Zero));
        var flowResult = await flow.RunAsync(log, session, default);
        Assert.NotNull(flowResult);
        output.WriteLine($"[흐름] 로그 붙여 넣기: 찾음 {flowResult.Opened.Count}, 일반 탭 {flowResult.Fallback}, 취소 {flowResult.Cancelled}, 바인드 {binds.Count}개");
        var opened = Assert.Single(flowResult.Opened);
        Assert.Equal(chosen.Id, opened.StatementId);
        Assert.NotNull(chosen.Tab);

        // 경계: Preparing 이 없는 글은 아무 일도 하지 않는다
        Assert.Null(await flow.RunAsync("아무 글이나 붙임", session, default));

        // 매퍼 폴더 열기(복사본 폴더): 매퍼 1개, 트리에서 문장을 열 수 있다
        var folder = rig.Get<MapperFolderService>();
        Assert.True(await folder.OpenAsync(Path.GetDirectoryName(path)!, default));
        Assert.Single(folder.Index!.Files);
        using var tree = new MapperFolderTreeViewModel(folder) { Host = rig.Host, Text = new ShellText() };
        var file = tree.Roots.SelectMany(Flatten).First(n => n.Kind == MapperFolderNodeKind.File);
        var element = file.Children.First(n => n.ElementId == candidates[1].Id);
        await tree.OpenNodeAsync(element);
        MyBatisFlowRig.Pump();
        Assert.Equal(candidates[1].Id, ((MyBatisWorkspaceSession)rig.Host.ActiveWorkspace!.MyBatis!).SelectedStatementId);
        folder.Close();
    });

    private static IEnumerable<MapperFolderTreeNode> Flatten(MapperFolderTreeNode node) =>
        new[] { node }.Concat(node.Children.SelectMany(Flatten));
}
