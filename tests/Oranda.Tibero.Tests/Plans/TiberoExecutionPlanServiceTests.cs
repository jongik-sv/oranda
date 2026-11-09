using System.Data;
using Oranda.Core.Catalog.Providers;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Core.Plans.Providers;
using Oranda.Core.Sessions;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Driver;
using Oranda.Tibero.Plans;

namespace Oranda.Tibero.Tests.Plans;

public class TiberoExecutionPlanServiceTests
{
    [Theory]
    [InlineData("TABLE ACCESS (FULL)", "TABLE ACCESS", "FULL")]
    [InlineData("INDEX (RANGE SCAN)", "INDEX", "RANGE SCAN")]
    [InlineData("table access (full)", "table access", "full")] // 대소문자 그대로
    [InlineData("index (full)", "index", "full")]
    [InlineData("GROUP BY (SORT)", "GROUP BY", "SORT")]
    [InlineData("NESTED LOOPS (CARTESIAN)", "NESTED LOOPS", "CARTESIAN")]
    [InlineData("hash join (cartesian)", "hash join", "cartesian")]
    [InlineData("TABLE ACCESS (  FULL  )", "TABLE ACCESS", "FULL")] // 괄호 안 공백 뗌
    [InlineData("TABLE ACCESS   (FULL)  ", "TABLE ACCESS", "FULL")] // 앞뒤 공백
    [InlineData("INDEX (UNIQUE (PK_T))", "INDEX", "UNIQUE (PK_T)")] // 중첩: 바깥 짝, 안쪽은 Options 에
    [InlineData("table access (full) (x)", "table access", "full) (x")] // 첫 「 (」 에서 나눈다(recommend_plan.sql 과 같다), 끝까지 균형이면 괄호 사이 전부가 Options
    public void Operation_with_parentheses_is_split(string operation, string expectedOperation, string expectedOptions)
    {
        var step = TiberoExecutionPlanService.SplitOperation(Raw(operation));

        Assert.Equal(expectedOperation, step.Operation);
        Assert.Equal(expectedOptions, step.Options);
    }

    [Theory]
    [InlineData("SELECT STATEMENT")]
    [InlineData("HASH JOIN")]
    [InlineData("FOO(bar)")] // 함수 모양: 앞에 공백이 없다
    [InlineData("(FULL)")] // 이름이 없다
    [InlineData("TABLE ACCESS (FULL")] // 닫는 괄호 없음
    [InlineData("TABLE ACCESS FULL)")] // 여는 괄호 없음
    [InlineData("TABLE ACCESS (FULL) (x")] // 닫는 괄호로 안 끝남
    [InlineData("TABLE ACCESS (FULL)) x)")] // 괄호가 균형이 아님
    [InlineData("")]
    public void Operation_without_a_trailing_parenthesis_group_is_unchanged(string operation)
    {
        var raw = Raw(operation);

        Assert.Same(raw, TiberoExecutionPlanService.SplitOperation(raw));
    }

    [Fact]
    public void Existing_options_are_kept()
    {
        var raw = Raw("TABLE ACCESS (FULL)") with { Options = "BY INDEX ROWID" };

        Assert.Same(raw, TiberoExecutionPlanService.SplitOperation(raw));
    }

    [Fact]
    public void Blank_options_do_not_block_the_split()
    {
        var step = TiberoExecutionPlanService.SplitOperation(Raw("INDEX (FULL)") with { Options = "  " });

        Assert.Equal("INDEX", step.Operation);
        Assert.Equal("FULL", step.Options);
    }

    [Fact]
    public void Other_fields_survive_the_split()
    {
        var raw = new RawPlanStep(3, 2, "TABLE ACCESS (FULL)", null, "SCOTT", "EMP", 10, 100, 90, 1, 5, "a", "f");

        var step = TiberoExecutionPlanService.SplitOperation(raw);

        Assert.Equal(raw with { Operation = "TABLE ACCESS", Options = "FULL" }, step);
    }

