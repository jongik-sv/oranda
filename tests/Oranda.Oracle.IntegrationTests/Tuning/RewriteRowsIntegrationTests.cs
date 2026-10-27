using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Plans;
using Oranda.Oracle.Connection;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Language.Tuning;

namespace Oranda.Oracle.IntegrationTests.Tuning;

// 소유: TSK-06-09. 재작성 후보가 만든 SQL 이 실제 서버에서 원문과 같은 행 수를 돌려주는가(26ai·11.2 둘 다).
// 시험 표는 시험 안에서 만들고 지운다. 계획 조건은 EXPLAIN PLAN 의 plan_table 에서 읽어(TO_NUMBER 가 실제로 보이는지까지) 규칙에 넘긴다.

[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class RewriteRowsIntegrationTests(OracleFreeFixture db) : RewriteRowsCases(db.DataSource, db.SystemPassword)
{
}

[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class RewriteRows11gIntegrationTests(Oracle11gFixture db) : RewriteRowsCases(db.DataSource, db.SystemPassword)
{
}

public abstract class RewriteRowsCases(string dataSource, string systemPassword) : IAsyncLifetime
{
    private readonly string _table = "RWR_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<OracleConnection> OpenAsync()
    {
        var conn = new OracleConnection(new ConnectionProfile("rewrite-rows", "system", dataSource).BuildConnectionString(systemPassword));
        await conn.OpenAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        // code: 숫자만 든 문자 열('0'~'49'), d: 2026-10-01 부터 6 시간 간격(시각이 있다), name: 'A0'~'C9' 같은 앞부분 비교용.
        await using var conn = await OpenAsync();
        await ExecAsync($"CREATE TABLE {_table} (id NUMBER PRIMARY KEY, code VARCHAR2(10), d DATE, name VARCHAR2(20))", conn);
        await ExecAsync($"INSERT INTO {_table} SELECT level, TO_CHAR(MOD(level, 50)), DATE '2026-10-01' + (level - 1) / 4, CHR(65 + MOD(level, 3)) || TO_CHAR(MOD(level, 10)) || 'x' FROM dual CONNECT BY level <= 400", conn);
        await ExecAsync("COMMIT", conn);
    }

    public async Task DisposeAsync()
    {
        await ExecAsync($"DROP TABLE {_table} PURGE");
    }

    private async Task ExecAsync(string sql, OracleConnection? on = null)
    {
        var conn = on ?? await OpenAsync();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            if (on is null)
            {
                await conn.DisposeAsync();
            }
        }
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>EXPLAIN PLAN 의 단계 조건만 담은 계획(규칙은 조건만 본다).</summary>
    private async Task<ExecutionPlan> ExplainAsync(string sql)
    {
        var tag = "RW" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        await using var conn = await OpenAsync(); // plan_table 은 세션 임시 표일 수 있어 한 세션에서 쓰고 읽는다
        await ExecAsync($"EXPLAIN PLAN SET STATEMENT_ID = '{tag}' FOR {sql}", conn);
        var steps = new List<PlanStep>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"SELECT id, operation, access_predicates, filter_predicates FROM plan_table WHERE statement_id = '{tag}' ORDER BY id";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                var id = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                steps.Add(new PlanStep(id, id == 0 ? null : 0, id == 0 ? 0 : 1, r.GetString(1))
                {
                    AccessPredicates = r.IsDBNull(2) ? null : r.GetString(2),
                    FilterPredicates = r.IsDBNull(3) ? null : r.GetString(3),
                });
            }
        }

        await ExecAsync($"DELETE FROM plan_table WHERE statement_id = '{tag}'", conn);
        await ExecAsync("COMMIT", conn);
        return new ExecutionPlan(steps, string.Empty);
    }

    [Fact]
    public async Task Number_literal_against_a_text_column_is_quoted_and_returns_the_same_rows()
    {
        var sql = $"SELECT COUNT(*) FROM {_table} x WHERE x.code = 7";
        var plan = await ExplainAsync(sql);
        Assert.Contains(plan.Steps, s => (s.FilterPredicates ?? s.AccessPredicates ?? string.Empty).Contains("TO_NUMBER(\"X\".\"CODE\")", StringComparison.Ordinal));

        var a = Assert.Single(RewriteAdvisor.Analyze(sql, plan), r => r.Rule == RewriteRule.ImplicitConversion);

        Assert.True(a.HasRewrite);
        Assert.Contains("x.code = '7'", a.Candidate.Sql, StringComparison.Ordinal);
        Assert.Equal(await CountAsync(sql), await CountAsync(a.Candidate.Sql));
        Assert.Equal(8, await CountAsync(a.Candidate.Sql));
    }

    [Theory]
    [InlineData("TRUNC(x.d) = DATE '2026-10-05'", 4)]
    [InlineData("TO_CHAR(x.d, 'YYYYMMDD') = '20261003'", 4)]
    [InlineData("TO_CHAR(x.d, 'YYYY-MM') = '2026-10'", 124)]
    [InlineData("TO_CHAR(x.d, 'YYYY') = '2026'", 368)]
    [InlineData("SUBSTR(x.name, 1, 2) = 'B4'", 14)]
    [InlineData("SUBSTR(x.name, 1, 3) = 'C2x'", -1)]
    public async Task Function_on_a_column_rewrites_return_the_same_rows(string condition, long expected)
    {
        var sql = $"SELECT COUNT(*) FROM {_table} x WHERE {condition}";

        var a = Assert.Single(RewriteAdvisor.Analyze(sql), r => r.Rule == RewriteRule.FunctionOnColumn);

        Assert.True(a.HasRewrite, a.Candidate.Description);
        var original = await CountAsync(sql);
        Assert.Equal(original, await CountAsync(a.Candidate.Sql));
        if (expected >= 0)
        {
            Assert.Equal(expected, original);
        }
        else
        {
            Assert.True(original > 0);
        }
    }
}
