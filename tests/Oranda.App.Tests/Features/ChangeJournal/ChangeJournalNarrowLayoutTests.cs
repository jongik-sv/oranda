using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Rollback;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Rollback;
using Oranda.Testing.Rollback;

namespace Oranda.App.Tests.Features.ChangeJournal;

// 소유: gc-w9-narrow NL-02·NL-03. 변경 일지를 좁게(왼쪽·오른쪽) 붙였을 때 카드 목록·세로로 쌓은 아래 상자로 바뀌는지(Headless).
public sealed class ChangeJournalNarrowLayoutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 4, 0, 0, TimeSpan.Zero);
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static RollbackStatementInfo Stmt(int seq, string summary, long rows, long point) =>
        new(seq, T0.AddMinutes(seq), DmlVerb.Update, Emp, summary, rows, new ChangePoint(point, "SCN", T0, "test"),
            RollbackScriptState.Open, null, (int)rows, false);

    private static RollbackGroup Committed(DateTimeOffset ended) => new()
    {
        ProfileId = Guid.NewGuid(),
        ProfileName = "prod-example",
        DbKey = "prod-example",
        Environment = ConnectionEnvironment.Production,
        StartedAt = ended.AddMinutes(-2),
        EndedAt = ended,
        EndKind = TransactionEndKind.Commit,
        BasePoint = new ChangePoint(48213907, "SCN", ended, "test"),
        CommitPoint = new ChangePoint(48214022, "SCN", ended, "test"),
        State = RollbackScriptState.Confirmed,
        StepCount = 1,
        RowsTotal = 1,
        HasScriptFile = true,
        Tables = [Emp],
        Statements = [Stmt(1, "UPDATE SCOTT.EMP SET SAL = 3100 WHERE EMPNO = 7788", 1, 48213907)],
    };

    private static RollbackScriptDocument Script(string ran, string undo) => new(RollbackScriptDocument.CurrentSchemaVersion, 0,
    [
        new RollbackScriptStatement(1, T0.AddMinutes(1), ran, [], [new RollbackStep(undo)], [], false, null, 1),
    ]);

    private static async Task<(ChangeJournalViewModel Vm, long CommitId, long RunId)> CreateAsync()
    {
        var store = new InMemoryRollbackStore();
        var commitId = await store.SaveAsync(Committed(T0), Script("UPDATE SCOTT.EMP SET SAL = 3100 WHERE EMPNO = 7788", "UPDATE SCOTT.EMP SET SAL = 3000 WHERE EMPNO = 7788 AND SAL = 3100"), default);
        var runId = await store.SaveAsync(
            Committed(T0.AddHours(1)) with { Origin = RollbackOrigin.RollbackRun, OriginGroupId = commitId },
            Script("UPDATE SCOTT.EMP SET SAL = 3000 WHERE EMPNO = 7788 AND SAL = 3100", "UPDATE SCOTT.EMP SET SAL = 3100 WHERE EMPNO = 7788 AND SAL = 3000"),
            default);
        var journal = new FixedJournal(
        [
            new JournalEntry
            {
                Id = 3,
                CapturedAt = T0.AddHours(-1),
                ProfileName = "dev-example",
                DbKey = "dev-example",
                Owner = "SCOTT",
                Name = "EMP_IDX1",
                ObjectType = "INDEX",
                Cause = JournalCause.Ddl,
                StatementSummary = "CREATE INDEX SCOTT.EMP_IDX1 ON SCOTT.EMP (DEPTNO)",
                PreviousDdl = "CREATE INDEX SCOTT.EMP_IDX1 ON SCOTT.EMP (EMPNO)",
            },
        ]);
        var vm = ChangeJournalViewModel.ForTests(journal, store);
        await vm.ReloadAsync();
        return (vm, commitId, runId);
    }

    [Theory]
    [InlineData("커밋", "commit")]
    [InlineData("커밋(추정)", "unknown")]
    [InlineData("DML 실행", "commit")]
    [InlineData("원복 실행(#18)", "run")]
    [InlineData("롤백", "unknown")]
    [InlineData("DDL 실행", "ddl")]
    [InlineData("컴파일", "ddl")]
    [InlineData("모름", "unknown")]
    public void Cause_badge_tone_follows_the_cause_text(string cause, string tone) => Assert.Equal(tone, JournalTone.CauseOf(cause));

    [Theory]
    [InlineData("확정 · 16문장", "ok")]
    [InlineData("12문장", "ok")]
    [InlineData("DDL 보관", "ddl")]
    [InlineData("보관 실패", "fail")]
    [InlineData("무효 · 롤백함", "none")]
    [InlineData("만들지 않음 · 개발 접속", "none")]
    public void Script_chip_tone_follows_the_state_text(string text, string tone) => Assert.Equal(tone, JournalTone.ScriptOf(text));

    [Fact]
    public async Task Confirmed_group_without_a_script_file_says_the_script_could_not_be_read()
    {
        var store = new InMemoryRollbackStore();
        var id = await store.SaveAsync(Committed(T0), null, default); // 상태는 확정인데 보관 파일이 없다
        var vm = ChangeJournalViewModel.ForTests(new FixedJournal([]), store);
        await vm.ReloadAsync();
        await vm.SetRowSelectionAsync([vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == id)]);
        Assert.Equal("원복 스크립트를 읽지 못함: 보관된 파일이 없다", vm.DiffTitle);
    }

    [Fact]
    public void Statement_chip_is_green_only_when_its_group_is_confirmed()
    {
        var confirmed = new RollbackGroupItem(Committed(T0));
        var open = new RollbackGroupItem(Committed(T0) with { EndKind = TransactionEndKind.Unknown, State = RollbackScriptState.Open });
        Assert.True(JournalTone.IsConfirmed(confirmed));
        Assert.True(JournalTone.IsConfirmed(new RollbackStatementItem(confirmed, confirmed.Group.Statements[0])));
        Assert.False(JournalTone.IsConfirmed(open));
        Assert.False(JournalTone.IsConfirmed(new RollbackStatementItem(open, open.Group.Statements[0]))); // 글은 「1문장」 이어도 묶음이 열림이면 초록이 아니다
    }

    [Fact]
    public async Task Journal_switches_to_cards_and_stacked_panes_when_narrow_and_back()
    {
        var (vm, commitId, runId) = await CreateAsync();

        await HeadlessHost.Run(async () =>
        {
            var view = new ChangeJournalView { DataContext = vm };
            var window = new Window { Content = view, Width = 800, Height = 640 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                var list = view.FindControl<ListBox>("List")!;
                Assert.False(NarrowLayout.GetIsNarrow(view));
                Assert.False(view.PanesStacked);
                Assert.True(view.FindControl<Border>("JournalHeaderBorder")!.IsVisible);
                Assert.Empty(Cards(list));

                var run = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == runId);
                vm.ToggleExpand(run);
                list.SelectedItem = vm.Rows.OfType<RollbackStatementItem>().Single(s => ReferenceEquals(s.Parent, run));
                await WaitAsync(() => vm.SelectedGroup?.Id == runId);

                window.Width = 400;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();

                Assert.True(NarrowLayout.GetIsNarrow(view));
                Assert.False(view.FindControl<Border>("JournalHeaderBorder")!.IsVisible);
                Assert.False(view.FindControl<Border>("WideToolbar")!.IsVisible);
                Assert.True(view.FindControl<Border>("NarrowToolbar")!.IsVisible);
                Assert.True(view.PanesStacked);
                Assert.Equal(GridResizeDirection.Rows, view.FindControl<GridSplitter>("PaneSplitter")!.ResizeDirection);
                Assert.True(view.FindControl<Button>("CopyDetailButton")!.IsVisible);
                Assert.Same(vm.Rows.OfType<RollbackStatementItem>().Single(s => ReferenceEquals(s.Parent, run)), list.SelectedItem);
                Assert.Equal(runId, vm.SelectedGroup?.Id);
                Assert.StartsWith("원복 스크립트: #", vm.DiffTitle, StringComparison.Ordinal);
                Assert.True(vm.Diff.Any(d => d.Text.Contains("WHERE EMPNO = 7788 AND SAL = 3000;", StringComparison.Ordinal)), string.Join(" | ", vm.Diff.Select(d => d.Text)));
                Assert.True(view.DetailEditor!.WordWrap); // 좁으면 위 상자 글을 줄바꿈
                Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled, view.FindControl<ScrollViewer>("DiffScroll")!.HorizontalScrollBarVisibility);

                // 카드: DDL 1 + 묶음 2 + 펼친 문장 1. 문장 카드는 들여 쓰고(객체 줄 없음), 원복 실행 배지는 주황 갈래.
                var cards = Cards(list);
                Assert.Equal(vm.Rows.Count, cards.Count);
                var child = cards.Single(c => c.DataContext is RollbackStatementItem);
                Assert.Contains("child", child.Classes);
                var badge = child.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("badge"));
                Assert.Contains("run", badge.Classes);
                Assert.Equal($"원복 실행(#{commitId})", ((TextBlock)badge.Child!).Text);
                var ddlCard = cards.Single(c => c.DataContext is JournalItem);
                Assert.Contains(ddlCard.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge") && b.Classes.Contains("ddl"));
                var commitCard = cards.Single(c => c.DataContext is RollbackGroupItem { Id: var id } && id == commitId);
                Assert.Contains(commitCard.GetVisualDescendants().OfType<Border>(), b => b.Classes.Contains("badge") && b.Classes.Contains("ok"));

                Oranda.App.Tests.Features.MyBatisWorkspace.Flow.MyBatisFlowRig.Render(window, "journal-narrow-400", 400, 640);
                if (Oranda.App.Tests.Manual.ManualCapture.Enabled)
                {
                    Oranda.App.Tests.Manual.ManualCapture.Save(window, "change-journal-narrow"); // 매뉴얼 그림(좁게 붙였을 때)
                }

                window.Width = 800;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Assert.False(NarrowLayout.GetIsNarrow(view));
                Assert.False(view.PanesStacked);
                Assert.Equal(GridResizeDirection.Columns, view.FindControl<GridSplitter>("PaneSplitter")!.ResizeDirection);
                Assert.False(view.DetailEditor!.WordWrap);
                Assert.Equal(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, view.FindControl<ScrollViewer>("DiffScroll")!.HorizontalScrollBarVisibility);
                Assert.False(view.FindControl<Button>("CopyDetailButton")!.IsVisible);
                Assert.Empty(Cards(list));
                Assert.Equal(runId, vm.SelectedGroup?.Id);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Narrow_journal_buttons_run_the_same_commands()
    {
        var (vm, commitId, _) = await CreateAsync();
        var calls = new List<string>();
        vm.CommandRunner = (id, _) =>
        {
            calls.Add(id);
            return Task.FromResult(true);
        };

        await HeadlessHost.Run(async () =>
        {
            var view = new ChangeJournalView { DataContext = vm };
            var window = new Window { Content = view, Width = 400, Height = 600 };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs();
                Assert.True(NarrowLayout.GetIsNarrow(view));
                var list = view.FindControl<ListBox>("List")!;
                list.SelectedItem = vm.Rows.OfType<RollbackGroupItem>().Single(g => g.Id == commitId);
                await WaitAsync(() => vm.CanOpenScript);

                view.FindControl<Button>("NarrowOpenScriptButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.FindControl<Button>("NarrowCompareButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await WaitAsync(() => calls.Count == 2);
                Assert.Equal([RollbackCommandIds.OpenScript, RollbackCommandIds.ComparePoint], calls);

                // 칩 줄의 원인 = 넓은 배치의 원인 상자와 같은 값.
                var cause = vm.CauseChoices.Single(c => c.Filter == JournalCauseFilter.Dml);
                view.FindControl<ComboBox>("NarrowCauseBox")!.SelectedItem = cause;
                Assert.Same(cause, vm.SelectedCause);
                Assert.Same(cause, view.FindControl<ComboBox>("CauseBox")!.SelectedItem);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static List<StackPanel> Cards(ListBox list) =>
        [.. list.GetVisualDescendants().OfType<StackPanel>().Where(p => p.Classes.Contains("journal-card"))];

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(done());
    }

    private sealed class FixedJournal(IReadOnlyList<JournalEntry> all) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JournalEntry>>([.. all.OrderByDescending(e => e.CapturedAt).Take(query.Limit)]);

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult(all.FirstOrDefault(e => e.Id == id));

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }
}
