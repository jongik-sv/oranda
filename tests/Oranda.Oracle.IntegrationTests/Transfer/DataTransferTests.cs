using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Transfer;

namespace Oranda.Oracle.IntegrationTests.Transfer;

// 소유: TSK-06-07. 내보내기·가져오기.

/// <summary>DB 없이 도는 부분: 파일 쓰기·읽기, 값 변환, 정책 관문.</summary>
public class DataTransferLogicTests
{
    private static readonly ResultColumn[] Cols =
    [
        new(0, "ID", "NUMBER", ColumnDataKind.Number),
        new(1, "NAME", "VARCHAR2", ColumnDataKind.Text),
        new(2, "HIRED", "DATE", ColumnDataKind.Date),
    ];

    private static readonly DateTime Hired = new(2026, 3, 5, 14, 30, 15, DateTimeKind.Unspecified);

    private static byte[] Write(ExportOptions options, params object?[][] rows)
    {
        using var ms = new MemoryStream();
        using (var w = TransferWriters.Create(options, ms))
        {
            w.Begin(Cols);
            foreach (var r in rows)
            {
                w.WriteRow(r);
            }

            w.Finish();
        }

        return ms.ToArray();
    }

    [Fact]
    public void Csv_quotes_special_values_and_round_trips()
    {
        var bytes = Write(new ExportOptions(TransferFormat.Csv),
            [1m, "a,b", Hired], [2m, "say \"hi\"\nnext", null], [3m, null, Hired]);

        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("ID,NAME,HIRED\r\n1,\"a,b\",2026-03-05 14:30:15\r\n", text, StringComparison.Ordinal);

        using var ms = new MemoryStream(bytes);
        var rows = TransferFileReader.ReadRows(TransferFormat.Csv, ms).ToList();
        Assert.Equal(4, rows.Count);
        Assert.Equal(["ID", "NAME", "HIRED"], rows[0].Cast<string>());
        Assert.Equal("a,b", rows[1][1]);
        Assert.Equal("say \"hi\"\nnext", rows[2][1]);
        Assert.Equal(string.Empty, rows[2][2]);
    }

    [Fact]
    public void Tsv_uses_tab_and_no_header_when_asked()
    {
        var bytes = Write(new ExportOptions(TransferFormat.Tsv) { IncludeHeader = false }, [1m, "x\ty", Hired]);

        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal("1\t\"x\ty\"\t2026-03-05 14:30:15\r\n", text);
    }

    [Fact]
    public void Json_is_valid_with_native_numbers_and_nulls()
    {
        var bytes = Write(new ExportOptions(TransferFormat.Json), ["12.50", "한글 \"q\"", Hired], [3m, null, null]);

        using var doc = JsonDocument.Parse(bytes);
        var arr = doc.RootElement;
        Assert.Equal(2, arr.GetArrayLength());
        Assert.Equal(12.5m, arr[0].GetProperty("ID").GetDecimal());
        Assert.Equal("한글 \"q\"", arr[0].GetProperty("NAME").GetString());
        Assert.Equal("2026-03-05T14:30:15", arr[0].GetProperty("HIRED").GetString());
        Assert.Equal(JsonValueKind.Null, arr[1].GetProperty("NAME").ValueKind);
    }

    [Fact]
    public void Insert_statements_use_sql_literals()
    {
        var bytes = Write(new ExportOptions(TransferFormat.Insert) { InsertTableName = "HR.EMP" }, [1m, "O'Neil", Hired], [2m, null, null]);

        var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("INSERT INTO HR.EMP (ID, NAME, HIRED) VALUES (1, 'O''Neil', TO_DATE('2026-03-05 14:30:15', 'YYYY-MM-DD HH24:MI:SS'));", lines[0]);
        Assert.Equal("INSERT INTO HR.EMP (ID, NAME, HIRED) VALUES (2, NULL, NULL);", lines[1]);
    }

