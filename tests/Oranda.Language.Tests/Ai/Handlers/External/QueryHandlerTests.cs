using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

public class QueryHandlerTests
{
    private readonly FakeMetadataProvider _meta = new();
    private readonly FakeExecutionService _exec = new();
    private readonly VisibleCursors _cursors = new();

    public QueryHandlerTests()
    {
        _meta.NameSteps[("SCOTT", "EMP")] = NameStep.TableOrView;
        _meta.NameSteps[("APP", "EMP")] = NameStep.TableOrView;
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded) { ResultSet = Rows(3), Elapsed = TimeSpan.FromMilliseconds(5) };
    }

    private QueryHandler Handler() =>
        new(new LanguageService(), _exec, new AgentStatementReferenceExtractor(new LanguageService(), _meta), VisibleCursors.Boxes(_meta, _cursors));

    private static FakeResultSet Rows(int count, int pageSize = 2) =>
        new(
            [
                new ResultColumn(0, "EMP_ID", "NUMBER", ColumnDataKind.Number),
                new ResultColumn(1, "EMP_NM", "VARCHAR2", ColumnDataKind.Text),
                new ResultColumn(2, "PHONE_NO", "VARCHAR2", ColumnDataKind.Text),
                new ResultColumn(3, "HIRE_DT", "DATE", ColumnDataKind.Date),
            ],
            [.. Enumerable.Range(1, count).Select(i => (IReadOnlyList<object?>)[(decimal)i, "N" + i, "010-0000-000" + i, new DateTime(2026, 1, i % 28 + 1)])],
            pageSize);

    private static JsonObject Sql(string sql, int? maxRows = null)
    {
        var args = new JsonObject { ["sql"] = sql };
        if (maxRows is { } m)
        {
            args["max_rows"] = m;
        }

        return args;
    }

    [Fact]
    public async Task Query_returns_columns_rows_with_sensitive_columns_masked_and_agent_source()
    {
        var output = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp")), CancellationToken.None);

        Assert.False(output.IsError, output.ErrorMessage);
        var result = output.Result!;
        Assert.Equal(["EMP_ID", "EMP_NM", "PHONE_NO", "HIRE_DT"], result["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("NUMBER", result["columns"]![0]!["type"]!.GetValue<string>());
        var rows = result["row_values"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal(1m, rows[0]![0]!.GetValue<decimal>());
        Assert.Equal("***", rows[0]![2]!.GetValue<string>());
        Assert.Equal("2026-01-02T00:00:00", rows[0]![3]!.GetValue<string>());
        Assert.Equal(3, result["row_count"]!.GetValue<int>());
        Assert.False(result["truncated"]!.GetValue<bool>());
        Assert.NotNull(result["elapsed_ms"]);
        Assert.Equal(HistorySource.Agent, Assert.Single(_exec.Calls).Options.Source);
        var sqlId = result["sql_id"]!.GetValue<string>();
        Assert.Equal([sqlId], output.SqlIds);
    }

    [Fact]
    public async Task Query_stops_at_max_rows_and_says_truncated()
    {
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded) { ResultSet = Rows(10) };

        var output = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp", maxRows: 4)), CancellationToken.None);

        Assert.Equal(4, output.Result!["row_values"]!.AsArray().Count);
        Assert.Equal(4, output.Result["row_count"]!.GetValue<int>());
        Assert.True(output.Result["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Query_below_row_values_level_returns_columns_and_count_only_and_hides_unseen_sql_id()
    {
        _cursors.Visible = false;

        var output = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp"), AiAccessLevel.Query), CancellationToken.None);

        Assert.Null(output.Result!["row_values"]);
        Assert.Equal(3, output.Result["row_count"]!.GetValue<int>());
        Assert.Null(output.Result["sql_id"]);
        Assert.Empty(output.SqlIds);
    }

    [Theory]
    [InlineData("DELETE FROM emp", "sql_not_single_query")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual", "sql_not_single_query")]
    [InlineData("SELECT * FROM emp FOR UPDATE", "sql_unsafe")]
    [InlineData("SELECT pkg.f(emp_id) FROM emp", "sql_unsafe")]
    [InlineData("SELECT * FROM emp@lnk", "sql_unsafe")]
    public async Task Query_refuses_anything_but_one_plain_select_and_does_not_execute(string sql, string code)
    {
        var output = await Handler().ExecuteAsync(ExternalFakes.Db(Sql(sql)), CancellationToken.None);

        Assert.Equal(code, output.ErrorCode);
        Assert.Empty(_exec.Calls);
    }

    [Fact]
    public async Task Query_refuses_production_low_level_and_read_only_execution()
    {
        var prod = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp"), env: ConnectionEnvironment.Production), CancellationToken.None);
        var plan = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp"), AiAccessLevel.Plan), CancellationToken.None);
        var ro = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp"), readOnlyExecution: true), CancellationToken.None);

        // 운영: D5 스위치(AgentGrantPolicy.ProductionRuns)를 켜면 실행되고, 끄면 environment 로 거부.
        Assert.Equal(AgentGrantPolicy.ProductionRuns ? null : "environment", prod.ErrorCode);
        Assert.Equal("level", plan.ErrorCode);
        Assert.Equal("sql_unsafe", ro.ErrorCode);
        Assert.Equal(AgentGrantPolicy.ProductionRuns ? 1 : 0, _exec.Calls.Count);
    }

    [Fact]
    public async Task Query_passes_binds_and_refuses_missing_or_bad_binds()
    {
        var ok = await Handler().ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp WHERE emp_id = :id AND emp_nm = :nm", ["binds"] = new JsonObject { [":ID"] = 10, ["nm"] = "A" } }),
            CancellationToken.None);
        var missing = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp WHERE emp_id = :id")), CancellationToken.None);
        var bad = await Handler().ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp WHERE emp_id = :id", ["binds"] = new JsonObject { ["id"] = new JsonArray(1) } }),
            CancellationToken.None);

        Assert.False(ok.IsError, ok.ErrorMessage);
        var binds = Assert.Single(_exec.Calls).Binds;
        Assert.Equal(new BindValue(BindDataType.Number, 10m), binds["id"]);
        Assert.Equal(new BindValue(BindDataType.Varchar2, "A"), binds["nm"]);
        Assert.Equal("invalid_arguments", missing.ErrorCode);
        Assert.Contains("id", missing.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("invalid_arguments", bad.ErrorCode);
    }

    [Fact]
    public async Task Query_failure_shows_only_the_error_code_and_denial_maps_to_not_allowed()
    {
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-01722", "invalid number 'SECRET'") };
        var failed = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp")), CancellationToken.None);
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Denied);
        var denied = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp")), CancellationToken.None);

        Assert.Equal("tool_failed", failed.ErrorCode);
        Assert.Contains("ORA-01722", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", failed.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("SECRET", failed.LogDetail, StringComparison.Ordinal);
        Assert.Equal("not_allowed", denied.ErrorCode);
    }

    [Fact]
    public async Task Query_timeout_is_canceled_and_caller_cancel_propagates()
    {
        var slow = new SlowExecution();
        var handler = new QueryHandler(new LanguageService(), slow, new AgentStatementReferenceExtractor(new LanguageService(), _meta), VisibleCursors.Boxes(_meta));

        var timedOut = await handler.ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp", ["timeout_s"] = 1 }), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        Assert.Equal("canceled", timedOut.ErrorCode);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await handler.ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp")), cts.Token));
    }

    [Fact]
    public async Task Query_refuses_names_that_resolve_differently_in_current_schema()
    {
        var output = await Handler().ExecuteAsync(ExternalFakes.Db(Sql("SELECT * FROM emp"), currentSchema: "APP"), CancellationToken.None);

        Assert.Equal("sql_schema_unresolved", output.ErrorCode);
        Assert.Empty(_exec.Calls);
    }

    [Fact]
    public async Task Query_rows_stop_before_the_character_budget()
    {
        var wide = new string('x', ExternalJson.MaxCellChars);
        var set = new FakeResultSet(
            [new ResultColumn(0, "MEMO", "VARCHAR2", ColumnDataKind.Text)],
            [.. Enumerable.Range(0, 200).Select(_ => (IReadOnlyList<object?>)[wide])]);

        var json = await QueryHandler.ReadAsync(set, 200, true, CancellationToken.None);

        var rows = json["row_values"]!.AsArray();
        Assert.True(rows.Count is > 0 and < 200);
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.True(json.ToJsonString().Length < QueryHandler.MaxRowChars + 2000);
    }

    private sealed class SlowExecution : IExecutionService
    {
        public async Task<ExecutionResult> ExecuteAsync(Oranda.Core.Sessions.ISession s, SqlStatement stmt, IReadOnlyDictionary<string, BindValue> binds, ExecutionOptions? options, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }

        public IAsyncEnumerable<ExecutionResult> ExecuteScriptAsync(Oranda.Core.Sessions.ISession s, IReadOnlyList<SqlStatement> stmts, ScriptErrorMode mode, ExecutionOptions? options, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
