using System.Diagnostics;
using Oracle.ManagedDataAccess.Client;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Integration;

/// <summary>
/// TSK-14-09 / D-041 「한 연결을 지키는 잠금 하나」 실제 서버 시험(상주 컨테이너 26ai·11.2, ORANDA_IT_PORT/SERVICE 로 고른다).
/// 긴 실행 중 같은 세션에 메타데이터 조회를 보내면 그 조회는 실행이 끝날 때까지 기다리고(즉시성 범위면 서버에 보내지 않고 바쁨),
/// 연결은 Broken 이 되지 않으며, 실행 취소는 그 실행만 멈춘다(기다리던 조회는 ORA-01013 없이 이어서 돈다).
/// 긴 실행은 권한이 필요 없는 CPU 고리(DBMS_UTILITY.GET_TIME)로 만든다: 11g 에서 DBMS_LOCK.SLEEP 은 EXECUTE 권한이 있어야 하고,
/// 시험 이미지는 PL/SQL 대기 중 브레이크를 읽지 않는다(ExecutionServiceTests 참고).
/// ORANDA_IT_ADMIN_PASSWORD 가 없으면 아무것도 하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
[Collection("GridEditStatics")]
[Oranda.App.Tests.TestSupport.ResetUiLanguage]
public sealed class SessionCommandGateFlowTests(ItDb db, ITestOutputHelper output) : IClassFixture<ItDb>
{
    private static readonly Dictionary<string, BindValue> NoBinds = [];

    [Fact]
    public Task Metadata_on_the_same_session_waits_for_a_long_run_or_skips_when_brief() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        var t = "GT_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        db.Exec($"create table {t} (id number, name varchar2(20))");
        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var session = rig.Session;
        var exec = rig.Container.GetRequiredService<IExecutionService>();
        var meta = rig.Container.GetRequiredService<IMetadataProvider>();

        // 1. 기다림: 실행이 연결을 쥔 동안 조회는 서버에 가지 않는다.
        var run = exec.ExecuteAsync(session, CpuLoop(seconds: 3), NoBinds, null, default);
        await WaitBusyAsync(session);
        var sw = Stopwatch.StartNew();
        var table = meta.GetTableAsync(session, new DbObjectRef(db.User, t, "TABLE"), default);
        await Task.Delay(800);
        Assert.False(table.IsCompleted, "실행 중인데 같은 연결로 메타데이터 조회가 끝났다");
        Assert.Equal(ExecutionStatus.Succeeded, (await run.WaitAsync(TimeSpan.FromSeconds(60))).Status);
        var info = await table.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"table waited {sw.ElapsedMilliseconds} ms");
        Assert.Equal(["ID", "NAME"], info.Columns.Select(c => c.Name));
        Assert.Equal(SessionState.Idle, session.State);

        // 2. 즉시성 범위(자동 완성): 기다리지 않고 바쁨으로 끝나며 실행은 영향이 없다.
        run = exec.ExecuteAsync(session, CpuLoop(seconds: 2), NoBinds, null, default);
        await WaitBusyAsync(session);
        sw.Restart();
        using (SessionWait.Brief())
        {
            await Assert.ThrowsAsync<SessionBusyException>(() => meta.ListSchemasAsync(session, default));
        }

        output.WriteLine($"brief gave up after {sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1), $"즉시성 조회가 {sw.ElapsedMilliseconds} ms 기다렸다");
        Assert.Equal(ExecutionStatus.Succeeded, (await run.WaitAsync(TimeSpan.FromSeconds(60))).Status);

        // 3. 사용자가 연 도구 창: 기다리다 취소할 수 있고, 취소해도 실행·연결은 그대로다.
        run = exec.ExecuteAsync(session, CpuLoop(seconds: 2), NoBinds, null, default);
        await WaitBusyAsync(session);
        using (var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => meta.ListSchemasAsync(session, cts.Token));
        }

        Assert.Equal(ExecutionStatus.Succeeded, (await run.WaitAsync(TimeSpan.FromSeconds(60))).Status);
        Assert.Equal(SessionState.Idle, session.State);
        Assert.Contains(db.User.ToUpperInvariant(), await meta.ListSchemasAsync(session, default));
        await AssertNextStatementRunsAsync(exec, session);
    });

    [Fact]
    public Task Cancel_stops_only_the_running_statement_and_the_waiting_query_runs_afterwards() => Ui.Run(async () =>
    {
        if (!db.Enabled)
        {
            return;
        }

        using var rig = new FlowRig(db);
        await rig.ConnectAsync();
        var session = rig.Session;
        if (session.Server.Version.StartsWith("11.", StringComparison.Ordinal) && !OracleConfiguration.DisableOOB
            && Environment.GetEnvironmentVariable("ORANDA_IT_CANCEL_STRICT") != "1")
        {
            output.WriteLine("11g: ORANDA_IT_11G_DISABLE_OOB=1 이 없어 취소 시험은 건너뛴다(미실행). 개발 PC 의 podman 포트 중계는 OOB 를 버리고 11g 는 그때 in-band 브레이크를 읽지 않는다.");
            return;
        }

        var exec = rig.Container.GetRequiredService<IExecutionService>();
        var meta = rig.Container.GetRequiredService<IMetadataProvider>();
        using var cts = new CancellationTokenSource();
        var run = exec.ExecuteAsync(session, CpuLoop(seconds: 30), NoBinds, null, cts.Token);
        await WaitBusyAsync(session);
        var schemas = meta.ListSchemasAsync(session, default); // 실행 뒤에 줄을 선다
        await Task.Delay(1000);
        Assert.False(schemas.IsCompleted);

        var sw = Stopwatch.StartNew();
        await cts.CancelAsync();
        var cancelled = await run.WaitAsync(TimeSpan.FromSeconds(60));
        output.WriteLine($"cancel took {sw.ElapsedMilliseconds} ms");
        Assert.Equal(ExecutionStatus.Cancelled, cancelled.Status);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"취소 반영 {sw.ElapsedMilliseconds} ms (혼자 끝나면 30 s)");

        // 브레이크는 실행 명령에만 갔다: 기다리던 조회는 ORA-01013 없이 끝나고 연결은 쓸 수 있다.
        var list = await schemas.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Contains(db.User.ToUpperInvariant(), list);
        Assert.Equal(SessionState.Idle, session.State);
        await AssertNextStatementRunsAsync(exec, session);
    });

    /// <summary>
    /// <c>ORANDA_IT_11G_DISABLE_OOB=1</c> 이면 이 시험 프로세스만 in-band 브레이크를 쓴다(Oracle11gTests 와 같다). 첫 연결 전에만 바꿀 수 있어(ORA-50099)
    /// 시험 어셈블리를 올릴 때 정한다.
    /// </summary>
