using System.Reflection;
using System.Text.Json;
using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.ResultGrid;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Features.DataTransfer;

/// <summary>「Excel 로 내보내기」(<c>oranda.grid.exportExcel</c>): 명령 등록·단축키, 범위별 행·열, 기본 파일 이름, 파일 쓰기와 열기 흐름(합친 대화상자의 쓰기 경로 <see cref="ExportRunner"/>).</summary>
[Collection("ExcelExportStatics")]
public sealed class ExcelExportTests : IDisposable
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number) { BaseTable = "EMP" },
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text) { BaseTable = "EMP" },
        new(2, "SAL", "NUMBER", ColumnDataKind.Number) { BaseTable = "EMP" },
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-excel-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _notes = [];
    private readonly Action<string>? _oldNotify = DataTransferCommands.Notify;
    private readonly Func<string, bool> _oldOpen = ExportRunner.OpenFile;
    private readonly string? _oldFolder = ExcelExportPlan.LastFolder;
    private readonly TransferFormat? _oldFormat = ExcelExportPlan.LastFormat;
    private readonly bool? _oldOpenAfter = ExcelExportPlan.LastOpenAfter;

    public ExcelExportTests()
    {
        Directory.CreateDirectory(_dir);
        DataTransferCommands.Notify = _notes.Add;
    }

    public void Dispose()
    {
        DataTransferCommands.Notify = _oldNotify;
        ExportRunner.OpenFile = _oldOpen;
        ExcelExportPlan.LastFolder = _oldFolder;
        ExcelExportPlan.LastFormat = _oldFormat;
        ExcelExportPlan.LastOpenAfter = _oldOpenAfter;
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static async Task<ResultGridModel> GridAsync(int rows = 10)
    {
        var data = Enumerable.Range(0, rows).Select(i => new object?[] { (decimal)i, "n" + i, i * 100m }).ToArray();
        var m = new ResultGridModel(new MemResult(Cols, data), pageSize: 500);
        await m.LoadFirstPageAsync();
        return m;
    }

    private static ExportJob Xlsx(ExcelExportScope scope, ExportSource? source, ResultGridModel? grid, string title) =>
        new(scope, source, grid, new ExportOptions(TransferFormat.Xlsx) { IncludeHeader = true, SheetName = title });

    private static async Task<List<IReadOnlyList<object?>>> DrainAsync(IAsyncEnumerable<IReadOnlyList<object?>> rows)
    {
        var list = new List<IReadOnlyList<object?>>();
        await foreach (var r in rows)
        {
            list.Add(r);
        }

        return list;
    }

    // ---- 명령 등록 ----

    [Fact]
    public void Command_is_in_result_menu_grid_context_menu_above_export_and_palette()
    {
        var attr = typeof(ExcelExportCommandHandler).GetCustomAttributes<OrandaCommandAttribute>().Single();
        Assert.Equal("oranda.grid.exportExcel", attr.Id);
        Assert.Equal("gridFocus", attr.When);
        Assert.Equal("gridHasResult", attr.Enablement);
        Assert.True(attr.ShowInPalette);
        Assert.Contains("menu:result/exportExcel@-100", attr.Placements!);

        // 기존 「내보내기」(context:grid/export@10) 와 같은 묶음.
        var export = typeof(DataTransferCommandHandler).GetCustomAttributes<OrandaCommandAttribute>().Single(a => a.Id == "oranda.grid.export");
        var order = (string p) => int.Parse(p[(p.LastIndexOf('@') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        var ctx = attr.Placements!.Single(p => p.StartsWith("context:grid/", StringComparison.Ordinal));
        Assert.StartsWith("context:grid/export@", ctx, StringComparison.Ordinal);
        Assert.Equal(order(export.Placements!.Single()), order(ctx)); // 같은 순서 값 → 제목순으로 「Excel…」이 위
    }

    [Fact]
    public void Result_menu_starts_with_excel_export_then_a_separator()
    {
        var commands = ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition).ToList();
        var bar = MenuModelBuilder.BuildMenuBar(commands, new ShellText(), _ => null, []);
        var result = bar.Single(n => n.Title == "결과");
        Assert.Equal("oranda.grid.exportExcel", result.Children[0].CommandId);
        Assert.Equal(MenuNodeKind.Separator, result.Children[1].Kind);
        Assert.DoesNotContain(result.Children.Skip(2), n => n.CommandId == "oranda.grid.exportExcel");
    }

    [Fact]
    public void Grid_context_menu_puts_excel_right_above_export_in_the_same_section()
    {
        var commands = ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition).ToList();
        var context = MenuModelBuilder.BuildContext(commands, new ShellText(), _ => null, "grid");
        var ids = context.Select(n => n.CommandId).ToList();
        var excel = ids.IndexOf("oranda.grid.exportExcel");
        Assert.True(excel > 0);
        Assert.Equal("oranda.grid.export", ids[excel + 1]);
        Assert.Equal(MenuNodeKind.Separator, context[excel - 1].Kind); // 묶음의 첫 항목(앞 묶음과 구분선으로 갈린다)
    }

    [Theory]
    [InlineData("default")]
    [InlineData("orange5")]
    [InlineData("sqldev")]
    [InlineData("plsqldev")]
    public void Keymap_presets_bind_primary_alt_e_for_the_grid(string preset)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindFile("docs", "design", "keymaps", preset + ".json")));
        var bindings = doc.RootElement.GetProperty("bindings").EnumerateArray().ToList();
        var mine = bindings.Single(b => b.GetProperty("command").GetString() == "oranda.grid.exportExcel");
        Assert.Equal("Primary+Alt+E", mine.GetProperty("keys")[0].GetString());
        Assert.Equal("gridFocus", mine.GetProperty("when").GetString());

        // 같은 키를 쓰는 gridFocus 바인딩이 따로 없어야 한다(Primary+Shift+E 는 실제 계획이 sqlDocFocus 로 그리드 자리까지 겹쳐 충돌해서 피했다).
        var clash = bindings.Where(b => b.GetProperty("command").GetString() != "oranda.grid.exportExcel"
            && b.TryGetProperty("when", out var when) && when.GetString() == "gridFocus"
            && b.GetProperty("keys").EnumerateArray().Any(k => k.GetString() == "Primary+Alt+E"));
        Assert.Empty(clash);
    }

    // ---- 파일 이름·폴더 ----

    [Theory]
    [InlineData("EMP", "EMP_20260102_030405.xlsx")]
    [InlineData(null, "조회결과_20260102_030405.xlsx")]
    [InlineData("   ", "조회결과_20260102_030405.xlsx")]
    [InlineData("A/B:C", "A_B_C_20260102_030405.xlsx")]
    public void Default_file_name_is_title_plus_timestamp(string? title, string expected) =>
        Assert.Equal(expected, ExcelExportPlan.DefaultFileName(title, new DateTime(2026, 1, 2, 3, 4, 5)));

    [Fact]
    public void Default_folder_is_last_export_folder_when_it_exists_else_documents()
    {
        Assert.Equal(_dir, ExcelExportPlan.DefaultFolder(_dir));
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Assert.Equal(string.IsNullOrEmpty(docs) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : docs,
            ExcelExportPlan.DefaultFolder(Path.Combine(_dir, "없는폴더")));
    }

    // ---- 범위별 행·열 ----

    [Fact]
    public async Task Loaded_rows_scope_gives_every_visible_column_and_all_loaded_rows()
    {
        await using var grid = await GridAsync(10);
        grid.HideColumn(2);
        var data = ExcelExportPlan.FromGrid(grid, ExcelExportScope.LoadedRows);
        Assert.Equal(["ID", "NAME"], data.Columns.Select(c => c.Name));
        var rows = await DrainAsync(data.Rows);
        Assert.Equal(10, data.RowCount);
        Assert.Equal(10, rows.Count);
        Assert.All(rows, r => Assert.Equal(2, r.Count));
        Assert.Equal("n9", rows[9][1]);
        Assert.Equal("EMP", ExcelExportPlan.TableNameOf(data.Columns));
    }

    [Fact]
    public async Task Selection_scope_gives_only_selected_rows_and_columns()
    {
        await using var grid = await GridAsync(10);
        grid.SelectCell(new GridCell(2, 1));
        grid.ExtendTo(new GridCell(4, 2));
        Assert.Equal((3L, 2), ExcelExportPlan.SelectionSize(grid));
        var data = ExcelExportPlan.FromGrid(grid, ExcelExportScope.Selection);
        Assert.Equal(["NAME", "SAL"], data.Columns.Select(c => c.Name));
        var rows = await DrainAsync(data.Rows);
        Assert.Equal(3, data.RowCount);
        Assert.Equal(["n2", 200m], rows[0]);
        Assert.Equal(["n4", 400m], rows[2]);
    }

    [Fact]
    public async Task Selection_scope_without_selection_is_refused_and_size_is_zero()
    {
        await using var grid = await GridAsync(3);
        Assert.Equal((0L, 0), ExcelExportPlan.SelectionSize(grid));
        Assert.Throws<InvalidOperationException>(() => ExcelExportPlan.FromGrid(grid, ExcelExportScope.Selection));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExcelExportPlan.FromGrid(grid, ExcelExportScope.AllRows));
    }

    [Fact]
    public async Task Loaded_rows_beyond_one_read_chunk_are_all_returned()
    {
        await using var grid = await GridAsync(1200); // 페이지 500행 → 첫 페이지만 불러옴
        Assert.Equal(500, ExcelExportPlan.LoadedRows(grid));
        var rows = await DrainAsync(ExcelExportPlan.FromGrid(grid, ExcelExportScope.LoadedRows).Rows);
        Assert.Equal(500, rows.Count);
    }

    // ---- 파일 쓰기와 열기 ----

    [Fact]
    public async Task Loaded_rows_are_written_without_rerunning_and_opened_afterwards()
    {
        await using var grid = await GridAsync(4);
        var service = new FakeTransfer();
        var opened = new List<string>();
        ExportRunner.OpenFile = p => { opened.Add(p); return true; };
        var path = Path.Combine(_dir, "out.xlsx");

        var outcome = await ExportRunner.ExportToFileAsync(service, Xlsx(ExcelExportScope.LoadedRows, null, grid, "EMP"), path, openAfter: true, null, default);

        Assert.Equal(4, outcome.Summary!.Rows);
        Assert.False(outcome.IsError);
        Assert.Equal(0, service.ServerExports);
        Assert.Equal(TransferFormat.Xlsx, service.RowsOptions!.Format);
        Assert.Equal("EMP", service.RowsOptions.SheetName);
        Assert.True(service.RowsOptions.IncludeHeader);
        Assert.Equal(4, service.RowCount);
        Assert.True(File.Exists(path));
        Assert.Equal([path], opened);
        Assert.Equal(_dir, ExcelExportPlan.LastFolder);
        Assert.Contains(_notes, n => n.Contains("4행을 내보냈습니다", StringComparison.Ordinal) && n.Contains(path, StringComparison.Ordinal));
        Assert.Equal(TransferFormat.Xlsx, ExcelExportPlan.LastFormat); // 다음 「파일로 내보내기」 는 Excel 로 열린다
        Assert.True(ExcelExportPlan.LastOpenAfter);
    }

    [Fact]
    public async Task Open_is_skipped_when_unchecked_and_a_failed_open_is_reported()
    {
        await using var grid = await GridAsync(2);
        var opened = 0;
        ExportRunner.OpenFile = _ => { opened++; return false; };
        var job = Xlsx(ExcelExportScope.LoadedRows, null, grid, "EMP");

        await ExportRunner.ExportToFileAsync(new FakeTransfer(), job, Path.Combine(_dir, "a.xlsx"), openAfter: false, null, default);
        Assert.Equal(0, opened);

        await ExportRunner.ExportToFileAsync(new FakeTransfer(), job, Path.Combine(_dir, "b.xlsx"), openAfter: true, null, default);
        Assert.Equal(1, opened);
        Assert.Contains(_notes, n => n.Contains("열지 못했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_rows_scope_reruns_the_query_on_the_server()
    {
        await using var grid = await GridAsync(2);
        var service = new FakeTransfer();
        var source = new ExportSource(new StubSession(), new SqlStatement("SELECT * FROM emp", default, StatementKind.Query), new Dictionary<string, BindValue>());
        var path = Path.Combine(_dir, "all.xlsx");

        var outcome = await ExportRunner.ExportToFileAsync(service, Xlsx(ExcelExportScope.AllRows, source, grid, "EMP"), path, openAfter: false, null, default);

        Assert.Equal(7, outcome.Summary!.Rows);
        Assert.Equal(1, service.ServerExports);
        Assert.Equal("EMP", service.ServerOptions!.SheetName);
        Assert.Null(service.RowsOptions);
    }

    [Fact]
    public async Task All_rows_scope_without_a_query_fails_cleanly_and_leaves_no_file()
    {
        await using var grid = await GridAsync(2);
        var path = Path.Combine(_dir, "none.xlsx");

        var outcome = await ExportRunner.ExportToFileAsync(new FakeTransfer(), Xlsx(ExcelExportScope.AllRows, null, grid, "EMP"), path, openAfter: true, null, default);

        Assert.Null(outcome.Summary);
        Assert.True(outcome.IsError);
        Assert.False(File.Exists(path));
        Assert.Contains(_notes, n => n.Contains("실패", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Cancel_removes_the_partial_file()
    {
        await using var grid = await GridAsync(2);
        var path = Path.Combine(_dir, "cancel.xlsx");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var outcome = await ExportRunner.ExportToFileAsync(new FakeTransfer(), Xlsx(ExcelExportScope.LoadedRows, null, grid, "EMP"), path, openAfter: true, null, cts.Token);

        Assert.Null(outcome.Summary);
        Assert.False(outcome.IsError);
        Assert.False(File.Exists(path));
        Assert.Contains(_notes, n => n.Contains("취소", StringComparison.Ordinal));
    }

    private static string FindFile(params string[] parts)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var p = Path.Combine([dir.FullName, .. parts]);
            if (File.Exists(p))
            {
                return p;
            }
        }

        throw new FileNotFoundException(Path.Combine(parts));
    }

    private sealed class FakeTransfer : IDataTransferService
    {
        public int ServerExports { get; private set; }

        public ExportOptions? ServerOptions { get; private set; }

        public ExportOptions? RowsOptions { get; private set; }

        public int RowCount { get; private set; }

        public async Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            ServerExports++;
            ServerOptions = options;
            await destination.WriteAsync("x"u8.ToArray(), ct);
            return new TransferSummary(7, TimeSpan.Zero);
        }

        public async Task<TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            RowsOptions = options;
            await foreach (var _ in rows.WithCancellation(ct))
            {
                RowCount++;
            }

            await destination.WriteAsync("x"u8.ToArray(), ct);
            return new TransferSummary(RowCount, TimeSpan.Zero);
        }

        public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class MemResult(ResultColumn[] columns, object?[][] rows, int pageSize = 500) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * pageSize;
            var count = Math.Clamp(rows.Length - first, 0, pageSize);
            var cols = Enumerable.Range(0, columns.Length)
                .Select(c => (IReadOnlyList<object?>)rows.Skip(first).Take(count).Select(r => r[c]).ToList())
                .ToList();
            return Task.FromResult(new ResultPage(pageIndex, first, count, cols) { IsLast = first + count >= rows.Length });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("p", "scott", "h:1521/s");

        public ServerInfo Server { get; } = new("23.26.0.0.0", "FREE", "h", "s", "FREE", "FREEPDB1", true, 1,
            new ServerCapabilities(false, false, false, false, false));

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "FREE/FREEPDB1/SCOTT";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
