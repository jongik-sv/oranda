using System.Globalization;
using Oracle.ManagedDataAccess.Client;
using Oranda.Core.Connection;
using Oranda.Oracle.Connection;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Core.Security;
using Oranda.Core.Sessions;
using Oranda.Oracle.IntegrationTests.Sessions;
using Oranda.Oracle.Schema;
using Oranda.Oracle.Sessions;
using Xunit.Abstractions;

namespace Oranda.Oracle.IntegrationTests.Schema;

// AI 파티션 추천의 Oracle 근거 공급자(설계 2026-11-18 5장)를 실제 서버에서 본다: 조회 조건(V$SQL_PLAN)·날짜형 판정·월별 건수·상위 값·표본,
// V$ 권한이 없을 때 오류를 그대로 던지는지. 26ai 와 11g XE 에서 같은 시나리오를 돌린다. 객체는 시험 사용자(ORT_PA_xxxx) 소유이고 끝에 사용자째 지운다.
// 환경 변수는 PartitionEditIntegrationTests(26ai)·Oracle11gFixture(11g) 와 같다. 없으면 건너뛰고, ORANDA_IT_REQUIRE=1 이면 실패한다.
[Trait("Category", "Integration")]
public sealed class PartitionEvidenceIntegrationTests : IAsyncLifetime
{
    private const string Table = "ORT_PA_ORD";

    private readonly ITestOutputHelper _output;
    private readonly string _owner = "ORT_PA_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _low;
    private readonly string _password = "Pa" + Guid.NewGuid().ToString("N")[..16] + "x1";
    private readonly List<ISession> _sessions = [];
    private readonly List<(string DataSource, string AdminUser, string AdminPassword)> _created = [];

