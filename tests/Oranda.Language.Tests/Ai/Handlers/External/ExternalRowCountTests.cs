using System.Text.Json.Nodes;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Language.Ai.Handlers;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Language.Ai.Literals;
using Oranda.Testing;

namespace Oranda.Language.Tests.Ai.Handlers.External;

/// <summary>Host Db·Lang 처리자가 결과와 함께 낸 행 수(<see cref="AgentToolOutput.RowCount"/>). 게이트가 이 값으로 감사 사유에 <c>rows=N</c> 을 남긴다.</summary>
public class ExternalRowCountTests
{
    private readonly FakeMetadataProvider _meta = new();
    private readonly FakeExecutionService _exec = new();

    public ExternalRowCountTests()
    {
        _meta.NameSteps[("SCOTT", "EMP")] = NameStep.TableOrView;
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded) { ResultSet = Rows(3) };
    }

    private QueryHandler Query() =>
        new(new LanguageService(), _exec, new AgentStatementReferenceExtractor(new LanguageService(), _meta), VisibleCursors.Boxes(_meta));

    private static FakeResultSet Rows(int count) =>
        new(
            [new ResultColumn(0, "EMP_ID", "NUMBER", ColumnDataKind.Number)],
            [.. Enumerable.Range(1, count).Select(i => (IReadOnlyList<object?>)[(decimal)i])],
            2);

    [Fact]
    public async Task Query_row_count_is_the_rows_read_even_without_row_values()
    {
        var full = await Query().ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp" }), CancellationToken.None);
        var countOnly = await Query().ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp" }, AiAccessLevel.Query), CancellationToken.None);

        Assert.False(full.IsError, full.ErrorMessage);
        Assert.Equal(3, full.RowCount);
        Assert.Null(countOnly.Result!["row_values"]);
        Assert.Equal(3, countOnly.RowCount);
    }

    [Fact]
    public async Task Query_row_count_stops_at_max_rows_and_failures_have_none()
    {
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Succeeded) { ResultSet = Rows(10) };
        var truncated = await Query().ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp", ["max_rows"] = 4 }), CancellationToken.None);
        _exec.Responder = call => new ExecutionResult(call.Statement, ExecutionStatus.Failed) { Error = new DbError("ORA-00942", "table or view does not exist") };
        var failed = await Query().ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "SELECT * FROM emp" }), CancellationToken.None);
        var refused = await Query().ExecuteAsync(ExternalFakes.Db(new JsonObject { ["sql"] = "DELETE FROM emp" }), CancellationToken.None);

        Assert.True(truncated.Result!["truncated"]!.GetValue<bool>());
        Assert.Equal(4, truncated.RowCount);
        Assert.Equal("tool_failed", failed.ErrorCode);
        Assert.Null(failed.RowCount);
        Assert.NotNull(refused.ErrorCode);
        Assert.Null(refused.RowCount);
    }

    [Fact]
    public async Task List_tools_count_their_items_and_single_object_tools_have_no_row_count()
    {
        _meta.Objects.AddRange([new DbObjectRef("APP", "TB_ORDER", "TABLE"), new DbObjectRef("APP", "TB_ORDER_ITEM", "TABLE"), new DbObjectRef("APP", "TB_ORDERX", "VIEW")]);
        _meta.Schemas.AddRange(["APP", "SCOTT"]);

        var found = await new FindObjectsHandler(_meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["name_like"] = "TB%" }), CancellationToken.None);
        var limited = await new FindObjectsHandler(_meta).ExecuteAsync(ExternalFakes.Db(new JsonObject { ["name_like"] = "TB%", ["limit"] = 2 }), CancellationToken.None);
        var schemas = await new ListSchemasHandler(_meta).ExecuteAsync(ExternalFakes.Db([], AiAccessLevel.Metadata), CancellationToken.None);
        var ddl = await new GetDdlHandler(_meta).ExecuteAsync(
            ExternalFakes.Db(new JsonObject { ["object"] = "APP.TB_ORDER" }, resolved: ("object", new AgentObjectRef("APP", "TB_ORDER"))), CancellationToken.None);

        Assert.Equal(found.Result!["objects"]!.AsArray().Count, found.RowCount);
        Assert.Equal(3, found.RowCount);
        Assert.Equal(2, limited.RowCount); // 보인 항목만(한도 + 1 로 더 읽은 것은 세지 않는다)
        Assert.Equal(2, schemas.RowCount);
        Assert.False(ddl.IsError, ddl.ErrorMessage);
        Assert.Null(ddl.RowCount);
    }

    [Fact]
    public async Task History_row_count_is_the_entries_returned()
    {
        var history = new FakeHistory();
        var at = new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
        history.Entries.AddRange(
            [.. Enumerable.Range(1, 3).Select(i => new HistoryEntry
            {
                Id = i,
                ProfileId = ExternalFakes.DevId,
                ExecutedAt = at.AddMinutes(i),
                SqlText = "SELECT 1 FROM dual",
                Source = HistorySource.Editor,
                Status = HistoryStatus.Succeeded,
            })]);

        var output = await new GetHistoryHandler(history, new SqlLiteralMasker()).ExecuteAsync(ExternalFakes.App([]), CancellationToken.None);
        var limited = await new GetHistoryHandler(history, new SqlLiteralMasker()).ExecuteAsync(ExternalFakes.App(new JsonObject { ["limit"] = 2 }), CancellationToken.None);

        Assert.Equal(3, output.Result!["entries"]!.AsArray().Count);
        Assert.Equal(3, output.RowCount);
        Assert.Equal(2, limited.RowCount);
    }
}
