using Oranda.App.Features.DataTransfer;
using Oranda.App.Features.ResultGrid;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Features.DataTransfer;

/// <summary>
/// TSK-09-07 하나로 합친 내보내기 대화상자 모델(D-020): 여는 길별 기본 형식·범위, 형식별 선택 칸, 저장 위치 채우기·검사,
/// 범위·형식별 쓰기, 취소·오류 되돌림, 앱 실행 동안 기억하는 값.
/// </summary>
[Collection("ExcelExportStatics")]
public sealed class ExportDialogTests : IDisposable
{
    internal static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number) { BaseTable = "EMP" },
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text) { BaseTable = "EMP" },
        new(2, "SAL", "NUMBER", ColumnDataKind.Number) { BaseTable = "EMP" },
    ];

    private static readonly DateTime Now = new(2026, 10, 25, 9, 8, 7);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-export-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _notes = [];
    private readonly List<string> _opened = [];
    private readonly Action<string>? _oldNotify = DataTransferCommands.Notify;
    private readonly Func<string, bool> _oldOpen = ExportRunner.OpenFile;
    private readonly string? _oldFolder = ExcelExportPlan.LastFolder;
    private readonly TransferFormat? _oldFormat = ExcelExportPlan.LastFormat;
    private readonly bool? _oldOpenAfter = ExcelExportPlan.LastOpenAfter;

    public ExportDialogTests()
    {
        Directory.CreateDirectory(_dir);
        DataTransferCommands.Notify = _notes.Add;
        ExportRunner.OpenFile = p => { _opened.Add(p); return true; };
        ExcelExportPlan.LastFolder = _dir;
        ExcelExportPlan.LastFormat = null;
        ExcelExportPlan.LastOpenAfter = null;
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

    internal static async Task<ResultGridModel> GridAsync(int rows = 10)
    {
        var data = Enumerable.Range(0, rows).Select(i => new object?[] { (decimal)i, "n" + i, i * 100m }).ToArray();
        var m = new ResultGridModel(new MemResult(Cols, data), pageSize: 500);
        await m.LoadFirstPageAsync();
        return m;
    }

    internal static ExportSource Source(string? table = null) =>
        new(new StubSession(), new SqlStatement("SELECT * FROM emp", default, StatementKind.Query), new Dictionary<string, BindValue>()) { SuggestedTableName = table };

    // ---- 여는 길별 기본값 ----

    [Fact]
    public void Excel_quick_path_always_starts_on_excel_and_file_export_on_the_last_format_else_csv()
    {
        Assert.Equal(TransferFormat.Xlsx, ExcelExportPlan.StartFormat(excel: true));
        Assert.Equal(TransferFormat.Xlsx, ExcelExportPlan.StartFormat(excel: true, last: TransferFormat.Json));
        Assert.Equal(TransferFormat.Csv, ExcelExportPlan.StartFormat(excel: false));
        Assert.Equal(TransferFormat.Json, ExcelExportPlan.StartFormat(excel: false, last: TransferFormat.Json));
        ExcelExportPlan.LastFormat = TransferFormat.Insert;
        Assert.Equal(TransferFormat.Insert, ExcelExportPlan.StartFormat(excel: false));

        // 내보낸 뒤 열기: 기억한 값이 없으면 Excel 일 때만 켠다.
        Assert.True(ExcelExportPlan.StartOpenAfter(TransferFormat.Xlsx));
        Assert.False(ExcelExportPlan.StartOpenAfter(TransferFormat.Csv));
        ExcelExportPlan.LastOpenAfter = false;
        Assert.False(ExcelExportPlan.StartOpenAfter(TransferFormat.Xlsx));
    }

    [Fact]
    public async Task Excel_quick_path_opens_ready_to_save_with_all_rows_and_a_filled_path()
    {
        await using var grid = await GridAsync(10);
        using var vm = new ExportDialogViewModel(new RecordingTransfer(), Source(), grid, ExcelExportPlan.StartFormat(excel: true), now: Now);

        Assert.Equal(TransferFormat.Xlsx, vm.Format);
        Assert.Equal(vm.FormatOptions[2], vm.SelectedFormatOption);
        Assert.True(vm.IsAllRows);
        Assert.True(vm.HasGrid && vm.CanAllRows);
        Assert.False(vm.CanSelection);
        Assert.False(vm.HasFetchedAll);
        Assert.True(vm.OpenAfter);
        Assert.Equal("EMP", vm.Title);
        Assert.Equal("EMP", vm.SheetName);
        Assert.Equal(Path.Combine(_dir, "EMP_20261025_090807.xlsx"), vm.FilePath);
        Assert.Null(vm.PathError);
        Assert.True(vm.StartCommand.CanExecute(null)); // 바로 Enter 로 저장(R1 3클릭)
        Assert.Contains("10행", vm.LoadedLabel, StringComparison.Ordinal);
        Assert.Contains("서버에서 다시 실행", vm.ScopeHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task File_export_without_a_query_starts_on_loaded_rows_and_selection_is_offered()
    {
        await using var grid = await GridAsync(10);
        grid.SelectCell(new GridCell(2, 1));
        grid.ExtendTo(new GridCell(4, 2));
        using var vm = new ExportDialogViewModel(new RecordingTransfer(), null, grid, ExcelExportPlan.StartFormat(excel: false), now: Now);

        Assert.Equal(TransferFormat.Csv, vm.Format);
        Assert.False(vm.CanAllRows);
        Assert.Contains("다시 실행할 문장", vm.AllRowsTip, StringComparison.Ordinal);
        Assert.True(vm.IsLoadedRows);
        Assert.True(vm.CanSelection);
        Assert.Contains("3행 × 2열", vm.SelectionLabel, StringComparison.Ordinal);
        Assert.False(vm.OpenAfter);
        Assert.EndsWith("EMP_20261025_090807.csv", vm.FilePath, StringComparison.Ordinal);
        Assert.Contains("그리드", vm.ScopeHint, StringComparison.Ordinal);

        vm.IsSelection = true;
        Assert.Equal(ExcelExportScope.Selection, vm.Scope);
        Assert.False(vm.IsLoadedRows);
        vm.IsSelection = false; // 끄는 쪽 알림은 무시(다른 단추가 켜며 바뀐다)
        Assert.Equal(ExcelExportScope.Selection, vm.Scope);
    }

    [Fact]
    public void Query_only_export_has_no_grid_scopes_and_needs_a_query_or_a_grid()
    {
        using var vm = new ExportDialogViewModel(new RecordingTransfer(), Source("SCOTT.EMP"));
        Assert.False(vm.HasGrid);
        Assert.True(vm.IsAllRows);
        Assert.False(vm.OpenAfter);
        Assert.Equal("SCOTT.EMP", vm.InsertTableName);
        Assert.Equal("SCOTT.EMP", vm.SheetName);
        Assert.Throws<ArgumentException>(() => new ExportDialogViewModel(new RecordingTransfer(), null, null, TransferFormat.Csv));
    }

    // ---- 형식별 칸 ----

    [Fact]
    public async Task Changing_the_format_swaps_the_extension_and_shows_only_that_formats_options()
    {
        await using var grid = await GridAsync(3);
        using var vm = new ExportDialogViewModel(new RecordingTransfer(), Source(), grid, TransferFormat.Csv, now: Now);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.SelectedFormatOption = vm.FormatOptions.Single(o => o.Format == TransferFormat.Xlsx);
        Assert.True(vm.IsXlsx);
        Assert.True(vm.HasHeaderOption);
        Assert.False(vm.IsInsert);
        Assert.EndsWith(".xlsx", vm.FilePath, StringComparison.Ordinal);
        Assert.Contains(nameof(vm.IsXlsx), changed);
        Assert.Contains(nameof(vm.SelectedFormatOption), changed);

        vm.Format = TransferFormat.Insert;
        Assert.True(vm.IsInsert);
        Assert.False(vm.HasHeaderOption); // 고급 설정(열 이름·시트)이 숨는다
        Assert.EndsWith(".sql", vm.FilePath, StringComparison.Ordinal);
        Assert.Equal("EMP", vm.InsertTableName); // 질의가 표를 몰라도 그리드 열의 표

        vm.Format = TransferFormat.Json;
        Assert.False(vm.HasHeaderOption);
        Assert.EndsWith(".json", vm.FilePath, StringComparison.Ordinal);

        // 사용자가 다른 확장자를 적었으면 건드리지 않는다.
        vm.FilePath = Path.Combine(_dir, "mine.txt");
        vm.Format = TransferFormat.Tsv;
        Assert.Equal(Path.Combine(_dir, "mine.txt"), vm.FilePath);
        Assert.Equal("INSERT 문(.sql)", vm.FormatOptions[4].Label);
        Assert.Equal("INSERT 문(.sql)", vm.FormatOptions[4].ToString());
    }

    // ---- 저장 위치 검사 ----

    [Fact]
    public void Path_validation_names_what_is_wrong()
    {
        bool Exists(string p) => Directory.Exists(p);
        Assert.Contains("넣으세요", ExportDialogViewModel.ValidatePath("  ", Exists).Error, StringComparison.Ordinal);
        Assert.Contains("전체 경로", ExportDialogViewModel.ValidatePath("out.csv", Exists).Error, StringComparison.Ordinal);
        Assert.Contains("폴더입니다", ExportDialogViewModel.ValidatePath(_dir, Exists).Error, StringComparison.Ordinal);
        Assert.Contains("폴더입니다", ExportDialogViewModel.ValidatePath(_dir + Path.DirectorySeparatorChar, Exists).Error, StringComparison.Ordinal);
        Assert.Contains("쓸 수 없는 글자", ExportDialogViewModel.ValidatePath(Path.Combine(_dir, "a?b.csv"), Exists).Error, StringComparison.Ordinal);
        var missing = Path.Combine(_dir, "없는폴더");
        var (error, folder) = ExportDialogViewModel.ValidatePath(Path.Combine(missing, "a.csv"), Exists);
        Assert.Contains("폴더가 없습니다", error, StringComparison.Ordinal);
        Assert.Equal(missing, folder);
        Assert.Equal((null, _dir), ExportDialogViewModel.ValidatePath(" " + Path.Combine(_dir, "a.csv") + " ", Exists));
    }

    [Fact]
    public async Task Path_error_blocks_start_and_an_existing_file_only_warns()
    {
        await using var grid = await GridAsync(3);
        using var vm = new ExportDialogViewModel(new RecordingTransfer(), Source(), grid, TransferFormat.Csv, now: Now);
        var canChanged = 0;
        vm.StartCommand.CanExecuteChanged += (_, _) => canChanged++;

        vm.FilePath = "relative.csv";
        Assert.True(vm.HasPathError);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.True(canChanged > 0);

        var existing = Path.Combine(_dir, "there.csv");
        await File.WriteAllTextAsync(existing, "old");
        vm.FilePath = existing;
        Assert.False(vm.HasPathError);
        Assert.True(vm.HasPathWarning);
        Assert.Contains("덮어씁니다", vm.PathWarning, StringComparison.Ordinal);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    // ---- 쓰기 ----

    [Fact]
    public async Task Selection_scope_writes_grid_values_in_the_chosen_format_remembers_it_and_closes()
    {
        await using var grid = await GridAsync(10);
        grid.SelectCell(new GridCell(2, 1));
        grid.ExtendTo(new GridCell(4, 2));
        var service = new RecordingTransfer();
        using var vm = new ExportDialogViewModel(service, Source(), grid, TransferFormat.Tsv, openAfter: true, now: Now) { IncludeHeader = false };
        vm.IsSelection = true;
        var finished = 0;
        vm.Finished += (_, _) => finished++;

        await vm.StartAsync();

        Assert.Equal(0, service.ServerExports);
        Assert.Equal(TransferFormat.Tsv, service.RowsOptions!.Format);
        Assert.False(service.RowsOptions.IncludeHeader);
        Assert.Null(service.RowsOptions.SheetName);
        Assert.Equal(["NAME", "SAL"], service.RowsColumns!.Select(c => c.Name));
        Assert.Equal(3, service.Rows.Count);
        Assert.Equal(3, vm.RowCount);
        Assert.True(File.Exists(vm.FilePath));
        Assert.Equal(1, finished);
        Assert.False(vm.StatusIsError);
        Assert.False(vm.IsRunning);
        Assert.Equal([vm.FilePath], _opened);
        Assert.Equal(TransferFormat.Tsv, ExcelExportPlan.LastFormat);
        Assert.True(ExcelExportPlan.LastOpenAfter);
        Assert.Equal(_dir, ExcelExportPlan.LastFolder);
        Assert.Contains(_notes, n => n.Contains("3행을 내보냈습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task All_rows_excel_reruns_the_query_with_sheet_name_and_rows_per_sheet()
    {
        await using var grid = await GridAsync(3);
        var service = new RecordingTransfer();
        using var vm = new ExportDialogViewModel(service, Source(), grid, TransferFormat.Xlsx, openAfter: false, now: Now)
        {
            SheetName = "  급여  ",
            MaxRowsPerSheet = 0,
        };

        await vm.StartAsync();

        Assert.Equal(1, service.ServerExports);
        Assert.Equal(TransferFormat.Xlsx, service.ServerOptions!.Format);
        Assert.Equal("급여", service.ServerOptions.SheetName);
        Assert.Equal(1, service.ServerOptions.MaxRowsPerSheet); // 0 이하는 1 로
        Assert.True(service.ServerOptions.IncludeHeader);
        Assert.Equal(7, vm.RowCount);
        Assert.Empty(_opened);
        Assert.False(ExcelExportPlan.LastOpenAfter);
    }

    [Fact]
    public async Task Insert_uses_the_table_name_and_ignores_the_sheet()
    {
        var service = new RecordingTransfer();
        using var vm = new ExportDialogViewModel(service, Source("SCOTT.EMP")) { Format = TransferFormat.Insert, FilePath = Path.Combine(_dir, "o.sql") };

        await vm.StartAsync();

        Assert.Equal("SCOTT.EMP", service.ServerOptions!.InsertTableName);
        Assert.Null(service.ServerOptions.SheetName);
    }

    [Fact]
    public async Task Cancel_stops_the_export_deletes_the_partial_file_and_keeps_the_dialog_open()
    {
        await using var grid = await GridAsync(3);
        var service = new RecordingTransfer { Block = true };
        using var vm = new ExportDialogViewModel(service, Source(), grid, TransferFormat.Csv, openAfter: true, now: Now);
        var finished = 0;
        vm.Finished += (_, _) => finished++;

        var run = vm.StartAsync();
        await service.Started.Task;
        Assert.True(vm.IsRunning);
        Assert.True(vm.CancelCommand.CanExecute(null));
        Assert.False(vm.StartCommand.CanExecute(null));
        vm.CancelCommand.Execute(null);
        await run;

        Assert.False(vm.IsRunning);
        Assert.False(vm.StatusIsError);
        Assert.Contains("취소", vm.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(vm.FilePath));
        Assert.Equal(0, finished);
        Assert.Empty(_opened);
        Assert.Null(ExcelExportPlan.LastFormat); // 실패·취소는 기억하지 않는다
        Assert.True(vm.StartCommand.CanExecute(null)); // 다시 시도할 수 있다
    }

    [Fact]
    public async Task A_failing_export_shows_the_error_and_leaves_no_file()
    {
        var service = new RecordingTransfer { Throw = new InvalidOperationException("ORA-00942: 표가 없습니다") };
        var path = Path.Combine(_dir, "bad.csv");
        using var vm = new ExportDialogViewModel(service, Source()) { FilePath = path };

        await vm.StartAsync();

        Assert.True(vm.StatusIsError);
        Assert.Contains("ORA-00942", vm.Status, StringComparison.Ordinal);
        Assert.False(File.Exists(path));
        Assert.False(vm.IsRunning);

        // 쓸 수 없는 경로(폴더 없음)도 같은 길로 끝난다.
        vm.FilePath = Path.Combine(_dir, "none", "x.csv");
        service.Throw = null;
        await vm.StartAsync();
        Assert.True(vm.StatusIsError);
    }

    internal sealed class RecordingTransfer : IDataTransferService
    {
        public bool Block { get; init; }

        public Exception? Throw { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ServerExports { get; private set; }

        public ExportOptions? ServerOptions { get; private set; }

        public ExportOptions? RowsOptions { get; private set; }

        public IReadOnlyList<ResultColumn>? RowsColumns { get; private set; }

        public List<IReadOnlyList<object?>> Rows { get; } = [];

        public async Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            ServerExports++;
            ServerOptions = options;
            await destination.WriteAsync("x"u8.ToArray(), ct);
            Started.TrySetResult();
            if (Block)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            progress?.Report(7);
            return new TransferSummary(7, TimeSpan.FromSeconds(0.4));
        }

        public async Task<TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            RowsOptions = options;
            RowsColumns = columns;
            await foreach (var r in rows.WithCancellation(ct))
            {
                Rows.Add(r);
            }

            await destination.WriteAsync("rows"u8.ToArray(), ct);
            progress?.Report(Rows.Count);
            return new TransferSummary(Rows.Count, TimeSpan.FromSeconds(0.1));
        }

        public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    internal sealed class MemResult(ResultColumn[] columns, object?[][] rows, int pageSize = 500) : IResultSet
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

    internal sealed class StubSession : ISession
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
