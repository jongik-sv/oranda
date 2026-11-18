using System.Data.Common;
using System.Globalization;
using Oranda.Core.Schema.TableEdit.Partitions.Advisor;
using Oranda.Tibero.Schema;
using Oranda.Tibero.Sessions;
using Oranda.Tibero.Tests.Integration;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Schema;

/// <summary>
/// AI 파티션 추천의 Tibero 근거 공급자(설계 2026-11-18 5장)를 실서버에서 본다: 조회 조건(V$SQL_PLAN)·날짜형 판정·월별 건수·상위 값·표본.
/// 쓰기 시험(<c>ORANDA_IT_TIBERO_WRITE=1</c>). 객체는 <c>ORT_PA_</c> 표 둘뿐이고 처음과 끝에 지운다(공유 계정이라 다른 객체는 건드리지 않는다).
/// 두 번째 계정을 쓰지 않으므로 「V$ 권한 없음」 시험은 Oracle 쪽 IT 와 가짜 질의 단위 시험이 맡는다.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TiberoPartitionEvidenceIt(ITestOutputHelper output)
{
    // 표 이름은 실행마다 새로 짓는다: 공유 풀에 남은 앞선 실행의 문장(지운 표의 계획도 남는다)이 실행 수에 섞이지 않게.
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private string Table => TiberoItSessions.ObjectName("PA_" + _suffix + "_O");

    private string Empty => TiberoItSessions.ObjectName("PA_" + _suffix + "_E");

    [TiberoWriteFact]
    public async Task Gathers_predicates_date_like_distributions_and_samples_from_a_real_server()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null)
        {
            return;
        }

        var s = await it.OpenAsync();
        var owner = it.Env.User.ToUpperInvariant();
        await DropAsync(s);
        try
        {
            await ExecAsync(s, $"CREATE TABLE {Table} (ID NUMBER, ORDER_DT DATE, CODE VARCHAR2(12), YMD VARCHAR2(8), YM NUMBER(6), ISO VARCHAR2(10), STATUS VARCHAR2(4))");
            await ExecAsync(s, $"CREATE INDEX {Table}_DT ON {Table} (ORDER_DT)");
            // 1350 행: 이번 달부터 3달 전까지 300 행씩, 60달 전 100 행, 날짜 NULL 50 행. 상태 A 990 · B 240 · C 120.
            await ExecAsync(s, $@"INSERT INTO {Table}
                SELECT n, d, 'ID' || n, TO_CHAR(d, 'YYYYMMDD'), TO_NUMBER(TO_CHAR(d, 'YYYYMM')), TO_CHAR(d, 'YYYY-MM-DD'),
                       CASE WHEN n <= 1200 THEN CASE MOD(n, 10) WHEN 0 THEN 'C' WHEN 1 THEN 'B' WHEN 2 THEN 'B' ELSE 'A' END ELSE 'A' END
                  FROM (SELECT LEVEL AS n,
                               CASE WHEN LEVEL <= 1200 THEN ADD_MONTHS(TRUNC(SYSDATE, 'MM'), -MOD(LEVEL, 4)) + MOD(LEVEL, 20)
                                    WHEN LEVEL <= 1300 THEN ADD_MONTHS(TRUNC(SYSDATE, 'MM'), -60)
                                    ELSE CAST(NULL AS DATE) END AS d
                          FROM DUAL CONNECT BY LEVEL <= 1350)");
            await ExecAsync(s, "COMMIT");
            await ExecAsync(s, $"CREATE TABLE {Empty} (ID NUMBER, D DATE, S VARCHAR2(4))");

            // 공유 풀에 조건이 남게 같은 문장을 정해진 횟수만큼 돌린다(바인드 문장이라 sql_id 하나).
            for (var i = 0; i < 5; i++)
            {
                await ExecScalarAsync(s, $"SELECT COUNT(*) FROM {Table} WHERE ORDER_DT >= :d", c => Bind(c, "d", DateTime.Today.AddYears(-10)));
            }

            for (var i = 0; i < 2; i++)
            {
                await ExecScalarAsync(s, $"SELECT COUNT(*) FROM {Table} WHERE STATUS = :s", c => Bind(c, "s", "B"));
            }

            var provider = new TiberoPartitionEvidenceProvider();
            await PredicatesAsync(provider, s, owner);
            await DateLikeAsync(provider, s, owner);
            await MonthlyAsync(provider, s, owner);
            await TopValuesAsync(provider, s, owner);
            await EmptyAsync(provider, s, owner);
        }
        finally
        {
            await DropAsync(s);
        }

        // 만든 표가 하나도 남지 않았다.
        var left = await TiberoSessionQuery.RunAsync(s, "SELECT COUNT(*) FROM user_tables WHERE table_name LIKE 'ORT#_PA#_" + _suffix + "%' ESCAPE '#'");
        Assert.Equal(0L, Convert.ToInt64(left.Rows[0][0], CultureInfo.InvariantCulture));
    }

    private async Task PredicatesAsync(TiberoPartitionEvidenceProvider provider, TiberoSession s, string owner)
    {
        var use = await provider.GetPredicateUseAsync(s, owner, Table, default);

        Assert.NotNull(use);
        output.WriteLine(string.Join(", ", use.Select(u => $"{u.Column} x{u.Executions}/{u.Statements} {u.Share:P0}")));
        Assert.Equal(["ORDER_DT", "STATUS"], use.Select(u => u.Column));
        Assert.True(use[0].Executions >= 5, "ORDER_DT 실행 수 " + use[0].Executions);
        Assert.Equal(2, use[1].Executions);
        Assert.Equal(1, use[1].Statements);
    }

    private async Task DateLikeAsync(TiberoPartitionEvidenceProvider provider, TiberoSession s, string owner)
    {
        var ymd = await provider.DetectDateLikeAsync(s, owner, Table, "YMD", default);
        Assert.Equal(DateLikeFormat.Yyyymmdd, ymd!.Format);
        Assert.Equal(1.0, ymd.MatchRatio);
        Assert.Equal(EvidenceProviderRules.DateSampleRows, ymd.SampleSize);
        Assert.Equal(DateLikeFormat.Yyyymm, (await provider.DetectDateLikeAsync(s, owner, Table, "YM", default))!.Format);
        Assert.Equal(DateLikeFormat.YyyyDashMmDashDd, (await provider.DetectDateLikeAsync(s, owner, Table, "ISO", default))!.Format);

        Assert.Null(await provider.DetectDateLikeAsync(s, owner, Table, "CODE", default));
        Assert.Null(await provider.DetectDateLikeAsync(s, owner, Table, "STATUS", default));
        Assert.Null(await provider.DetectDateLikeAsync(s, owner, Table, "ID", default));
    }

    private async Task MonthlyAsync(TiberoPartitionEvidenceProvider provider, TiberoSession s, string owner)
    {
        var cases = new (string Column, DateLikeFormat? Format)[]
        {
            ("ORDER_DT", null), ("YMD", DateLikeFormat.Yyyymmdd), ("YM", DateLikeFormat.Yyyymm), ("ISO", DateLikeFormat.YyyyDashMmDashDd),
        };
        foreach (var (column, format) in cases)
        {
            var d = await provider.SampleMonthlyAsync(s, owner, Table, column, format, null, default);

            Assert.NotNull(d);
            Assert.Equal(4, d.Buckets.Count);
            Assert.All(d.Buckets, b => Assert.Equal(300, b.Count));
            Assert.Equal(DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture), d.Buckets[^1].Label);
            Assert.Equal(100, d.RestCount);
            Assert.Equal(50, d.NullCount);
            Assert.Equal(1350, d.SampledRows);
        }

        var sampled = await provider.SampleMonthlyAsync(s, owner, Table, "ORDER_DT", null, 50, default);
        Assert.Equal(50, sampled!.SamplePercent);
        Assert.InRange(sampled.SampledRows, 1, 1349);
    }

    private async Task TopValuesAsync(TiberoPartitionEvidenceProvider provider, TiberoSession s, string owner)
    {
        var d = await provider.SampleTopValuesAsync(s, owner, Table, "STATUS", 2, null, default);

        Assert.NotNull(d);
        Assert.Equal([("A", 990L), ("B", 240L)], d.Buckets.Select(b => (b.Label, b.Count)));
        Assert.Equal(120, d.RestCount);
        Assert.Equal(0, d.NullCount);
        Assert.Equal(1350, d.SampledRows);

        var all = await provider.SampleTopValuesAsync(s, owner, Table, "STATUS", 20, null, default);
        Assert.Equal(["A", "B", "C"], all!.Buckets.Select(b => b.Label));
        Assert.Equal(0, all.RestCount);

        var withNulls = await provider.SampleTopValuesAsync(s, owner, Table, "YMD", 3, null, default);
        Assert.Equal(50, withNulls!.NullCount);
        Assert.DoesNotContain(withNulls.Buckets, b => b.Label is null);
    }

    private async Task EmptyAsync(TiberoPartitionEvidenceProvider provider, TiberoSession s, string owner)
    {
        var top = await provider.SampleTopValuesAsync(s, owner, Empty, "S", 5, null, default);
        var monthly = await provider.SampleMonthlyAsync(s, owner, Empty, "D", null, null, default);

        Assert.Empty(top!.Buckets);
        Assert.Equal(0, top.SampledRows);
        Assert.Empty(monthly!.Buckets);
        Assert.Null(await provider.DetectDateLikeAsync(s, owner, Empty, "S", default));
    }

    private static void Bind(DbCommand c, string name, object value) => TiberoSessionQuery.Bind(c, name, value);

    private async Task DropAsync(TiberoSession s)
    {
        foreach (var table in new[] { Table, Empty })
        {
            try
            {
                await ExecAsync(s, $"DROP TABLE {table} CASCADE CONSTRAINTS PURGE");
            }
#pragma warning disable CA1031 // 없는 표를 지우려다 나는 오류는 무시한다.
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
    }

    private static async Task ExecAsync(TiberoSession s, string sql)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            await cmd.ExecuteNonQueryAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }

    private static async Task ExecScalarAsync(TiberoSession s, string sql, Action<DbCommand> prepare)
    {
        await s.CommandGate.EnterAsync(CancellationToken.None);
        try
        {
            await using var cmd = s.CreateCommand(sql);
            prepare(cmd);
            _ = await cmd.ExecuteScalarAsync();
        }
        finally
        {
            s.CommandGate.Release();
        }
    }
}
