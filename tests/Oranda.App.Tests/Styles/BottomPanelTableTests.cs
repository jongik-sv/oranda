using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Features.Execution;
using Oranda.App.Features.HistoryScripts;
using Oranda.App.Features.PlsqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.History;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;

namespace Oranda.App.Tests.Styles;

/// <summary>
/// TSK-13-15 아래 도구 창(메시지·실행 이력·변경 일지·컴파일 오류)의 표 모양: 심각도 아이콘 키, 촘촘한 표 목록·머리글, 아이콘 툴바.
/// </summary>
public class BottomPanelTableTests
{
    private static readonly string[] HistoryActions = ["RerunButton", "ToEditorButton", "DeleteButton", "ClearButton"];
    private static readonly string[] JournalActions = ["RefreshButton", "OpenButton"];

    [Theory]
    [InlineData(MessageKind.Info, "info", false, false)]
    [InlineData(MessageKind.Warning, "warn", true, false)]
    [InlineData(MessageKind.Error, "error", false, true)]
    public void A_message_line_names_its_severity_icon(MessageKind kind, string icon, bool warning, bool error)
    {
        var entry = new MessageEntry(DateTimeOffset.Now, kind, "x");
        Assert.Equal(icon, entry.IconKey);
        Assert.Equal(warning, entry.IsWarning);
        Assert.Equal(error, entry.IsError);
    }

    [Theory]
    [InlineData(HistoryStatus.Succeeded, "ok", false)]
    [InlineData(HistoryStatus.Failed, "error", true)]
    [InlineData(HistoryStatus.Cancelled, "cancel", false)]
    [InlineData(HistoryStatus.Denied, "lock", true)]
    public void A_history_line_names_its_status_icon(HistoryStatus status, string icon, bool problem)
    {
        var item = new HistoryItem(new HistoryEntry { Status = status, SqlText = "select 1 from dual" });
        Assert.Equal(icon, item.StatusIconKey);
        Assert.Equal(problem, item.IsProblem);
    }

    [Fact]
    public void A_journal_line_names_its_cause_icon_and_failures_win()
    {
        Assert.Equal("fileSql", new JournalItem(new JournalEntry { Cause = JournalCause.Ddl }).IconKey);
        Assert.Equal("compile", new JournalItem(new JournalEntry { Cause = JournalCause.Compile }).IconKey);
        Assert.Equal("error", new JournalItem(new JournalEntry { Cause = JournalCause.Compile, FailureReason = "권한 없음" }).IconKey);
    }

    [Fact]
    public Task Messages_show_a_severity_icon_per_line_and_color_errors() => HeadlessHost.Run(() =>
    {
        var vm = new MessagesViewModel();
        vm.Log.ClearMessages();
        vm.Log.AddMessage(MessageKind.Info, "SELECT 실행: 3행");
        vm.Log.AddMessage(MessageKind.Error, "ORA-00942: table or view does not exist");
        var view = new MessagesView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("MessageList")!;
            Assert.Contains("table", list.Classes);
            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.Equal(2, items.Count);
            Assert.All(items, i => Assert.InRange(i.Bounds.Height, 18, 28));
            Assert.All(items, i => Assert.Contains(i.GetVisualDescendants().OfType<Viewbox>(), v => v.Classes.Contains("shell-icon")));
            var error = items[1].GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "ORA-00942: table or view does not exist");
            Assert.Contains("danger-text", error.Classes);
            // 고를 수 있는 글(SelectableTextBlock)에도 색 클래스가 실제로 먹는다.
            Assert.True(window.TryFindResource("OrandaDangerForegroundBrush", window.ActualThemeVariant, out var danger));
            Assert.Same(danger, error.Foreground);
        }
        finally
        {
            window.Close();
            vm.Log.ClearMessages();
        }
    });

    [Fact]
    public Task History_has_a_column_header_aligned_with_rows_and_icon_only_actions() => HeadlessHost.Run(() =>
    {
        var vm = new HistoryViewModel();
        vm.Items.Add(new HistoryItem(new HistoryEntry { Status = HistoryStatus.Succeeded, ElapsedMs = 842, RowsAffected = 2315, SqlText = "select * from orders", ProfileName = "DEV" }));
        vm.Items.Add(new HistoryItem(new HistoryEntry { Status = HistoryStatus.Failed, ElapsedMs = 12, SqlText = "select * from x", ErrorCode = "ORA-00942", ProfileName = "DEV" }));
        var view = new HistoryView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 400 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("List")!;
            Assert.Contains("table", list.Classes);
            var items = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Assert.All(items, i => Assert.InRange(i.Bounds.Height, 18, 26));
            AssertColumnsAlign(view, view.FindControl<Grid>("HistoryHeader")!, items[0].GetVisualDescendants().OfType<Grid>().First());
            foreach (var name in HistoryActions)
            {
                Assert.IsNotType<string>(view.FindControl<Button>(name)!.Content);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Change_journal_has_a_column_header_and_icon_only_actions() => HeadlessHost.Run(() =>
    {
        var vm = new ChangeJournalViewModel();
        vm.Items.Add(new JournalItem(new JournalEntry { Owner = "S", Name = "T", ObjectType = "TABLE", Cause = JournalCause.Ddl, StatementSummary = "ALTER TABLE t ADD c NUMBER" }));
        var view = new ChangeJournalView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 500 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<ListBox>("List")!;
            Assert.Contains("table", list.Classes);
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
            Assert.InRange(item.Bounds.Height, 18, 26);
            AssertColumnsAlign(view, view.FindControl<Grid>("JournalHeader")!, item.GetVisualDescendants().OfType<Grid>().First());
            foreach (var name in JournalActions)
            {
                Assert.IsNotType<string>(view.FindControl<Button>(name)!.Content);
            }
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task Compile_errors_list_is_a_table_with_a_header() => HeadlessHost.Run(() =>
    {
        var vm = new CompileErrorsViewModel();
        vm.Items.Clear();
        vm.Items.Add(new CompileErrorItem(new CompileError(12, 5, "PLS-00201: identifier 'X' must be declared", false)));
        var view = new CompileErrorsView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 300 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Contains("table", list.Classes);
            var item = list.GetVisualDescendants().OfType<ListBoxItem>().Single();
            Assert.InRange(item.Bounds.Height, 18, 26);
            AssertColumnsAlign(view, view.FindControl<Grid>("ErrorsHeader")!, item.GetVisualDescendants().OfType<Grid>().First());
        }
        finally
        {
            window.Close();
        }
    });

    /// <summary>머리글 Grid 와 첫 줄 Grid 가 같은 곳에서 시작하고 고정 폭 열이 같다(가로 1 DIP 안).</summary>
    private static void AssertColumnsAlign(Visual view, Grid header, Grid row)
    {
        Assert.Equal(header.ColumnDefinitions.Count, row.ColumnDefinitions.Count);
        var headerX = header.TranslatePoint(default, view)!.Value.X;
        var rowX = row.TranslatePoint(default, view)!.Value.X;
        Assert.Equal(headerX, rowX, 1);
        for (var i = 0; i < header.ColumnDefinitions.Count; i++)
        {
            if (header.ColumnDefinitions[i].Width.IsAbsolute)
            {
                Assert.Equal(header.ColumnDefinitions[i].ActualWidth, row.ColumnDefinitions[i].ActualWidth, 1);
            }
        }
    }
}
