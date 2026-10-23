using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit.Rendering;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Editor.Highlighting;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.MyBatisWorkspace.Fakes;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.MyBatis;
using Oranda.Testing.MyBatis;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

// 「조합 검사」 창: 표·머리 줄·오른쪽 칸, 「문장」 창 배지, 빠른 고침(미리보기 뒤 적용), 시나리오로 저장, 편집기에서 보기, 취소.
public partial class ComboCheckTests : IDisposable
{
    private const string Id = "updateEmpSal";

    private readonly FakeMyBatisScenarioStore _store = new();

    public ComboCheckTests()
    {
        ComboServices.RunnerSource = () => new ComboCheckRunner(Evaluator, Language);
        ComboServices.ScenarioSource = () => _store;
    }

    public void Dispose()
    {
        ComboServices.RunnerSource = null;
        ComboServices.ScenarioSource = null;
        GC.SuppressFinalize(this);
    }

    private static FakeMyBatisWorkspaceSession Session(string text)
    {
        var session = new FakeMyBatisWorkspaceSession { MapperPath = "/tmp/EmpMapper.xml", MapperNamespace = "demo.EmpMapper" };
        session.Elements[Id] = Statement(text);
        return session;
    }

    private static MyBatisCombosViewModel Panel() => new() { PostToUi = a => a() };

    [Fact]
    public Task Detail_and_fix_preview_use_colored_code_views() => HeadlessHost.Run(async () =>
    {
        var vm = Panel();
        await vm.CheckAsync(Session(MockupUpdateEmpSal), Id);
        var view = new MyBatisCombosView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var detail = view.GetVisualDescendants().OfType<SqlCodeView>().First(c => c.Name == "DetailSqlView");
        var row = Assert.IsType<ComboRowItem>(vm.SelectedRow);
        Assert.Equal(row.SqlBefore + row.SqlError + row.SqlAfter, detail.TextEditorControl.Text);
        Assert.NotNull(detail.Colorizer);
        Assert.True(detail.TextEditorControl.IsReadOnly);
        Assert.Contains(detail.TextEditorControl.TextArea.TextView.LineTransformers, t => t is DocumentColorizingTransformer and not SqlSyntaxColorizer);

        await vm.PreviewAsync(vm.Fixes[0]);
        Dispatcher.UIThread.RunJobs();
        var fix = view.GetVisualDescendants().OfType<SqlCodeView>().First(c => c.Name == "FixPreviewView");
        Assert.Contains("<set>", fix.TextEditorControl.Text, StringComparison.Ordinal);
        Assert.Single(fix.TextEditorControl.TextArea.TextView.LineTransformers.OfType<MyBatisSyntaxColorizer>());
        Assert.Empty(fix.TextEditorControl.TextArea.TextView.LineTransformers.OfType<SqlSyntaxColorizer>());
        window.Close();
    });