    [Fact]
    public void Xlsx_keeps_values_types_and_splits_sheets()
    {
        var rows = Enumerable.Range(1, 7).Select(i => new object?[] { i.ToString(System.Globalization.CultureInfo.InvariantCulture), "n" + i, Hired.AddDays(i) }).ToList();
        rows[1][1] = "ctl\u0001x\ny"; // 제어 문자는 빠지고 줄바꿈은 남는다
        rows[2][0] = "12345678901234567890"; // 유효숫자 15자리 초과 → 글자
        var bytes = Write(new ExportOptions(TransferFormat.Xlsx) { MaxRowsPerSheet = 3 }, [.. rows]);

        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        Assert.Null(zip.GetEntry("xl/worksheets/sheet4.xml"));

        using var ms = new MemoryStream(bytes);
        var first = TransferFileReader.ReadRows(TransferFormat.Xlsx, ms).ToList();
        Assert.Equal(4, first.Count); // 헤더 + 3행
        Assert.Equal(["ID", "NAME", "HIRED"], first[0].Cast<string>());
        Assert.Equal("1", first[1][0]);
        Assert.Equal(Hired.AddDays(1), first[1][2]);
        Assert.Equal("ctlx\ny", first[2][1]);
        Assert.Equal("12345678901234567890", first[3][0]);

        using var last = new StreamReader(zip.GetEntry("xl/worksheets/sheet3.xml")!.Open());
        Assert.Contains("n7", last.ReadToEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public void Xlsx_empty_result_still_has_header_sheet()
    {
        var bytes = Write(new ExportOptions(TransferFormat.Xlsx));

        using var ms = new MemoryStream(bytes);
        var rows = TransferFileReader.ReadRows(TransferFormat.Xlsx, ms).ToList();
        Assert.Single(rows);
        Assert.Equal(["ID", "NAME", "HIRED"], rows[0].Cast<string>());
    }

    [Fact]
    public void Csv_reader_handles_crlf_blank_lines_and_missing_trailing_newline()
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("a,b\r\n\r\n1,\"x\"\"y\"\n2,"));

        var rows = TransferFileReader.ReadRows(TransferFormat.Csv, ms).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("x\"y", rows[1][1]);
        Assert.Equal(["2", string.Empty], rows[2].Cast<string>());
    }

    private static ColumnInfo Col(string type) => new("C", 1, type);

