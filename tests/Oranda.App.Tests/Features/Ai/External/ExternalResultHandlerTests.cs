using System.Text.Json.Nodes;
using Oranda.App.Features.Ai.External.Results;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Ai.Schemas;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.Ai.External;

/// <summary>
/// E3 앱 도구 처리자: get_status·list_connections·get_result·get_last_error(설계서 §5.3, 계약 A1).
/// 허가는 가짜 사본(<see cref="AgentGrantView"/>)이고, 탭·결과는 실제 편집기 호스트와 결과 탭 모델을 쓴다.
/// </summary>
public sealed class ExternalResultHandlerTests
{
    private static readonly Guid Dev = Guid.NewGuid();
    private static readonly Guid Prod = Guid.NewGuid();
    private static readonly Guid Outside = Guid.NewGuid();

    private static readonly ResultColumn[] Cols =
    [
        new(0, "ORDER_ID", "NUMBER", ColumnDataKind.Number),
        new(1, "CUST_PHONE", "VARCHAR2", ColumnDataKind.Text),
        new(2, "STATUS", "VARCHAR2", ColumnDataKind.Text),
    ];

    private static AgentGrantView Grant(AiAccessLevel devLevel = AiAccessLevel.QueryRows) => new(
        "g1",
        [
            new AgentGrantConnection(Dev, "dev", ConnectionEnvironment.Development, Dbms.Oracle, devLevel),
            new AgentGrantConnection(Prod, "prd", ConnectionEnvironment.Production, Dbms.Tibero, AiAccessLevel.Metadata, ReadOnly: true),
        ],
        new DateTimeOffset(2026, 11, 22, 9, 0, 0, TimeSpan.FromHours(9)),
        new DateTimeOffset(2026, 11, 22, 17, 0, 0, TimeSpan.FromHours(9)),
        ["Claude Code"]);

    private static AppToolInvocation Call(AgentGrantView grant, JsonObject? args = null) => new(grant, "Claude Code", args ?? []);

    private static JsonObject Ok(AgentToolOutput output)
    {
        Assert.False(output.IsError, output.ErrorMessage);
        return Assert.IsType<JsonObject>(output.Result);
    }

    private static void Denied(AgentToolOutput output, string code)
    {
        Assert.True(output.IsError);
        Assert.Equal(code, output.ErrorCode);
    }

    // ---- get_status · list_connections ----

