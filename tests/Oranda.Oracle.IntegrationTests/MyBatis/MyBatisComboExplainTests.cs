using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Language;
using Oranda.Core.MyBatis;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Language.MyBatis;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Plans;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.MyBatis;

// 소유: TSK-03-04. 조합 검사의 실행 계획 경로(MB-07)가 문장을 실행하지 않는다.
// 조합 검사 실행기(App)는 펼친 SQL 을 L1 분리로 SqlStatement 로 만들어 IExecutionPlanService.ExplainAsync 에 넘긴다. 이 시험은 같은 길을 App 없이 밟는다.

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class MyBatisComboExplainTests : IAsyncLifetime
{
    private const string UpdateTemplate =
        "<update id=\"updateEmpSal\" parameterType=\"map\">\n" +
        "  UPDATE {0}\n" +
        "  <set>\n" +
        "  <if test=\"sal != null\">SAL = #{{sal}},</if>\n" +
        "  <if test=\"comm != null\">COMM = #{{comm}}</if>\n" +
        "  </set>\n" +
        "   WHERE EMPNO = #{{empNo}}\n" +
        "</update>";

    private const string DeleteTemplate =
        "<delete id=\"deleteEmp\">\n" +
        "  DELETE FROM {0}\n" +
        "  <where>\n" +
        "    <if test=\"empNo != null\">AND EMPNO = #{{empNo}}</if>\n" +
        "    <if test=\"deptNo != null\">AND DEPTNO = #{{deptNo}}</if>\n" +
        "  </where>\n" +
        "</delete>";

    private readonly OracleFreeFixture _db;
    private readonly ExecBus _bus = new();
    private readonly ExecInteraction _ui = new();
    private OracleSessionManager _manager = null!;

    public MyBatisComboExplainTests(OracleFreeFixture db) => _db = db;

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
    public async Task Explaining_every_update_and_delete_combination_changes_no_rows()
    {
        var table = "COMBO_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var raw = await RawAsync();
        await ExecAsync(raw, $"CREATE TABLE {table} (EMPNO NUMBER PRIMARY KEY, DEPTNO NUMBER, SAL NUMBER, COMM NUMBER)");
        await ExecAsync(raw, $"INSERT INTO {table} SELECT level, mod(level, 3), level * 100, level FROM dual CONNECT BY level <= 20");
        await ExecAsync(raw, "COMMIT");
        try
        {
            var before = await SnapshotAsync(raw, table);
            await using var session = await OpenAsync();
            var plans = new OracleExecutionPlanService(new OracleExecutionService(new ExecutionPolicy(), _ui, _bus, null, pageSize: 500));
            var evaluator = new MyBatisEvaluator();
            var language = new LanguageService();
            int explained = 0;

            foreach (var (template, kind) in new[] { (UpdateTemplate, MyBatisElementKind.Update), (DeleteTemplate, MyBatisElementKind.Delete) })
            {
                var statement = new MyBatisElement
                {
                    Kind = kind,
                    Name = kind.ToString().ToLowerInvariant(),
                    Id = "s",
                    Text = string.Format(CultureInfo.InvariantCulture, template, table),
                };
                var none = new Dictionary<string, object?>();
                var allOn = evaluator.Evaluate(statement, NoFragments.Instance, none, null, new MyBatisEvalOptions(AllOn: true));
                foreach (var combination in evaluator.Combinations(allOn))
                {
                    var eval = evaluator.Evaluate(statement, NoFragments.Instance, none, combination.Overrides);
                    if ((await language.AnalyzeAsync(eval.Sql, default)).Any(d => d.Severity == DiagnosticSeverity.Error))
                    {
                        continue; // 구문 오류 조합은 실행 계획까지 가지 않는다(빈 <set>).
                    }

                    var parsed = language.Split(eval.Sql, SplitOptions.Default).Single();
                    Assert.Equal(StatementKind.Dml, parsed.Kind);
                    var plan = await plans.ExplainAsync(session, parsed, default);
                    Assert.Contains(plan.Steps, s => s.Operation.StartsWith(kind == MyBatisElementKind.Update ? "UPDATE" : "DELETE", StringComparison.Ordinal));
                    explained++;
                }
            }

            // update 는 빈 <set> 1개를 빼고 3개, delete 는 4개(<where> 가 비어도 문장이 된다).
            Assert.Equal(7, explained);
            Assert.Equal(before, await SnapshotAsync(raw, table));

            await using var left = raw.CreateCommand();
            left.CommandText = "SELECT COUNT(*) FROM plan_table WHERE statement_id LIKE 'ORANDA_%'";
            Assert.Equal(0m, Convert.ToDecimal(await left.ExecuteScalarAsync(), CultureInfo.InvariantCulture));
        }
        finally
        {
            await ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }

    private static async Task<string> SnapshotAsync(OracleConnection c, string table)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) || ':' || SUM(SAL) || ':' || SUM(NVL(COMM, 0)) || ':' || SUM(DEPTNO) FROM {table}";
        return Convert.ToString(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture)!;
    }

    private async Task<OracleSession> OpenAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        using var cred = new SecureCredential(_db.SystemPassword);
        return (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
    }

    private async Task<OracleConnection> RawAsync()
    {
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        var conn = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(OracleConnection c, string sql)
    {
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public static NoFragments Instance { get; } = new();

        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }
}