    // tuning.recommend_plan 이 나누는 세 짝(Tibero 소문자 OPERATION)과 같은 곳에서 나누는지 대조한다.
    // 다른 점(알고 둔 것): SQL 은 UPPER 로 올리고 index (full) 의 방식을 Oracle 이름 「FULL SCAN」 으로 바꾼다. 이 규칙은 대소문자·이름을 바꾸지 않는다.
    [Theory]
    [InlineData("table access (full)", "TABLE ACCESS", "FULL")]
    [InlineData("index (full)", "INDEX", "FULL SCAN")]
    [InlineData("nested loops (cartesian)", "NESTED LOOPS", "CARTESIAN")]
    public void Split_point_matches_recommend_plan_sql(string operation, string sqlOperation, string sqlOptions)
    {
        var step = TiberoExecutionPlanService.SplitOperation(Raw(operation));

        Assert.Equal(sqlOperation, step.Operation, ignoreCase: true);
        if (sqlOptions != "FULL SCAN")
        {
            Assert.Equal(sqlOptions, step.Options, ignoreCase: true);
        }
        else
        {
            Assert.StartsWith("full", step.Options, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Risk_is_found_after_the_split()
    {
        var full = TiberoExecutionPlanService.SplitOperation(Raw("TABLE ACCESS (FULL)"));
        var indexFull = TiberoExecutionPlanService.SplitOperation(Raw("INDEX (FULL)"));
        var cartesian = TiberoExecutionPlanService.SplitOperation(Raw("NESTED LOOPS (CARTESIAN)"));
        var lower = TiberoExecutionPlanService.SplitOperation(Raw("table access (full)"));

        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(Raw("TABLE ACCESS (FULL)"))); // 나누기 전엔 위험 표시가 사라진다
        Assert.Equal(PlanRisk.FullScan, PlanRows.ClassifyRisk(full));
        Assert.Equal(PlanRisk.FullScan, PlanRows.ClassifyRisk(lower));
        Assert.Equal(PlanRisk.None, PlanRows.ClassifyRisk(indexFull)); // 인덱스 전체 스캔은 FullScan 이 아니다(Oracle INDEX FULL SCAN 과 같다)
        Assert.Equal(PlanRisk.Cartesian, PlanRows.ClassifyRisk(cartesian));
        // 첫 「 (」 에서 나눠 뒤따르는 괄호 묶음이 있어도 FullScan 이 산다.
        Assert.Equal(PlanRisk.FullScan, PlanRows.ClassifyRisk(TiberoExecutionPlanService.SplitOperation(Raw("table access (full) (x)"))));
    }

    [Fact]
    public void Last_cursor_reads_the_sql_id_and_leaves_the_child_number_null()
    {
        // 자식 번호는 열이 있어도 쓰지 않는다: PREV_CHILD_NUMBER 와 V$SQL_PLAN.CHILD_NUMBER 의 같음을 아직 재지 않았다(M6).
        var cursor = TiberoExecutionPlanService.ParseLastCursor(Rows(["abc123def4567", 3m, 1234567890m]));

        Assert.NotNull(cursor);
        Assert.Equal("abc123def4567", cursor.SqlId);
        Assert.Null(cursor.ChildNumber);
        Assert.Null(cursor.InstanceId);
        Assert.Equal(new CursorRef("abc123def4567"), TiberoExecutionPlanService.ParseLastCursor(Rows(["abc123def4567", null, null])));
    }

    [Fact]
    public void Last_cursor_is_null_without_a_row_or_sql_id()
    {
        Assert.Null(TiberoExecutionPlanService.ParseLastCursor(new CatalogRows([], [])));
        Assert.Null(TiberoExecutionPlanService.ParseLastCursor(Rows([null, null, null])));
        Assert.Null(TiberoExecutionPlanService.ParseLastCursor(Rows(["  ", 0m, null])));
    }

    [Theory]
    [InlineData(CatalogBindKind.Text, DbType.String)]
    [InlineData(CatalogBindKind.Int32, DbType.Int32)]
    [InlineData(CatalogBindKind.Int64, DbType.Int64)]
    [InlineData(CatalogBindKind.Double, DbType.Double)]
    [InlineData(CatalogBindKind.Date, DbType.DateTime)]
    public void Bind_kinds_map_to_db_types(CatalogBindKind kind, DbType expected)
    {
        Assert.Equal(expected, TiberoCatalogPort.DbTypeOf(kind));
    }

    [Theory]
    [InlineData(CatalogBindKind.Text, "Varchar2")]
    [InlineData(CatalogBindKind.Int32, "Int32")]
    [InlineData(CatalogBindKind.Int64, "Int64")]
    [InlineData(CatalogBindKind.Double, "Double")]
    [InlineData(CatalogBindKind.Date, "Date")]
    public void Bind_kinds_map_to_driver_param_kinds(CatalogBindKind kind, string expected)
    {
        Assert.Equal(Enum.Parse<TiberoParamKind>(expected), TiberoCatalogPort.ParamKindOf(kind));
    }

    [Fact]
    public void Same_name_with_equal_value_is_added_once()
    {
        var binds = TiberoCatalogPort.Dedupe(
            [CatalogBinds.Text("sql_id", "abc"), CatalogBinds.Int("inst_id", null), CatalogBinds.Int("INST_ID", null), CatalogBinds.Text("sql_id", "abc")]);

        Assert.Equal(["sql_id", "inst_id"], binds.Select(b => b.Name));
    }

    [Fact]
    public void Same_name_with_a_different_value_or_kind_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => TiberoCatalogPort.Dedupe([CatalogBinds.Int("child", 1), CatalogBinds.Int("child", 2)]));
        Assert.Throws<ArgumentException>(() => TiberoCatalogPort.Dedupe([CatalogBinds.Int("child", null), CatalogBinds.Int("child", 0)]));
        Assert.Throws<ArgumentException>(() => TiberoCatalogPort.Dedupe([CatalogBinds.Int("n", 1), CatalogBinds.Long("n", 1)]));
    }

