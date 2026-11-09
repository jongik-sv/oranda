using System.Diagnostics;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Monitoring;
using Oranda.Core.Sessions;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Monitoring;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// P7 실측(쓰기, <c>ORANDA_IT_TIBERO_WRITE=1</c>): 세션 Kill(<c>ALTER SYSTEM KILL SESSION (sid, serial)</c>).
/// K1 = 일반 시험 계정: 권한이 없어 서버가 TBR-17004 로 거절한다(서비스 예외, 실행 서비스 결과 분류, SERIAL# 보정). 아무것도 죽지 않는다.
/// K2 = 권한 계정(<c>ORANDA_IT_TIBERO_KILL=1</c> + <c>ORANDA_IT_TIBERO_KILL_USER</c>·<c>ORANDA_IT_TIBERO_KILL_PASSWORD</c> 가 모두 있을 때만):
/// 이 시험이 연 작업 세션 하나만 죽이고 끊김을 확인한다. 접속 정보는 시험 로그에 싣지 않는다. 측정값은 <c>[Kn] …</c> 줄.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoMonitorKillIt(ITestOutputHelper output)
{
    private const string Prefix = "ORANDA_IT_TIBERO_";

    private static readonly TimeSpan GoneTimeout = TimeSpan.FromSeconds(10);

    // K1: 일반 계정의 Kill 은 17004 로 거절되고 작업 세션은 살아 있다.
    [TiberoWriteFact]
    public async Task K1_kill_without_privilege_is_rejected_with_17004_and_session_survives()
    {
        if (TiberoItSessions.SkipWrite())
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var monitor = await it.OpenAsync(SessionPurpose.Monitor);
        var work = await it.OpenAsync();
        var execution = NewExecution(it);
        var service = new TiberoMonitorService(execution);
        var key = await WorkKeyAsync(work);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        output.WriteLine($"[K1] work = inst {key.InstanceId}, sid {key.Sid}, serial {key.Serial}");

        // 서비스 길: 실패 결과는 InvalidOperationException(서버 메시지). 드라이버 머리말이 ODP- 든 TBR- 든 번호만 본다.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.KillAsync(monitor, key, true, cts.Token));
        Assert.Contains("17004", ex.Message, StringComparison.Ordinal);
        await AssertAliveAsync(work, cts.Token);

        // 실행 서비스 직접: 실패 + 분류가 권한 부족.
        var result = await execution.ExecuteAsync(monitor, TiberoMonitorService.KillStatement(key), new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Monitor }, cts.Token);
        Assert.Equal(ExecutionStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
        output.WriteLine($"[K1] kill error: number {result.Error.Number}, kind {result.Error.Kind}");
        Assert.Equal(DbErrorKind.InsufficientPrivileges, result.Error.Kind);
        await AssertAliveAsync(work, cts.Token);

        // 큰 SERIAL#: V$SESSION 이 음수로 보여 준 값(-2138093902)은 문장에서 2^32 를 더한 2156873394(int 최대값 초과)가 된다.
        // 서버가 이 큰 수를 문법상 받아들이는지는 「권한 없음」(17004) 응답으로 알 수 있다(문법·범위 오류면 다른 오류가 난다).
        // 일반 계정이라 권한 검사에서 걸러져 아무 세션도 죽지 않는다(SID 도 이 시험의 작업 세션 것이고 SERIAL# 은 어차피 안 맞는다).
        var big = TiberoMonitorService.KillStatement(new SessionKey(key.InstanceId, key.Sid, -2138093902));
        Assert.Contains("2156873394", big.Text, StringComparison.Ordinal);
        var bigResult = await execution.ExecuteAsync(monitor, big, new Dictionary<string, BindValue>(),
            new ExecutionOptions { Source = HistorySource.Monitor }, cts.Token);
        Assert.Equal(ExecutionStatus.Failed, bigResult.Status);
        Assert.NotNull(bigResult.Error);
        output.WriteLine($"[K1] big serial kill error: number {bigResult.Error.Number}, kind {bigResult.Error.Kind}");
        Assert.Equal(DbErrorKind.InsufficientPrivileges, bigResult.Error.Kind);
        await AssertAliveAsync(work, cts.Token);
        Assert.False(monitor.CommandGate.IsHeld);
    }

    // K2: 권한 계정이 이 시험의 작업 세션만 죽인다. 작업 세션의 다음 명령은 끊김으로 실패하거나, 세션이 목록에서 사라진다.
    [TiberoKillFact]
    public async Task K2_privileged_kill_ends_the_work_session()
    {
        if (TiberoItSessions.SkipWrite() || KillEnv() is not { } killEnv)
        {
            return;
        }

        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        await using var privileged = TiberoItSessions.Create(killEnv);
        if (privileged is null)
        {
            return;
        }

        var work = await it.OpenAsync(); // 일반 계정(죽을 세션)
        var monitor = await privileged.OpenAsync(SessionPurpose.Monitor); // 권한 계정
        var service = NewService(privileged);
        var key = await WorkKeyAsync(work);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        output.WriteLine($"[K2] work = inst {key.InstanceId}, sid {key.Sid}, serial {key.Serial}");

        var before = await service.GetSessionsAsync(monitor, new SessionFilter(), cts.Token);
        Assert.True(before.Any(r => r.Key == key), "권한 계정이 작업 세션을 보지 못한다"); // 컬렉션 단정은 실패 때 행(계정 이름)을 찍는다

        await service.KillAsync(monitor, key, false, cts.Token); // 이 시험이 만든 세션 하나만

        var classifier = new TiberoErrorClassifier();
        var clock = Stopwatch.StartNew();
        string? outcome = null;
        while (outcome is null)
        {
            try
            {
                await AssertAliveAsync(work, cts.Token);
            }
#pragma warning disable CA1031 // 죽은 세션의 명령은 드라이버 예외로 실패한다. 어떤 예외든 분류해 확인한다.
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                var error = classifier.FromException(ex);
                output.WriteLine($"[K2] next command failed: {ex.GetType().Name}, number {error?.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}, kind {error?.Kind.ToString() ?? "-"}");
                Assert.True(error is { Kind: DbErrorKind.Disconnected }, "죽은 세션의 다음 명령이 Disconnected 로 분류되지 않았다");
                outcome = "다음 명령이 Disconnected 로 실패";
                break;
            }

            var sessions = await service.GetSessionsAsync(monitor, new SessionFilter(), cts.Token);
            if (sessions.All(r => r.Key != key))
            {
                outcome = "목록에서 사라짐(다음 명령은 아직 성공)";
                break;
            }

            if (clock.Elapsed > GoneTimeout)
            {
                Assert.Fail($"{GoneTimeout.TotalSeconds:0}초 안에 작업 세션이 끊기지도 목록에서 사라지지도 않았다");
            }

            await Task.Delay(500, cts.Token);
        }

        output.WriteLine($"[K2] result: {outcome} ({clock.ElapsedMilliseconds} ms)");
        Assert.False(monitor.CommandGate.IsHeld);
    }

    /// <summary>권한 계정 환경: KILL=1 이고 KILL_USER·KILL_PASSWORD 가 모두 있을 때만. 나머지(주소·포트·DB 이름)는 일반 환경과 같다. 아니면 null.</summary>
    private static TiberoItEnv? KillEnv()
    {
        var user = Environment.GetEnvironmentVariable(Prefix + "KILL_USER");
        var password = Environment.GetEnvironmentVariable(Prefix + "KILL_PASSWORD");
        if (Environment.GetEnvironmentVariable(Prefix + "KILL") != "1" || string.IsNullOrEmpty(user) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        return new TiberoItEnv(name => name switch
        {
            Prefix + "USER" => user,
            Prefix + "PASSWORD" => password,
            _ => Environment.GetEnvironmentVariable(name),
        });
    }

    /// <summary>작업 세션이 살아 있다: <c>SELECT 1 FROM dual</c> 이 1 을 준다.</summary>
    private static async Task AssertAliveAsync(TiberoSession work, CancellationToken ct)
    {
        // 끊긴 소켓에서 드라이버가 취소를 무시하고 막힐 수 있어 제한 시간으로 감싼다(넘기면 시험 실패, 멈추지 않음).
        var probe = TiberoSessionQuery.ScalarAsync(work, "SELECT 1 FROM dual", ct: ct);
        var done = await Task.WhenAny(probe, Task.Delay(GoneTimeout, ct));
        Assert.True(ReferenceEquals(done, probe), $"작업 세션 확인이 {GoneTimeout.TotalSeconds:0}초 안에 끝나지 않았다");
        Assert.Equal(1L, TiberoSessionQuery.Long(await probe));
    }

    /// <summary>작업 세션의 키(V$SESSION 원값 SERIAL#, 음수일 수 있다). 인스턴스 번호 = 카탈로그 SQL 과 같은 USERENV INSTANCE.</summary>
    private static async Task<SessionKey> WorkKeyAsync(TiberoSession work)
    {
        var table = await TiberoSessionQuery.RunAsync(work, "SELECT TO_NUMBER(SYS_CONTEXT('USERENV', 'INSTANCE')), sid, serial# FROM v$session WHERE sid = (SELECT m.sid FROM v$mystat m WHERE ROWNUM = 1)");
        var row = Assert.Single(table.Rows);
        return new SessionKey((int)(TiberoSessionQuery.Long(row[0]) ?? 0), TiberoSessionQuery.Long(row[1]) ?? 0, TiberoSessionQuery.Long(row[2]) ?? 0);
    }

    private static TiberoExecutionService NewExecution(TiberoItSessions it) =>
        new(new AllowAll(), new NoQuestions(), it.Events, null, dbmsOutput: false);

    private static TiberoMonitorService NewService(TiberoItSessions it) => new(NewExecution(it));

    private sealed class AllowAll : IExecutionPolicy
    {
        public ValueTask<PolicyDecision> EvaluateAsync(PolicyContext ctx, SqlStatement stmt, CancellationToken ct) =>
            ValueTask.FromResult(PolicyDecision.Allowed);
    }

    private sealed class NoQuestions : IExecutionInteraction
    {
        public Task<bool> ConfirmAsync(SqlStatement stmt, PolicyDecision decision, CancellationToken ct) => Task.FromResult(true);

        public Task<IReadOnlyDictionary<string, string>?> AskSubstitutionsAsync(IReadOnlyList<string> names, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public Task<ScriptErrorAction> AskScriptErrorAsync(SqlStatement stmt, DbError failure, CancellationToken ct) =>
            Task.FromResult(ScriptErrorAction.Stop);
    }
}