    [Fact]
    public Task Status_reports_window_grant_and_connections_without_host_names() => HeadlessHost.Run(async () =>
    {
        var handler = new GetStatusHandler(new ServiceContainer()) { IsConnected = id => id == Dev, Label = () => "기본 작업 공간" };

        var json = Ok(await handler.ExecuteAsync(Call(Grant()), CancellationToken.None));

        Assert.Equal(AppToolNames.GetStatus, handler.ToolName);
        Assert.Equal(Environment.ProcessId, json["pid"]!.GetValue<int>());
        Assert.False(string.IsNullOrEmpty(json["app_version"]!.GetValue<string>()));
        Assert.Equal("기본 작업 공간", json["label"]!.GetValue<string>());
        Assert.Equal("2026-11-22T17:00:00+09:00", json["expires_at"]!.GetValue<string>());
        Assert.Equal(["Claude Code"], json["clients"]!.AsArray().Select(c => c!.GetValue<string>()));
        var conns = json["connections"]!.AsArray();
        Assert.Equal(2, conns.Count);
        Assert.Equal("dev", conns[0]!["alias"]!.GetValue<string>());
        Assert.Equal("Development", conns[0]!["environment"]!.GetValue<string>());
        Assert.Equal("QueryRows", conns[0]!["level"]!.GetValue<string>());
        Assert.Equal("oracle", conns[0]!["dbms"]!.GetValue<string>());
        Assert.True(conns[0]!["connected"]!.GetValue<bool>());
        Assert.Equal("tibero", conns[1]!["dbms"]!.GetValue<string>());
        Assert.False(conns[1]!["connected"]!.GetValue<bool>());
        Assert.True(json["tool_count"]!.GetValue<int>() > AppTools.All.Count / 2);
        Assert.DoesNotContain("host", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task Status_without_expiry_or_connections_still_answers() => HeadlessHost.Run(async () =>
    {
        var grant = new AgentGrantView("g2", [], DateTimeOffset.UnixEpoch, null, []);
        var handler = new GetStatusHandler(new ServiceContainer()) { IsConnected = _ => false, Label = () => string.Empty };

        var json = Ok(await handler.ExecuteAsync(Call(grant), CancellationToken.None));

        Assert.Null(json["expires_at"]);
        Assert.Empty(json["connections"]!.AsArray());
        // 접속이 없으면 수준 없이 쓸 수 있는 도구(편집기·언어 등)만 센다.
        Assert.Equal(AppTools.External.Count(t => t.MinimumLevel == AiAccessLevel.Off), json["tool_count"]!.GetValue<int>());
    });

    [Fact]
    public async Task List_connections_has_read_only_and_connected_and_is_an_object_at_the_top()
    {
        var handler = new ListConnectionsHandler(new ServiceContainer()) { IsConnected = id => id == Prod };

        var json = Ok(await handler.ExecuteAsync(Call(Grant()), CancellationToken.None));

        var conns = json["connections"]!.AsArray();
        Assert.Equal(AppToolNames.ListConnections, handler.ToolName);
        Assert.False(conns[0]!["read_only"]!.GetValue<bool>());
        Assert.True(conns[1]!["read_only"]!.GetValue<bool>());
        Assert.Equal("Metadata", conns[1]!["level"]!.GetValue<string>());
        Assert.True(conns[1]!["connected"]!.GetValue<bool>());
    }

    // ---- get_result ----

    private static async Task<(SqlEditorHost Host, SqlEditorViewModel Tab, ResultTabsViewModel Results)> TabWithResultAsync(Guid? profile, int rows = 3)
    {
        var host = new SqlEditorHost();
        var tab = host.OpenTextTab("SELECT * FROM SAMPLE.ORDERS", "주문", profile);
        var results = new ResultTabsViewModel();
        tab.ResultPane = results;
        var data = Enumerable.Range(1, rows).Select(i => new object?[] { (decimal)i, "010-0000-000" + i, i == 2 ? null : "OPEN" }).ToArray();
        var item = results.AddQuery(new MemoryResult(Cols, data), "결과 1", Query("SELECT * FROM SAMPLE.ORDERS"));
        await item.Pane!.Model!.LoadFirstPageAsync();
        return (host, tab, results);
    }

    private static SqlStatement Query(string text) => new(text, new Oranda.Core.Text.TextRange(0, text.Length), StatementKind.Query);

    private static JsonObject TabArgs(SqlEditorViewModel tab, int? offset = null, int? limit = null, int? index = null)
    {
        var args = new JsonObject { ["tab"] = tab.TabId.ToString("D") };
        if (offset is { } o)
        {
            args["offset"] = o;
        }

        if (limit is { } l)
        {
            args["limit"] = l;
        }

        if (index is { } i)
        {
            args["index"] = i;
        }

        return args;
    }

    [Fact]
    public Task Result_rows_are_the_fetched_ones_with_sensitive_columns_masked() => HeadlessHost.Run(async () =>
    {
        var (host, tab, _) = await TabWithResultAsync(Dev);
        var handler = new GetResultHandler() { Host = host };

        var json = Ok(await handler.ExecuteAsync(Call(Grant(), TabArgs(tab)), CancellationToken.None));

        Assert.Equal(["ORDER_ID", "CUST_PHONE", "STATUS"], json["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal("NUMBER", json["columns"]![0]!["type"]!.GetValue<string>());
        var rows = json[AgentFields.RowValues]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal("1", rows[0]![0]!.GetValue<string>());
        Assert.Equal(AppToolValues.Masked, rows[0]![1]!.GetValue<string>());
        Assert.Null(rows[1]![2]);
        Assert.Equal(3, json["fetched"]!.GetValue<int>());
        Assert.False(json["more"]!.GetValue<bool>());
        Assert.Equal("query", json["statement_kind"]!.GetValue<string>());
    });

    [Fact]
    public Task Offset_and_limit_page_through_the_rows_and_more_says_there_is_more() => HeadlessHost.Run(async () =>
    {
        var (host, tab, _) = await TabWithResultAsync(Dev, rows: 5);
        var handler = new GetResultHandler() { Host = host };

        var json = Ok(await handler.ExecuteAsync(Call(Grant(), TabArgs(tab, offset: 1, limit: 2)), CancellationToken.None));

        var rows = json[AgentFields.RowValues]!.AsArray();
        Assert.Equal(["2", "3"], rows.Select(r => r![0]!.GetValue<string>()));
        Assert.True(json["more"]!.GetValue<bool>());
    });

    [Fact]
    public Task Query_level_gives_columns_and_counts_but_no_row_values() => HeadlessHost.Run(async () =>
    {
        var (host, tab, _) = await TabWithResultAsync(Dev);
        var handler = new GetResultHandler() { Host = host };

        var json = Ok(await handler.ExecuteAsync(Call(Grant(AiAccessLevel.Query), TabArgs(tab)), CancellationToken.None));

        Assert.Null(json[AgentFields.RowValues]);
        Assert.Equal(3, json["columns"]!.AsArray().Count);
        Assert.Equal(3, json["fetched"]!.GetValue<int>());
    });

    [Fact]
    public Task A_local_model_grant_gets_row_values_and_unmasked_columns_on_a_production_query_level_tab() => HeadlessHost.Run(async () =>
    {
        // 운영 실효 수준은 Query 가 최대다. 로컬 모델 허가는 게이트와 같은 DataLevel 로 행 값까지 본다(D4·D5, w-it-p2 IT 가 찾은 빠짐).
        var (host, tab, _) = await TabWithResultAsync(Prod);
        var handler = new GetResultHandler() { Host = host };
        AgentGrantView ProdGrant(bool local) => new(
            "g1", [new AgentGrantConnection(Prod, "prd", ConnectionEnvironment.Production, Dbms.Oracle, AiAccessLevel.Query)],
            DateTimeOffset.UnixEpoch, null, ["Claude Code"]) { LocalModel = local };

        var local = Ok(await handler.ExecuteAsync(Call(ProdGrant(local: true), TabArgs(tab)), CancellationToken.None));
        var cloud = Ok(await handler.ExecuteAsync(Call(ProdGrant(local: false), TabArgs(tab)), CancellationToken.None));

        Assert.NotEqual(AppToolValues.Masked, local[AgentFields.RowValues]!.AsArray()[0]![1]!.GetValue<string>());
        Assert.Null(cloud[AgentFields.RowValues]);
    });

    [Fact]
    public Task Result_is_refused_below_query_on_production_outside_the_grant_and_for_unknown_tabs() => HeadlessHost.Run(async () =>
    {
        var (devHost, devTab, _) = await TabWithResultAsync(Dev);
        var handler = new GetResultHandler() { Host = devHost };
        Denied(await handler.ExecuteAsync(Call(Grant(AiAccessLevel.Plan), TabArgs(devTab)), CancellationToken.None), AgentErrorCodes.Level);

        var (prodHost, prodTab, _) = await TabWithResultAsync(Prod);
        handler.Host = prodHost;
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(prodTab)), CancellationToken.None), AgentErrorCodes.Level);

        var (outHost, outTab, _) = await TabWithResultAsync(Outside);
        handler.Host = outHost;
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(outTab)), CancellationToken.None), AgentErrorCodes.Locked);

        Denied(await handler.ExecuteAsync(Call(Grant(), new JsonObject { ["tab"] = Guid.NewGuid().ToString("D") }), CancellationToken.None), AgentErrorCodes.TabNotFound);
        Denied(await handler.ExecuteAsync(Call(Grant(), new JsonObject { ["tab"] = "not-a-tab" }), CancellationToken.None), AgentErrorCodes.TabNotFound);
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(devTab, limit: 500)), CancellationToken.None), AgentErrorCodes.InvalidArguments);
    });

    [Fact]
    public Task A_tab_without_results_or_with_a_message_result_has_no_result() => HeadlessHost.Run(async () =>
    {
        var host = new SqlEditorHost();
        var empty = host.OpenTextTab("SELECT 1 FROM DUAL", "빈 탭", Dev);
        var handler = new GetResultHandler() { Host = host };
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(empty)), CancellationToken.None), AgentErrorCodes.NoResult);

        var (host2, tab, results) = await TabWithResultAsync(Dev);
        results.AddMessage("메시지", "1 행이 갱신되었습니다.", isError: false);
        handler.Host = host2;
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(tab, index: 1)), CancellationToken.None), AgentErrorCodes.NoResult);
        Denied(await handler.ExecuteAsync(Call(Grant(), TabArgs(tab, index: 9)), CancellationToken.None), AgentErrorCodes.NoResult);

        // index 를 빼면 그리드가 있는 마지막 결과다(메시지 탭은 건너뛴다).
        Ok(await handler.ExecuteAsync(Call(Grant(), TabArgs(tab)), CancellationToken.None));
    });

    // ---- get_last_error ----

    private static (GetLastErrorHandler Handler, ExecutionState State, SqlEditorHost Host, SqlEditorViewModel Tab) ErrorSetup(Guid profile)
    {
        var bus = new EventBus();
        var services = new ServiceContainer();
        services.AddInstance<IEventBus>(bus);
        var host = new SqlEditorHost();
        var tab = host.OpenTextTab("SELECT X FROM SAMPLE.ORDERS", "주문", profile);
        var state = ExecutionState.For(bus);
        state.SetError(Query("SELECT X FROM SAMPLE.ORDERS"), new DbError("ORA-00904", "ORA-00904: \"X\": invalid identifier 'SAMPLE.ORDERS'") { Number = 904, Line = 1, Column = 8 }, tab.TabId, profile);
        return (new GetLastErrorHandler(services) { Host = host }, state, host, tab);
    }

    [Fact]
    public Task Last_error_gives_code_message_statement_and_position() => HeadlessHost.Run(async () =>
    {
        var (handler, _, _, tab) = ErrorSetup(Dev);

        var json = Ok(await handler.ExecuteAsync(Call(Grant(), new JsonObject { ["tab"] = tab.TabId.ToString("D") }), CancellationToken.None));

        Assert.Equal("ORA-00904", json["code"]!.GetValue<string>());
        Assert.Contains("invalid identifier", json["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("SELECT X FROM SAMPLE.ORDERS", json["sql_text"]!.GetValue<string>());
        Assert.Equal(1, json["line"]!.GetValue<int>());
        Assert.Equal(8, json["col"]!.GetValue<int>());

        // tab 을 빼면 창의 마지막 오류다.
        Ok(await handler.ExecuteAsync(Call(Grant()), CancellationToken.None));
    });

    [Fact]
    public Task Production_error_shows_only_code_and_position() => HeadlessHost.Run(async () =>
    {
        var (handler, _, _, _) = ErrorSetup(Prod);

        var json = Ok(await handler.ExecuteAsync(Call(Grant()), CancellationToken.None));

        Assert.Equal("ORA-00904", json["code"]!.GetValue<string>());
        Assert.Equal(AppToolValues.Masked, json["message"]!.GetValue<string>());
        Assert.Null(json["sql_text"]);
        Assert.Equal(8, json["col"]!.GetValue<int>());
        Assert.DoesNotContain("SAMPLE", json.ToJsonString(), StringComparison.Ordinal);
    });

    [Fact]
    public Task Last_error_is_refused_outside_the_grant_and_missing_for_other_tabs_or_after_clear() => HeadlessHost.Run(async () =>
    {
        var (outside, _, _, _) = ErrorSetup(Outside);
        Denied(await outside.ExecuteAsync(Call(Grant()), CancellationToken.None), AgentErrorCodes.Locked);

        var (handler, state, host, _) = ErrorSetup(Dev);
        var other = host.OpenTextTab("SELECT 1 FROM DUAL", "다른 탭", Dev);
        Denied(await handler.ExecuteAsync(Call(Grant(), new JsonObject { ["tab"] = other.TabId.ToString("D") }), CancellationToken.None), AgentErrorCodes.NoResult);

        state.ClearError();
        Denied(await handler.ExecuteAsync(Call(Grant()), CancellationToken.None), AgentErrorCodes.NoResult);
    });

    /// <summary>열과 행을 그대로 돌려주는 메모리 결과.</summary>
    private sealed class MemoryResult(ResultColumn[] columns, object?[][] rows) : IResultSet
    {
        public IReadOnlyList<ResultColumn> Columns => columns;

        public bool IsComplete => true;

        public long? KnownRowCount => rows.Length;

        public Task<ResultPage> GetPageAsync(int pageIndex, CancellationToken ct)
        {
            var cols = new List<IReadOnlyList<object?>>();
            for (var c = 0; c < columns.Length; c++)
            {
                cols.Add(pageIndex == 0 ? [.. rows.Select(r => r[c])] : []);
            }

            return Task.FromResult(new ResultPage(pageIndex, 0, pageIndex == 0 ? rows.Length : 0, cols) { IsLast = true });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
