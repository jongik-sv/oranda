using System.Data;
using System.Reflection;
using Oranda.Core.Composition;
using Oranda.Core.Connection;
using Oranda.Core.Events;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Core.Tuning.Providers;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Driver;
using Oranda.Tibero.Tuning;

namespace Oranda.Tibero.Tests.Tuning;

/// <summary>
/// Tibero 튜닝 서비스 단위 시험(서버 없음). 없는 기능은 실패한 작업으로 돌려주고, 고리(읽기 전용 벤치마크 거절·계획 해시)는 가짜 드라이버로 확인한다.
/// 가짜 드라이버 처리기가 정적이라 FakeDriver 컬렉션에 둔다(병렬 금지).
/// </summary>
[Collection("FakeDriver")]
public sealed class TiberoTuningServiceTests : IDisposable
{
    private static readonly ServerInfo Server = new("6", "i", "h", null, "D", null, false, 7,
        new ServerCapabilities(false, false, true, false, false, Dbms.Tibero), DbUniqueName: "D", SessionUser: "U", Dbms: Dbms.Tibero);

    private readonly List<string> _sent = [];

    public TiberoTuningServiceTests() => FakeDriverHost.Reset();

    public void Dispose() => FakeDriverHost.Reset();

    // 생성자는 의존성을 저장만 한다(이력·추천이 기본 본문을 상속해도 만들 때 던지지 않는다). 시험은 null 의존성으로 만든다.
    private static TiberoTuningService NewService() => new(null!, null!, null!);

    [Fact]
    public void Service_is_registered_for_tibero_as_the_tuning_service()
    {
        var attribute = Assert.Single(typeof(TiberoTuningService).GetCustomAttributes<OrandaServiceAttribute>(inherit: false));

        Assert.Equal(typeof(ITuningService), attribute.ServiceType);
        Assert.Equal((Dbms?)Dbms.Tibero, attribute.ForDbms);
        Assert.True(typeof(ITuningService).IsAssignableFrom(typeof(TiberoTuningService)));
        Assert.True(typeof(CatalogTuningService).IsAssignableFrom(typeof(TiberoTuningService)));
    }

