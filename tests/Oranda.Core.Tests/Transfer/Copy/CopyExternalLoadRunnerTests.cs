using System.Runtime.CompilerServices;
using System.Text;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Transfer.Copy;
using Oranda.Core.Transfer.Copy.CrossDbms;
using Oranda.Core.Transfer.Copy.ExternalLoad;
using Oranda.Testing;

namespace Oranda.Core.Tests.Transfer.Copy;

/// <summary>
/// 실행기의 외부 적재 도구 경로(조정 결정 88·89, <c>CopyRunner.External.cs</c>, 서버·도구 없음): 대상 확정 → 데이터 파일 → 제어 파일 → 도구 실행 → 로그 읽기.
/// 가짜 도구가 실행 순간의 작업 폴더(데이터·제어 파일)를 보고, 작업 폴더의 로그 파일에 SQL*Loader 형식 로그를 쓴 뒤 종료 코드를 돌려준다.
/// </summary>
public sealed class CopyExternalLoadRunnerTests : IDisposable
{
    private const string Password = "Ld#Secret@42!";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "oranda-xl-runner-" + Guid.NewGuid().ToString("N"));

    public CopyExternalLoadRunnerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static string Log(long loaded, long rejected = 0, string rejections = "") => $"""
        SQL*Loader: Release 19.0.0.0.0 - Production on Tue Nov 10 21:14:03 2026

        Table "DST"."ORDERS", loaded from every logical record.

        {rejections}
        Table "DST"."ORDERS":
          {loaded} Rows successfully loaded.
          {rejected} Rows not loaded due to data errors.
          0 Rows not loaded because all WHEN clauses were failed.
          0 Rows not loaded because all fields were null.

        Total logical records read:           {loaded + rejected}
        """;

    // ---- 성공 ----

    [Fact]
    public async Task Tool_load_commits_the_target_first_writes_the_source_rows_and_takes_the_count_from_the_log()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (start, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "load.log"), Log(3));
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        };
        f.TargetCount = 3;

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Succeeded, table.Outcome);
        Assert.Equal(3, table.SourceRows);
        Assert.Contains("외부 도구(SQL*Loader)로 넣었습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains(f.Logs, l => l.StartsWith("끝: 성공", StringComparison.Ordinal) && l.Contains("· 3행 넣음", StringComparison.Ordinal));

        // 실행 순간의 작업 폴더: 작업 폴더 = 실행 폴더, 데이터·제어 파일이 있고 데이터 파일에 원본 3행.
        var seen = Assert.Single(f.Tool.Runs);
        Assert.Equal(seen.Directory, seen.Start.WorkingDirectory);
        Assert.StartsWith(Path.Combine(_root, LoaderWorkspace.FolderName), seen.Directory, StringComparison.OrdinalIgnoreCase);
        Assert.True(seen.DataExisted && seen.ControlExisted);
        Assert.Equal(3, seen.Records);
        Assert.StartsWith("OPTIONS (DIRECT=FALSE", seen.Control, StringComparison.Ordinal); // 이미 있는 테이블 = 일반 경로
        Assert.Equal(@"C:\ic\sqlldr.exe", seen.Start.FileName);

        // 정리 DELETE → 확정(COMMIT) → 도구. 도구 경로에서는 배열 INSERT 가 없다.
        var delete = f.All.FindIndex(x => x.StartsWith("EXEC DELETE", StringComparison.Ordinal));
        var tool = f.All.IndexOf("TOOL");
        var commit = f.All.FindLastIndex(tool, x => x == "CTL COMMIT");
        Assert.True(delete >= 0 && delete < commit && commit < tool, string.Join(" | ", f.All));
        Assert.DoesNotContain(f.All, x => x.StartsWith("BULK ", StringComparison.Ordinal));

        // 끝나면 작업 폴더를 지운다.
        Assert.False(Directory.Exists(seen.Directory));
    }

    [Fact]
    public async Task Password_reaches_the_tool_only_on_standard_input_and_never_the_arguments_or_the_run_log()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (start, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "load.log"), Log(3));
            return new ExternalProcessResult(0, string.Empty, string.Empty);
        };

        var result = await f.RunAsync();

        var seen = Assert.Single(f.Tool.Runs);
        Assert.All(seen.Start.Arguments, a => Assert.DoesNotContain(Password, a, StringComparison.Ordinal));
        Assert.All(seen.Start.Environment.Values, v => Assert.DoesNotContain(Password, v ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(Password, seen.Start.ToString(), StringComparison.Ordinal);
        Assert.Equal("DST/\"" + Password + "\"@localhost:1521/FREEPDB1\n", seen.Start.StandardInput); // 표준 입력 방식(userid 전체)
        Assert.DoesNotContain(Password, seen.ParFile ?? string.Empty, StringComparison.Ordinal);

        Assert.NotEmpty(f.Logs);
        Assert.Contains(f.Logs, l => l.StartsWith("SQL*Loader 실행:", StringComparison.Ordinal));
        Assert.All(f.Logs, l => Assert.DoesNotContain(Password, l, StringComparison.Ordinal));
        Assert.All(result.Notices, n => Assert.DoesNotContain(Password, n, StringComparison.Ordinal));
    }

    // ---- 거절 행 ----

    [Fact]
    public async Task Rejected_records_within_the_limit_become_failed_rows_with_their_keys()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert, maxFailedRows: 10);
        f.Tool.OnRun = (start, dir) =>
        {
            const string rejections = """
                Record 2: Rejected - Error on table "DST"."ORDERS".
                ORA-00001: unique constraint (DST.PK_ORDERS) violated

                """;
            File.WriteAllText(Path.Combine(dir, "load.log"), Log(2, 1, rejections));
            return new ExternalProcessResult(2, string.Empty, string.Empty); // 경고(거절 행 있음)
        };
        f.TargetCount = 2;

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.PartiallyFailed, result.Outcome);
        var failed = Assert.Single(result.FailedRows);
        Assert.Equal("ORDERS", failed.Table);
        Assert.Equal("2", failed.Key["ID"]); // 두 번째 레코드 = 원본 두 번째 행(ID 2)
        Assert.Contains("ORA-00001", failed.Error, StringComparison.Ordinal);
        Assert.Single(f.RunRecord.FailedRows);
        Assert.Contains(f.Logs, l => l.StartsWith("끝: 일부 실패", StringComparison.Ordinal) && l.Contains("· 2행 넣음", StringComparison.Ordinal));
        Assert.DoesNotContain(f.All, x => x.StartsWith("BULK ", StringComparison.Ordinal)); // 다시 넣지 않는다
    }

    // ---- 실패 → 기본 방식 ----

    [Fact]
    public async Task Tool_that_cannot_start_cleans_the_table_again_and_loads_it_the_standard_way()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (_, _) => throw new ExternalProcessException("sqlldr 을(를) 띄우지 못했습니다.");
        f.TargetCount = 3;

        var result = await f.RunAsync();

        AssertFellBack(f, result);
    }

    [Fact]
    public async Task Tool_failure_exit_code_cleans_the_table_again_and_loads_it_the_standard_way()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (start, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "load.log"), "SQL*Loader-350: Syntax error at line 3.\n");
            return new ExternalProcessResult(1, string.Empty, string.Empty);
        };
        f.TargetCount = 3;

        var result = await f.RunAsync();

        AssertFellBack(f, result);
    }

    [Fact]
    public async Task Login_failure_shows_the_tool_output_and_its_ora_cause_with_the_password_masked()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (start, dir) => new ExternalProcessResult(
            1,
            "SQL*Loader: Release 23.0.0.0.0\n\nSQL*Loader-128: unable to begin a session\n\nORA-01017: invalid credential or not authorized; logon denied\n",
            "echo " + Password + "\n"); // 비밀번호가 출력에 섞여 와도 가린다
        f.TargetCount = 3;

        var result = await f.RunAsync();

        AssertFellBack(f, result);
        var output = Assert.Single(f.Logs, l => l.StartsWith("SQL*Loader 출력(실패 원인 확인용):", StringComparison.Ordinal));
        Assert.Contains("[표준 출력]", output, StringComparison.Ordinal);
        Assert.Contains("ORA-01017", output, StringComparison.Ordinal);
        Assert.Contains("echo ********", output, StringComparison.Ordinal);
        Assert.All(f.Logs, l => Assert.DoesNotContain(Password, l, StringComparison.Ordinal));
        Assert.Contains("ORA-01017", Assert.Single(result.Tables).Message, StringComparison.Ordinal); // 실패 이유에 원인 코드
        Assert.All(result.Notices, n => Assert.DoesNotContain(Password, n, StringComparison.Ordinal));
    }

    private static void AssertFellBack(Fixture f, CopyRunResult result)
    {
        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Single(f.Tool.Runs);

        // 정리 DELETE 두 번(처음 정리 + 도구 실패 뒤 다시 정리), 그 뒤 배열 INSERT 로 3행.
        var deletes = f.All.Select((x, i) => (x, i)).Where(p => p.x.StartsWith("EXEC DELETE", StringComparison.Ordinal)).Select(p => p.i).ToList();
        Assert.Equal(2, deletes.Count);
        var tool = f.All.IndexOf("TOOL");
        Assert.True(deletes[0] < tool && tool < deletes[1]);
        var insert = Assert.Single(f.Bulk.Dml);
        Assert.StartsWith("INSERT INTO \"DST\".\"ORDERS\"", insert.Text, StringComparison.Ordinal);
        Assert.Equal(3, insert.Rows);
        Assert.True(f.All.IndexOf("BULK " + insert.Text) > deletes[1]);

        Assert.Contains(result.Notices, n => n.Contains("기본 방식", StringComparison.Ordinal) && n.StartsWith("ORDERS:", StringComparison.Ordinal));
        Assert.Contains("기본 방식으로 다시 넣었습니다", Assert.Single(result.Tables).Message, StringComparison.Ordinal);
        Assert.Contains(f.Logs, l => l.Contains("· 3행 넣음", StringComparison.Ordinal));
        Assert.False(Directory.Exists(f.Tool.Runs[0].Directory));
    }

    [Fact]
    public async Task Append_only_existing_table_fails_without_a_retry_when_the_tool_loaded_some_rows()
    {
        var f = new Fixture(_root, TargetAction.AppendOnly);
        f.Tool.OnRun = (start, dir) =>
        {
            File.WriteAllText(Path.Combine(dir, "load.log"), Log(2) + "\nSQL*Loader-2026: the load was aborted because SQL Loader cannot continue.\n");
            return new ExternalProcessResult(3, string.Empty, string.Empty); // 치명
        };

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Failed, result.Outcome);
        var table = Assert.Single(result.Tables);
        Assert.Equal(CopyOutcome.Failed, table.Outcome);
        Assert.Contains("다시 넣지 않았습니다", table.Message, StringComparison.Ordinal);
        Assert.Contains("2행", table.Message, StringComparison.Ordinal);
        Assert.Empty(f.Bulk.Dml); // 기본 방식으로 다시 넣지 않는다
        Assert.DoesNotContain(f.All, x => x.StartsWith("EXEC DELETE", StringComparison.Ordinal)); // 추가만 하기: 지우지 않는다
        Assert.False(Directory.Exists(f.Tool.Runs[0].Directory));
    }

    // ---- 취소 ----

    [Fact]
    public async Task Cancel_while_the_tool_runs_stops_the_run_and_removes_the_work_folder()
    {
        using var cts = new CancellationTokenSource();
        var f = new Fixture(_root, TargetAction.DeleteAllInsert);
        f.Tool.OnRun = (_, _) =>
        {
            cts.Cancel(); // 실행기가 프로세스 트리를 끝내고 OperationCanceledException 을 올린 것처럼
            throw new OperationCanceledException(cts.Token);
        };

        var result = await f.RunAsync(cts.Token);

        Assert.Equal(CopyOutcome.Stopped, result.Outcome);
        Assert.Empty(f.Bulk.Dml);
        var seen = Assert.Single(f.Tool.Runs);
        Assert.False(Directory.Exists(seen.Directory));
        Assert.False(File.Exists(Path.Combine(seen.Directory, SqlLoaderCommand.ParFileName)));
    }

    // ---- 기본 방식 테이블 ----

    [Fact]
    public async Task Standard_plan_never_calls_the_tool()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert, load: LoadMethod.Standard);
        f.TargetCount = 3;

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Tool.Runs);
        Assert.False(f.LoginAsked);
        Assert.Equal(3, Assert.Single(f.Bulk.Dml).Rows);
    }

    [Fact]
    public async Task External_plan_without_a_usable_tool_in_the_report_loads_the_standard_way()
    {
        var f = new Fixture(_root, TargetAction.DeleteAllInsert, toolInReport: false);
        f.TargetCount = 3;

        var result = await f.RunAsync();

        Assert.Equal(CopyOutcome.Succeeded, result.Outcome);
        Assert.Empty(f.Tool.Runs);
        Assert.Equal(3, Assert.Single(f.Bulk.Dml).Rows);
    }

    // ---- 가짜 협력자 ----

    private static readonly ExternalToolInfo SqlLoader = new(ExternalLoaderKind.SqlLoader, @"C:\ic\sqlldr.exe", 19, "SQL*Loader: Release 19.0.0.0.0", null);

    private sealed class Fixture
    {
        public Fixture(string root, TargetAction action, int maxFailedRows = 100, LoadMethod load = LoadMethod.ExternalTool, bool toolInReport = true)
        {
            Rule = new TableRule("ORDERS", RuleKind.All, action) { Load = LoadMethod.ExternalTool };
            Job = new CopyJob
            {
                Id = "1", Name = "n", Source = new ConnectionRef("s", "SRC"), Target = new ConnectionRef("t", "DST"),
                SourceOwner = "SRC", TargetOwner = "DST", MaskSalt = "AA==", Tables = [Rule],
                Options = new CopyOptions { MaxFailedRows = maxFailedRows },
            };
            var plan = new CopyTablePlan(Rule, Orders("SRC"), Orders("DST"), ["ID", "NAME"], ["ID"]) { Load = load };
            var builder = new CopySqlBuilder(Job, new ResolvedPeriod(null, null),
                new Dictionary<string, TableInfo> { ["ORDERS"] = plan.Source }, new Dictionary<string, TableInfo> { ["ORDERS"] = plan.Target });
            var preview = new[] { builder.TargetDelete("ORDERS") }.OfType<SqlWithBinds>()
                .Select(d => new PreviewStatement(d.Sql, PreviewSide.Target, PolicyDecisionKind.Allow, null, null)).ToList();
            Report = new PrecheckReport([], [plan], preview, new Dictionary<string, long>()) { ExternalTool = toolInReport ? SqlLoader : null };

            Source = new FakeSession(TestData.Profile(readOnly: true), TestData.Server()) { DbKey = "SRCDB/PDB1/SRC" };
            Target = new FakeSession(TestData.Profile() with { Name = "dev" }, TestData.Server()) { DbKey = "DSTDB/PDB1/DST" };
            Execution = new FakeExecutionService
            {
                Responder = call =>
                {
                    All.Add("EXEC " + call.Statement.Text);
                    return new ExecutionResult(call.Statement, ExecutionStatus.Succeeded);
                },
            };
            Bulk = new FakeBulk(this);
            Tool = new FakeTool(this);
            RunRecord = new FakeRunRecord();
            Kit = new CopyExternalLoad(new UnusedProbe(), Tool, ExternalToolPaths.None, _ =>
            {
                LoginAsked = true;
                return Task.FromResult<ExternalLoadLogin?>(new ExternalLoadLogin("DST", "localhost:1521/FREEPDB1", Password, null));
            })
            {
                WorkRoot = root,
            };
            var parts = new CopyDbmsParts(Dbms.Oracle, new NoCatalog(), new FakeClassifier(), new FakeDialect(), new FakePrecheckDialect(), new PortableCodec(), new FakeRecreateStep());
            Runner = new CopyRunner(Bulk, Execution, new FakeRunLog(RunRecord), parts, parts);
        }

        public TableRule Rule { get; }

        public CopyJob Job { get; }

        public PrecheckReport Report { get; }

        public FakeSession Source { get; }

        public FakeSession Target { get; }

        public FakeExecutionService Execution { get; }

        public FakeBulk Bulk { get; }

        public FakeTool Tool { get; }

        public FakeRunRecord RunRecord { get; }

        public CopyExternalLoad Kit { get; }

        public bool LoginAsked { get; private set; }

        /// <summary>대상 <c>COUNT(*)</c> 의 답.</summary>
        public long TargetCount { get; set; }

        /// <summary>
        /// 보낸 모든 것(순서대로): <c>EXEC</c>=실행 엔진(DELETE), <c>BULK</c>=배열 DML, <c>CTL</c>=트랜잭션 제어(대상만), <c>TOOL</c>=도구 실행.
        /// </summary>
        public List<string> All { get; } = [];

        /// <summary>작업 로그 줄(글).</summary>
        public List<string> Logs { get; } = [];

        private CopyRunner Runner { get; }

        public Task<CopyRunResult> RunAsync(CancellationToken ct = default) =>
            Runner.RunAsync(new CopySessions(Source, Target) { ExternalLoad = Kit }, Job, Report,
                new CopyDecisions(new HashSet<string>(), new HashSet<ForeignKeyRef>(), new HashSet<string>(), false),
                new ResolvedPeriod(null, null), new LogCollector(Logs), new CopyRunControl(), ct);

        private static TableInfo Orders(string owner) =>
            new(new DbObjectRef(owner, "ORDERS", "TABLE"),
            [
                new ColumnInfo("ID", 1, "NUMBER") { Precision = 10, Scale = 0, IsNullable = false },
                new ColumnInfo("NAME", 2, "VARCHAR2") { Length = 20 },
            ])
            {
                Constraints = [new ConstraintInfo("PK_ORDERS", ConstraintKind.PrimaryKey, ["ID"])],
            };
    }

    /// <summary>도구가 본 실행 순간의 작업 폴더.</summary>
    private sealed record ToolRun(ExternalProcessStart Start, string Directory, bool DataExisted, bool ControlExisted, int Records, string? Control, string? ParFile);

    /// <summary>가짜 도구: 실행 순간의 작업 폴더를 남기고 <see cref="OnRun"/> 의 답을 돌려준다(로그 파일은 <see cref="OnRun"/> 이 쓴다).</summary>
    private sealed class FakeTool(Fixture fixture) : IExternalProcessRunner
    {
        public Func<ExternalProcessStart, string, ExternalProcessResult> OnRun { get; set; } = (_, _) => new ExternalProcessResult(0, string.Empty, string.Empty);

        public List<ToolRun> Runs { get; } = [];

        public Task<ExternalProcessResult> RunAsync(ExternalProcessStart start, CancellationToken ct)
        {
            fixture.All.Add("TOOL");
            var dir = start.WorkingDirectory;
            var data = Path.Combine(dir, LoaderDataWriter.DataFileName);
            var control = Path.Combine(dir, SqlLoaderControlFile.FileName);
            var par = Path.Combine(dir, SqlLoaderCommand.ParFileName);
            var records = File.Exists(data) ? CountOf(File.ReadAllText(data, Encoding.UTF8), LoaderDataWriter.RecordTerminator) : -1;
            Runs.Add(new ToolRun(start, dir, File.Exists(data), File.Exists(control), records,
                File.Exists(control) ? File.ReadAllText(control) : null, File.Exists(par) ? File.ReadAllText(par) : null));
            return Task.FromResult(OnRun(start, dir));
        }

        private static int CountOf(string text, string terminator)
        {
            var count = 0;
            for (var i = text.IndexOf(terminator, StringComparison.Ordinal); i >= 0; i = text.IndexOf(terminator, i + terminator.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }
    }

    private sealed class UnusedProbe : IExternalToolProbe
    {
        public ExternalToolLocation Locate(ExternalLoaderKind kind, string? configuredPath) => throw new NotSupportedException("실행기는 도구를 다시 찾지 않는다.");

        public Task<ExternalToolInfo> CheckAsync(ExternalLoaderKind kind, string? configuredPath, CancellationToken ct) =>
            throw new NotSupportedException("실행기는 도구를 다시 확인하지 않는다(사전 점검 결과를 쓴다).");
    }

    private sealed class LogCollector(List<string> lines) : IProgress<CopyProgress>
    {
        public void Report(CopyProgress value)
        {
            if (value.Log is { } entry)
            {
                lock (lines)
                {
                    lines.Add(entry.Text);
                }
            }
        }
    }

    private sealed record DmlCall(string Text, int Rows);

    private sealed class FakeBulk(Fixture fixture) : IBulkExecutionService
    {
        public List<DmlCall> Dml { get; } = [];

        public async IAsyncEnumerable<object?[]> StreamRowsAsync(ISession s, SqlStatement query, IReadOnlyDictionary<string, BindValue> binds,
                                                                 ExecutionOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            if (ReferenceEquals(s, fixture.Source))
            {
                yield return [1L, "kim"];
                yield return [2L, "lee"];
                yield return [3L, "park"];
                yield break;
            }

            yield return [fixture.TargetCount]; // 대상 COUNT(*)
        }

        public Task<BulkResult> ExecuteArrayAsync(ISession s, SqlStatement stmt, IReadOnlyList<BulkColumn> columns, IReadOnlyList<object?[]> rows,
                                                  ExecutionOptions options, CancellationToken ct)
        {
            if (columns.Count == 0)
            {
                if (ReferenceEquals(s, fixture.Target))
                {
                    fixture.All.Add("CTL " + stmt.Text);
                }

                return Task.FromResult(new BulkResult(ExecutionStatus.Succeeded, 0));
            }

            fixture.All.Add("BULK " + stmt.Text);
            Dml.Add(new DmlCall(stmt.Text, rows.Count));
            return Task.FromResult(new BulkResult(ExecutionStatus.Succeeded, rows.Count));
        }
    }

    /// <summary>같은 DBMS 가짜 코덱. 외부 도구 경로가 쓰는 이식 값: 정수 → <see cref="PortableNumber"/>, 글자 → 그대로.</summary>
    private sealed class PortableCodec : IBulkValueCodec
    {
        public bool IsSupported(string serverType) => true;

        public BulkKind KindOf(string serverType) => serverType.StartsWith("VARCHAR", StringComparison.Ordinal) ? BulkKind.Text : BulkKind.Other;

        public BindDataType BindTypeOf(string serverType) => BindDataType.Varchar2;

        public string? ToKeyText(object? value, string serverType) => value?.ToString();

        public object? FromKeyText(string? text, string serverType) => text;

        public long? ToInt64(object? value) => value is long l ? l : null;

        public string? ToText(object? value) => value?.ToString();

        public string? NumberText(object? value) => value is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

        public bool IsZero(object? value) => value is 0L;

        public long ToInt64Exact(object? value) => Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);

        public object? ToPortable(object? value, string serverType) => value switch
        {
            null => null,
            long l => PortableNumber.FromText(l.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            string s => s,
            _ => throw new PortableConversionException("모르는 값"),
        };
    }

    private sealed class FakeRunRecord : ICopyRunRecord
    {
        public List<FailedRow> FailedRows { get; } = [];

        public Task RecordDisabledAsync(IReadOnlyList<DisabledObject> objects, CancellationToken ct) => Task.CompletedTask;

        public Task RecordStatementAsync(string sql, CancellationToken ct) => Task.CompletedTask;

        public Task RecordTableAsync(TableRunResult result, CancellationToken ct) => Task.CompletedTask;

        public Task RecordFailedRowAsync(FailedRow row, CancellationToken ct)
        {
            FailedRows.Add(row);
            return Task.CompletedTask;
        }

        public Task CompleteAsync(CopyOutcome outcome, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRunLog(FakeRunRecord record) : ICopyRunLog
    {
        public Task<ICopyRunRecord> BeginAsync(CopyRunHeader header, CancellationToken ct) => Task.FromResult<ICopyRunRecord>(record);

        public Task<IReadOnlyList<UnfinishedRun>> FindUnfinishedAsync(string jobId, CancellationToken ct) => Task.FromResult<IReadOnlyList<UnfinishedRun>>([]);

        public Task MarkRecoveredAsync(string runFile, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoCatalog : Oranda.Core.Catalog.ISqlCatalog
    {
        public Oranda.Core.Catalog.CatalogQuery Get(string id, ServerInfo server) =>
            new(new Oranda.Core.Catalog.CatalogHeader(id, 11, [], Oranda.Core.Catalog.CatalogLicense.None, Oranda.Core.Catalog.CatalogRac.V, id + ".sql"), id);
    }

    private sealed class FakeClassifier : IDbErrorClassifier
    {
        public Dbms Dbms => Dbms.Oracle;

        public DbErrorKind Classify(int number) => DbErrorKind.Other;

        public DbError? FromException(Exception exception) => null;
    }

    private sealed class FakeRecreateStep : ICopyRecreateStep
    {
        public Task<PreservedDdl> CaptureAsync(ISession target, string owner, string table, CancellationToken ct) => Task.FromResult(PreservedDdl.Empty);

        public Task<IReadOnlyList<(string Parent, string Ddl, string? Name)>> SourceForeignKeysAsync(ISession source, string sourceOwner, string table,
            IReadOnlySet<string> parents, string targetOwner, CancellationToken ct) => Task.FromResult<IReadOnlyList<(string Parent, string Ddl, string? Name)>>([]);

        public Task<HashSet<string>> RecycleBinAsync(ISession target, string table, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakePrecheckDialect : ICopyPrecheckDialect
    {
        public bool SupportsReadPrivilege => false;

        public string? RecreateBlockedReason(ISession target) => null;

        public Task<IReadOnlyList<string>> ExtendedStringBlockersAsync(TableInfo source, Func<string, CancellationToken, Task<string?>> targetParameter,
                                                                       CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);

        public IReadOnlyList<(string CatalogId, string Label)> NotRestoredProbes => [];

        public string RecycleBinNote(string? tablespace) => string.Empty;

        public string? ReadConsistencyParameter => null;

        public (CheckLevel Level, string Message) ReadConsistencyHint(long estimatedSeconds, long? retention) => (CheckLevel.Pass, string.Empty);

        public string ChildRecordFoundError => "ORA-02292";
    }

    /// <summary>문장 글만 만드는 방언(값 자리 = <c>:이름</c>).</summary>
    private sealed class FakeDialect : ICopyDialect
    {
        public bool IsServerName(string name, string owner) => CreateTableDdl.IsServerName(name);

        private static SqlStatement Ddl(string text) => new(text, default, StatementKind.Ddl);

        public string BindPlaceholder(string bindName, string serverType) => ":" + bindName;

        public SqlStatement DisableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" DISABLE");

        public SqlStatement DisableConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DISABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement DropTable(string owner, string table) => Ddl($"DROP TABLE \"{owner}\".\"{table}\"");

        public SqlStatement NextValues(string owner, string sequence) => throw new NotSupportedException();

        public SqlStatement SetTransactionReadOnly() => new("SET TRANSACTION READ ONLY", default, StatementKind.TransactionControl);

        public SqlStatement Savepoint(string name) => new("SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement RollbackToSavepoint(string name) => new("ROLLBACK TO SAVEPOINT " + name, default, StatementKind.TransactionControl);

        public SqlStatement EnableTrigger(string owner, string trigger) => Ddl($"ALTER TRIGGER \"{owner}\".\"{trigger}\" ENABLE");

        public SqlStatement DropConstraint(DisabledObject fk) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" DROP CONSTRAINT \"{fk.Name}\"");

        public SqlStatement EnableConstraint(DisabledObject fk, bool validate) => Ddl($"ALTER TABLE \"{fk.Owner}\".\"{fk.Table}\" ENABLE CONSTRAINT \"{fk.Name}\"");

        public SqlStatement RecycleBinOn() => throw new NotSupportedException();

        public string RestartIdentity(string owner, string table, string column) => throw new NotSupportedException();

        public string AddForeignKeyWithoutValidation(string addForeignKeyDdl) => addForeignKeyDdl;

        public bool IsParentKeysNotFound(DbError? dbError) => false;

        public bool IsReferencedByForeignKeys(DbError? dbError) => false;
    }
}