#pragma warning disable CA2255 // 시험 어셈블리 전용: 첫 Oracle 연결보다 먼저 드라이버 설정을 정해야 한다
    [System.Runtime.CompilerServices.ModuleInitializer]
#pragma warning restore CA2255
    internal static void UseInBandBreakWhenAsked()
    {
        if (Environment.GetEnvironmentVariable("ORANDA_IT_11G_DISABLE_OOB") == "1")
        {
            OracleConfiguration.DisableOOB = true;
        }
    }

    /// <summary>권한 없이 도는 CPU 고리(초 단위). 취소 브레이크는 CPU 를 쓰는 동안 읽힌다.</summary>
    private static SqlStatement CpuLoop(int seconds) => new(
        $"DECLARE t PLS_INTEGER := DBMS_UTILITY.GET_TIME; n PLS_INTEGER := 0; BEGIN WHILE DBMS_UTILITY.GET_TIME - t < {seconds * 100} LOOP n := n + 1; END LOOP; END;",
        default, StatementKind.PlSql) { Verb = "DECLARE" };

    private static async Task WaitBusyAsync(ISession session)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (session.State != SessionState.Busy)
        {
            Assert.True(DateTime.UtcNow < until, "실행이 시작되지 않았다");
            await Task.Delay(20);
        }

        await Task.Delay(200); // 서버 호출이 나간 뒤
    }

    private static async Task AssertNextStatementRunsAsync(IExecutionService exec, ISession session)
    {
        var r = await exec.ExecuteAsync(session, new SqlStatement("SELECT 42 FROM dual", default, StatementKind.Query) { Verb = "SELECT" },
            NoBinds, null, default);
        Assert.Equal(ExecutionStatus.Succeeded, r.Status);
        var page = await r.ResultSet!.GetPageAsync(0, default);
        Assert.Equal("42", Convert.ToString(page.Columns[0][0], System.Globalization.CultureInfo.InvariantCulture));
        await r.ResultSet.DisposeAsync();
    }
}
