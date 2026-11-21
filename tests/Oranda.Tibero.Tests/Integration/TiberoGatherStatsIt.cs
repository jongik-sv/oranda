using Oranda.Core.Connection;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// SG-1 통계 수집 실행의 Tibero 문장(<see cref="GatherStatsDialect"/>)을 실서버로 확인한다. 옵션 조합마다 성공하고 LAST_ANALYZED 가 채워지는지 본다.
/// 조정자 실측(Tibero 6)으로 AUTO_SAMPLE_SIZE·SKEWONLY 는 되고 AUTO_INVALIDATE 는 ODP-15146 — 생성기가 그 상수를 넣지 않는지도 본다.
/// 공유 계정이라 <c>ORT_AE_GS</c> 표 하나만 만들고 끝에 지운다.
/// </summary>
[Trait("Category", "Integration")]
public class TiberoGatherStatsIt(ITestOutputHelper output)
{
    [TiberoWriteFact]
    public async Task Gather_statements_for_tibero_run_and_fill_last_analyzed()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser!.ToUpperInvariant();
        var p = new TableEditProbe(session, output, owner, it.Env.DbName);
        var t = TiberoItSessions.ObjectName("AE_GS");
        await p.DropTableAsync(t);
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, CD VARCHAR2(10))");
            await p.MustAsync($"INSERT INTO {t} SELECT LEVEL, MOD(LEVEL, 7) FROM DUAL CONNECT BY LEVEL <= 200");
            await p.MustAsync("COMMIT");
            var dialect = GatherStatsDialect.For(Dbms.Tibero);
            GatherStatsOptions[] cases =
            [
                GatherStatsOptions.Default,
                new() { SamplePercent = 30, Histogram = GatherStatsHistogram.None, Cascade = false, Degree = 2 },
                GatherStatsOptions.Default with { Histogram = GatherStatsHistogram.SkewOnly },
            ];
            foreach (var options in cases)
            {
                Assert.DoesNotContain("AUTO_INVALIDATE", dialect.Build(owner, t, options), StringComparison.Ordinal);
                await p.ExpectAsync("gather " + options, "OK", dialect.Build(owner, t, options));
            }

            Assert.NotNull(await p.ScalarAsync($"SELECT TO_CHAR(LAST_ANALYZED, 'YYYY-MM-DD') FROM USER_TABLES WHERE TABLE_NAME = '{t}'"));
        }
        finally
        {
            await p.DropTableAsync(t);
        }
    }
}
