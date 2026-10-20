using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Oranda.App.Features.SchemaBrowser;
using Oranda.App.Tests.Features.Navigation;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Events;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;

namespace Oranda.App.Tests.Features.SchemaBrowser;

/// <summary>
/// 스키마 브라우저 트리 끌기(Headless). Windows 에서 OLE DoDragDrop 이 COMException(0x80004005)으로 실패해 앱이 죽던 결함(TSK-07-05)을
/// 끌기 시작 호출을 대리자로 바꿔 흉내 낸다. 끌 글자는 누름 때 미리 불러 둔 재료로 끌기 시작 전에(await 없이) 만든다.
/// </summary>
public sealed class SchemaBrowserDragTests
{
    private const string FailedPrefix = "끌어 놓기를 시작하지 못했습니다: ";

    private delegate Task<DragDropEffects> Starter(PointerPressedEventArgs trigger, IDataTransfer data, DragDropEffects effects);

    /// <summary>열 조회 횟수를 세고, 문(gate)을 닫아 두면 열린 뒤에야 끝나는 메타데이터. 나머지는 가짜 메타데이터에 맡긴다.</summary>
    private sealed class DragMeta(NavFakeMeta inner) : IMetadataProvider
    {
        public int TableCalls { get; private set; }

        public TaskCompletionSource? Gate { get; set; }

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => inner.ListSchemasAsync(s, ct);

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => inner.ListObjectsAsync(s, q, ct);

        public async Task<TableInfo> GetTableAsync(ISession s, DbObjectRef table, CancellationToken ct)
        {
            TableCalls++;
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            return new TableInfo(table,
            [
                new ColumnInfo("ENAME", 2, "VARCHAR2"),
                new ColumnInfo("EMPNO", 1, "NUMBER"),
            ]);
        }

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetProceduresAsync(s, obj, ct);

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => inner.GetDdlAsync(s, obj, o, ct);

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetObjectDetailAsync(s, obj, ct);

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetSourceAsync(s, obj, ct);

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => inner.GetCompileErrorsAsync(s, obj, ct);
    }

    private sealed class DragHarness : IServiceProvider, IAsyncDisposable
    {
        private readonly NavFakeMeta _listing = new();
        private readonly NavFakeSessions _sessions = new();
        private readonly EventBus _bus = new();

        public NavFakeSession Session { get; } = new();

        public DragMeta Meta { get; }

        public SchemaBrowserViewModel Vm { get; }

        public Window Window { get; private set; } = null!;

        public SchemaBrowserView View { get; private set; } = null!;

        public SchemaNode EmpNode { get; private set; } = null!;

        private DragHarness()
        {
            _listing.Schemas.AddRange(["HR", "SCOTT"]);
            _listing.Objects.AddRange([new DbObjectRef("SCOTT", "EMP", "TABLE"), new DbObjectRef("SCOTT", "PROC1", "PROCEDURE")]);
            _sessions.Sessions.Add(Session);
            Meta = new DragMeta(_listing);
            Vm = new SchemaBrowserViewModel(this) { PostToUi = a => a() };
            Vm.DebounceMilliseconds = 3_600_000;
        }

        public object? GetService(Type serviceType) =>
            serviceType == typeof(IMetadataProvider) ? Meta
            : serviceType == typeof(IEventBus) ? _bus
            : serviceType == typeof(ISessionManager) ? _sessions
            : null;

        public static async Task<DragHarness> CreateAsync(Starter starter)
        {
            var h = new DragHarness();
            await h.Vm.SetSessionAsync(h.Session);
            var scott = h.Vm.Roots.Single(r => r.Title == "SCOTT");
            await h.Vm.ExpandAsync(scott);
            scott.IsExpanded = true;
            var tables = scott.Children.Single(c => c.CategoryType == "TABLE");
            await h.Vm.ExpandAsync(tables);
            tables.IsExpanded = true;
            h.EmpNode = tables.Children.Single(c => c.Title == "EMP");

            h.View = new SchemaBrowserView { DataContext = h.Vm, DragStarter = (e, d, fx) => starter(e, d, fx) };
            h.Window = new Window { Width = 400, Height = 600, Content = h.View };
            h.Window.Show();
            Dispatcher.UIThread.RunJobs();
            return h;
        }

