using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.ObjectDetail;
using Oranda.App.Shell.Controls;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Connection;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.ObjectDetail;

/// <summary>
/// TSK-13-34 객체 상세: C안 격자 표(D4) · 행 선택·복사(D2) · 인덱스·제약을 고르면 그 열(D3).
/// 가짜 메타데이터·키 제공자로 돈다(Oracle 없음). 실제 서버 확인은 <c>ObjectDetailKeyColumnsIntegrationTests</c>.
/// </summary>
public class ObjectDetailKeyColumnsTests
{
    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static RawInputModifiers Primary
        => Application.Current!.PlatformSettings!.HotkeyConfiguration.CommandModifiers.HasFlag(KeyModifiers.Meta) ? RawInputModifiers.Meta : RawInputModifiers.Control;

    private static readonly DetailSection Indexes = new("인덱스", ["이름", "유일", "종류", "열"],
    [
        ["EMP_PK", "UNIQUE", "NORMAL", "EMPNO"],
        ["EMP_NAME_DESC_IX", "NONUNIQUE", "NORMAL", "SYS_NC00009$, EMPNO"],
        ["EMP_UPPER_IX", "NONUNIQUE", "FUNCTION-BASED NORMAL", "SYS_NC00010$"],
        ["\"한글_인덱스\"", "NONUNIQUE", "NORMAL", "이름"],
    ]);

    private static readonly DetailSection Constraints = new("제약", ["이름", "종류", "열", "참조", "조건", "사용"],
    [
        ["EMP_PK", "PRIMARY KEY", "EMPNO", null, null, "ENABLED"],
        ["EMP_DEPT_FK", "FOREIGN KEY", "DEPT_ID, DEPT_SEQ", "HR.DEPT", null, "ENABLED"],
    ]);

    private static (ObjectDetailViewModel Vm, FakeKeys Keys) Create(FakeKeys? keys = null, bool withKeys = true)
    {
        keys ??= new FakeKeys();
        var vm = new ObjectDetailViewModel(new FakeMeta(), withKeys ? keys : null);
        return (vm, keys);
    }

    private static async Task<ObjectDetailViewModel> OpenAsync(ObjectDetailViewModel vm, string section)
    {
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        vm.SelectedSection = vm.Sections.Single(s => s.Title == section);
        return vm;
    }

    private static IReadOnlyList<object?> Row(ObjectDetailViewModel vm, string name) =>
        vm.SelectedSection!.Rows.First(r => Equals(r[0], name));

    // ---- D3 ViewModel ------------------------------------------------------------------------------

