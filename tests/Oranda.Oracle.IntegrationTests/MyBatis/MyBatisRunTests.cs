using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Language.MyBatis;
using Oranda.Oracle.Connection;
using Oranda.Oracle.Execution;
using Oranda.Oracle.Grid;
using Oranda.Oracle.IntegrationTests.Execution;
using Oranda.Oracle.IntegrationTests.Integration;
using Oranda.Oracle.Sessions;

namespace Oranda.Oracle.IntegrationTests.MyBatis;

// 소유: TSK-03-02. 계산기로 펼친 SQL·바인드를 실행한 결과가, 같은 SQL 을 손으로 쓴 :바인드로 실행한 결과와 같은지 본다.
// 이 프로젝트는 Oranda.Oracle·Oranda.Language 만 참조하므로 App 의 실행 흐름(MyBatisRunner)은 쓰지 않고, 같은 입력(펼친 SQL + 바인드)을 실행 엔진에 직접 넣는다.
// 바인드 변환은 App 의 MyBatisRunBinds 와 같은 단순 대응(jdbcType 의 NUMERIC → Number, 글자 → Varchar2)이다.

/// <summary>두 서버 시험이 함께 쓰는 표본 매퍼 문장과 비교 로직.</summary>
internal static class MyBatisRunChecks
{
    /// <summary>if 2개(minSal·ename) + foreach IN 목록(deptIds) + <c>${orderBy}</c> 치환 하나.</summary>
    public static string Xml(string table) => $$"""
        <select id="selectEmp" parameterType="map">
          SELECT EMPNO, ENAME, SAL, DEPTNO FROM {{table}}
          <where>
            <if test="minSal != null">
              AND SAL >= #{minSal,jdbcType=NUMERIC}
            </if>
            <if test="ename != null and ename != ''">
              AND ENAME LIKE '%' || #{ename} || '%'
            </if>
            AND DEPTNO IN
            <foreach collection="deptIds" item="id" open="(" separator="," close=")">
              #{id}
            </foreach>
          </where>
          ORDER BY ${orderBy}
        </select>
        """;

    /// <summary>표본 문장을 시험 값으로 펼친다(DB 없이 계산기만).</summary>
    public static EvalResult Expand(string table)
    {
        var element = new MyBatisElement { Kind = MyBatisElementKind.Select, Name = "select", Id = "selectEmp", Text = Xml(table) };
        var values = new Dictionary<string, object?>
        {
            ["minSal"] = 1000m,
            ["ename"] = "A",
            ["deptIds"] = new List<object?> { 10m, 20m, 30m },
            ["orderBy"] = "EMPNO DESC",
        };
        return new MyBatisEvaluator().Evaluate(element, new NoFragments(), values, null);
    }

    public static async Task CreateTableAsync(OracleConnection conn, string table)
    {
        await ExecAsync(conn, $"CREATE TABLE {table} (EMPNO NUMBER, ENAME VARCHAR2(20), SAL NUMBER, DEPTNO NUMBER)");
        await ExecAsync(
            conn,
            $"INSERT INTO {table} SELECT 7369, 'SMITH', 800, 20 FROM dual UNION ALL SELECT 7499, 'ALLEN', 1600, 30 FROM dual UNION ALL SELECT 7521, 'WARD', 1250, 30 FROM dual " +
            "UNION ALL SELECT 7566, 'JONES', 2975, 20 FROM dual UNION ALL SELECT 7782, 'CLARK', 2450, 10 FROM dual UNION ALL SELECT 7788, 'SCOTT', 3000, 20 FROM dual " +
            "UNION ALL SELECT 7876, 'ADAMS', 1100, 20 FROM dual UNION ALL SELECT 7900, 'JAMES', 950, 30 FROM dual");
        await ExecAsync(conn, "COMMIT");
    }

    public static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>계산기로 펼친 문장과 손으로 쓴 같은 문장을 각각 실행해 열 이름과 행이 같은지 단정한다.</summary>
    public static async Task ExpandedEqualsHandWrittenAsync(OracleExecutionService exec, ISession session, string table)
    {
        var expanded = Expand(table);
        Assert.Equal("EMPNO DESC", Assert.Single(expanded.Substitutions).Value);
        Assert.Empty(expanded.Issues);

        var binds = new Dictionary<string, BindValue>(StringComparer.OrdinalIgnoreCase);
        foreach (var bind in expanded.Binds)
        {
            binds[bind.Name] = bind.Value switch
            {
                null => new BindValue(BindDataType.Varchar2, null),
                string s => new BindValue(BindDataType.Varchar2, s),
                decimal d => new BindValue(BindDataType.Number, d),
                _ => throw new InvalidOperationException("시험이 모르는 바인드 값 형식: " + bind.Value.GetType().Name),
            };
        }

        var viaMyBatis = await RunAsync(exec, session, expanded.Sql, [.. binds.Keys], binds);

        var handBinds = new Dictionary<string, BindValue>
        {
            ["minSal"] = new(BindDataType.Number, 1000m),
            ["ename"] = new(BindDataType.Varchar2, "A"),
            ["d1"] = new(BindDataType.Number, 10m),
            ["d2"] = new(BindDataType.Number, 20m),
            ["d3"] = new(BindDataType.Number, 30m),
        };
        var hand = await RunAsync(
            exec,
            session,
            $"SELECT EMPNO, ENAME, SAL, DEPTNO FROM {table} WHERE SAL >= :minSal AND ENAME LIKE '%' || :ename || '%' AND DEPTNO IN (:d1, :d2, :d3) ORDER BY EMPNO DESC",
            [.. handBinds.Keys],
            handBinds);

        Assert.Equal(hand.Columns, viaMyBatis.Columns);
        Assert.Equal(hand.Rows, viaMyBatis.Rows);
        Assert.Equal(["7876|ADAMS|1100|20", "7782|CLARK|2450|10", "7521|WARD|1250|30", "7499|ALLEN|1600|30"], viaMyBatis.Rows); // 조건이 실제로 걸렸다
    }

