using Oranda.Core.Sessions;
using Oranda.Tibero.Sessions;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// Tibero 열 통계 글(low_value·high_value·히스토그램 끝점)이 화면 글로 풀리는지 실서버로 확인한다. Tibero 는 이 값을 RAW 가 아니라 글로 준다
/// (<see cref="Tibero.Tuning.TiberoStatValue"/>).
/// <list type="bullet">
/// <item>환경: <c>ORANDA_IT_TIBERO_*</c>·<c>ORANDA_TIBERO_DRIVER</c>·<c>ORANDA_IT_TIBERO_WRITE=1</c>. 없으면 건너뛴다.</item>
/// <item>내 계정에 <c>ORT_CS_TSV</c> 를 만들어 통계를 모으고(DBMS_STATS) 끝에 지운다. 남의 객체는 건드리지 않는다.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class TiberoColumnStatValueIt(ITestOutputHelper output)
{
    private static readonly string Table = TiberoItSessions.ObjectName("CS_TSV");

    [TiberoWriteFact]
    public async Task Low_high_and_histogram_values_are_shown_as_text()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var session = await it.OpenAsync(SessionPurpose.Dedicated);
        var owner = session.Server.SessionUser!;
        await SilentAsync(session, $"DROP TABLE {Table} PURGE");
        try
        {
            await ExecAsync(session, $"CREATE TABLE {Table} (N NUMBER, D DATE, T TIMESTAMP(6), C VARCHAR2(10))");
            await ExecAsync(session, $"""
                INSERT INTO {Table}
                SELECT DECODE(MOD(LEVEL, 3), 0, -341.13, 1, 900, 2, 12.5),
                       DECODE(MOD(LEVEL, 2), 0, TO_DATE('20200103140000', 'YYYYMMDDHH24MISS'), TO_DATE('20200106', 'YYYYMMDD')),
                       TO_TIMESTAMP('20230615093001623456', 'YYYYMMDDHH24MISSFF6'),
                       DECODE(MOD(LEVEL, 3), 0, 'AA', 1, 'P1', 2, '가나다0')
                  FROM DUAL CONNECT BY LEVEL <= 30
                """);
            await ExecAsync(session, "COMMIT");
            await ExecAsync(session, $"BEGIN DBMS_STATS.GATHER_TABLE_STATS('{owner}', '{Table}', method_opt => 'FOR ALL COLUMNS SIZE 10'); END;");

            var service = TiberoTuningServiceIt.NewService(it);
            var columns = await service.GetColumnStatsAsync(session, owner, Table, CancellationToken.None);
            foreach (var c in columns)
            {
                output.WriteLine($"[CS] {c.ColumnName}: low = {c.LowValue}, high = {c.HighValue}, buckets = {c.NumBuckets}");
            }

            var n = Assert.Single(columns, c => c.ColumnName == "N");
            Assert.Equal("-341.13", n.LowValue);
            Assert.Equal("900", n.HighValue);
            var d = Assert.Single(columns, c => c.ColumnName == "D");
            Assert.Equal("2020-01-03 14:00:00", d.LowValue);
            Assert.Equal("2020-01-06", d.HighValue);
            var t = Assert.Single(columns, c => c.ColumnName == "T");
            Assert.Equal("2023-06-15 09:30:01.623456", t.LowValue);
            var c2 = Assert.Single(columns, c => c.ColumnName == "C");
            Assert.Equal("AA", c2.LowValue);
            Assert.Equal("가나다0", c2.HighValue);

            foreach (var column in new[] { "N", "D", "C" })
            {
                var buckets = await service.GetHistogramAsync(session, owner, Table, column, CancellationToken.None);
                output.WriteLine($"[CS] histogram {column}: {string.Join(" | ", buckets.Select(b => b.EndpointValue))}");
                if (buckets.Count == 0)
                {
                    continue; // 서버가 히스토그램을 만들지 않았다 — 값 단정은 열 통계로 충분하다.
                }

                var expected = column switch
                {
                    "N" => new[] { "-341.13", "12.5", "900" },
                    "D" => ["2020-01-03 14:00:00", "2020-01-06"],
                    _ => ["AA", "P1", "가나다0"],
                };
                Assert.All(buckets, b => Assert.Contains(b.EndpointValue, expected));
            }
        }
        finally
        {
            await SilentAsync(session, $"DROP TABLE {Table} PURGE");
        }
    }

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

    private static async Task SilentAsync(TiberoSession session, string sql)
    {
        try
        {
            await ExecAsync(session, sql);
        }
#pragma warning disable CA1031 // 정리 단계: 없는 표를 지우려 했다 — 실패해도 시험 결과를 가리지 않는다.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }
}
