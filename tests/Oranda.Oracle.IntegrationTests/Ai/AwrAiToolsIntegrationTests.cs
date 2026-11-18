using System.Text.Json.Nodes;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Ai.Access;
using Oranda.Core.Ai.Contracts;
using Oranda.Core.Ai.Gate;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Storage;
using Oranda.Language;
using Oranda.Language.Ai.Handlers;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Metadata;
using Oranda.Oracle.Metadata;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Ai;

// 소유: ai-expand ae-awr(TSK-15-05). AWR 계획 이력 도구 두 개를 실제 26ai·11g 로 부른다(게이트를 거치지 않는 부분).

/// <summary>
/// <c>get_awr_plan_history</c>·<c>get_awr_plan</c> 를 실제 서버로.
/// 26ai(FREEPDB1): 표지를 단 문장을 여러 번 돌리고 AWR 스냅숏(flush_level ALL)을 하나 만든 뒤 계획 이력·저장 계획을 읽는다. 만든 스냅숏은 끝에 지운다.
/// 11g(XE): 서버 control_management_pack_access 가 NONE 이면 선언이 있어도 보내지 않고 「지원 안 함」 결과다.
/// 환경 변수: ORANDA_IT_HOST/PORT/SERVICE/ADMIN_USER/ADMIN_PASSWORD, ORANDA_IT_11G_DATASOURCE·ORANDA_IT_11G_SYSTEM_PASSWORD. 없으면 건너뛴다(ORANDA_IT_REQUIRE=1 이면 26ai 는 실패). 값은 출력하지 않는다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AwrAiToolsIntegrationTests : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private readonly List<OracleSessionManager> _managers = [];
    private readonly List<(string DataSource, string User, string Password, long SnapId)> _snapshots = [];

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            foreach (var s in manager.Sessions)
            {
                await s.DisposeAsync();
            }
        }

        foreach (var (dataSource, user, password, snapId) in _snapshots)
        {
            try
            {
                await using var raw = Connect(dataSource, user, password);
                await raw.OpenAsync();
                await using var cmd = raw.CreateCommand();
                cmd.BindByName = true;
                cmd.CommandText = "BEGIN DBMS_WORKLOAD_REPOSITORY.DROP_SNAPSHOT_RANGE(low_snap_id => :id, high_snap_id => :id); END;";
                cmd.Parameters.Add(new OracleParameter("id", OracleDbType.Int64) { Value = snapId });
                await cmd.ExecuteNonQueryAsync();
            }
            catch (OracleException)
            {
                // 지우지 못한 스냅숏은 AWR 보존 기간이 지나면 사라진다.
            }
        }
    }

    [Fact]
    public async Task Oracle_26ai_history_lists_the_plan_of_a_snapshotted_statement_and_its_stored_lines()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } password)
        {
            Assert.False(Env("REQUIRE") == "1", "ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            return;
        }

        var dataSource = $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";
        var tag = "ORT_AE_AWR_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        const int runs = 30;
        string sqlId;
        await using (var raw = Connect(dataSource, user, password))
        {
            await raw.OpenAsync();
            var text = $"SELECT /* {tag} */ COUNT(*) FROM all_objects WHERE ROWNUM < 50";
            for (var i = 0; i < runs; i++)
            {
                await using var run = raw.CreateCommand();
                run.CommandText = text;
                await run.ExecuteScalarAsync();
            }

            sqlId = await SqlIdAsync(raw, tag);
            var snap = await CreateSnapshotAsync(raw);
            _snapshots.Add((dataSource, user, password, snap));
        }

        var declared = await OpenAsync(dataSource, user, password, LicensePack.Diagnostics);
        var serverAllows = ExecutionPolicy.ParseServerPackAccess(declared.Server.ManagementPackAccess) is not { } server || server >= LicensePack.Diagnostics;
        Assert.True(serverAllows, "시험 서버(26ai Free)는 DIAGNOSTIC+TUNING 이어야 한다.");
        var boxes = Boxes();

        var history = Ok(await new GetAwrPlanHistoryHandler(boxes).ExecuteAsync(Invoke(declared, sqlId, new JsonObject { ["sql_id"] = sqlId }), default));

        Assert.True(history["supported"]!.GetValue<bool>());
        var plan = Assert.Single(history["plans"]!.AsArray())!;
        Assert.True(plan["executions"]!.GetValue<long>() >= runs);
        Assert.NotNull(plan["elapsed_ms_per_exec"]);
        Assert.NotNull(plan["buffer_gets_per_exec"]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}$", plan["last_snap_time"]!.GetValue<string>());
        var hash = plan["plan_hash"]!.GetValue<long>();
        Assert.Contains(history["snapshots"]!.AsArray(), x => x!["plan_hash"]!.GetValue<long>() == hash && x["snap_id"]!.GetValue<long>() == _snapshots[^1].SnapId);

        var stored = Ok(await new GetAwrPlanHandler(boxes).ExecuteAsync(Invoke(declared, sqlId, new JsonObject { ["sql_id"] = sqlId, ["plan_hash_value"] = hash }), default));

        Assert.True(stored["found"]!.GetValue<bool>());
        Assert.Equal(hash, stored["plan_hash"]!.GetValue<long>());
        Assert.Contains(stored["steps"]!.AsArray(), x => x!["operation"]!.GetValue<string>().StartsWith("SORT AGGREGATE", StringComparison.Ordinal));
        Assert.False(Ok(await new GetAwrPlanHandler(boxes).ExecuteAsync(Invoke(declared, sqlId, new JsonObject { ["sql_id"] = sqlId, ["plan_hash_value"] = hash + 1 }), default))["found"]!.GetValue<bool>());

        // 팩 선언이 없으면 보내지 않고 「지원 안 함」 결과(오류가 아니다).
        var none = await OpenAsync(dataSource, user, password, LicensePack.None);
        var refused = Ok(await new GetAwrPlanHistoryHandler(boxes).ExecuteAsync(Invoke(none, sqlId, new JsonObject { ["sql_id"] = sqlId }), default));
        Assert.False(refused["supported"]!.GetValue<bool>());
        Assert.Contains("Diagnostics Pack", refused["reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oracle_11g_with_pack_access_none_answers_not_supported_without_sending()
    {
        var dataSource = Env("11G_DATASOURCE");
        var password = Env("11G_SYSTEM_PASSWORD");
        if (string.IsNullOrEmpty(dataSource) || string.IsNullOrEmpty(password))
        {
            return; // 11g 서버가 없으면 건너뛴다(Oracle11gTests 와 같은 변수)
        }

        if (Env("11G_DISABLE_OOB") == "1")
        {
            OracleConfiguration.DisableOOB = true; // podman 중계가 OOB 를 버린다(Oracle11gFixture 주석)
        }

        var declared = await OpenAsync(dataSource, "system", password, LicensePack.Diagnostics);
        var boxes = Boxes();
        const string sqlId = "0000000000000";

        var history = Ok(await new GetAwrPlanHistoryHandler(boxes).ExecuteAsync(Invoke(declared, sqlId, new JsonObject { ["sql_id"] = sqlId }), default));
        var plan = Ok(await new GetAwrPlanHandler(boxes).ExecuteAsync(Invoke(declared, sqlId, new JsonObject { ["sql_id"] = sqlId, ["plan_hash_value"] = 1 }), default));

        var serverAllows = ExecutionPolicy.ParseServerPackAccess(declared.Server.ManagementPackAccess) is not { } server || server >= LicensePack.Diagnostics;
        if (serverAllows)
        {
            // 팩을 켠 11g 서버: 보낸다(없는 sql_id 라 빈 이력·빈 계획).
            Assert.True(history["supported"]!.GetValue<bool>());
            Assert.Empty(history["plans"]!.AsArray());
            Assert.False(plan["found"]!.GetValue<bool>());
        }
        else
        {
            Assert.False(history["supported"]!.GetValue<bool>());
            Assert.False(plan["supported"]!.GetValue<bool>());
            Assert.Contains("control_management_pack_access", history["reason"]!.GetValue<string>(), StringComparison.Ordinal);
        }
    }

    private static JsonNode Ok(AgentToolOutput o)
    {
        Assert.False(o.IsError, o.ErrorMessage);
        return o.Result!;
    }

    private AgentToolboxProvider Boxes()
    {
        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        var language = new LanguageService();
        var plans = new OracleExecutionPlanService(exec);
        var metadata = new OracleMetadataProvider(new MemoryMetadataCache(), _bus, Timeout.InfiniteTimeSpan);
        return new AgentToolboxProvider(new OracleTuningService(exec, plans, language), metadata, new OracleTableKeyDetailProvider(), plans, language);
    }

    private static AgentToolInvocation Invoke(ISession db, string sqlId, JsonObject args)
    {
        var run = new AgentRunScope("it-awr", db.Profile, new AiContext(db.Profile.Name, db.Profile.Environment, AiAccessLevel.Plan, SqlId: sqlId));
        return new AgentToolInvocation(new AgentSession(run), db, AiAccessLevel.Plan, args, new Dictionary<string, AgentObjectRef>());
    }

    private async Task<ISession> OpenAsync(string dataSource, string user, string password, LicensePack pack)
    {
        var manager = new OracleSessionManager(_bus);
        _managers.Add(manager);
        using var credential = new SecureCredential(password);
        return await manager.OpenAsync(new ConnectionProfile("it-awr", user, dataSource, LicensePack: pack), credential, SessionPurpose.Monitor, default);
    }

    private static OracleConnection Connect(string dataSource, string user, string password) =>
        new(new ConnectionProfile("it-awr-raw", user, dataSource).BuildConnectionString(password));

    private static async Task<string> SqlIdAsync(OracleConnection raw, string tag)
    {
        await using var find = raw.CreateCommand();
        find.BindByName = true;
        find.CommandText = "SELECT sql_id FROM v$sql WHERE sql_text LIKE :t AND sql_text NOT LIKE '%v$sql%' AND ROWNUM = 1";
        find.Parameters.Add(new OracleParameter("t", OracleDbType.Varchar2) { Value = "%" + tag + "%" });
        return (string)(await find.ExecuteScalarAsync() ?? throw new InvalidOperationException("v$sql 에서 시험 문장을 찾지 못했다."));
    }

    private static async Task<long> CreateSnapshotAsync(OracleConnection raw)
    {
        await using var cmd = raw.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = "BEGIN :id := DBMS_WORKLOAD_REPOSITORY.CREATE_SNAPSHOT('ALL'); END;";
        var id = new OracleParameter("id", OracleDbType.Int64) { Direction = System.Data.ParameterDirection.Output };
        cmd.Parameters.Add(id);
        await cmd.ExecuteNonQueryAsync();
        return Convert.ToInt64(id.Value?.ToString(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
