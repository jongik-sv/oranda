using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.Language;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.SqlPlus;

namespace Oranda.Oracle.IntegrationTests.SqlPlus;

// 소유: TSK-06-06. SQL*Plus 명령(DEFINE·PROMPT·SET SERVEROUTPUT·@파일·SPOOL·WHENEVER SQLERROR·통계).

/// <summary>DB 없이 도는 처리기 시험: 실행 엔진은 가짜로 바꾸고 보낸 문장만 본다.</summary>
public sealed class SqlPlusCommandLogicTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-sqlplus-" + Guid.NewGuid().ToString("N"));
    private readonly RecordingEngine _engine = new();
    private readonly SqlPlusCommandProcessor _processor;
    private readonly DriverlessSession _session = new(new ConnectionProfile("p", "u", "h:1521/s"));

    public SqlPlusCommandLogicTests()
    {
        Directory.CreateDirectory(_dir);
        _processor = new SqlPlusCommandProcessor(() => _engine, () => new LanguageService(), () => _engine.Bus);
        _engine.Processor = _processor;
    }

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

    private static SqlStatement Cmd(string text) => new(text, default, StatementKind.SqlPlus) { Verb = text.Split(' ')[0].ToUpperInvariant() };

    private Task<ExecutionResult> RunAsync(string text) => _processor.ExecuteAsync(_session, Cmd(text), default);

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public async Task Define_sets_shows_lists_and_undefines()
    {
        Assert.Equal(ExecutionStatus.Succeeded, (await RunAsync("DEFINE a = 'hello world'")).Status);
        Assert.Equal("hello world", _processor.GetDefine(_session, "A"));

        var shown = await RunAsync("DEFINE a");
        Assert.Contains("hello world", Assert.Single(shown.DbmsOutput), StringComparison.Ordinal);
        Assert.Single((await RunAsync("DEFINE")).DbmsOutput);

        await RunAsync("UNDEFINE a");
        Assert.Null(_processor.GetDefine(_session, "a"));
        var missing = await RunAsync("DEFINE a");
        Assert.Equal(ExecutionStatus.Failed, missing.Status);
        Assert.Equal("SP2-0135", missing.Error?.Code);
    }

    [Fact]
    public async Task Prompt_returns_its_text()
    {
        var r = await RunAsync("PROMPT 작업 시작 ...");

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal("작업 시작 ...", Assert.Single(r.DbmsOutput));
    }

    [Fact]
    public async Task Set_serveroutput_enables_and_disables_dbms_output_without_history()
    {
        await RunAsync("SET SERVEROUTPUT ON SIZE 5000");
        await RunAsync("SET SERVEROUTPUT ON SIZE UNLIMITED");
        await RunAsync("SET SERVEROUTPUT OFF");
        var bad = await RunAsync("SET SERVEROUTPUT MAYBE");

        Assert.Equal(["BEGIN DBMS_OUTPUT.ENABLE(5000); END;", "BEGIN DBMS_OUTPUT.ENABLE(NULL); END;", "BEGIN DBMS_OUTPUT.DISABLE; END;"],
            _engine.Sent.Select(c => c.Statement.Text));
        Assert.All(_engine.Sent, c => Assert.True(c.Options.SkipHistory));
        Assert.Equal(ExecutionStatus.Failed, bad.Status);
    }

    [Fact]
    public async Task Display_options_are_accepted_and_ignored_unsupported_are_reported()
    {
        Assert.Equal(ExecutionStatus.Succeeded, (await RunAsync("SET LINESIZE 200")).Status);
        Assert.Equal(ExecutionStatus.Succeeded, (await RunAsync("COLUMN name FORMAT a20")).Status);
        var host = await RunAsync("HOST dir");
        Assert.Equal(ExecutionStatus.Failed, host.Status);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public async Task Prompt_replaces_defined_substitution_variables()
    {
        await RunAsync("DEFINE d = 20");
        var prompt = await RunAsync("PROMPT dept is &d and &unknown");

        Assert.Equal(["dept is 20 and &unknown"], prompt.DbmsOutput);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public async Task Desc_reports_a_clear_error_without_metadata_service()
    {
        var desc = await RunAsync("DESC emp");

        Assert.Equal(ExecutionStatus.Failed, desc.Status);
        Assert.Contains("DESC", desc.Error!.Message, StringComparison.Ordinal);
        Assert.Empty(_engine.Sent);
    }

    [Fact]
    public async Task Exec_wraps_the_call_in_a_plsql_block()
    {
        await RunAsync("EXEC dbms_stats.gather_schema_stats('SCOTT');");

        var sent = Assert.Single(_engine.Sent);
        Assert.Equal("BEGIN dbms_stats.gather_schema_stats('SCOTT'); END;", sent.Statement.Text);
        Assert.Equal(StatementKind.PlSql, sent.Statement.Kind);
    }

    [Fact]
    public async Task File_runs_statements_in_order_with_defines_and_arguments()
    {
        var file = Write("main.sql", "DEFINE x = 7\nSELECT &x FROM dual;\nSELECT '&1' FROM dual;\nPROMPT done\n");

        var r = await RunAsync($"@{file} first");

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(
            ["DEFINE x = 7", "SELECT 7 FROM dual", "SELECT 'first' FROM dual", "PROMPT done"],
            _engine.Sent.Select(c => c.Statement.Text));
        Assert.All(_engine.Sent, c => Assert.Equal(Oranda.Core.History.HistorySource.Script, c.Options.Source));
        Assert.Contains("done", r.DbmsOutput);
    }

    [Fact]
    public async Task Nested_files_resolve_relative_to_the_calling_script_and_add_sql_extension()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "sub"));
        Write(Path.Combine("sub", "child.sql"), "SELECT 'child' FROM dual;\n");
        Write(Path.Combine("sub", "parent.sql"), "SELECT 'before' FROM dual;\n@@child\nSELECT 'after' FROM dual;\n");

        var r = await RunAsync($"@\"{Path.Combine(_dir, "sub", "parent")}\"");

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(["SELECT 'before' FROM dual", "@@child", "SELECT 'child' FROM dual", "SELECT 'after' FROM dual"],
            _engine.Sent.Select(c => c.Statement.Text));
    }

    [Fact]
    public async Task Missing_file_fails()
    {
        var r = await RunAsync("@does-not-exist-" + Guid.NewGuid().ToString("N"));

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal("SP2-0310", r.Error?.Code);
    }

    [Fact]
    public async Task Self_including_file_stops_at_the_depth_limit()
    {
        var file = Write("loop.sql", "@@loop\n");

        var r = await RunAsync($"@{file}");

        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal(SqlPlusCommandProcessor.MaxDepth, _engine.Sent.Count(c => c.Statement.Text.StartsWith("@@loop", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Errors_continue_by_default_and_stop_with_whenever_sqlerror_exit()
    {
        var file = Write("err.sql", "SELECT 'one' FROM dual;\nSELECT 'bad' FROM dual;\nSELECT 'three' FROM dual;\n");
        _engine.Responder = c => c.Statement.Text.Contains("bad", StringComparison.Ordinal)
            ? new ExecutionResult(c.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") }
            : new ExecutionResult(c.Statement, ExecutionStatus.Succeeded);

        var continued = await RunAsync($"@{file}");
        Assert.Equal(ExecutionStatus.Failed, continued.Status);
        Assert.Equal("ORA-00942", continued.Error?.Code);
        Assert.Equal(3, _engine.Sent.Count);
        Assert.Contains(continued.DbmsOutput, l => l.StartsWith("ORA-00942", StringComparison.Ordinal));

        _engine.Sent.Clear();
        await RunAsync("WHENEVER SQLERROR EXIT ROLLBACK");
        var stopped = await RunAsync($"@{file}");
        Assert.Equal(ExecutionStatus.Failed, stopped.Status);
        Assert.Equal(2, _engine.Sent.Count);
        Assert.Equal(1, _session.DriverTouches); // ROLLBACK 이 한 번

        await RunAsync("WHENEVER SQLERROR CONTINUE");
        _engine.Sent.Clear();
        await RunAsync($"@{file}");
        Assert.Equal(3, _engine.Sent.Count);
    }

    [Fact]
    public async Task Exit_inside_a_file_stops_the_file()
    {
        var file = Write("exit.sql", "SELECT 'one' FROM dual;\nEXIT\nSELECT 'two' FROM dual;\n");

        var r = await RunAsync($"@{file}");

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Equal(2, _engine.Sent.Count);
    }

    [Fact]
    public async Task Spool_writes_output_of_statements_until_spool_off()
    {
        var spool = Path.Combine(_dir, "out.lst");
        _engine.Responder = c => new ExecutionResult(c.Statement, ExecutionStatus.Succeeded) { DbmsOutput = [$"out:{c.Statement.Text}"] };

        var on = await RunAsync($"SPOOL \"{spool}\"");
        Assert.Equal(ExecutionStatus.Succeeded, on.Status);
        _engine.Publish(_session.Id, Cmd("PROMPT hi"));
        _engine.Publish(_session.Id, new SqlStatement("SELECT 1 FROM dual", default, StatementKind.Query));
        await RunAsync("SPOOL OFF");
        _engine.Publish(_session.Id, new SqlStatement("SELECT 2 FROM dual", default, StatementKind.Query));

        Assert.Equal(["out:PROMPT hi", "out:SELECT 1 FROM dual"], File.ReadAllLines(spool));
    }

    [Fact]
    public async Task Spool_without_extension_gets_lst_and_create_refuses_existing_file()
    {
        var basePath = Path.Combine(_dir, "report");

        await RunAsync($"SPOOL {basePath}");
        await RunAsync("SPOOL OFF");
        Assert.True(File.Exists(basePath + ".lst"));

        var again = await RunAsync($"SPOOL {basePath} CREATE");
        Assert.Equal(ExecutionStatus.Failed, again.Status);
    }

    [Fact]
    public async Task Statistics_report_follows_each_file_statement_when_on()
    {
        var file = Write("stat.sql", "SELECT 1 FROM dual;\n");
        var calls = 0;
        _engine.Responder = c =>
        {
            if (!c.Statement.Text.Contains("v$mystat", StringComparison.Ordinal))
            {
                return new ExecutionResult(c.Statement, ExecutionStatus.Succeeded);
            }

            calls++;
            return new ExecutionResult(c.Statement, ExecutionStatus.Succeeded)
            {
                ResultSet = new StatResultSet(("session logical reads", calls * 10m), ("redo size", 100m)),
            };
        };

        await RunAsync("SET STATISTICS ON");
        var r = await RunAsync($"@{file}");

        var line = Assert.Single(r.DbmsOutput);
        Assert.Equal("통계: session logical reads=10", line);
    }

    [Fact]
    public void Statistics_diff_keeps_only_increases_and_describes_known_names()
    {
        var before = new Dictionary<string, decimal> { ["session logical reads"] = 5, ["redo size"] = 100, ["other"] = 1 };
        var after = new Dictionary<string, decimal> { ["session logical reads"] = 12, ["redo size"] = 100, ["other"] = 9, ["physical reads"] = 2 };

        var diff = SessionStatistics.Diff(before, after);

        Assert.Equal(7m, diff["session logical reads"]);
        Assert.False(diff.ContainsKey("redo size"));
        Assert.Equal("통계: session logical reads=7, physical reads=2", Assert.Single(SessionStatistics.Describe(diff)));
        Assert.Empty(SessionStatistics.Describe(new Dictionary<string, decimal>()));
    }

    private sealed class RecordingEngine : IExecutionService
    {
        public List<ExecutionCall> Sent { get; } = [];

        public Func<ExecutionCall, ExecutionResult> Responder { get; set; } = c => new ExecutionResult(c.Statement, ExecutionStatus.Succeeded);

        public SpoolBus Bus { get; } = new();

        public async Task<ExecutionResult> ExecuteAsync(ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds,
                                                        ExecutionOptions? options, CancellationToken ct)
        {
            var call = new ExecutionCall(stmt, options ?? ExecutionOptions.Default);
            Sent.Add(call);

            // 실제 엔진처럼 SQL*Plus 문장은 처리기로 돌려보낸다.
            var result = stmt.Kind == StatementKind.SqlPlus && Processor is { } p
                ? await p.ExecuteAsync(s, stmt, ct)
                : Responder(call);
            Bus.Publish(new StatementExecuted(s.Id, stmt, result, call.Options));
            return result;
        }

        public SqlPlusCommandProcessor? Processor { get; set; }

        /// <summary>스풀 시험: 엔진이 낸 것처럼 이벤트를 발행한다.</summary>
        public void Publish(Guid sessionId, SqlStatement stmt)
        {
            var call = new ExecutionCall(stmt, ExecutionOptions.Default);
            Bus.Publish(new StatementExecuted(sessionId, stmt, Responder(call), call.Options));
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode,
                                                                    ExecutionOptions? options, CancellationToken ct)
            => throw new NotSupportedException();
    }

    internal sealed record ExecutionCall(SqlStatement Statement, ExecutionOptions Options);

    private sealed class StatResultSet((string Name, decimal Value) a, (string Name, decimal Value) b) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns { get; } =
            [new(0, "NAME", "VARCHAR2", ColumnDataKind.Text), new(1, "VALUE", "NUMBER", ColumnDataKind.Number)];

        public bool IsComplete => true;

        public long? KnownRowCount => 2;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
            => Task.FromResult(new ResultPage(pageIndex, 0, 2, [[a.Name, b.Name], [a.Value, b.Value]]) { IsLast = true });

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class SpoolBus : IEventBus
    {
        private readonly List<Action<OrandaEvent>> _handlers = [];

        public IDisposable Subscribe<TEvent>(object owner, Action<TEvent> handler)
            where TEvent : OrandaEvent
        {
            void Wrapper(OrandaEvent e)
            {
                if (e is TEvent t)
                {
                    handler(t);
                }
            }

            _handlers.Add(Wrapper);
            return new Token(() => _handlers.Remove(Wrapper));
        }

        public void Publish<TEvent>(TEvent e)
            where TEvent : OrandaEvent
        {
            foreach (var h in _handlers.ToList())
            {
                h(e);
            }
        }

        private sealed class Token(Action dispose) : IDisposable
        {
            public void Dispose() => dispose();
        }
    }
}

/// <summary>실제 26ai 컨테이너에서 실제 실행 엔진과 함께 도는 시험.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class SqlPlusCommandTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oranda-sqlplus-it-" + Guid.NewGuid().ToString("N"));
    private readonly ExecBus _bus = new();
    private OracleSessionManager _manager = null!;
    private OracleSession _session = null!;
    private OracleExecutionService _engine = null!;

    public SqlPlusCommandTests(OracleFreeFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _manager = new OracleSessionManager(_bus);
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        _session = (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
        OracleExecutionService? engine = null;
        var processor = new SqlPlusCommandProcessor(() => engine!, () => new LanguageService());
        engine = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, processor, 500);
        _engine = engine;
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 임시 폴더 정리 실패는 무시한다.
        }
    }

    private async Task<ExecutionResult> RunAsync(string sqlPlusText)
    {
        var stmt = new SqlStatement(sqlPlusText, default, StatementKind.SqlPlus) { Verb = sqlPlusText.Split(' ')[0].ToUpperInvariant() };
        return await _engine.ExecuteAsync(_session, stmt, new Dictionary<string, BindValue>(), null, default);
    }

    [Fact]
    public async Task File_script_runs_in_order_against_the_server()
    {
        var table = "SP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var file = Path.Combine(_dir, "run.sql");
        await File.WriteAllTextAsync(file, $"""
            DEFINE n = 3
            SET SERVEROUTPUT ON
            CREATE TABLE {table} (id NUMBER, v VARCHAR2(20));
            INSERT INTO {table} VALUES (1, 'a');
            INSERT INTO {table} VALUES (&n, 'c');
            BEGIN
              FOR r IN (SELECT id FROM {table} ORDER BY id) LOOP
                DBMS_OUTPUT.PUT_LINE('row ' || r.id);
              END LOOP;
            END;
            /
            SELECT COUNT(*) FROM {table};
            """);
        try
        {
            var r = await RunAsync($"@{file}");

            Assert.Equal(ExecutionStatus.Succeeded, r.Status);
            Assert.Equal(["row 1", "row 3"], r.DbmsOutput.Where(l => l.StartsWith("row ", StringComparison.Ordinal)));
            Assert.NotNull(r.ResultSet);
            var page = await r.ResultSet!.GetPageAsync(0, default);
            Assert.Equal(2m, Convert.ToDecimal(page.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture));
            await r.ResultSet.DisposeAsync();
        }
        finally
        {
            await _engine.ExecuteAsync(_session, new SqlStatement($"DROP TABLE {table} PURGE", default, StatementKind.Ddl) { Verb = "DROP" },
                new Dictionary<string, BindValue>(), null, default);
        }
    }

    [Fact]
    public async Task Statistics_report_appears_for_file_statements()
    {
        var file = Path.Combine(_dir, "stat.sql");
        await File.WriteAllTextAsync(file, "SELECT COUNT(*) FROM all_objects;\n");

        await RunAsync("SET STATISTICS ON");
        var r = await RunAsync($"@{file}");
        if (r.ResultSet is not null)
        {
            await r.ResultSet.DisposeAsync();
        }

        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        Assert.Contains(r.DbmsOutput, l => l.StartsWith("통계: ", StringComparison.Ordinal));
    }
}
