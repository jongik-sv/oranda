using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.ChangeJournal;
using Oranda.App.Tests.Shell;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Journal;
using Oranda.Core.Metadata;
using Oranda.Core.Shell;

namespace Oranda.App.Tests.Features.ChangeJournal;

// 소유: TSK-13-07. 변경 일지 창(목록·검색·차이·새 편집기 열기·진입점). 모델 + Headless.
public sealed class ChangeJournalTests
{
    private const string Db = "FREE/FREEPDB1/APP";

    private static JournalEntry Entry(long id, string ddl, string name = "EMP", string type = "TABLE", JournalCause cause = JournalCause.Ddl,
                                      string? failure = null) => new()
                                      {
                                          Id = id,
                                          CapturedAt = new DateTimeOffset(2026, 10, 20, 9, 0, (int)id, TimeSpan.Zero),
                                          ProfileId = Guid.NewGuid(),
                                          ProfileName = "개발",
                                          DbKey = Db,
                                          Owner = "APP",
                                          Name = name,
                                          ObjectType = type,
                                          Cause = cause,
                                          StatementSummary = "ALTER TABLE " + name + " ADD x NUMBER",
                                          PreviousDdl = failure is null ? ddl : null,
                                          FailureReason = failure,
                                      };

    [Fact]
    public void Panel_and_commands_are_registered()
    {
        var panel = ShellTestHelpers.RequirePanel("oranda.panel.changeJournal");
        Assert.Equal(ToolPanelKind.Tool, panel.Kind);
        Assert.Equal(typeof(ChangeJournalViewModel), panel.ViewModelType);

        var registry = ShellTestHelpers.CreateCommandRegistry();
        var open = registry.Find("oranda.view.changeJournal");
        Assert.NotNull(open);
        Assert.Contains("menu:tools/changeJournal@50", open.Placements);
        var context = registry.Find("oranda.journal.showForObject");
        Assert.NotNull(context);
        Assert.Contains("context:schema/changeJournal@60", context.Placements);
    }