    public PartitionEvidenceIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
        _low = _owner + "_L";
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var s in _sessions)
        {
            await s.DisposeAsync();
        }

        foreach (var (dataSource, user, password) in _created)
        {
            await using var admin = Connect(dataSource, user, password);
            await admin.OpenAsync();
            foreach (var dropped in new[] { _owner, _low })
            {
                try
                {
                    await ExecAsync(admin, $"DROP USER {dropped} CASCADE");
                }
                catch (OracleException ex) when (ex.Number == 1918)
                {
                    // 만들기 전에 실패해서 없는 사용자
                }
            }
        }
    }

    [Fact]
    public async Task Oracle_26ai_gathers_predicates_date_like_distributions_and_throws_without_v_privileges()
    {
        if (Env("HOST") is null || Env("SERVICE") is null || Env("ADMIN_USER") is null || Env("ADMIN_PASSWORD") is null)
        {
            Skip("ORANDA_IT_HOST/SERVICE/ADMIN_USER/ADMIN_PASSWORD 가 없다");
            return;
        }

        await RunScenarioAsync($"{Env("HOST")}:{Env("PORT") ?? "1521"}/{Env("SERVICE")}", Env("ADMIN_USER")!, Env("ADMIN_PASSWORD")!);
    }

    [Fact]
    public async Task Oracle_11g_gathers_predicates_date_like_distributions_and_throws_without_v_privileges()
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

    private async Task RunScenarioAsync(string dataSource, string adminUser, string adminPassword)
    {
        await using (var admin = Connect(dataSource, adminUser, adminPassword))
        {
            await admin.OpenAsync();
            _created.Add((dataSource, adminUser, adminPassword)); // 만들다 실패해도 Dispose 가 있는 만큼 지운다
            await ExecAsync(admin, $"CREATE USER {_owner} IDENTIFIED BY \"{_password}\" DEFAULT TABLESPACE users QUOTA UNLIMITED ON users");
            await ExecAsync(admin, $"CREATE USER {_low} IDENTIFIED BY \"{_password}\"");
            await ExecAsync(admin, $"GRANT CREATE SESSION, CREATE TABLE, SELECT_CATALOG_ROLE TO {_owner}");
            await ExecAsync(admin, $"GRANT CREATE SESSION TO {_low}");
        }

        await using (var owner = Connect(dataSource, _owner, _password))
        {
            await owner.OpenAsync();
            await ExecAsync(owner, $"CREATE TABLE {Table} (ID NUMBER, ORDER_DT DATE, CODE VARCHAR2(12), YMD VARCHAR2(8), YM NUMBER(6), ISO VARCHAR2(10), STATUS VARCHAR2(4))");
            await ExecAsync(owner, $"CREATE INDEX {Table}_DT ON {Table} (ORDER_DT)");
            // 1200 행: 이번 달부터 3달 전까지 300 행씩, 60달 전 100 행, 날짜 NULL 50 행. 상태 A 990 · B 240 · C 120.
            await ExecAsync(owner, $@"INSERT INTO {Table}
                SELECT n, d, 'ID' || n, TO_CHAR(d, 'YYYYMMDD'), TO_NUMBER(TO_CHAR(d, 'YYYYMM')), TO_CHAR(d, 'YYYY-MM-DD'),
                       CASE WHEN n <= 1200 THEN CASE MOD(n, 10) WHEN 0 THEN 'C' WHEN 1 THEN 'B' WHEN 2 THEN 'B' ELSE 'A' END ELSE 'A' END
                  FROM (SELECT LEVEL AS n,
                               CASE WHEN LEVEL <= 1200 THEN ADD_MONTHS(TRUNC(SYSDATE, 'MM'), -MOD(LEVEL, 4)) + MOD(LEVEL, 20)
                                    WHEN LEVEL <= 1300 THEN ADD_MONTHS(TRUNC(SYSDATE, 'MM'), -60)
                                    ELSE CAST(NULL AS DATE) END AS d
                          FROM DUAL CONNECT BY LEVEL <= 1350)");
            await ExecAsync(owner, "COMMIT");
            await ExecAsync(owner, $"CREATE TABLE {Table}_E (ID NUMBER, D DATE, S VARCHAR2(4))");
            await ExecAsync(owner, $"GRANT SELECT ON {Table} TO {_low}");

            // 공유 풀에 조건이 남게 같은 문장을 정해진 횟수만큼 돌린다(바인드 문장이라 sql_id 하나).
            await RunRepeatedAsync(owner, $"SELECT COUNT(*) FROM {Table} WHERE ORDER_DT >= :d", new OracleParameter("d", OracleDbType.Date) { Value = DateTime.Today.AddYears(-10) }, 5);
            await RunRepeatedAsync(owner, $"SELECT COUNT(*) FROM {Table} WHERE STATUS = :s", new OracleParameter("s", OracleDbType.Varchar2) { Value = "B" }, 2);
        }

        var provider = new OraclePartitionEvidenceProvider();
        var s = await OpenAsync(dataSource, _owner);

        await PredicatesAsync(provider, s);
        await DateLikeAsync(provider, s);
        await MonthlyAsync(provider, s);
        await TopValuesAsync(provider, s);
        await EmptyTableAsync(provider, s);

        // V$ 를 볼 수 없는 사용자: 오류를 그대로 던진다(모으기가 「권한 없음」으로 적는다). 표 읽기 몫(날짜형·월별)은 된다.
        var low = await OpenAsync(dataSource, _low);
        var ex = await Assert.ThrowsAsync<OracleException>(() => provider.GetPredicateUseAsync(low, _owner, Table, default));
        _output.WriteLine("V$ 권한 없음 → ORA-" + ex.Number.ToString("00000", CultureInfo.InvariantCulture));
        Assert.True(ex.Number is 942 or 1031, "ORA-" + ex.Number);
        Assert.NotNull(await provider.SampleMonthlyAsync(low, _owner, Table, "ORDER_DT", null, null, default));
    }

    private async Task PredicatesAsync(OraclePartitionEvidenceProvider provider, ISession s)
    {
        var use = await provider.GetPredicateUseAsync(s, _owner, Table, default);

        Assert.NotNull(use);
        _output.WriteLine(string.Join(", ", use.Select(u => $"{u.Column} x{u.Executions}/{u.Statements} {u.Share:P0}")));
        Assert.Equal(["ORDER_DT", "STATUS"], use.Select(u => u.Column));
        // 서버가 스스로 돌린 재귀 문장(동적 통계 표본 등)이 끼면 ORDER_DT 쪽이 한두 건 늘 수 있어, 내가 돌린 만큼 이상인지 본다. STATUS 는 내 문장뿐.
        Assert.True(use[0].Executions >= 5, "ORDER_DT 실행 수 " + use[0].Executions);
        Assert.True(use[0].Statements >= 1);
        Assert.Equal(2, use[1].Executions);
        Assert.Equal(1, use[1].Statements);
        Assert.Equal(1.0, use.Sum(u => u.Share), 6); // 두 열이 문장을 나눠 가짐(한 문장에 두 열이 없음)
    }

    private async Task DateLikeAsync(OraclePartitionEvidenceProvider provider, ISession s)
    {
        var ymd = await provider.DetectDateLikeAsync(s, _owner, Table, "YMD", default);
        Assert.Equal(DateLikeFormat.Yyyymmdd, ymd!.Format);
        Assert.Equal(1.0, ymd.MatchRatio);
        Assert.Equal(EvidenceProviderRules.DateSampleRows, ymd.SampleSize); // NULL 은 빼고 앞 500 행
        Assert.Equal(DateLikeFormat.Yyyymm, (await provider.DetectDateLikeAsync(s, _owner, Table, "YM", default))!.Format);   // 숫자 열
        Assert.Equal(DateLikeFormat.YyyyDashMmDashDd, (await provider.DetectDateLikeAsync(s, _owner, Table, "ISO", default))!.Format);

        Assert.Null(await provider.DetectDateLikeAsync(s, _owner, Table, "CODE", default));
        Assert.Null(await provider.DetectDateLikeAsync(s, _owner, Table, "STATUS", default));
        Assert.Null(await provider.DetectDateLikeAsync(s, _owner, Table, "ID", default));
    }

    private async Task MonthlyAsync(OraclePartitionEvidenceProvider provider, ISession s)
    {
        var cases = new (string Column, DateLikeFormat? Format)[]
        {
            ("ORDER_DT", null), ("YMD", DateLikeFormat.Yyyymmdd), ("YM", DateLikeFormat.Yyyymm), ("ISO", DateLikeFormat.YyyyDashMmDashDd),
        };
        foreach (var (column, format) in cases)
        {
            var d = await provider.SampleMonthlyAsync(s, _owner, Table, column, format, null, default);

            Assert.NotNull(d);
            Assert.Equal(DistributionKind.Monthly, d.Kind);
            Assert.Equal(4, d.Buckets.Count);
            Assert.All(d.Buckets, b => Assert.Equal(300, b.Count));
            Assert.Equal(DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture), d.Buckets[^1].Label);
            Assert.Equal(100, d.RestCount);
            Assert.Equal(50, d.NullCount);
            Assert.Equal(1350, d.SampledRows);
            Assert.Null(d.SamplePercent);
        }

        var sampled = await provider.SampleMonthlyAsync(s, _owner, Table, "ORDER_DT", null, 50, default);
        Assert.Equal(50, sampled!.SamplePercent);
        Assert.InRange(sampled.SampledRows, 1, 1349); // 절반쯤 읽는다(정확한 수는 서버 마음)
    }

    private async Task TopValuesAsync(OraclePartitionEvidenceProvider provider, ISession s)
    {
        var d = await provider.SampleTopValuesAsync(s, _owner, Table, "STATUS", 2, null, default);

        Assert.NotNull(d);
        Assert.Equal(DistributionKind.TopValues, d.Kind);
        Assert.Equal([("A", 990L), ("B", 240L)], d.Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal(120, d.RestCount);
        Assert.Equal(0, d.NullCount);
        Assert.Equal(1350, d.SampledRows);

        var all = await provider.SampleTopValuesAsync(s, _owner, Table, "STATUS", 20, null, default);
        Assert.Equal(["A", "B", "C"], all!.Buckets.Select(b => b.Label));
        Assert.Equal(0, all.RestCount);

        var withNulls = await provider.SampleTopValuesAsync(s, _owner, Table, "YMD", 3, null, default);
        Assert.Equal(50, withNulls!.NullCount); // NULL 은 버킷이 아니라 NullCount
        Assert.DoesNotContain(withNulls.Buckets, b => b.Label is null);
        Assert.Equal(1350, withNulls.SampledRows);
    }

    private async Task EmptyTableAsync(OraclePartitionEvidenceProvider provider, ISession s)
    {
        var top = await provider.SampleTopValuesAsync(s, _owner, Table + "_E", "S", 5, null, default);
        var monthly = await provider.SampleMonthlyAsync(s, _owner, Table + "_E", "D", null, null, default);
        var date = await provider.DetectDateLikeAsync(s, _owner, Table + "_E", "S", default);

        Assert.Empty(top!.Buckets);
        Assert.Equal(0, top.SampledRows);
        Assert.Empty(monthly!.Buckets);
        Assert.Equal(0, monthly.SampledRows);
        Assert.Null(date);
    }

    private static async Task RunRepeatedAsync(OracleConnection conn, string sql, OracleParameter parameter, int times)
    {
        await using var cmd = conn.CreateCommand();
        cmd.BindByName = true;
        cmd.CommandText = sql;
        cmd.Parameters.Add(parameter);
        for (var i = 0; i < times; i++)
        {
            await cmd.ExecuteScalarAsync();
        }
    }

    private async Task<ISession> OpenAsync(string dataSource, string user)
    {
        var manager = new OracleSessionManager(new RecordingBus());
        using var cred = new SecureCredential(_password);
        var session = await manager.OpenAsync(new ConnectionProfile("t-partition-evidence", user, dataSource), cred, SessionPurpose.Work, default);
        _sessions.Add(session);
        return session;
    }

    private void Skip(string reason)
    {
        if (Env("REQUIRE") == "1")
        {
            throw new InvalidOperationException(reason);
        }

        _output.WriteLine("건너뜀: " + reason);
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable("ORANDA_IT_" + name);

    private static OracleConnection Connect(string dataSource, string user, string password) =>
        new(new ConnectionProfile("t-partition-evidence", user, dataSource).BuildConnectionString(password));

    private static async Task ExecAsync(OracleConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
