using System.Reflection;
using Oranda.App.Features.DataTransfer;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Transfer;

namespace Oranda.App.Tests.Features.DataTransfer;

// 소유: TSK-06-07. 내보내기·가져오기 대화상자 모델과 명령 등록.
public sealed class DataTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-transfer-" + Guid.NewGuid().ToString("N"));

    public DataTransferTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private static readonly DbObjectRef Emp = new("SCOTT", "EMP", "TABLE");

    private static TableInfo EmpTable() => new(Emp,
    [
        new ColumnInfo("ID", 1, "NUMBER"),
        new ColumnInfo("NAME", 2, "VARCHAR2"),
        new ColumnInfo("HIRED", 3, "DATE"),
        new ColumnInfo("BONUS", 4, "NUMBER") { IsVirtual = true },
    ]);

    [Fact]
    public void Namespace_and_commands_are_registered()
    {
        Assert.Equal("Oranda.App.Features.DataTransfer", typeof(DataTransferCommands).Namespace);
        var ids = typeof(DataTransferCommandHandler).GetCustomAttributes<OrandaCommandAttribute>().Select(a => a.Id).ToList();
        Assert.Contains("oranda.grid.export", ids);
        Assert.Contains("oranda.tools.import", ids);
    }

    [Theory]
    [InlineData("a.csv", TransferFormat.Csv)]
    [InlineData("a.TSV", TransferFormat.Tsv)]
    [InlineData("a.xlsx", TransferFormat.Xlsx)]
    public void Format_is_detected_from_extension(string name, TransferFormat expected)
    {
        Assert.True(ImportDialogViewModel.TryDetectFormat(name, out var f));
        Assert.Equal(expected, f);
        Assert.False(ImportDialogViewModel.TryDetectFormat("a.json", out _));
    }

    [Fact]
    public async Task Import_dialog_maps_by_name_previews_conversion_errors_and_sends_mapping()
    {
        var path = Path.Combine(_dir, "emp.csv");
        await File.WriteAllTextAsync(path, "id,name,hired,extra\n1,Kim,2026-01-02,x\nabc,Lee,not-a-date,y\n");
        var service = new RecordingService();
        using var vm = new ImportDialogViewModel(service, new OneTable(EmpTable()), new StubSession());
        vm.FilePath = path;
        vm.TargetOwner = "scott";
        vm.TargetTable = "emp";

        await vm.LoadAsync();

        Assert.True(vm.IsReady);
        Assert.Equal(["id", "name", "hired", "extra"], vm.Mappings.Select(m => m.Source));
        Assert.Equal(["ID", "NAME", "HIRED"], vm.Mappings.Take(3).Select(m => m.Target));
        Assert.True(vm.Mappings[3].IsSkipped);
        Assert.Equal(["ID", "NAME", "HIRED"], vm.TargetColumns); // 가상 열은 고를 수 없다
        Assert.Contains("→ 'Kim'", vm.Mappings[1].Preview, StringComparison.Ordinal);
        Assert.True(vm.Mappings[0].PreviewIsError); // 두 번째 행 'abc'
        Assert.Contains("숫자가 아님", vm.Mappings[0].Preview, StringComparison.Ordinal);
        Assert.False(vm.Mappings[1].PreviewIsError);

        vm.BatchSize = 250;
        vm.CommitAtEnd = false;
        await vm.StartAsync();

        var opts = Assert.IsType<ImportOptions>(service.LastImport);
        Assert.Equal(TransferFormat.Csv, opts.Format);
        Assert.Equal(250, opts.BatchSize);
        Assert.False(opts.CommitAtEnd);
        Assert.Equal(Emp, opts.Target);
        Assert.Equal([("id", "ID"), ("name", "NAME"), ("hired", "HIRED"), ("extra", (string?)null)], opts.Mappings.Select(m => (m.SourceColumn, m.TargetColumn)));
        Assert.Equal(7, vm.InsertedRows);
        Assert.Equal(["행 2: ORA-12899 값이 너무 큼"], vm.ErrorRows);
        Assert.True(vm.HasErrors);
        Assert.True(vm.StatusIsError);
    }

    [Fact]
    public async Task Import_dialog_without_header_names_columns_by_ordinal()
    {
        var path = Path.Combine(_dir, "raw.csv");
        await File.WriteAllTextAsync(path, "1,Kim\n2,Lee\n");
        using var vm = new ImportDialogViewModel(new RecordingService(), new OneTable(EmpTable()), new StubSession())
        {
            FilePath = path,
            TargetOwner = "SCOTT",
            TargetTable = "EMP",
            HasHeader = false,
        };

        await vm.LoadAsync();
        vm.Mappings[0].Target = "ID";
        vm.Mappings[1].Target = "NAME";

        Assert.Equal(["1", "2"], vm.Mappings.Select(m => m.Source));
        Assert.Equal(["1 | Kim", "2 | Lee"], vm.PreviewLines);
        Assert.Contains("→ 1", vm.Mappings[0].Preview, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_dialog_reports_policy_denial_as_error_without_throwing()
    {
        var path = Path.Combine(_dir, "ro.csv");
        await File.WriteAllTextAsync(path, "id\n1\n");
        var service = new RecordingService { Throw = new InvalidOperationException("가져오기 INSERT 가 실행되지 않았다: 정책 관문이 막았다(ReadOnlyProfile)") };
        using var vm = new ImportDialogViewModel(service, new OneTable(EmpTable()), new StubSession()) { FilePath = path, TargetOwner = "SCOTT", TargetTable = "EMP" };
        await vm.LoadAsync();

        await vm.StartAsync();

        Assert.True(vm.StatusIsError);
        Assert.Contains("정책", vm.Status, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Export_dialog_writes_file_and_deletes_partial_file_on_failure()
    {
        var source = new ExportSource(new StubSession(), new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query), new Dictionary<string, BindValue>())
        {
            SuggestedTableName = "SCOTT.EMP",
        };
        var service = new RecordingService();
        using var vm = new ExportDialogViewModel(service, source) { FilePath = Path.Combine(_dir, "out.sql"), Format = TransferFormat.Insert };

        Assert.Equal("SCOTT.EMP", vm.InsertTableName);
        Assert.True(vm.IsInsert);
        Assert.Equal(".sql", vm.SuggestedExtension);
        await vm.StartAsync();

        Assert.Equal("payload", await File.ReadAllTextAsync(vm.FilePath));
        Assert.Equal(TransferFormat.Insert, service.LastExport?.Format);
        Assert.Equal("SCOTT.EMP", service.LastExport?.InsertTableName);
        Assert.Equal(3, vm.RowCount);

        service.Throw = new InvalidOperationException("질의를 실행하지 못했다");
        vm.FilePath = Path.Combine(_dir, "bad.csv");
        vm.Format = TransferFormat.Csv;
        await vm.StartAsync();

        Assert.True(vm.StatusIsError);
        Assert.False(File.Exists(vm.FilePath));
    }

    private sealed class RecordingService : IDataTransferService
    {
        public Exception? Throw { get; set; }

        public ImportOptions? LastImport { get; private set; }

        public ExportOptions? LastExport { get; private set; }

        public async Task<TransferSummary> ExportAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            LastExport = options;
            await destination.WriteAsync("payload"u8.ToArray(), ct);
            if (Throw is not null)
            {
                throw Throw;
            }

            progress?.Report(3);
            return new TransferSummary(3, TimeSpan.FromSeconds(1));
        }

        public ExportOptions? LastRowsOptions { get; private set; }

        public IReadOnlyList<ResultColumn>? LastRowsColumns { get; private set; }

        public List<IReadOnlyList<object?>> LastRows { get; } = [];

        public async Task<TransferSummary> ExportRowsAsync(IReadOnlyList<ResultColumn> columns, IAsyncEnumerable<IReadOnlyList<object?>> rows, ExportOptions options, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            LastRowsOptions = options;
            LastRowsColumns = columns;
            await foreach (var r in rows.WithCancellation(ct))
            {
                LastRows.Add(r);
            }

            await destination.WriteAsync("rows"u8.ToArray(), ct);
            progress?.Report(LastRows.Count);
            return new TransferSummary(LastRows.Count, TimeSpan.FromSeconds(1));
        }

        public Task<TransferSummary> ImportAsync(ISession s, ImportOptions options, Stream source, IProgress<long>? progress, CancellationToken ct)
        {
            LastImport = options;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(new TransferSummary(7, TimeSpan.FromSeconds(2)) { Warnings = ["행 2: ORA-12899 값이 너무 큼"] });
        }
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

    private sealed class OneTable(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<Oranda.Core.Metadata.ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}