    [Fact]
    public async Task The_port_rejects_a_non_tibero_session_before_anything_else()
    {
        // 카탈로그 Get·라이선스 관문보다 먼저 세션 형식을 본다(없는 id 라도 ArgumentException).
        await Assert.ThrowsAsync<ArgumentException>(() =>
            TiberoCatalogPort.QueryAsync(new FakeSession(), "no.such.id", CatalogBinds.None, CatalogQueryOptions.Default, CancellationToken.None));
    }

    [Fact]
    public void An_unknown_bind_kind_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TiberoCatalogPort.DbTypeOf((CatalogBindKind)99));
    }

    [Fact]
    public async Task Html_monitor_report_is_not_supported()
    {
        var service = new TiberoExecutionPlanService(null!);

        await Assert.ThrowsAsync<NotSupportedException>(() => service.GetSqlMonitorReportAsync(new FakeSession(), new CursorRef("abc"), html: true, CancellationToken.None));
    }

    [Fact]
    public async Task Text_monitor_report_goes_to_the_tibero_port()
    {
        var service = new TiberoExecutionPlanService(null!);

        // Tibero 세션이 아니면 포트(TiberoSessionQuery.RunAsync)가 거절한다 = html 이 아닌 길은 기반 클래스를 거쳐 포트까지 간다.
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetSqlMonitorReportAsync(new FakeSession(), new CursorRef("abc"), html: false, CancellationToken.None));
    }

    private static RawPlanStep Raw(string operation) =>
        new(1, null, operation, null, null, null, null, null, null, null, null, null, null);

    private static CatalogRows Rows(object?[] row) => new([], [row]);

    private sealed class FakeSession : ISession
    {
        public Guid Id { get; } = Guid.NewGuid();

        public ConnectionProfile Profile { get; } = new("t", "u", "h:1/d", Dbms: Dbms.Tibero);

        public ServerInfo Server { get; } = new("6", null, null, null, null, null, false, 1,
            new ServerCapabilities(false, false, false, false, false, Dbms.Tibero), Dbms: Dbms.Tibero);

        public SessionPurpose Purpose => SessionPurpose.Work;

        public string DbKey => "D/-/U";

        public SessionState State => SessionState.Idle;

        public bool HasPendingTransaction => false;

        public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
