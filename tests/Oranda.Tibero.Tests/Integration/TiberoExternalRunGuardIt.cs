using Oranda.Core.Ai.Gate;
using Oranda.Core.Ai.Protocol;
using Oranda.Core.Execution;
using Oranda.Core.Grid;
using Oranda.Core.History;
using Oranda.Core.Language;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Language.Ai.Handlers.External;
using Oranda.Tibero.Catalog;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Support;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// agent-cli-1122 레인 w-it-p2(R5 Tibero): <c>run_tab</c>·<c>export_result</c> 의 문장 가드(<see cref="IExternalSelectGuard"/>, 사용자 함수 확인 + 이름 모두 풀림)를 Tibero 실서버로.
/// <c>run_tab</c> 본체는 App 처리자라 이 프로젝트에서 부르지 못한다 → 「기본 성공」은 같은 길의 핵심(가드 통과 → 실제 정책 <see cref="ExecutionPolicy"/> 규칙 0 + 출처 Agent 실행)까지 본다.
/// 사용자 함수(w-tibero-fn): 확인기가 Tibero 카탈로그 글(<c>app.user_fn_*</c>, parent_obj_*·org_object_*)로 딕셔너리를 읽는다 →
/// 안전 함수 = 통과, 동의어(PUBLIC.DUAL·PUBLIC.DBMS_OUTPUT)를 거치는 안전 함수 = 통과, 자치 트랜잭션 함수·PUBLIC.UTL_HTTP 를 거치는 함수 = <c>sql_unsafe</c>.
/// 내장 AI 의 <c>benchmark_queries</c> 게이트(AgentGate)도 같은 확인기의 판정을 쓰므로 판정 값을 따로 본다.
/// 쓰기(<c>ORANDA_IT_TIBERO_WRITE=1</c>)일 때만 ORT_AC_ 표·함수를 만들고 끝에 지운다. 함수 권한이 없으면(17004) 함수 시험은 관찰 줄만 남긴다.
/// 시험 계정은 동의어를 만들 권한이 없을 수 있어(2026-11-23 실측 17004) 개인 동의어 경유 자치 함수는 만들어질 때만 본다. 접속 값은 출력하지 않는다.
/// 가드·추출기·확인기는 Oranda.Language 의 internal 형식이라 이름으로 만든다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoExternalRunGuardIt(ITestOutputHelper output)
{
    private static readonly string Table = TiberoItSessions.ObjectName("AC_RUN_T");
    private static readonly string Log = TiberoItSessions.ObjectName("AC_RUN_LOG");
    private static readonly string Plain = TiberoItSessions.ObjectName("AC_RUN_FN_PLAIN");
    private static readonly string Auto = TiberoItSessions.ObjectName("AC_RUN_FN_AUTO");
    private static readonly string ViaPublic = TiberoItSessions.ObjectName("AC_RUN_FN_PUB");
    private static readonly string Http = TiberoItSessions.ObjectName("AC_RUN_FN_HTTP");
    private static readonly string Syn = TiberoItSessions.ObjectName("AC_RUN_SYN_AUTO");
    private static readonly string ViaSyn = TiberoItSessions.ObjectName("AC_RUN_FN_VIA");

    private static readonly ExternalSelectOptions TabOptions = new() { VerifyUserFunctions = true, RequireResolvedNames = true };

    [TiberoWriteFact]
    public async Task Tab_guard_passes_select_and_safe_functions_refuses_lock_dml_and_risky_functions_and_the_agent_run_succeeds_on_tibero()
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

        var setup = await it.OpenAsync();
        await DropAsync(setup);
        try
        {
            await ExecAsync(setup, $"CREATE TABLE {Table} (ID NUMBER PRIMARY KEY, NM VARCHAR2(20))");
            await ExecAsync(setup, $"INSERT INTO {Table} SELECT LEVEL, 'N' || LEVEL FROM DUAL CONNECT BY LEVEL <= 30");
            await ExecAsync(setup, "COMMIT");
            await ExecAsync(setup, $"CREATE TABLE {Log} (N NUMBER)");
            var (hasFunctions, hasSynonym) = await CreateFunctionsAsync(setup);
            output.WriteLine($"[run-guard] functions={hasFunctions} synonym={hasSynonym}");
            var tab = await it.OpenAsync();
            var exec = new TiberoExecutionService(new ExecutionPolicy(), ExecutionFakes.NoQuestions(), it.Events, null, dbmsOutput: false);
            var guard = NewGuard(new TiberoMetadataProvider(TiberoCatalogQuery.RunAsync), exec);

            var select = await guard.CheckAsync(tab, $"SELECT id, nm FROM {Table} ORDER BY id", TabOptions, default);
            var forUpdate = await guard.CheckAsync(tab, $"SELECT id FROM {Table} WHERE id = 1 FOR UPDATE", TabOptions, default);
            var dml = await guard.CheckAsync(tab, $"DELETE FROM {Table}", TabOptions, default);
            Assert.True(select.Allowed, select.Code + " " + select.Message);
            Assert.Equal(AgentErrorCodes.SqlUnsafe, forUpdate.Code);
            Assert.Equal(AgentErrorCodes.SqlNotSingleQuery, dml.Code);

            // run_tab 의 실행: 가드가 돌려준 문장 그대로 출처 Agent(정책 규칙 0 = Query 만).
            var run = await exec.ExecuteAsync(tab, select.Statement!, new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true }, default);
            Assert.Equal(ExecutionStatus.Succeeded, run.Status);
            await using (var rs = Assert.IsAssignableFrom<IResultSet>(run.ResultSet))
            {
                Assert.Equal(30, (await rs.GetPageAsync(0, default)).RowCount);
            }

            // Export_after_partial_fetch_reads_schema: 그리드처럼 첫 쪽만 읽고 커서를 연 채, 같은 세션에서 export_result 의 가드(SYS_CONTEXT 로 현재 스키마 읽기)를 돈다.
            // Tibero 세션은 열린 결과가 명령 관문을 잡을 수 있어 막히는지 본다(30초 넘으면 실패).
            var big = await exec.ExecuteAsync(tab, new SqlStatement("SELECT LEVEL AS n FROM DUAL CONNECT BY LEVEL <= 2000", default, StatementKind.Query) { Verb = "SELECT" },
                new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true }, default);
            await using (var open = Assert.IsAssignableFrom<IResultSet>(big.ResultSet))
            {
                var first = await open.GetPageAsync(0, default);
                Assert.False(first.IsLast);
                var during = guard.CheckAsync(tab, $"SELECT id, nm FROM {Table}", TabOptions, default).AsTask();
                var finished = await Task.WhenAny(during, Task.Delay(TimeSpan.FromSeconds(30)));
                output.WriteLine($"[run-guard] guard-during-open-cursor finished={ReferenceEquals(finished, during)}");
                Assert.Same(during, finished);
                var check = await during;
                output.WriteLine($"[run-guard] guard-during-open-cursor allowed={check.Allowed} code={check.Code}");
                Assert.True(check.Allowed, check.Code + " " + check.Message);
                Assert.False((await open.GetPageAsync(1, default)).RowCount == 0); // 열린 커서도 계속 읽힌다
            }

            // 세션 현재 스키마를 바꾸면(Tibero 도 ALTER SESSION SET CURRENT_SCHEMA) 가드는 SYS_CONTEXT 를 읽어 그 스키마로 푼다:
            // 계정 표 이름만 쓰면 사전에 없는 이름이라 가드는 통과하고(Oracle 과 같은 현재 동작) 실행은 그 스키마에서 못 찾아 실패, 계정을 적으면 성공.
            var owner = Convert.ToString(await TiberoSessionQuery.ScalarAsync(setup, "SELECT USER FROM DUAL"), System.Globalization.CultureInfo.InvariantCulture)!;
            await ExecAsync(tab, "ALTER SESSION SET CURRENT_SCHEMA = SYS");
            var unqualified = await guard.CheckAsync(tab, $"SELECT id FROM {Table}", TabOptions, default);
            var qualified = await guard.CheckAsync(tab, $"SELECT id FROM {owner}.{Table}", TabOptions, default);
            var wrongPlace = await exec.ExecuteAsync(tab, unqualified.Statement ?? new SqlStatement($"SELECT id FROM {Table}", default, StatementKind.Query),
                new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true }, default);
            if (wrongPlace.ResultSet is { } wrs)
            {
                await wrs.DisposeAsync();
            }

            output.WriteLine($"[run-guard] altered unqualified={unqualified.Allowed}/{unqualified.Code} run={wrongPlace.Status} qualified={qualified.Allowed}/{qualified.Code}");
            Assert.NotEqual(ExternalSelectRefusal.SchemaUnknown, unqualified.Refusal); // Tibero 에서도 현재 스키마를 읽었다
            Assert.Equal(ExecutionStatus.Failed, wrongPlace.Status);
            Assert.True(qualified.Allowed, qualified.Code + " " + qualified.Message);
            await ExecAsync(tab, $"ALTER SESSION SET CURRENT_SCHEMA = {owner}");

            if (hasFunctions)
            {
                // 확인기가 Tibero 열(parent_obj_*·org_object_*)로 읽는다: 안전 함수는 통과하고 실행하면 값이 나온다.
                var safe = await guard.CheckAsync(tab, $"SELECT {Plain}(id) FROM {Table}", TabOptions, default);
                var viaPublic = await guard.CheckAsync(tab, $"SELECT {ViaPublic}(id) FROM {Table}", TabOptions, default);
                var autonomous = await guard.CheckAsync(tab, $"SELECT {Auto}(1) FROM DUAL", TabOptions, default);
                var http = await guard.CheckAsync(tab, $"SELECT {Http}(1) FROM DUAL", TabOptions, default);
                output.WriteLine($"[run-guard] safe={safe.Allowed}/{safe.Code} via-public={viaPublic.Allowed}/{viaPublic.Code} autonomous={autonomous.Refusal}/{autonomous.Code} http={http.Refusal}/{http.Code}");
                Assert.True(safe.Allowed, safe.Code + " " + safe.Message);
                Assert.True(viaPublic.Allowed, viaPublic.Code + " " + viaPublic.Message); // PUBLIC.DUAL → SYS._VT_DUAL, PUBLIC.DBMS_OUTPUT → SYS.DBMS_OUTPUT
                Assert.Equal(ExternalSelectRefusal.UserFunction, autonomous.Refusal);
                Assert.Equal(AgentErrorCodes.SqlUnsafe, autonomous.Code);
                Assert.Equal(ExternalSelectRefusal.UserFunction, http.Refusal); // PUBLIC.UTL_HTTP → SYS.UTL_HTTP(외부 패키지)
                Assert.Equal(AgentErrorCodes.SqlUnsafe, http.Code);
                if (hasSynonym)
                {
                    var viaSyn = await guard.CheckAsync(tab, $"SELECT {ViaSyn}(1) FROM DUAL", TabOptions, default);
                    output.WriteLine($"[run-guard] via-synonym={viaSyn.Refusal}/{viaSyn.Code}");
                    Assert.Equal(AgentErrorCodes.SqlUnsafe, viaSyn.Code);
                }

                var safeRun = await exec.ExecuteAsync(tab, safe.Statement!, new Dictionary<string, BindValue>(), new ExecutionOptions { Source = HistorySource.Agent, SkipHistory = true }, default);
                Assert.Equal(ExecutionStatus.Succeeded, safeRun.Status);
                await using (var rs = Assert.IsAssignableFrom<IResultSet>(safeRun.ResultSet))
                {
                    Assert.Equal(30, (await rs.GetPageAsync(0, default)).RowCount);
                }

                Assert.Equal(0, await CountAsync(setup, $"SELECT COUNT(*) FROM {Log}")); // 검사는 함수를 부르지 않는다
            }
        }
        finally
        {
            await DropAsync(setup);
        }
    }

    /// <summary>
    /// 내장 AI 경로: <c>benchmark_queries</c> 의 게이트(AgentGate)는 같은 확인기의 판정으로 실행할지(Safe) 커서 실측으로 대체할지 정한다.
    /// Tibero 에서 판정이 Oracle 과 같은 뜻으로 나오는지 본다(안전 = Safe, 자치 = Autonomous, UTL_HTTP = External).
    /// </summary>
    [TiberoWriteFact]
    public async Task Checker_verdicts_that_the_builtin_benchmark_gate_uses_match_oracle_meaning_on_tibero()
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

        var setup = await it.OpenAsync();
        await DropAsync(setup);
        try
        {
            await ExecAsync(setup, $"CREATE TABLE {Log} (N NUMBER)");
            var (hasFunctions, _) = await CreateFunctionsAsync(setup);
            if (!hasFunctions)
            {
                output.WriteLine("[gate-verdict] no function privilege");
                return;
            }

            var exec = new TiberoExecutionService(new ExecutionPolicy(), ExecutionFakes.NoQuestions(), it.Events, null, dbmsOutput: false);
            var checker = NewChecker(exec);
            var session = await it.OpenAsync();
            var owner = Convert.ToString(await TiberoSessionQuery.ScalarAsync(setup, "SELECT USER FROM DUAL"), System.Globalization.CultureInfo.InvariantCulture)!;
            var plain = await checker.CheckAsync(session, [Plain], owner, default);
            var viaPublic = await checker.CheckAsync(session, [ViaPublic], owner, default);
            var autonomous = await checker.CheckAsync(session, [Auto], owner, default);
            var http = await checker.CheckAsync(session, [Http], owner, default);
            output.WriteLine($"[gate-verdict] plain={plain} via-public={viaPublic} autonomous={autonomous} http={http}");
            Assert.Equal(UserFunctionVerdict.Safe, plain);
            Assert.Equal(UserFunctionVerdict.Safe, viaPublic);
            Assert.Equal(UserFunctionVerdict.Autonomous, autonomous);
            Assert.Equal(UserFunctionVerdict.External, http);
        }
        finally
        {
            await DropAsync(setup);
        }
    }

    /// <summary>안전·PUBLIC 동의어 경유·자치·UTL_HTTP 함수를 만든다. 함수 권한이 없으면 (false, false). 개인 동의어 경유 자치 함수는 동의어 권한이 있을 때만(둘째 값).</summary>
    private static async Task<(bool Functions, bool Synonym)> CreateFunctionsAsync(TiberoSession setup)
    {
        if (!await CreateOrNoPrivilegeAsync(setup, $"CREATE OR REPLACE FUNCTION {Plain}(x NUMBER) RETURN NUMBER IS BEGIN RETURN x * 2; END;"))
        {
            return (false, false);
        }

        await ExecAsync(setup, $"CREATE OR REPLACE FUNCTION {ViaPublic}(x NUMBER) RETURN NUMBER IS n NUMBER; BEGIN SELECT x INTO n FROM DUAL; DBMS_OUTPUT.PUT_LINE('x'); RETURN n; END;");
        await ExecAsync(setup, $"CREATE OR REPLACE FUNCTION {Auto}(x NUMBER) RETURN NUMBER IS PRAGMA AUTONOMOUS_TRANSACTION; BEGIN INSERT INTO {Log} VALUES (x); COMMIT; RETURN x; END;");
        await ExecAsync(setup, $"CREATE OR REPLACE FUNCTION {Http}(x NUMBER) RETURN VARCHAR2 IS BEGIN RETURN UTL_HTTP.REQUEST('http://localhost'); END;");
        var synonym = await CreateOrNoPrivilegeAsync(setup, $"CREATE OR REPLACE SYNONYM {Syn} FOR {Auto}");
        if (synonym)
        {
            await ExecAsync(setup, $"CREATE OR REPLACE FUNCTION {ViaSyn}(x NUMBER) RETURN NUMBER IS BEGIN RETURN {Syn}(x); END;");
        }

        return (true, synonym);
    }

    private static IUserFunctionSafetyChecker NewChecker(IExecutionService exec) =>
        (IUserFunctionSafetyChecker)Activator.CreateInstance(
            typeof(IExternalSelectGuard).Assembly.GetType("Oranda.Language.Ai.Handlers.UserFunctionSafetyChecker", throwOnError: true)!, exec, TiberoEmbeddedCatalog.Load())!;

    private static IExternalSelectGuard NewGuard(IMetadataProvider metadata, IExecutionService exec)
    {
        var assembly = typeof(IExternalSelectGuard).Assembly;
        var language = (ILanguageService)Activator.CreateInstance(assembly.GetType("Oranda.Language.LanguageService", throwOnError: true)!, nonPublic: true)!;
        var extractor = (IStatementReferenceExtractor)Activator.CreateInstance(
            assembly.GetType("Oranda.Language.Ai.Handlers.AgentStatementReferenceExtractor", throwOnError: true)!, language, metadata)!;
        var checker = NewChecker(exec);
        return (IExternalSelectGuard)Activator.CreateInstance(
            assembly.GetType("Oranda.Language.Ai.Handlers.External.ExternalSelectGuard", throwOnError: true)!, language, extractor, checker, exec)!;
    }

    private static async Task DropAsync(TiberoSession session)
    {
        foreach (var fn in new[] { ViaSyn, Http, ViaPublic, Auto, Plain })
        {
            if (await CountAsync(session, $"SELECT COUNT(*) FROM user_objects WHERE object_name = '{fn}' AND object_type = 'FUNCTION'") > 0)
            {
                await ExecAsync(session, $"DROP FUNCTION {fn}");
            }
        }

        if (await CountAsync(session, $"SELECT COUNT(*) FROM user_synonyms WHERE synonym_name = '{Syn}'") > 0)
        {
            await ExecAsync(session, $"DROP SYNONYM {Syn}");
        }

        foreach (var t in new[] { Log, Table })
        {
            if (await CountAsync(session, $"SELECT COUNT(*) FROM user_tables WHERE table_name = '{t}'") > 0)
            {
                await ExecAsync(session, $"DROP TABLE {t}");
            }
        }
    }

    private static async Task<bool> CreateOrNoPrivilegeAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("17004", StringComparison.Ordinal))
        {
            return false;
        }
    }

    private static async Task<long> CountAsync(TiberoSession session, string sql) =>
        TiberoSessionQuery.Long(await TiberoSessionQuery.ScalarAsync(session, sql)) ?? 0;

    private static async Task ExecAsync(TiberoSession session, string sql)
    {
        await session.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var command = session.CreateCommand(sql);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            session.CommandGate.Release();
        }
    }
}
