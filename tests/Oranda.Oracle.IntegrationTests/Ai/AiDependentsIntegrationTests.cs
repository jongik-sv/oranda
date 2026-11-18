using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Metadata;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language;
using Oranda.Language.Ai.Tools;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Metadata;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: TSK-15-09. get_dependents 의 카탈로그(tuning.object_dependents)를 실제 26ai·11g 로 읽는다.
// 시험 객체(ORT_AE_ 앞머리 + 실행마다 고유 접미사): 부모 표 · FK 자식 표 · 뷰 · 트리거 · 동의어. 끝에 모두 지운다.

/// <summary>
/// 환경 변수: 26ai = ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER/ADMIN_PASSWORD, 11g = ORANDA_IT_11G_DATASOURCE/ORANDA_IT_11G_SYSTEM_PASSWORD.
/// 없으면 건너뛴다(ORANDA_IT_REQUIRE=1 이면 실패). 접속 정보는 출력하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AiDependentsIntegrationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Oracle_26ai_lists_dependents_foreign_keys_triggers_and_synonyms()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } password)
        {
            Skip("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            return;
        }

        await RunScenarioAsync($"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}", user, password);
    }

    [Fact]
    public async Task Oracle_11g_lists_dependents_foreign_keys_triggers_and_synonyms()
    {
        var dataSource = Environment.GetEnvironmentVariable("ORANDA_IT_11G_DATASOURCE");
        var password = Environment.GetEnvironmentVariable("ORANDA_IT_11G_SYSTEM_PASSWORD");
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(password))
        {
            Skip("ORANDA_IT_11G_DATASOURCE/ORANDA_IT_11G_SYSTEM_PASSWORD 가 없다");
            return;
        }

        if (Environment.GetEnvironmentVariable("ORANDA_IT_11G_DISABLE_OOB") == "1")
        {
            OracleConfiguration.DisableOOB = true; // podman 중계가 OOB 를 버린다(Oracle11gFixture 주석)
        }

        await RunScenarioAsync(dataSource, "system", password);
    }

    private async Task RunScenarioAsync(string dataSource, string user, string password)
    {
        var sfx = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var (parent, child, view, trigger, synonym) = ($"ORT_AE_P_{sfx}", $"ORT_AE_C_{sfx}", $"ORT_AE_V_{sfx}", $"ORT_AE_T_{sfx}", $"ORT_AE_S_{sfx}");
        var owner = user.ToUpperInvariant();
        var profile = new ConnectionProfile("t-ae-dependents", user, dataSource);
        await using var raw = new OracleConnection(profile.BuildConnectionString(password));
        await raw.OpenAsync();
        var manager = new OracleSessionManager(new ExecBus());
        ISession? session = null;
        try
        {
            await ExecAsync(raw, $"CREATE TABLE {parent} (id NUMBER PRIMARY KEY, nm VARCHAR2(20))");
            await ExecAsync(raw, $"CREATE TABLE {child} (id NUMBER PRIMARY KEY, parent_id NUMBER CONSTRAINT {child}_FK REFERENCES {parent} (id))");
            await ExecAsync(raw, $"CREATE VIEW {view} AS SELECT id, nm FROM {parent}");
            await ExecAsync(raw, $"CREATE TRIGGER {trigger} BEFORE INSERT OR UPDATE ON {parent} FOR EACH ROW BEGIN :NEW.nm := UPPER(:NEW.nm); END;");
            await ExecAsync(raw, $"CREATE SYNONYM {synonym} FOR {parent}");

            using (var credential = new SecureCredential(password))
            {
                session = await manager.OpenAsync(profile, credential, SessionPurpose.Monitor, default);
            }

            var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);
            var language = new LanguageService();
            var plans = new OracleExecutionPlanService(exec);
            var tuning = new OracleTuningService(exec, plans, language);

            var rows = await tuning.GetObjectDependentsAsync(session, owner, parent, default);
            output.WriteLine($"[dependents] {rows.Count} 줄: " + string.Join(", ", rows.Select(r => $"{r.Kind}:{r.Name}")));

            Assert.Contains(rows, r => r is { Kind: ObjectDependentKind.Dependent, Detail: "VIEW", Status: "VALID" } && r.Name == view && r.Owner == owner);
            Assert.Contains(rows, r => r is { Kind: ObjectDependentKind.Dependent, Detail: "TRIGGER" } && r.Name == trigger);
            Assert.Contains(rows, r => r is { Kind: ObjectDependentKind.ForeignKey, Status: "ENABLED" } && r.Name == child && r.Detail == child + "_FK");
            Assert.Contains(rows, r => r is { Kind: ObjectDependentKind.Trigger, Status: "ENABLED" } && r.Name == trigger && r.Detail!.Contains("INSERT", StringComparison.Ordinal));
            Assert.Contains(rows, r => r.Kind == ObjectDependentKind.Synonym && r.Name == synonym && r.Owner == owner);
            Assert.DoesNotContain(rows, r => r.Kind == ObjectDependentKind.Dependent && r.Name == parent); // 자기 자신은 뺀다

            // 자식 표에는 참조하는 FK 가 없다(자기 FK 는 부모 쪽에서만 보인다).
            var leaf = await tuning.GetObjectDependentsAsync(session, owner, child, default);
            Assert.DoesNotContain(leaf, r => r.Kind == ObjectDependentKind.ForeignKey);

            // 도구 응답 모양(수·목록).
            var box = new TuningToolbox(tuning, new OracleMetadataProvider(new MemoryMetadataCache(), new ExecBus(), Timeout.InfiniteTimeSpan),
                new OracleTableKeyDetailProvider(), plans, language);
            var described = await box.GetDependentsAsync(session, new DbObjectRef(owner, parent, "TABLE"), default);
            Assert.Equal(owner + "." + parent, described.Target);
            Assert.True(described.InvalidatedCount >= 2);
            Assert.Equal(1, described.ForeignKeyCount);
            Assert.Equal(1, described.TriggerCount);
            Assert.True(described.SynonymCount >= 1);
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }

            foreach (var sql in new[] { $"DROP SYNONYM {synonym}", $"DROP VIEW {view}", $"DROP TABLE {child} PURGE", $"DROP TABLE {parent} PURGE" })
            {
                try
                {
                    await ExecAsync(raw, sql);
                }
                catch (OracleException)
                {
                    // 만들다 만 객체는 없을 수 있다(트리거는 표와 함께 지워진다).
                }
            }
        }
    }

    private void Skip(string reason)
    {
        if (Env("REQUIRE") == "1")
        {
            throw new InvalidOperationException(reason);
        }

        output.WriteLine("건너뜀: " + reason);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
