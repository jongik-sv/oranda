using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Language;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;
using Oranda.Oracle.Tuning;

namespace Oranda.Oracle.IntegrationTests.Tuning;

// 소유: TSK-14-17. 튜닝 추천 후보 조회(gv$sqlstats·리터럴 묶음·계획 줄).

/// <summary>DB 없이 도는 부분: sql_id 목록 다듬기, 행 매핑.</summary>
public class TuningRecommendLogicTests
{
    [Fact]
    public void Sql_id_list_drops_blanks_duplicates_and_non_identifiers()
    {
        Assert.Null(OracleTuningService.SqlIdList(null));
        Assert.Null(OracleTuningService.SqlIdList([" ", "a'b"]));
        Assert.Equal("abc,def", OracleTuningService.SqlIdList(["abc", " abc ", "def", "x,y"]));
        Assert.Equal(OracleTuningService.MaxSqlIds, OracleTuningService.SqlIdList([.. Enumerable.Range(0, 300).Select(i => "id" + i)])!.Split(',').Length);
    }

    [Fact]
    public void Rows_map_by_catalog_column_order()
    {
        var c = OracleTuningService.ToCandidate([1m, "abc", 42m, "SELECT 1", 3m, 4m, 5m, 6m, 7m, 8m, 9m, 10m, "17415914164128958688", 11m, 12m, 13m, 14m, null, 99m, "APP_OWNER"]);
        Assert.Equal(("abc", 1, 42L, 3L, 4L, 10L, "17415914164128958688", "APP_OWNER"), (c.SqlId, c.InstanceId, c.PlanHashValue, c.Executions, c.ElapsedMicros, c.VersionCount, c.ForceMatchingSignature, c.ParsingSchema));
        Assert.Equal(14, c.ClusterWaitMicros);

        var g = OracleTuningService.ToLiteralGroup(["123", 12m, 100m, 200m, 150m, 300m, 0m, 50m, 100m, 1m, "s1", "SELECT 2", null]);
        Assert.Equal(("123", 12, "s1", 100L, true), (g.ForceMatchingSignature, g.SqlCount, g.Sample.SqlId, g.Sample.Executions, g.Sample.IsLiteralGroup));

        var p = OracleTuningService.ToPlanFinding([3m, "TABLE ACCESS", "FULL", "APP_OWNER", "T", 1000m, null, null]);
        Assert.Equal((3, "TABLE ACCESS", "FULL", 1000L, (long?)null), (p.Id, p.Operation, p.Options, p.Cardinality, p.TableBlocks));
    }
}

/// <summary>
/// 상주 컨테이너(26ai oracle-26ai-free, 11g oranda-ora11g)에서 조회 세 개가 돈다는 것(내용은 환경 의존이라 형태만). DB 변경 없음(SELECT 만).
/// 환경 변수(SafetyCrossDbTests 와 같다): ORANDA_IT_HOST, ORANDA_IT_PORT(1521), ORANDA_IT_SERVICE, ORANDA_IT_ADMIN_USER, ORANDA_IT_ADMIN_PASSWORD.
/// 11g 는 ORANDA_IT_PORT=1522, ORANDA_IT_SERVICE=XE 로 한 번 더 돌린다. 없으면 아무것도 안 하고 끝난다(ORANDA_IT_REQUIRE=1 이면 실패).
/// </summary>
[Trait("Category", "Integration")]
public sealed class TuningRecommendTests : IAsyncLifetime
{
    private readonly ExecBus _bus = new();
    private OracleSessionManager _manager = null!;

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static string DataSource => $"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}";

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(_bus);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var s in _manager.Sessions)
        {
            await s.DisposeAsync();
        }
    }

    [Fact]
    public async Task Candidate_literal_and_plan_queries_run_on_the_server()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is not { } user || Env("ADMIN_PASSWORD") is not { } password)
        {
            Assert.False(Env("REQUIRE") == "1", "ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다.");
            return;
        }

        var profile = new ConnectionProfile("it-admin", user, DataSource);
        await using var conn = new OracleConnection(profile.BuildConnectionString(password));
        await conn.OpenAsync();
        var tag = "REC_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT /* {tag} */ COUNT(*) FROM all_users a, (SELECT level n FROM dual CONNECT BY level <= 3) b";
            await cmd.ExecuteScalarAsync();
        }

        string sqlId;
        await using (var find = conn.CreateCommand())
        {
            find.CommandText = "SELECT MAX(sql_id) FROM v$sql WHERE sql_text LIKE :t";
            find.Parameters.Add(new OracleParameter("t", "SELECT /* " + tag + " */%"));
            sqlId = (string)(await find.ExecuteScalarAsync())!;
        }

        using var cred = new SecureCredential(password);
        var session = await _manager.OpenAsync(profile, cred, SessionPurpose.Monitor, default);
        var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), _bus, null, pageSize: 500);
        var service = new OracleTuningService(exec, new OracleExecutionPlanService(exec), new LanguageService());

        // 전체 조회: 상위 N 이하·경과 큰 순, 비중 분모 > 0, 리터럴 묶음 형태.
        var all = await service.GetTuningCandidatesAsync(session, new TuningCandidateQuery { Top = 30 }, default);
        Assert.InRange(all.Rows.Count, 1, 30);
        Assert.True(all.TotalElapsedMicros > 0);
        Assert.True(all.Rows.Zip(all.Rows.Skip(1)).All(p => p.First.ElapsedMicros >= p.Second.ElapsedMicros));
        Assert.All(all.LiteralGroups, g => Assert.True(g.SqlCount >= TuningRecommendationRules.LiteralGroupMinCount));
        Assert.InRange(all.LiteralGroups.Count, 0, TuningRecommendationRules.LiteralGroupMaxCount);

        // sql_id 로 좁힌 조회(세션 모니터): 그 SQL 만, 비중 분모 0, 리터럴 묶음 없음.
        var mine = await service.GetTuningCandidatesAsync(session, new TuningCandidateQuery { SqlIds = [sqlId, "nonexistent01"], MinExecutions = 0 }, default);
        var row = Assert.Single(mine.Rows);
        Assert.Equal(sqlId, row.SqlId);
        Assert.Equal(user.ToUpperInvariant(), row.ParsingSchema);
        Assert.True(row.Executions >= 1);
        Assert.True(row.BufferGets > 0);
        Assert.Equal(0, mine.TotalElapsedMicros);
        Assert.Empty(mine.LiteralGroups);

        // 계획 줄: 카티션 조인이 잡힌다(그 밖의 줄은 환경 의존).
        var plan = await service.GetPlanFindingsAsync(session, row.InstanceId, row.SqlId, row.PlanHashValue, default);
        Assert.Contains(plan, l => string.Equals(l.Options, "CARTESIAN", StringComparison.Ordinal));
        Assert.Contains(TuningRecommendationRules.Evaluate(row, 0, plan).Reasons, r => r.Rule == TuningRule.CartesianJoin);

        // 규칙 적용이 실제 행에서 던지지 않는다.
        _ = TuningRecommendationRules.Rank(all, new TuningRecommendationOptions { ExcludeInternalSchemas = false });
    }
}