    private static async Task<(string[] Columns, List<string> Rows)> RunAsync(
        OracleExecutionService exec, ISession session, string sql, string[] bindNames, IReadOnlyDictionary<string, BindValue> binds)
    {
        var stmt = new SqlStatement(sql, default, StatementKind.Query) { Verb = "SELECT", BindNames = bindNames };
        var result = await exec.ExecuteAsync(session, stmt, binds, null, default);
        Assert.True(result.Status == ExecutionStatus.Succeeded, result.Error?.Message ?? result.Status.ToString());
        await using var rs = Assert.IsType<OracleResultSet>(result.ResultSet);
        var page = await rs.GetPageAsync(0, default);
        var rows = new List<string>();
        for (var r = 0; r < page.RowCount; r++)
        {
            rows.Add(string.Join('|', page.Columns.Select(c => Convert.ToString(c[r], System.Globalization.CultureInfo.InvariantCulture))));
        }

        return ([.. rs.Columns.Select(c => c.Name)], rows);
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }
}

/// <summary>DB 없이 도는 확인: 표본 문장이 의도한 SQL·바인드로 펼쳐진다(서버 시험의 전제).</summary>
public sealed class MyBatisRunExpansionTests
{
    [Fact]
    public void Sample_statement_expands_to_two_ifs_a_foreach_list_and_one_substitution()
    {
        var r = MyBatisRunChecks.Expand("T");

        Assert.Empty(r.Issues);
        Assert.Equal(
            "SELECT EMPNO, ENAME, SAL, DEPTNO FROM T WHERE SAL >= :minSal AND ENAME LIKE '%' || :ename || '%' AND DEPTNO IN ( :id_1 , :id_2 , :id_3 ) ORDER BY EMPNO DESC",
            System.Text.RegularExpressions.Regex.Replace(r.Sql, @"\s+", " "));
        Assert.Equal(["minSal", "ename", "id_1", "id_2", "id_3"], r.Binds.Select(b => b.Name));
        Assert.Equal("NUMERIC", r.Binds[0].JdbcType);
        Assert.Equal(("orderBy", "EMPNO DESC"), (r.Substitutions[0].Name, r.Substitutions[0].Value));
    }
}

/// <summary>26ai 서버(컨테이너)에서의 비교.</summary>
[Trait("Category", "Integration")]
[Collection(OracleFreeShared.Name)]
public sealed class MyBatisRunFreeTests : IAsyncLifetime
{
    private readonly OracleFreeFixture _db;
    private OracleSessionManager _manager = null!;

    public MyBatisRunFreeTests(OracleFreeFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(new ExecBus());
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
    public async Task Expanded_sql_gives_the_same_rows_as_the_hand_written_binds()
    {
        var table = "MBRUN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var profile = new ConnectionProfile("free-system", "system", _db.DataSource);
        await using var raw = new OracleConnection(profile.BuildConnectionString(_db.SystemPassword));
        await raw.OpenAsync();
        await MyBatisRunChecks.CreateTableAsync(raw, table);
        try
        {
            using var cred = new SecureCredential(_db.SystemPassword);
            await using var session = (OracleSession)await _manager.OpenAsync(profile, cred, SessionPurpose.Work, default);
            var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);

            await MyBatisRunChecks.ExpandedEqualsHandWrittenAsync(exec, session, table);
        }
        finally
        {
            await MyBatisRunChecks.ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }
}

/// <summary>11.2 서버에서의 비교(11g 에는 BOOLEAN 이 없으므로 바인드는 글자·숫자만 쓴다).</summary>
[Trait("Category", "Integration")]
[Collection(Oracle11gShared.Name)]
public sealed class MyBatisRun11gTests : IAsyncLifetime
{
    private readonly Oracle11gFixture _db;
    private OracleSessionManager _manager = null!;

    public MyBatisRun11gTests(Oracle11gFixture db) => _db = db;

    public Task InitializeAsync()
    {
        _manager = new OracleSessionManager(new ExecBus());
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
    public async Task Expanded_sql_gives_the_same_rows_as_the_hand_written_binds()
    {
        var table = "MBRUN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using var raw = await _db.OwnerAsync();
        await MyBatisRunChecks.CreateTableAsync(raw, table);
        try
        {
            using var cred = new SecureCredential(_db.OwnerPassword);
            await using var session = (OracleSession)await _manager.OpenAsync(new ConnectionProfile("ora11-owner", _db.Owner, _db.DataSource), cred, SessionPurpose.Work, default);
            var exec = new OracleExecutionService(new ExecutionPolicy(), new ExecInteraction(), new ExecBus(), null, pageSize: 500);

            await MyBatisRunChecks.ExpandedEqualsHandWrittenAsync(exec, session, table);
        }
        finally
        {
            await MyBatisRunChecks.ExecAsync(raw, $"DROP TABLE {table} PURGE");
        }
    }
}