    [Fact]
    public async Task Reload_lists_newest_first_and_search_filters()
    {
        var store = new ListStore([Entry(1, "CREATE TABLE emp (a NUMBER)"), Entry(2, "CREATE TABLE dept (a NUMBER)", name: "DEPT"),
                                   Entry(3, "x", cause: JournalCause.Compile, failure: "ORA-31603")]);
        using var vm = new ChangeJournalViewModel(store);

        await vm.ReloadAsync();
        Assert.Equal([3L, 2L, 1L], vm.Items.Select(i => i.Id));
        Assert.Equal("컴파일", vm.Items[0].CauseText);
        Assert.StartsWith("보관 실패", vm.Items[0].SummaryText, StringComparison.Ordinal);

        vm.SearchText = "dept";
        await vm.ReloadAsync();
        Assert.Equal([2L], vm.Items.Select(i => i.Id));

        vm.SearchText = string.Empty;
        vm.OnlySucceeded = true;
        await vm.ReloadAsync();
        Assert.Equal([2L, 1L], vm.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Object_filter_limits_to_that_object_and_clears()
    {
        var store = new ListStore([Entry(1, "a"), Entry(2, "b", name: "DEPT")]);
        using var vm = new ChangeJournalViewModel(store);

        vm.ShowObject(new DbObjectRef("APP", "DEPT", "TABLE"));
        await vm.ReloadAsync();
        Assert.Equal([2L], vm.Items.Select(i => i.Id));
        Assert.True(vm.HasObjectFilter);
        Assert.Equal("대상: APP.DEPT", vm.ObjectFilterText);

        vm.ClearObjectFilter();
        await vm.ReloadAsync();
        Assert.Equal(2, vm.Items.Count);
    }

    [Fact]
    public async Task Selecting_one_version_diffs_against_previous_and_two_versions_diff_each_other()
    {
        var store = new ListStore([Entry(1, "CREATE TABLE emp (\n  a NUMBER\n)"), Entry(2, "CREATE TABLE emp (\n  a NUMBER,\n  b NUMBER\n)"),
                                   Entry(3, "CREATE TABLE dept (\n  a NUMBER\n)", name: "DEPT")]);
        using var vm = new ChangeJournalViewModel(store);
        await vm.ReloadAsync();

        await vm.SetSelectionAsync([vm.Items.Single(i => i.Id == 2)]);
        Assert.Contains("#2", vm.DetailTitle, StringComparison.Ordinal);
        Assert.Contains("b NUMBER", vm.DetailText, StringComparison.Ordinal);
        Assert.Equal("차이: #1 → #2", vm.DiffTitle);
        Assert.Contains(vm.Diff, l => l.IsAdded && l.Text.Contains("b NUMBER", StringComparison.Ordinal));
        Assert.Contains(vm.Diff, l => l.IsRemoved && l.Text.Trim() == "a NUMBER");

        await vm.SetSelectionAsync([vm.Items.Single(i => i.Id == 1)]);
        Assert.Empty(vm.Diff);
        Assert.Contains("이전 버전이 없다", vm.DiffTitle, StringComparison.Ordinal);

        await vm.SetSelectionAsync([vm.Items.Single(i => i.Id == 2), vm.Items.Single(i => i.Id == 1)]); // 순서 무관: 옛 → 새
        Assert.Equal("차이: #1 → #2", vm.DiffTitle);

        await vm.SetSelectionAsync([vm.Items.Single(i => i.Id == 2), vm.Items.Single(i => i.Id == 3)]);
        Assert.Empty(vm.Diff);
        Assert.Contains("같은 객체", vm.DiffTitle, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_in_editor_passes_restore_script_and_never_executes()
    {
        var store = new ListStore([Entry(1, "CREATE TABLE emp (a NUMBER)"), Entry(2, "PACKAGE BODY p AS\nEND p;", name: "P", type: "PACKAGE BODY"),
                                   Entry(3, "x", failure: "ORA-31603")]);
        using var vm = new ChangeJournalViewModel(store);
        await vm.ReloadAsync();
        var opened = new List<(string Text, Guid? Profile)>();
        vm.EditorOpener = (text, profile) =>
        {
            opened.Add((text, profile));
            return true;
        };

        Assert.True(vm.OpenInEditor(vm.Items.Single(i => i.Id == 1)));
        Assert.True(vm.OpenInEditor(vm.Items.Single(i => i.Id == 2)));
        Assert.False(vm.OpenInEditor(vm.Items.Single(i => i.Id == 3))); // 보관 실패 항목은 열 DDL 이 없다.

        Assert.Equal(2, opened.Count);
        Assert.StartsWith("-- 변경 일지 #1: APP.EMP (TABLE)", opened[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("CREATE TABLE emp (a NUMBER);\n", opened[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("END p;\n/\n", opened[1].Text, StringComparison.Ordinal); // PL/SQL 소스는 / 로 끝맺는다.
        Assert.Contains("직접 실행한다", opened[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_script_keeps_server_slash_for_triggers_and_adds_terminators_otherwise()
    {
        var trigger = Entry(7, "CREATE OR REPLACE TRIGGER \"APP\".\"TRG\" BEFORE INSERT ON t BEGIN NULL; END;\n/\nALTER TRIGGER \"APP\".\"TRG\" ENABLE;", name: "TRG", type: "TRIGGER");
        var script = ChangeJournalViewModel.BuildRestoreScript(trigger);
        Assert.Equal(1, script.Split('\n').Count(l => l.Trim() == "/")); // 서버 / 하나만, 끝에 또 붙이지 않는다.
        Assert.EndsWith("ENABLE;\n", script, StringComparison.Ordinal);

        Assert.EndsWith("(a NUMBER);\n", ChangeJournalViewModel.BuildRestoreScript(Entry(8, "CREATE TABLE emp (a NUMBER)")), StringComparison.Ordinal);
        Assert.EndsWith("(a NUMBER);\n", ChangeJournalViewModel.BuildRestoreScript(Entry(9, "CREATE TABLE emp (a NUMBER);")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Window_shows_list_diff_and_opens_new_editor()
    {
        var store = new ListStore([Entry(1, "CREATE TABLE emp (\n  a NUMBER\n)"), Entry(2, "CREATE TABLE emp (\n  a NUMBER,\n  b NUMBER\n)")]);
        using var vm = new ChangeJournalViewModel(store);
        await vm.ReloadAsync();
        var opened = new List<string>();
        vm.EditorOpener = (text, _) =>
        {
            opened.Add(text);
            return true;
        };

        await vm.SetSelectionAsync([vm.Items[0]]); // 최신(#2): 이전 버전(#1)과의 차이
        Assert.NotEmpty(vm.Diff);

        await HeadlessHost.Run(() =>
        {
            var view = new ChangeJournalView { DataContext = vm };
            var window = new Window { Content = view, Width = 1100, Height = 520 };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(2, view.FindControl<ListBox>("List")!.ItemCount);
            Assert.Equal(vm.Diff.Count, view.FindControl<ItemsControl>("DiffList")!.ItemCount);
            Assert.NotNull(view.FindControl<Button>("OpenButton"));
            window.Close();
        });

        Assert.True(vm.OpenInEditor(vm.Items[0]));
        Assert.Single(opened);
    }

    private sealed class ListStore(IReadOnlyList<JournalEntry> all) : IChangeJournalStore
    {
        public Task<long?> AddAsync(JournalEntry entry, CancellationToken ct) => Task.FromResult<long?>(null);

        public Task<IReadOnlyList<JournalEntry>> SearchAsync(JournalQuery q, CancellationToken ct)
        {
            IEnumerable<JournalEntry> rows = all.OrderByDescending(e => e.Id);
            if (q.Text is { } text)
            {
                rows = rows.Where(e => e.Name.Contains(text, StringComparison.OrdinalIgnoreCase)
                    || (e.PreviousDdl ?? string.Empty).Contains(text, StringComparison.OrdinalIgnoreCase));
            }

            if (q.DbKey is { } db)
            {
                rows = rows.Where(e => e.DbKey == db);
            }

            if (q.Owner is { } owner)
            {
                rows = rows.Where(e => e.Owner == owner);
            }

            if (q.Name is { } name)
            {
                rows = rows.Where(e => e.Name == name);
            }

            if (q.ObjectType is { } type)
            {
                rows = rows.Where(e => e.ObjectType == type);
            }

            if (q.OnlySucceeded)
            {
                rows = rows.Where(e => !e.IsFailure);
            }

            return Task.FromResult<IReadOnlyList<JournalEntry>>([.. rows.Take(q.Limit)]);
        }

        public Task<JournalEntry?> GetAsync(long id, CancellationToken ct) => Task.FromResult(all.FirstOrDefault(e => e.Id == id));

        public Task PruneAsync(JournalRetention retention, CancellationToken ct) => Task.CompletedTask;
    }
}