    [Fact]
    public Task Table_sql_cell_is_colored_inline_and_reuses_the_token_cache() => HeadlessHost.Run(() =>
    {
        const string Sql = "SELECT /* c */ nvl(sal, 0) AS \"S\", 'x' || :name FROM emp_cache_probe WHERE n > 10";
        var box = new TextBlock();
        var window = new Window { Content = box, Width = 800, Height = 100 };
        window.Show();
        int before = SqlInlineText.Tokenizations;

        SqlInlineText.SetSql(box, Sql);
        Assert.Equal(before + 1, SqlInlineText.Tokenizations);
        var runs = box.Inlines!.OfType<Avalonia.Controls.Documents.Run>().ToList();
        Assert.Equal(Sql, string.Concat(runs.Select(r => r.Text)));
        Assert.True(runs.First(r => r.Text == "SELECT").Foreground is not null);
        Assert.True(runs.First(r => r.Text == "nvl").Foreground is not null);
        Assert.True(runs.First(r => r.Text == ":name").Foreground is not null);
        Assert.True(runs.First(r => r.Text == "'x'").Foreground is not null);
        Assert.NotEqual(runs.First(r => r.Text == "SELECT").Foreground, runs.First(r => r.Text == "'x'").Foreground);

        // 가상화가 다른 글을 썼다가 같은 글로 되돌려도 다시 렉싱하지 않는다.
        SqlInlineText.SetSql(box, "SELECT 1 FROM dual");
        int afterOther = SqlInlineText.Tokenizations;
        SqlInlineText.SetSql(box, Sql);
        Assert.Equal(afterOther, SqlInlineText.Tokenizations);
        Assert.Equal(Sql, string.Concat(box.Inlines!.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Text)));

        // 다크 테마는 다른 팔레트로 다시 칠한다.
        var lightKeyword = box.Inlines!.OfType<Avalonia.Controls.Documents.Run>().First(r => r.Text == "SELECT").Foreground;
        window.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Dispatcher.UIThread.RunJobs();
        var darkKeyword = box.Inlines!.OfType<Avalonia.Controls.Documents.Run>().First(r => r.Text == "SELECT").Foreground;
        Assert.NotEqual(lightKeyword, darkKeyword);

        SqlInlineText.SetSql(box, null);
        Assert.Empty(box.Inlines!);
        window.Close();
    });

    // ORANDA_RENDER_DIR 가 있을 때만 조합 검사 표(한 줄 SQL 색·고른 줄)와 오른쪽 칸을 라이트·다크 PNG 로 뽑는다(렌더 점검용).
    [Fact]
    public Task Render_combo_table_colors() => HeadlessHost.Run(async () =>
    {
        var vm = Panel();
        await vm.CheckAsync(Session(MockupUpdateEmpSal), Id);
        var window = new Window { Content = new MyBatisCombosView { DataContext = vm }, Width = 1300, Height = 420 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Flow.MyBatisFlowRig.Render(window, "combos-table", 1300, 420);
        await vm.PreviewAsync(vm.Fixes[0]);
        Dispatcher.UIThread.RunJobs();
        Flow.MyBatisFlowRig.Render(window, "combos-fix-preview", 1300, 420);
        window.Close();
    });

    [Fact]
    public async Task Panel_shows_the_mockup_table_badge_and_set_quick_fix()
    {
        var session = Session(MockupUpdateEmpSal);
        var vm = Panel();

        await vm.CheckAsync(session, Id);

        Assert.Equal("updateEmpSal · 조건 2개 → 조합 4개", vm.Title);
        Assert.Equal(["sal != null", "comm != null"], vm.ConditionLabels);
        Assert.Equal(4, vm.Rows.Count);
        Assert.Equal((2, 2), (vm.PassCount, vm.ErrorCount));
        Assert.False(vm.IsRunning);
        Assert.StartsWith("검사를 마쳤습니다", vm.StatusText, StringComparison.Ordinal);

        // 첫 오류 줄이 골라져 오른쪽 칸에 오류 자리가 강조된다.
        var row = Assert.IsType<ComboRowItem>(vm.SelectedRow);
        Assert.True(row.IsError);
        Assert.Equal(",", row.SqlError);
        Assert.Equal("✕ WHERE 앞 쉼표", row.ResultText);
        Assert.Equal(["켬", "끔"], row.States.Select(s => s.Text));
        Assert.StartsWith("조합 ", row.Heading, StringComparison.Ordinal);
        Assert.EndsWith("· sal 켬, comm 끔", row.Heading, StringComparison.Ordinal);
        Assert.Equal("UPDATE EMP SET SAL = :sal, WHERE EMPNO = :empNo", row.SqlLine);

        var badge = Assert.Single(new ComboStatementBadgeProvider().BadgesFor(session, Id));
        Assert.Equal("조합 오류 2", badge.Text);

        var fix = Assert.Single(vm.Fixes);
        Assert.Equal("<set> 으로 고치기", fix.ActionText);
        Assert.Equal("빠른 고침 · 줄 3–9", fix.Heading);
    }

    [Fact]
    public Task Quick_fix_previews_first_then_applies_as_one_undo_step() => HeadlessHost.Run(async () =>
    {
        var session = Session(MockupUpdateEmpSal);
        var tab = new SqlEditorViewModel(Guid.NewGuid(), MockupUpdateEmpSal);
        session.OpenableTabs[Id] = tab;
        var vm = Panel();
        await vm.CheckAsync(session, Id);

        await vm.PreviewAsync(vm.Fixes[0]);

        Assert.True(vm.IsPreviewing);
        Assert.Contains("<set>", vm.PreviewText, StringComparison.Ordinal);
        Assert.Equal("고친 뒤: 조합 4개가 모두 통과합니다.", vm.PreviewSummary);
        Assert.Equal(MockupUpdateEmpSal, tab.Text); // 미리보기는 편집기를 바꾸지 않는다

        await vm.ApplyFixCommand.ExecuteAsync(null);

        Assert.False(vm.IsPreviewing);
        Assert.Equal(MyBatisAutoFix.Apply(MockupUpdateEmpSal, MyBatisAutoFix.Find(MockupUpdateEmpSal)[0]), tab.Text);

        // 세션이 고친 원문을 돌려주면 배지는 다시 검사할 때까지 숨고, 다시 검사하면 4개 모두 통과한다.
        session.Elements[Id] = Statement(tab.Text);
        Assert.Empty(new ComboStatementBadgeProvider().BadgesFor(session, Id));
        await vm.RecheckAsync();
        Assert.Equal((4, 0), (vm.PassCount, vm.ErrorCount));
        Assert.Empty(vm.Fixes);

        tab.Document.UndoStack.Undo();
        Assert.Equal(MockupUpdateEmpSal, tab.Text);
    });

    [Fact]
    public Task Quick_fix_follows_source_changes_and_skips_read_only_statements() => HeadlessHost.Run(async () =>
    {
        var session = Session(MockupUpdateEmpSal);
        var tab = new SqlEditorViewModel(Guid.NewGuid(), MockupUpdateEmpSal);
        session.OpenableTabs[Id] = tab;
        var vm = Panel();
        await vm.CheckAsync(session, Id);

        // 검사 뒤 원문이 짧아졌다: 옛 범위가 아니라 지금 원문의 같은 제안으로 미리 본다.
        var shorter = MockupUpdateEmpSal.Replace("  UPDATE EMP\n", "UPDATE EMP\n", StringComparison.Ordinal);
        session.Elements[Id] = Statement(shorter);
        await vm.PreviewAsync(vm.Fixes[0]);
        Assert.Equal(MyBatisAutoFix.Apply(shorter, MyBatisAutoFix.Find(shorter)[0]), vm.PreviewText);

        // 미리 본 원문과 탭 원문이 다르면 바꾸지 않고 미리보기를 다시 연다.
        await vm.ApplyFixCommand.ExecuteAsync(null);
        Assert.Equal(MockupUpdateEmpSal, tab.Text);
        Assert.True(vm.IsPreviewing);

        // 읽기 전용 문장은 바꾸지 않는다.
        session.Elements[Id] = Statement(MockupUpdateEmpSal);
        await vm.PreviewAsync(vm.Fixes[0]);
        tab.IsReadOnly = true;
        await vm.ApplyFixCommand.ExecuteAsync(null);
        Assert.Equal(MockupUpdateEmpSal, tab.Text);
        Assert.Equal("읽기 전용 문장이라 고칠 수 없습니다.", vm.StatusText);
    });

    [Fact]
    public async Task Saving_a_combination_as_a_scenario_fills_values_that_reproduce_it()
    {
        var session = Session(MockupUpdateEmpSal);
        var vm = Panel();
        await vm.CheckAsync(session, Id);
        vm.SelectedRow = vm.Rows.Single(r => r.Result.States.SequenceEqual([true, false]));

        await vm.SaveScenarioCommand.ExecuteAsync(null);

        var saved = Assert.Single(_store.All);
        Assert.Equal(("/tmp/EmpMapper.xml", "demo.EmpMapper", Id), (saved.MapperPath, saved.Namespace, saved.StatementId));
        Assert.Equal(vm.SelectedRow.Result.Title, saved.Name);
        Assert.Equal(MyBatisScenarioValue.Null, saved.Values["comm"]);
        Assert.Equal("1", saved.Values["sal"].Text);
        Assert.Equal("1", saved.Values["empNo"].Text);
        Assert.Contains("저장했습니다.", vm.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("고쳐야", vm.StatusText, StringComparison.Ordinal);

        // 저장한 시나리오는 다음 검사에서 조합 뒤에 함께 검사된다.
        await vm.RecheckAsync();
        var scenarioRow = vm.Rows[^1];
        Assert.True(scenarioRow.Result.IsScenario);
        Assert.Equal("✕ WHERE 앞 쉼표", scenarioRow.ResultText);
    }

    [Fact]
    public Task Show_in_editor_opens_the_statement_at_the_cause_line() => HeadlessHost.Run(async () =>
    {
        var session = Session(MockupUpdateEmpSal);
        var tab = new SqlEditorViewModel(Guid.NewGuid(), MockupUpdateEmpSal);
        session.OpenableTabs[Id] = tab;
        var vm = Panel();
        await vm.CheckAsync(session, Id);

        vm.ShowInEditorCommand.Execute(null);

        Assert.Equal([Id], session.OpenedStatementIds);
        Assert.Equal(5, tab.CaretPosition.Line);
    });

    [Fact]
    public async Task Cancelling_from_the_panel_keeps_rows_and_sets_no_badge()
    {
        var session = Session(SelectWithIfs(8));
        var vm = Panel();
        vm.ProgressInterval = TimeSpan.Zero; // 줄마다 넘겨 둘째 줄에서 취소한다
        vm.PostToUi = a =>
        {
            a();
            if (vm.Rows.Count == 2)
            {
                vm.CancelCommand.Execute(null);
            }
        };

        await vm.CheckAsync(session, Id);

        Assert.True(vm.Report!.Cancelled);
        Assert.InRange(vm.Rows.Count, 2, 255);
        Assert.False(vm.IsRunning);
        Assert.StartsWith("취소했습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(new ComboStatementBadgeProvider().BadgesFor(session, Id));
    }

    [Fact]
    public async Task Result_rows_reach_the_ui_in_batches_and_all_arrive_in_order()
    {
        // 줄마다 UI 로 넘기면 그리기가 몰려 창이 깜박였다(2026-10-24): 조합 256개를 50 ms 간격으로 모아 넘긴다.
        var session = Session(SelectWithIfs(8));
        var vm = Panel();
        int posts = 0;
        var seen = new List<int>();
        vm.PostToUi = a =>
        {
            Interlocked.Increment(ref posts);
            a();
            lock (seen)
            {
                seen.Add(vm.ProgressDone);
            }
        };

        await vm.CheckAsync(session, Id);

        Assert.Equal(256, vm.Rows.Count);
        Assert.Equal(vm.Report!.Results.Select(r => r.Number), vm.Rows.Select(r => r.Result.Number));
        Assert.InRange(posts, 0, 256 / 4); // 예전에는 줄마다 256 번(간격 50 ms 라 느린 기계에서도 6 초 넘게 걸려야 넘는다)

        // 머리 줄 진행: 움직이는 막대 대신 끝난 줄 수 / 전체.
        Assert.Equal((256, 256), (vm.ProgressDone, vm.ProgressTotal));
        Assert.Equal("검사 중 256/256", vm.ProgressText);
        Assert.Equal(seen.Order(), seen); // 중간에 넘긴 묶음마다 진행 수가 줄지 않고 따라온다
    }

    [Fact]
    public async Task Missing_statement_or_evaluator_only_sets_the_status()
    {
        var vm = Panel();

        await vm.CheckAsync(Session(MockupUpdateEmpSal), "noSuchStatement");
        Assert.Contains("noSuchStatement", vm.StatusText, StringComparison.Ordinal);

        ComboServices.RunnerSource = () => null;
        await vm.CheckAsync(Session(MockupUpdateEmpSal), Id);
        Assert.Contains("계산기가 없습니다", vm.StatusText, StringComparison.Ordinal);
        Assert.Empty(vm.Rows);
    }
}
