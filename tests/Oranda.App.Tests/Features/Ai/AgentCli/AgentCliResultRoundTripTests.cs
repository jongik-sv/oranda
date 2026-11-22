using Oranda.App.Features.Ai.External.Results;
using Oranda.App.Features.Execution;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell.Composition;
using Oranda.App.Tests.Shell.Headless;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Grid;

namespace Oranda.App.Tests.Features.Ai.AgentCli;

/// <summary>
/// 끝 왕복(A5 마무리): 실제 <c>orandactl</c> → 실제 다리·관문 → <b>실제 상태·결과 처리자(E)</b>(get_status·list_connections·get_result·get_last_error).
/// 결과 그리드·실행 상태는 가짜(메모리 결과·이벤트 버스). 허가 수준에 따라 <c>row_values</c> 가 빠지고 민감 열이 <c>***</c> 이며, 운영 오류 글이 가려지는지 본다.
/// 처리자가 UI 스레드로 넘기므로(Avalonia) Headless 앱의 UI 스레드에서 돈다.
/// </summary>
[Collection(AgentCliSerial.Name)]
public sealed class AgentCliResultRoundTripTests
{
    private static readonly ConnectionProfile Dev = new("dev-erp", "scott", "h/s", Id: Guid.NewGuid(), AiAccess: AiAccessLevel.QueryRows);
    private static readonly ConnectionProfile Tst = new("tst-erp", "scott", "h/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Test, AiAccess: AiAccessLevel.Query);
    private static readonly ConnectionProfile Prd = new("prd-erp", "scott", "h/s", Id: Guid.NewGuid(), Environment: ConnectionEnvironment.Production, Dbms: Dbms.Tibero);

    private static readonly ResultColumn[] Cols =
    [
        new(0, "ORDER_ID", "NUMBER", ColumnDataKind.Number),
        new(1, "CUST_PHONE", "VARCHAR2", ColumnDataKind.Text),
    ];

    [Fact]
    public Task Status_connections_result_and_error_round_trip_with_level_masking() => HeadlessHost.Run(async () =>
    {
        var bus = new EventBus();
        var services = new ServiceContainer();
        services.AddInstance<IEventBus>(bus);
        var host = new SqlEditorHost();
        var devTab = await TabWithResultAsync(host, Dev.Id, "개발 주문");
        var tstTab = await TabWithResultAsync(host, Tst.Id, "시험 주문");

        IAppToolHandler[] handlers =
        [
            new GetStatusHandler(services) { IsConnected = id => id == Dev.Id, Label = () => "A5 rig" },
            new ListConnectionsHandler(services) { IsConnected = id => id == Dev.Id },
            new GetResultHandler { Host = host },
            new GetLastErrorHandler(services) { Host = host },
        ];
        await using var rig = new AgentCliRig(handlers, profiles: [Dev, Tst, Prd]);

        // status: 허가·창·접속 요약
        var status = await rig.RunAsync(["status"], client: "codex");
        Assert.True(status.ExitCode == 0, status.ToString());
        Assert.Equal("A5 rig", (string?)status.Json["label"]);
        Assert.Equal(Environment.ProcessId, (int)status.Json["pid"]!);
        Assert.Contains("codex", status.Json["clients"]!.AsArray().Select(c => (string?)c));
        // 운영 = 접속 최대 Query(D5 실행 스위치를 끄면 외부 상한 Metadata).
        var prdLevel = Oranda.Core.Ai.Gate.AgentGrantPolicy.ProductionRuns ? "Query" : "Metadata";
        Assert.Equal(["QueryRows", "Query", prdLevel], status.Json["connections"]!.AsArray().Select(c => (string?)c!["level"]));
        Assert.DoesNotContain("h/s", status.Stdout, StringComparison.Ordinal);

        // conn list
        var conns = await rig.RunAsync(["conn", "list"]);
        Assert.True(conns.ExitCode == 0, conns.ToString());
        var list = conns.Json["connections"]!.AsArray();
        Assert.Equal(["dev-erp", "tst-erp", "prd-erp"], list.Select(c => (string?)c!["alias"]));
        Assert.Equal("tibero", (string?)list[2]!["dbms"]);
        Assert.True((bool)list[0]!["connected"]!);

        // result get: QueryRows 는 행 값(민감 열 ***), Query 는 행 값 없이 열·수만
        var devRows = await rig.RunAsync(["result", "get", "--tab", devTab.TabId.ToString("D")]);
        Assert.True(devRows.ExitCode == 0, devRows.ToString());
        var values = devRows.Json[Oranda.Core.Ai.Schemas.AgentFields.RowValues]!.AsArray();
        Assert.Equal(2, values.Count);
        Assert.Equal("1", (string?)values[0]![0]);
        Assert.Equal("***", (string?)values[0]![1]);
        Assert.DoesNotContain("010-", devRows.Stdout, StringComparison.Ordinal);

        var tstRows = await rig.RunAsync(["result", "get", "--tab", tstTab.TabId.ToString("D")]);
        Assert.True(tstRows.ExitCode == 0, tstRows.ToString());
        Assert.Null(tstRows.Json[Oranda.Core.Ai.Schemas.AgentFields.RowValues]);
        Assert.Equal(2, (int)tstRows.Json["fetched"]!);
        Assert.Equal(["ORDER_ID", "CUST_PHONE"], tstRows.Json["columns"]!.AsArray().Select(c => (string?)c!["name"]));

        // error: 개발은 글·문장·위치, 운영은 번호와 위치만
        var state = ExecutionState.For(bus);
        state.SetError(Query("SELECT X FROM SAMPLE.ORDERS"), new DbError("ORA-00904", "ORA-00904: \"X\": invalid identifier") { Number = 904, Line = 1, Column = 8 }, devTab.TabId, Dev.Id);
        var devError = await rig.RunAsync(["error", "--tab", devTab.TabId.ToString("D")]);
        Assert.True(devError.ExitCode == 0, devError.ToString());
        Assert.Equal("ORA-00904", (string?)devError.Json["code"]);
        Assert.Contains("invalid identifier", (string?)devError.Json["message"], StringComparison.Ordinal);
        Assert.Equal("SELECT X FROM SAMPLE.ORDERS", (string?)devError.Json["sql_text"]);
        Assert.Equal(8, (int)devError.Json["col"]!);

        var prdTab = host.OpenTextTab("SELECT Y FROM SAMPLE.ORDERS", "운영", Prd.Id);
        state.SetError(Query("SELECT Y FROM SAMPLE.ORDERS"), new DbError("TBR-8033", "TBR-8033: Specified schema object was not found: 'SAMPLE.ORDERS'") { Line = 1, Column = 15 }, prdTab.TabId, Prd.Id);
        var prdError = await rig.RunAsync(["error"]);
        Assert.True(prdError.ExitCode == 0, prdError.ToString());
        Assert.Equal("TBR-8033", (string?)prdError.Json["code"]);
        Assert.Equal("***", (string?)prdError.Json["message"]);
        Assert.Null(prdError.Json["sql_text"]);
        Assert.DoesNotContain("SAMPLE", prdError.Stdout, StringComparison.Ordinal);

        // 오류가 없으면 no_result(도구 실패, 종료 코드 1)
        state.ClearError();
        var none = await rig.RunAsync(["error"]);
        Assert.Equal(AgentCliExit.ToolFailed, none.ExitCode);
        Assert.Equal(AgentErrorCodes.NoResult, (string?)none.Json["code"]);

        // 감사에는 연결의 클라이언트 이름과 허가 id 가 남는다.
        Assert.Contains(rig.Audit.Entries, e => e.Tool == "get_status" && e.Client == "codex" && e.RunId == rig.Issue.Grant.GrantId);
    });

    private static async Task<SqlEditorViewModel> TabWithResultAsync(SqlEditorHost host, Guid profile, string title)
    {
        var tab = host.OpenTextTab("SELECT * FROM SAMPLE.ORDERS", title, profile);
        var results = new ResultTabsViewModel();
        tab.ResultPane = results;
        object?[][] data = [[1m, "010-0000-0001"], [2m, "010-0000-0002"]];
        var item = results.AddQuery(new MemoryResult(Cols, data), "결과 1", Query("SELECT * FROM SAMPLE.ORDERS"));
        await item.Pane!.Model!.LoadFirstPageAsync();
        return tab;
    }

    private static SqlStatement Query(string text) => new(text, new Oranda.Core.Text.TextRange(0, text.Length), StatementKind.Query);

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