        /// <summary>EMP 줄의 가운데 점(창 좌표).</summary>
        private Point EmpPoint()
        {
            var item = View.GetVisualDescendants().OfType<TreeViewItem>().First(i => ReferenceEquals(i.DataContext, EmpNode));
            return item.TranslatePoint(new Point(item.Bounds.Width / 2, 8), Window)!.Value;
        }

        public void Press(RawInputModifiers modifiers = RawInputModifiers.None) => Window.MouseDown(EmpPoint(), MouseButton.Left, modifiers);

        public void Release() => Window.MouseUp(EmpPoint(), MouseButton.Left);

        public void Move(RawInputModifiers modifiers = RawInputModifiers.None)
        {
            var p = EmpPoint();
            Window.MouseMove(new Point(p.X + 20, p.Y), modifiers | RawInputModifiers.LeftMouseButton);
        }

        public ValueTask DisposeAsync()
        {
            Window.Close();
            return ValueTask.CompletedTask;
        }
    }

    private static string? TextOf(IDataTransfer data) => data.Items.Select(i => i.TryGetText()).FirstOrDefault(t => t is not null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Drag_start_failing_with_COMException_is_reported_in_the_status_line_and_never_unhandled(bool throwsSynchronously) => Ui.Run(async () =>
    {
        using var spy = new DragTestSupport.UnhandledSpy();
        var started = 0;
        await using var h = await DragHarness.CreateAsync((_, _, _) =>
        {
            started++;
            return throwsSynchronously ? throw DragTestSupport.OleFailure() : Task.FromException<DragDropEffects>(DragTestSupport.OleFailure());
        });

        h.Press();
        h.Move();
        await DragTestSupport.PumpAsync();

        Assert.Equal(1, started);
        Assert.Null(spy.Caught);
        Assert.StartsWith(FailedPrefix, h.Vm.StatusText);
        Assert.Contains("E_FAIL", h.Vm.StatusText);
    });

    [Fact]
    public Task Drag_start_cancellation_is_silent() => Ui.Run(async () =>
    {
        using var spy = new DragTestSupport.UnhandledSpy();
        await using var h = await DragHarness.CreateAsync((_, _, _) => Task.FromCanceled<DragDropEffects>(new CancellationToken(true)));

        h.Press();
        var before = h.Vm.StatusText;
        h.Move();
        await DragTestSupport.PumpAsync();

        Assert.Null(spy.Caught);
        Assert.Equal(before, h.Vm.StatusText);
    });

    [Fact]
    public Task Drag_starts_synchronously_in_the_move_event_with_the_same_text_as_PrepareDragTextAsync() => Ui.Run(async () =>
    {
        string? text = null;
        var started = 0;
        await using var h = await DragHarness.CreateAsync((_, data, _) =>
        {
            started++;
            text = TextOf(data);
            return Task.FromResult(DragDropEffects.Copy);
        });
        var emp = h.EmpNode;

        h.Press();
        await DragTestSupport.PumpAsync(); // 누름 때 시작한 미리 불러오기가 끝난다.
        h.Move();

        // 이동 이벤트 안에서 곧바로(await 없이) 끌기가 시작됐다.
        Assert.Equal(1, started);
        Assert.Equal(await h.Vm.PrepareDragTextAsync(emp, false, false), text);
        Assert.Equal("SELECT EMPNO,\n       ENAME\nFROM   SCOTT.EMP", text);
        Assert.DoesNotContain("아직 불러오지 못해", h.Vm.StatusText);
    });

    [Theory]
    [InlineData(RawInputModifiers.Control, true, false)]
    [InlineData(RawInputModifiers.Shift, false, true)]
    public Task Ctrl_drags_the_name_and_Shift_drags_the_column_list(RawInputModifiers modifiers, bool ctrl, bool shift) => Ui.Run(async () =>
    {
        string? text = null;
        await using var h = await DragHarness.CreateAsync((_, data, _) =>
        {
            text = TextOf(data);
            return Task.FromResult(DragDropEffects.Copy);
        });
        var emp = h.EmpNode;

        h.Press(modifiers);
        await DragTestSupport.PumpAsync();
        h.Move(modifiers);

        Assert.Equal(await h.Vm.PrepareDragTextAsync(emp, ctrl, shift), text);
        Assert.Equal(ctrl ? "SCOTT.EMP" : "EMPNO, ENAME", text);
    });

    [Theory]
    [InlineData(RawInputModifiers.None, "SELECT *\nFROM   SCOTT.EMP", "열 목록을 아직 불러오지 못해 SELECT * 로 넣었습니다")]
    [InlineData(RawInputModifiers.Shift, "SCOTT.EMP", "열 목록을 아직 불러오지 못해 이름만 넣었습니다")]
    public Task Drag_before_the_columns_arrive_uses_the_fallback_text_and_says_so(RawInputModifiers modifiers, string expected, string message) => Ui.Run(async () =>
    {
        string? text = null;
        var started = 0;
        await using var h = await DragHarness.CreateAsync((_, data, _) =>
        {
            started++;
            text = TextOf(data);
            return Task.FromResult(DragDropEffects.Copy);
        });
        h.Meta.Gate = new TaskCompletionSource(); // 열 조회가 아직 끝나지 않은 상태

        h.Press(modifiers);
        h.Move(modifiers);

        Assert.Equal(1, started);
        Assert.Equal(expected, text);
        Assert.Equal(message, h.Vm.StatusText);
        h.Meta.Gate.SetResult(); // 늦게 끝나도 문제없다.
        await DragTestSupport.PumpAsync();
    });

    [Fact]
    public Task Press_preloads_the_drag_material_once_and_moving_does_not_query_the_database() => Ui.Run(async () =>
    {
        await using var h = await DragHarness.CreateAsync((_, _, _) => Task.FromResult(DragDropEffects.Copy));

        h.Press();
        await DragTestSupport.PumpAsync();
        Assert.Equal(1, h.Meta.TableCalls);

        h.Move();
        await DragTestSupport.PumpAsync();
        Assert.Equal(1, h.Meta.TableCalls);
    });

    [Fact]
    public Task Failure_while_preloading_the_columns_does_not_break_the_press() => Ui.Run(async () =>
    {
        using var spy = new DragTestSupport.UnhandledSpy();
        await using var h = await DragHarness.CreateAsync((_, _, _) => Task.FromResult(DragDropEffects.Copy));
        h.Meta.Gate = new TaskCompletionSource();

        h.Press();
        h.Meta.Gate.SetException(new InvalidOperationException("연결 끊김"));
        await DragTestSupport.PumpAsync();

        Assert.Null(spy.Caught);
    });

    [Fact]
    public Task Second_press_drops_the_old_columns_so_a_quick_drag_never_uses_stale_text() => Ui.Run(async () =>
    {
        string? text = null;
        await using var h = await DragHarness.CreateAsync((_, data, _) =>
        {
            text = TextOf(data);
            return Task.FromResult(DragDropEffects.Copy);
        });
        h.Press();
        await DragTestSupport.PumpAsync();
        h.Release();

        h.Meta.Gate = new TaskCompletionSource(); // 두 번째 불러오기는 아직 끝나지 않았다.
        h.Press();
        h.Move();

        Assert.Equal("SELECT *\nFROM   SCOTT.EMP", text);
        h.Meta.Gate.SetResult();
        await DragTestSupport.PumpAsync();
    });
}