    [Theory]
    [InlineData("NUMBER", "12.5", "12.5")]
    [InlineData("NUMBER", "+3", "3")]
    [InlineData("NUMBER", "1.5E3", "1.5E3")]
    [InlineData("NUMBER", "", "NULL")]
    [InlineData("VARCHAR2", "O'Neil", "'O''Neil'")]
    [InlineData("DATE", "2026-03-05 14:30:15", "TO_DATE('2026-03-05 14:30:15', 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("DATE", "2026-03-05", "TO_DATE('2026-03-05 00:00:00', 'YYYY-MM-DD HH24:MI:SS')")]
    [InlineData("TIMESTAMP(6)", "2026-03-05 14:30:15.25", "TO_TIMESTAMP('2026-03-05 14:30:15.250000', 'YYYY-MM-DD HH24:MI:SS.FF6')")]
    [InlineData("RAW", "0xCAFE", "HEXTORAW('CAFE')")]
    [InlineData("BOOLEAN", "yes", "TRUE")]
    public void Converter_makes_typed_literals(string type, string input, string expected)
        => Assert.Equal(expected, ImportValueConverter.ToLiteral(Col(type), input));

    [Theory]
    [InlineData("NUMBER", "abc")]
    [InlineData("NUMBER", "1,5")]
    [InlineData("DATE", "내일")]
    [InlineData("RAW", "XYZ")]
    [InlineData("BOOLEAN", "maybe")]
    public void Converter_rejects_values_that_do_not_fit(string type, string input)
        => Assert.Throws<FormatException>(() => ImportValueConverter.ToLiteral(Col(type), input));

    [Fact]
    public void Converter_splits_long_clob_literals()
    {
        var literal = ImportValueConverter.ToLiteral(Col("CLOB"), new string('가', 4000));

        Assert.StartsWith("TO_CLOB('", literal, StringComparison.Ordinal);
        Assert.True(literal.Split("||").Length >= 4);
    }

    [Fact]
    public async Task Read_only_profile_cannot_import_and_nothing_reaches_driver_beyond_gate()
    {
        var session = new DriverlessSession(new ConnectionProfile("ro", "u", "h:1521/s", ReadOnly: true));
        var meta = new OneTableMetadata(new TableInfo(new DbObjectRef("U", "T", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER")]));
        var service = new OracleDataTransferService(new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500), meta);
        var options = new ImportOptions(TransferFormat.Csv, new DbObjectRef("U", "T", "TABLE"), [new ColumnMapping("ID", "ID")]);
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("ID\n1\n2\n"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(session, options, csv, null, default));

        Assert.Contains("정책", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, session.DriverTouches);
    }

    [Fact]
    public async Task Unknown_mapping_columns_are_rejected_before_any_insert()
    {
        var session = new DriverlessSession(new ConnectionProfile("p", "u", "h:1521/s"));
        var meta = new OneTableMetadata(new TableInfo(new DbObjectRef("U", "T", "TABLE"), [new ColumnInfo("ID", 1, "NUMBER")]));
        var service = new OracleDataTransferService(new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500), meta);
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("ID\n1\n"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(session,
            new ImportOptions(TransferFormat.Csv, new DbObjectRef("U", "T", "TABLE"), [new ColumnMapping("ID", "NOPE")]), csv, null, default));
        csv.Position = 0;
        await Assert.ThrowsAsync<ArgumentException>(() => service.ImportAsync(session,
            new ImportOptions(TransferFormat.Csv, new DbObjectRef("U", "T", "TABLE"), [new ColumnMapping("MISSING", "ID")]), csv, null, default));
    }

    [Theory]
    [InlineData(3, 2, true)]
    [InlineData(3, 2, false)]
    [InlineData(4, 2, true)]
    [InlineData(0, 2, true)]
    [InlineData(3, 500, true)]
    public async Task Export_counts_each_row_once_across_pages(int total, int pageSize, bool lastFlagged)
    {
        var rs = new PagedResultSet(total, pageSize, lastFlagged);
        var service = new OracleDataTransferService(new FixedExecution(rs), new OneTableMetadata(new TableInfo(new DbObjectRef("U", "T", "TABLE"), [])));
        using var ms = new MemoryStream();

        var summary = await service.ExportAsync(new DriverlessSession(new ConnectionProfile("p", "u", "h")),
            new SqlStatement("SELECT 1", default, StatementKind.Query), new Dictionary<string, BindValue>(),
            new ExportOptions(TransferFormat.Insert), ms, null, default);

        Assert.Equal(total, summary.Rows);
        Assert.Equal(total, Regex.Count(Encoding.UTF8.GetString(ms.ToArray()), "^INSERT INTO ", RegexOptions.Multiline));
    }

    private sealed class FixedExecution(IResultSet rs) : IExecutionService
    {
        public Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
            => Task.FromResult(new ExecutionResult(stmt, ExecutionStatus.Succeeded) { ResultSet = rs });

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class PagedResultSet(int total, int pageSize, bool lastFlagged) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } = [new(0, "ID", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => false;

        public long? KnownRowCount => null;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var first = pageIndex * pageSize;
            var count = Math.Max(0, Math.Min(pageSize, total - first));
            IReadOnlyList<object?> col = Enumerable.Range(first, count).Select(i => (object?)(decimal)i).ToList();
            return Task.FromResult(new ResultPage(pageIndex, first, count, [col]) { IsLast = lastFlagged && first + count >= total });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OneTableMetadata(TableInfo table) : IMetadataProvider
    {
        public Task<TableInfo> GetTableAsync(ISession s, DbObjectRef t, CancellationToken ct) => Task.FromResult(table);

        public Task<IReadOnlyList<string>> ListSchemasAsync(ISession s, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<DbObjectRef>> ListObjectsAsync(ISession s, ObjectQuery q, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<ProcedureInfo>> GetProceduresAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetDdlAsync(ISession s, DbObjectRef obj, DdlOptions o, CancellationToken ct) => throw new NotSupportedException();

        public Task<ObjectDetail> GetObjectDetailAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<string> GetSourceAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<CompileError>> GetCompileErrorsAsync(ISession s, DbObjectRef obj, CancellationToken ct) => throw new NotSupportedException();
    }
}

internal sealed class SyncProgress(Action<long> action) : IProgress<long>
{
    public void Report(long value) => action(value);
}

/// <summary>26ai 컨테이너: 서버에서 다시 스트리밍한 내보내기, 오류 행 보고가 있는 가져오기, 정책 관문.</summary>
[Trait("Category", "Integration")]
public sealed class DataTransferTests : IClassFixture<OracleFreeFixture>, IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private readonly string _tag = "XFER_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly List<string> _files = [];
    private OracleSessionManager _manager = null!;

    public DataTransferTests(OracleFreeFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }

        foreach (var f in _files.Where(File.Exists))
        {
            File.Delete(f);
        }

        await using var raw = await RawAsync();
        foreach (var suffix in new[] { "_SRC", "_DST", "_ERR", "_RO" })
        {
            try
            {
                await ExecAsync(raw, "DROP TABLE " + _tag + suffix + " PURGE");
            }
            catch (OracleException)
            {
                // 만들지 않은 표는 무시.
            }
        }
    }

    private OracleDataTransferService Service() =>
        new(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500),
            new OracleMetadataProvider(new MemoryMetadataCache(), new ExecBus(), Timeout.InfiniteTimeSpan));

    private async Task<OracleSession> OpenAsync(bool readOnly = false)
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource, ReadOnly: readOnly);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<OracleConnection> RawAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("free-system", "system", _db.DataSource).BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<decimal> ScalarAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqlStatement Q(string text) => new(text, default, StatementKind.Query) { Verb = "SELECT" };

    private string TempFile(string ext)
    {
        var p = Path.Combine(Path.GetTempPath(), _tag + "." + ext);
        _files.Add(p);
        return p;
    }

    private DbObjectRef Ref(string suffix) => new("SYSTEM", _tag + suffix, "TABLE");

    [Fact]
    public async Task Export_100k_rows_to_xlsx_matches_source_and_splits_sheets()
    {
        await using var s = await OpenAsync();
        var path = TempFile("xlsx");
        TransferSummary summary;
        await using (var fs = File.Create(path))
        {
            summary = await Service().ExportAsync(s,
                Q("SELECT level AS id, 'name ' || level AS name, DATE '2026-01-01' + MOD(level, 365) AS d, level / 8 AS ratio FROM dual CONNECT BY level <= 100000"),
                new Dictionary<string, BindValue>(), new ExportOptions(TransferFormat.Xlsx) { MaxRowsPerSheet = 40000 }, fs, null, default);
        }

        Assert.Equal(100000, summary.Rows);
        using var zip = ZipFile.OpenRead(path);
        Assert.NotNull(zip.GetEntry("xl/worksheets/sheet3.xml"));
        Assert.Null(zip.GetEntry("xl/worksheets/sheet4.xml"));

        await using var rd = File.OpenRead(path);
        var rows = TransferFileReader.ReadRows(TransferFormat.Xlsx, rd).ToList();
        Assert.Equal(40001, rows.Count);
        Assert.Equal("40000", rows[^1][0]);
        Assert.Equal("name 40000", rows[^1][1]);
        Assert.Equal(new DateTime(2026, 1, 1).AddDays(40000 % 365), rows[^1][2]);
        Assert.Equal("5000", rows[^1][3]);
        Assert.Equal("0.125", rows[1][3]);
    }

    [Fact]
    public async Task Export_all_formats_then_csv_import_round_trips_with_types()
    {
        await using var s = await OpenAsync();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_tag}_SRC (id NUMBER(10) PRIMARY KEY, name VARCHAR2(50), amt NUMBER(14,3), born DATE, ts TIMESTAMP(6), note CLOB)");
        await ExecAsync(raw, $"CREATE TABLE {_tag}_DST AS SELECT * FROM {_tag}_SRC WHERE 1 = 0");
        await ExecAsync(raw, $"INSERT INTO {_tag}_SRC VALUES (1, 'plain', 12.345, DATE '2026-02-03', TIMESTAMP '2026-02-03 04:05:06.123456', 'hello')");
        await ExecAsync(raw, $"INSERT INTO {_tag}_SRC VALUES (2, 'com,ma \"q\"' || CHR(10) || 'line2', -0.5, NULL, NULL, NULL)");
        await ExecAsync(raw, $"INSERT INTO {_tag}_SRC VALUES (3, NULL, 1234567890.125, DATE '1999-12-31', NULL, 'x')");
        await ExecAsync(raw, "COMMIT");
        var svc = Service();
        var query = Q($"SELECT * FROM {_tag}_SRC ORDER BY id");
        var none = new Dictionary<string, BindValue>();

        var csv = new MemoryStream();
        var cs = await svc.ExportAsync(s, query, none, new ExportOptions(TransferFormat.Csv), csv, null, default);
        Assert.True(cs.Rows == 3, "csv rows=" + cs.Rows + " " + Encoding.UTF8.GetString(csv.ToArray()));

        var json = new MemoryStream();
        await svc.ExportAsync(s, query, none, new ExportOptions(TransferFormat.Json), json, null, default);
        using (var doc = JsonDocument.Parse(json.ToArray()))
        {
            Assert.True(doc.RootElement.GetArrayLength() == 3, Encoding.UTF8.GetString(json.ToArray()));
            Assert.Equal(1234567890.125m, doc.RootElement[2].GetProperty("AMT").GetDecimal());
            Assert.Equal(JsonValueKind.Null, doc.RootElement[1].GetProperty("BORN").ValueKind);
        }

        var ins = new MemoryStream();
        await svc.ExportAsync(s, query, none, new ExportOptions(TransferFormat.Insert) { InsertTableName = $"{_tag}_DST" }, ins, null, default);
        var insText = Encoding.UTF8.GetString(ins.ToArray());
        Assert.True(Regex.Count(insText, "^INSERT INTO ", RegexOptions.Multiline) == 3, insText);

        // 내보낸 INSERT 문을 그대로 실행하면 원본과 같은 표가 된다.
        foreach (var line in Encoding.UTF8.GetString(ins.ToArray()).Split(";\n", StringSplitOptions.RemoveEmptyEntries))
        {
            await ExecAsync(raw, line.Trim().TrimEnd(';'));
        }

        Assert.Equal(0, await ScalarAsync(raw, $"SELECT COUNT(*) FROM (SELECT id, name, amt, born, ts, DBMS_LOB.SUBSTR(note, 100) n FROM {_tag}_SRC MINUS SELECT id, name, amt, born, ts, DBMS_LOB.SUBSTR(note, 100) FROM {_tag}_DST)"));
        await ExecAsync(raw, $"DELETE FROM {_tag}_DST");
        await ExecAsync(raw, "COMMIT");

        // CSV 가져오기: 열 순서를 바꾼 매핑 + 대상에 없는 열 건너뛰기.
        csv.Position = 0;
        var opts = new ImportOptions(TransferFormat.Csv, Ref("_DST"),
            [new ColumnMapping("ID", "ID"), new ColumnMapping("NAME", "NAME"), new ColumnMapping("AMT", "AMT"), new ColumnMapping("BORN", "BORN"),
             new ColumnMapping("TS", "TS"), new ColumnMapping("NOTE", "NOTE")]) { BatchSize = 2 };
        var imp = await svc.ImportAsync(s, opts, csv, null, default);

        Assert.True(imp.Rows == 3, $"rows={imp.Rows} warnings={string.Join('|', imp.Warnings)} csv={Encoding.UTF8.GetString(csv.ToArray())}");
        Assert.Empty(imp.Warnings);
        // CSV 의 빈 칸은 NULL 로 들어간다.
        Assert.Equal(0, await ScalarAsync(raw, $"SELECT COUNT(*) FROM (SELECT id, name, amt, born, ts, DBMS_LOB.SUBSTR(note, 100) n FROM {_tag}_SRC MINUS SELECT id, name, amt, born, ts, DBMS_LOB.SUBSTR(note, 100) FROM {_tag}_DST)"));
    }

    [Fact]
    public async Task Import_reports_error_rows_separately_and_keeps_good_rows()
    {
        await using var s = await OpenAsync();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_tag}_ERR (id NUMBER(5) PRIMARY KEY, name VARCHAR2(5), born DATE)");
        var csv = "ID,NAME,BORN\n1,aaa,2026-01-01\n2,toolongvalue,2026-01-02\nxx,bad,2026-01-03\n4,ok,not-a-date\n1,dup,2026-01-05\n6,ok6,2026-01-06\n7,ok7,\n";
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var progress = new List<long>();

        var r = await Service().ImportAsync(s,
            new ImportOptions(TransferFormat.Csv, Ref("_ERR"), [new ColumnMapping("ID", "ID"), new ColumnMapping("NAME", "NAME"), new ColumnMapping("BORN", "BORN")]) { BatchSize = 3 },
            ms, new Progress<long>(progress.Add), default);

        Assert.Equal(3, r.Rows); // ID 1, 6, 7
        Assert.Equal(4, r.Warnings.Count);
        Assert.Contains(r.Warnings, w => w.StartsWith("행 3:", StringComparison.Ordinal) && w.Contains("ORA-12899", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.StartsWith("행 4:", StringComparison.Ordinal) && w.Contains("ID", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.StartsWith("행 5:", StringComparison.Ordinal) && w.Contains("날짜", StringComparison.Ordinal));
        Assert.Contains(r.Warnings, w => w.StartsWith("행 6:", StringComparison.Ordinal) && w.Contains("ORA-00001", StringComparison.Ordinal));
        Assert.Equal(3, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_ERR"));
        Assert.Equal(0, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_ERR WHERE id IN (2)"));
        Assert.Equal(1, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_ERR WHERE id = 7 AND born IS NULL"));
        Assert.False(s.HasPendingTransaction); // 끝에서 커밋했다
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task Import_without_header_uses_one_based_ordinals_and_xlsx_source()
    {
        await using var s = await OpenAsync();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_tag}_DST (id NUMBER(5), name VARCHAR2(20), born DATE)");

        // xlsx 만들기(내보내기 쓰기 경로 재사용): 헤더 없음
        var xlsx = new MemoryStream();
        var cols = new ResultColumn[] { new(0, "A", "NUMBER", ColumnDataKind.Number), new(1, "B", "VARCHAR2", ColumnDataKind.Text), new(2, "C", "DATE", ColumnDataKind.Date) };
        using (var w = TransferWriters.Create(new ExportOptions(TransferFormat.Xlsx) { IncludeHeader = false }, xlsx))
        {
            w.Begin(cols);
            w.WriteRow(["1", "가나다", new DateTime(2026, 5, 6, 7, 8, 9)]);
            w.WriteRow(["2", "라마바", null]);
            w.Finish();
        }

        xlsx.Position = 0;
        var r = await Service().ImportAsync(s,
            new ImportOptions(TransferFormat.Xlsx, Ref("_DST"), [new ColumnMapping("2", "NAME"), new ColumnMapping("1", "ID"), new ColumnMapping("3", "BORN")]) { HasHeader = false },
            xlsx, null, default);

        Assert.Equal(2, r.Rows);
        Assert.Empty(r.Warnings);
        Assert.Equal(1, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_DST WHERE id = 1 AND name = '가나다' AND born = TIMESTAMP '2026-05-06 07:08:09'"));
        Assert.Equal(1, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_DST WHERE id = 2 AND born IS NULL"));
    }

    [Fact]
    public async Task Import_without_commit_at_end_commits_per_batch_and_with_it_rolls_back_on_cancel()
    {
        await using var s = await OpenAsync();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_tag}_DST (id NUMBER(5))");
        var csv = "ID\n" + string.Join('\n', Enumerable.Range(1, 10)) + "\n";
        var map = new[] { new ColumnMapping("ID", "ID") };

        using var cts = new CancellationTokenSource();
        var cancelAfter = new SyncProgress(n =>
        {
            if (n >= 4)
            {
                cts.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service().ImportAsync(s,
            new ImportOptions(TransferFormat.Csv, Ref("_DST"), map) { BatchSize = 2, CommitAtEnd = true },
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), cancelAfter, cts.Token));
        Assert.Equal(0, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_DST"));

        var r = await Service().ImportAsync(s,
            new ImportOptions(TransferFormat.Csv, Ref("_DST"), map) { BatchSize = 4, CommitAtEnd = false },
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), null, default);
        Assert.Equal(10, r.Rows);
        Assert.Equal(10, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_DST"));
    }

    [Fact]
    public async Task Import_into_read_only_profile_is_denied_by_policy_gate_and_inserts_nothing()
    {
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {_tag}_RO (id NUMBER)");
        await using var s = await OpenAsync(readOnly: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Service().ImportAsync(s,
            new ImportOptions(TransferFormat.Csv, Ref("_RO"), [new ColumnMapping("ID", "ID")]),
            new MemoryStream(Encoding.UTF8.GetBytes("ID\n1\n2\n")), null, default));

        Assert.Contains("정책", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await ScalarAsync(raw, $"SELECT COUNT(*) FROM {_tag}_RO"));
    }
}