    [Fact]
    public async Task Shared_cursor_reasons_fail_as_a_faulted_task_with_the_reason()
    {
        var task = NewService().GetSharedCursorReasonsAsync(null!, "abc", CancellationToken.None);

        Assert.True(task.IsFaulted); // 동기로 던지지 않고 실패한 작업으로 돌려준다
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(TiberoTuningService.SharedCursorReasonsUnavailable, ex.Message);
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task Sql_plan_controls_fail_as_a_faulted_task_with_the_reason()
    {
        var task = NewService().GetSqlPlanControlsAsync(null!, "abc", CancellationToken.None);

        Assert.True(task.IsFaulted);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(TiberoTuningService.PlanControlsUnavailable, ex.Message);
        Assert.NotEmpty(ex.Message);
    }

    [Fact]
    public async Task Trace_file_fails_as_a_faulted_task_with_the_trace_reason_and_sends_nothing()
    {
        Serve();
        var service = NewService();
        await using var session = NewSession(SessionPurpose.Work);

        var task = service.GetTraceFileAsync(session, default, CancellationToken.None);

        Assert.True(task.IsFaulted);
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(TiberoTuningService.TraceUnavailable, ex.Message);
        Assert.Contains("트레이스", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Trace_file_checks_the_session_argument_first()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => NewService().GetTraceFileAsync(null!, default, CancellationToken.None));
    }

    [Fact]
    public void Benchmark_reads_use_the_tibero_statistic_names()
    {
        var service = NewService();

        var logical = (string)typeof(CatalogTuningService).GetProperty("LogicalReadsStatistic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var physical = (string)typeof(CatalogTuningService).GetProperty("PhysicalReadsStatistic", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;

        Assert.Equal("logical reads", logical);
        Assert.NotEqual("session logical reads", logical); // Oracle 이름이면 v$mystat 에 없어 논리 읽기가 늘 0
        // 이름이 세션 통계 출처가 실제로 읽는 이름과 같아야 한다(어긋나면 0).
        Assert.Contains(logical, TiberoSessionStatistics.Interesting);
        Assert.Contains(physical, TiberoSessionStatistics.Interesting);
    }

    // Tibero 의 READ ONLY 트랜잭션은 쓰기를 막지 않는다(쓰기 IT #28, main a0140964) → 사용자 함수가 든 문장의 읽기 전용 벤치마크는 거절한다.
    // 기본 본문은 이 고리를 문장을 보내기 전에 부르므로 서버로는 아무 것도 가지 않는다(실행 서비스가 null 이어도 닿지 않는다).
    [Theory]
    [InlineData(SessionPurpose.Monitor)]
    [InlineData(SessionPurpose.Work)]
    public async Task Read_only_benchmark_is_refused_without_sending_anything(SessionPurpose purpose)
    {
        Serve();
        var service = new TiberoTuningService(null!, null!, new SelectOnlyLanguage());
        await using var session = NewSession(purpose);

        var task = service.RunBenchmarkAsync(session, new BenchmarkCase("t", "SELECT f(1) FROM dual") { ReadOnly = true }, 1, CancellationToken.None);

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => task);
        Assert.Equal(TiberoTuningService.ReadOnlyBenchmarkUnavailable, ex.Message);
        Assert.Contains("쓰기", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_sent);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Previous_plan_hash_is_null_when_there_is_no_row()
    {
        Serve(cmd => PrevCursorTable());
        await using var session = NewSession(SessionPurpose.Monitor);

        Assert.Null(await TiberoTuningService.ReadPreviousPlanHashAsync(session, CancellationToken.None));
        Assert.Single(_sent);
        Assert.False(session.CommandGate.IsHeld);
    }

    [Fact]
    public async Task Previous_plan_hash_is_the_third_column_of_the_first_row()
    {
        Serve(cmd => PrevCursorTable(["abc123", "451113", "987654321"]));
        await using var session = NewSession(SessionPurpose.Monitor);

        Assert.Equal(987654321L, await TiberoTuningService.ReadPreviousPlanHashAsync(session, CancellationToken.None));
    }

    [Fact]
    public async Task Previous_plan_hash_is_null_when_the_view_cannot_be_read()
    {
        Serve(cmd => throw FakeDriverHost.ServerError(-8033, "TBR-8033: not found"));
        await using var session = NewSession(SessionPurpose.Monitor);

        Assert.Null(await TiberoTuningService.ReadPreviousPlanHashAsync(session, CancellationToken.None));
        Assert.False(session.CommandGate.IsHeld);
    }

    private static DataTable PrevCursorTable(params string[][] rows)
    {
        var table = new DataTable();
        foreach (var c in new[] { "PREV_SQL_ID", "PREV_CHILD_NUMBER", "PLAN_HASH_VALUE" })
        {
            table.Columns.Add(c, typeof(string));
        }

        foreach (var row in rows)
        {
            table.Rows.Add([.. row]);
        }

        return table;
    }

    private void Serve(Func<System.Data.Common.DbCommand, object?>? respond = null) =>
        FakeDriverHost.SetHandler(cmd =>
        {
            lock (_sent)
            {
                _sent.Add(cmd.CommandText);
            }

            return respond?.Invoke(cmd);
        });

    /// <summary>가짜 드라이버 연결을 쓰는 Tibero 세션(보낸 문장은 <see cref="Serve"/> 가 기록한다).</summary>
    private static TiberoSession NewSession(SessionPurpose purpose)
    {
        var driver = FakeDriverHost.Driver;
        var connection = driver.CreateConnection("Data Source=x");
        connection.Open();
        var profile = new ConnectionProfile("t", "u", "h:1/d", Dbms: Dbms.Tibero);
        return new TiberoSession(Guid.NewGuid(), profile, Server, purpose, "D/-/U", connection, null, new EventBus(), driver: driver);
    }

    /// <summary>문장 하나를 SELECT 질의로 돌려주는 언어 서비스(벤치마크 문장 검사만 통과시킨다).</summary>
    private sealed class SelectOnlyLanguage : ILanguageService
    {
        public IReadOnlyList<SqlStatement> Split(string text, SplitOptions o) =>
            [new SqlStatement(text, default, StatementKind.Query) { Verb = "SELECT" }];

        public SqlStatement? GetStatementAt(string text, int offset, SplitOptions o) => throw new NotSupportedException();

        public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct) => throw new NotSupportedException();

        public CompletionContext GetCompletionContext(string text, int offset) => throw new NotSupportedException();
    }
}