    [Fact]
    public async Task Index_row_shows_position_column_or_expression_and_order_and_fixes_the_hidden_column_names_above()
    {
        var (vm, keys) = Create();
        await OpenAsync(vm, "인덱스");
        Assert.True(vm.HasKeyColumns);
        Assert.Contains("인덱스를 고르면", vm.KeyStatus, StringComparison.Ordinal);
        Assert.Null(vm.KeyColumns);

        await vm.SelectKeyRowAsync(Row(vm, "EMP_NAME_DESC_IX"));
        Assert.Equal(["#", "열/식", "정렬"], vm.KeyColumns!.Columns);
        Assert.Equal([[1, "ENAME", "DESC"], [2, "EMPNO", "ASC"]], vm.KeyColumns.Rows.Select(r => r.ToArray()));
        Assert.Equal("ENAME DESC, EMPNO", Row(vm, "EMP_NAME_DESC_IX")[3]); // 숨은 SYS_NC 이름 대신
        Assert.Equal(("SCOTT.EMP", "EMP_NAME_DESC_IX"), keys.Calls[^1]);
        Assert.Contains("열 2개", vm.KeyStatus, StringComparison.Ordinal);

        await vm.SelectKeyRowAsync(Row(vm, "EMP_UPPER_IX"));
        Assert.Equal([[1, "UPPER(\"ENAME\")", "ASC"]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));
        Assert.Equal("UPPER(\"ENAME\")", Row(vm, "EMP_UPPER_IX")[3]);

        // 한글·따옴표 이름도 그대로 넘긴다.
        await vm.SelectKeyRowAsync(Row(vm, "\"한글_인덱스\""));
        Assert.Equal("\"한글_인덱스\"", keys.Calls[^1].Name);
        Assert.Equal([[1, "이름", "ASC"]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public async Task Constraint_row_shows_columns_and_for_a_foreign_key_the_referenced_table_and_columns()
    {
        var (vm, _) = Create();
        await OpenAsync(vm, "제약");
        Assert.True(vm.HasKeyColumns);

        await vm.SelectKeyRowAsync(Row(vm, "EMP_DEPT_FK"));
        Assert.Equal(["#", "열", "참조 표", "참조 열"], vm.KeyColumns!.Columns);
        Assert.Equal([[1, "DEPT_ID", "HR.DEPT", "ID"], [2, "DEPT_SEQ", "HR.DEPT", "SEQ"]], vm.KeyColumns.Rows.Select(r => r.ToArray()));

        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Equal([[1, "EMPNO", null, null]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));
    }

    [Fact]
    public async Task Same_key_is_read_once_and_changing_section_or_object_clears_the_table()
    {
        var (vm, keys) = Create();
        await OpenAsync(vm, "인덱스");
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        await vm.SelectKeyRowAsync(Row(vm, "EMP_UPPER_IX"));
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Equal(2, keys.Calls.Count); // 두 번째 EMP_PK 는 캐시
        Assert.Equal([[1, "EMPNO", "ASC"]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));

        vm.SelectedSection = vm.Sections.Single(s => s.Title == "열");
        Assert.False(vm.HasKeyColumns);
        Assert.Null(vm.KeyColumns);
        Assert.Equal(string.Empty, vm.KeyStatus);

        // 다른 객체를 열면 캐시도 버린다.
        await OpenAsync(vm, "인덱스");
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Equal(3, keys.Calls.Count);
    }

    [Fact]
    public async Task Choosing_another_row_while_reading_cancels_the_first_and_ignores_its_late_result()
    {
        var keys = new FakeKeys { Gate = new TaskCompletionSource() };
        var (vm, _) = Create(keys);
        await OpenAsync(vm, "인덱스");

        var first = vm.SelectKeyRowAsync(Row(vm, "EMP_NAME_DESC_IX"));
        Assert.Contains("읽는 중", vm.KeyStatus, StringComparison.Ordinal);
        var firstToken = keys.Tokens[^1];
        keys.Gate = null;
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.True(firstToken.IsCancellationRequested);
        await first;

        Assert.Equal([[1, "EMPNO", "ASC"]], vm.KeyColumns!.Rows.Select(r => r.ToArray()));
        Assert.Equal("SYS_NC00009$, EMPNO", Row(vm, "EMP_NAME_DESC_IX")[3]); // 늦게 온(취소된) 결과로 위 표를 고치지 않는다
        Assert.DoesNotContain("읽는 중", vm.KeyStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Late_result_for_a_previous_object_is_dropped_even_if_the_provider_ignores_cancellation()
    {
        var keys = new FakeKeys { Gate = new TaskCompletionSource(), IgnoreCancel = true };
        var (vm, _) = Create(keys);
        await OpenAsync(vm, "인덱스");
        var pending = vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));

        await vm.OpenAsync(new FakeSession(), new DbObjectRef("SCOTT", "DEPT", "TABLE"), showDdl: false);
        keys.Gate.SetResult();
        await pending;

        Assert.Null(vm.KeyColumns);
        Assert.Equal(string.Empty, vm.KeyStatus);
    }

    [Fact]
    public async Task Denied_and_failed_reads_say_so_and_leave_the_table_empty()
    {
        var (vm, keys) = Create(new FakeKeys { Denied = true });
        await OpenAsync(vm, "인덱스");
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Contains("권한이 없어", vm.KeyStatus, StringComparison.Ordinal);
        Assert.Empty(vm.KeyColumns!.Rows);

        var (vm2, _) = Create(new FakeKeys { Fail = new InvalidOperationException("ORA-01652: unable to extend") });
        await OpenAsync(vm2, "제약");
        await vm2.SelectKeyRowAsync(Row(vm2, "EMP_PK"));
        Assert.Contains("열을 읽지 못했다", vm2.KeyStatus, StringComparison.Ordinal);
        Assert.Contains("ORA-01652", vm2.KeyStatus, StringComparison.Ordinal);
        Assert.Null(vm2.KeyColumns);

        // 권한 없음은 캐시하지 않는다(권한을 받은 뒤 다시 고르면 다시 읽는다).
        var calls = keys.Calls.Count;
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Equal(calls + 1, keys.Calls.Count);
    }

    [Fact]
    public async Task Without_the_key_provider_the_column_table_is_hidden_and_selecting_does_nothing()
    {
        var (vm, keys) = Create(withKeys: false);
        await OpenAsync(vm, "인덱스");
        Assert.False(vm.HasKeyColumns);
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        Assert.Null(vm.KeyColumns);
        Assert.Empty(keys.Calls);
    }

    [Fact]
    public async Task Row_from_another_section_is_ignored_and_quick_moves_are_debounced()
    {
        var (vm, keys) = Create();
        await OpenAsync(vm, "인덱스");
        var indexRow = Row(vm, "EMP_PK");
        vm.SelectedSection = vm.Sections.Single(s => s.Title == "제약");
        await vm.SelectKeyRowAsync(indexRow); // 늦게 온 인덱스 구역 알림
        Assert.Empty(keys.Calls);
        Assert.Null(vm.KeyColumns);

        // 기다리는 동안 다음 줄로 옮기면 앞 줄은 서버에 묻지 않는다.
        vm.KeyReadDelay = TimeSpan.FromMilliseconds(300);
        var first = vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        var second = vm.SelectKeyRowAsync(Row(vm, "EMP_DEPT_FK"));
        await Task.WhenAll(first, second);
        Assert.Equal([("SCOTT.EMP", "EMP_DEPT_FK")], keys.Calls);
        Assert.Equal(4, vm.KeyColumns!.Columns.Count);
    }

    [Fact]
    public async Task Empty_or_null_key_name_resets_to_the_hint()
    {
        var (vm, keys) = Create();
        await OpenAsync(vm, "인덱스");
        await vm.SelectKeyRowAsync(Row(vm, "EMP_PK"));
        await vm.SelectKeyRowAsync(null); // 현재 줄 없음
        Assert.Null(vm.KeyColumns);
        Assert.Contains("인덱스를 고르면", vm.KeyStatus, StringComparison.Ordinal);
        await vm.SelectKeyRowAsync([null, "x"]); // 표에 없는 줄은 무시
        Assert.Null(vm.KeyColumns);
        Assert.Single(keys.Calls);
    }

    // ---- D2·D4 공유 표(DetailTable)에 더한 것 -------------------------------------------------------

    private static (Window Window, DetailTable Table) Show(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows, bool keep = false, bool numbers = true)
    {
        var table = new DetailTable { Columns = columns, Rows = rows, KeepSelectionOnSameShape = keep, RowNumbers = numbers };
        var window = new Window { Content = table, Width = 800, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, table);
    }

    private static void Click(Window w, DetailTable t, int row, RawInputModifiers mods = RawInputModifiers.None)
    {
        var item = t.List.GetVisualDescendants().OfType<ListBoxItem>().ElementAt(row);
        var p = item.TranslatePoint(new Point(40 + row, item.Bounds.Height / 2), w)!.Value;
        w.MouseDown(p, MouseButton.Left, mods);
        w.MouseUp(p, MouseButton.Left, mods);
        Dispatcher.UIThread.RunJobs();
    }

    private static readonly string[] Cols = ["#", "이름", "형식"];

    private static readonly IReadOnlyList<object?>[] Data =
    [
        [1, "ID", "NUMBER"],
        [2, "NAME", "VARCHAR2(50)"],
        [3, "CREATED", null],
    ];

    private static List<string?> HeadTexts(DetailTable t) =>
        t.List.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("row-head"))
            .Select(b => b.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(x => !x.Classes.Contains("grid-c-marker"))?.Text).ToList();

    [Fact]
    public Task Row_numbers_can_be_turned_off_when_the_table_has_its_own_number_column() => HeadlessHost.Run(() =>
    {
        var (w, t) = Show(Cols, Data, numbers: false);
        try
        {
            Assert.Equal(["", "", ""], HeadTexts(t));
            Click(w, t, 1);
            Assert.Contains(t.List.GetVisualDescendants().OfType<TextBlock>(), b => b.Classes.Contains("grid-c-marker") && b.Text == "▶"); // ▶ 는 그대로

            t.RowNumbers = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["1", "2", "3"], HeadTexts(t));
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Current_row_changed_fires_on_click_and_keyboard_but_not_twice_for_the_same_row() => HeadlessHost.Run(() =>
    {
        var (w, t) = Show(Cols, Data);
        var seen = new List<string?>();
        t.CurrentRowChanged += (_, r) => seen.Add(r[1] as string);
        try
        {
            Click(w, t, 0);
            Click(w, t, 0);
            Click(w, t, 2, RawInputModifiers.Shift);
            w.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            w.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["ID", "CREATED", "NAME"], seen);
            Assert.Equal(1, t.CurrentRowIndex);
        }
        finally
        {
            w.Close();
        }
    });

    [Fact]
    public Task Keep_selection_on_same_shape_keeps_picked_and_current_rows_when_only_values_change() => HeadlessHost.Run(() =>
    {
        var (w, t) = Show(Cols, Data, keep: true);
        var fired = 0;
        t.CurrentRowChanged += (_, _) => fired++;
        try
        {
            Click(w, t, 0);
            Click(w, t, 2, Primary);
            fired = 0;

            t.Rows = [Data[0], [2, "NAME", "VARCHAR2(80)"], Data[2]];
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["ID", "CREATED"], t.SelectedRows.Select(r => r[1]));
            Assert.Equal("CREATED", t.CurrentRow?[1]);
            Assert.Equal(0, fired); // 값 고침은 「현재 행이 바뀜」이 아니다
            Assert.Contains(t.List.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "VARCHAR2(80)");

            // 줄 수가 바뀌면 기존처럼 지운다.
            t.Rows = Data.Take(2).ToList();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(t.SelectedRows);
            Assert.Null(t.CurrentRow);
        }
        finally
        {
            w.Close();
        }
    });

    // ---- 상세 화면(실제 컨트롤 트리) ---------------------------------------------------------------

    [Fact]
    public Task Detail_view_uses_grid_c_tables_and_clicking_an_index_row_fills_the_column_table_below() => HeadlessHost.Run(async () =>
    {
        var (vm, _) = Create();
        await vm.OpenAsync(new FakeSession(), Emp, showDdl: false);
        var view = new ObjectDetailView { DataContext = vm };
        var w = new Window { Content = view, Width = 1100, Height = 700 };
        w.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var section = view.FindControl<DetailTable>("SectionTable")!;
            var keyTable = view.FindControl<DetailTable>("KeyTable")!;
            Assert.True(section.IsEffectivelyVisible);
            Assert.Contains("grid-c", section.List.Classes);
            Assert.False(keyTable.IsEffectivelyVisible); // 「열」 구역에는 열 표가 없다
            Assert.True(section.RowNumbers); // 「열」 구역 첫 칸은 「이름」(자체 # 없음)

            vm.SelectedSection = vm.Sections.Single(s => s.Title == "인덱스");
            Dispatcher.UIThread.RunJobs();
            Assert.True(keyTable.IsEffectivelyVisible);
            Assert.False(keyTable.RowNumbers); // 열 표는 자체 # 칸
            Assert.True(keyTable.Bounds.Height > 60, "열 표 높이 " + keyTable.Bounds.Height);
            Assert.Contains("인덱스를 고르면", view.FindControl<TextBlock>("KeyStatusText")!.Text, StringComparison.Ordinal);

            Click(w, section, 1);
            Assert.Equal(["EMP_NAME_DESC_IX"], section.SelectedRows.Select(r => r[0]));
            await HeadlessHost.WaitUntil(() => vm.KeyColumns is not null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, keyTable.List.ItemCount);
            Assert.Contains(keyTable.List.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "DESC");

            // 위 표의 「열」 칸이 식으로 바뀌어도 고른 줄·현재 줄은 그대로다.
            Assert.Equal("EMP_NAME_DESC_IX", section.CurrentRow?[0]);
            Assert.Equal(["EMP_NAME_DESC_IX"], section.SelectedRows.Select(r => r[0]));
            Assert.Contains(section.List.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "ENAME DESC, EMPNO");

            // 키보드로 옮겨도 따라간다.
            w.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            w.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            await HeadlessHost.WaitUntil(() => vm.KeyColumns?.Rows is [[1, "UPPER(\"ENAME\")", "ASC"]]);

            vm.SelectedSection = vm.Sections.Single(s => s.Title == "제약");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(vm.KeyColumns);
            Click(w, section, 1);
            await HeadlessHost.WaitUntil(() => vm.KeyColumns?.Columns.Count == 4);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains(keyTable.List.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == "HR.DEPT");
        }
        finally
        {
            w.Close();
        }
    });

    // ---- 가짜 --------------------------------------------------------------------------------------

    internal sealed class FakeKeys : ITableKeyDetailProvider
    {
        public List<(string Table, string Name)> Calls { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public TaskCompletionSource? Gate { get; set; }

        public bool IgnoreCancel { get; init; }

        public bool Denied { get; init; }

        public Exception? Fail { get; init; }

        public async Task<SchemaFolderResult<IndexColumnDetail>> GetIndexColumnsAsync(ISession s, DbObjectRef table, string indexName, CancellationToken ct)
        {
            await EnterAsync(table, indexName, ct);
            return Denied ? new([], Denied: true) : new(indexName switch
            {
                "EMP_NAME_DESC_IX" => [new(1, "ENAME", null, true), new(2, "EMPNO", null, false)],
                "EMP_UPPER_IX" => [new(1, "SYS_NC00010$", "UPPER(\"ENAME\")", false)],
                "\"한글_인덱스\"" => [new(1, "이름", null, false)],
                _ => [new(1, "EMPNO", null, false)],
            });
        }

        public async Task<SchemaFolderResult<ConstraintColumnDetail>> GetConstraintColumnsAsync(ISession s, DbObjectRef table, string constraintName, CancellationToken ct)
        {
            await EnterAsync(table, constraintName, ct);
            return Denied ? new([], Denied: true) : new(constraintName == "EMP_DEPT_FK"
                ? [new(1, "DEPT_ID") { ReferencedOwner = "HR", ReferencedTable = "DEPT", ReferencedColumn = "ID" },
                   new(2, "DEPT_SEQ") { ReferencedOwner = "HR", ReferencedTable = "DEPT", ReferencedColumn = "SEQ" }]
                : [new(1, "EMPNO")]);
        }

        private async Task EnterAsync(DbObjectRef table, string name, CancellationToken ct)
        {
            Calls.Add(($"{table.Owner}.{table.Name}", name));
            Tokens.Add(ct);
            if (Gate is { } gate)
            {
                await (IgnoreCancel ? gate.Task : gate.Task.WaitAsync(ct));
            }

            if (Fail is not null)
            {
                throw Fail;
            }
        }
    }

    private sealed class FakeSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "SCOTT", "localhost/FREEPDB1", Id: Guid.NewGuid());

        public ServerInfo Server => throw new NotSupportedException();

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMeta : IMetadataProvider
    {
        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => Task.FromResult($"CREATE TABLE {obj.Name};");

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct)
            => Task.FromResult(new Oranda.Core.Metadata.ObjectDetail(obj,
            [
                new DetailSection("열", ["이름", "형식"], [["EMPNO", "NUMBER"], ["ENAME", "VARCHAR2(30)"]]),
                Constraints,
                Indexes,
            ]));

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
