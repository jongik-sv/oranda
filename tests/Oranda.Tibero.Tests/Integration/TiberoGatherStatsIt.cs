using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.History;
using Oranda.Core.Metadata;
using Oranda.Core.Sessions;
using Oranda.Core.Tuning;
using Oranda.Tibero.Execution;
using Oranda.Tibero.Tests.Support;
using Xunit.Abstractions;

namespace Oranda.Tibero.Tests.Integration;

/// <summary>
/// SG-1 통계 수집 실행의 Tibero 문장(<see cref="GatherStatsDialect"/>)을 실서버로 확인한다. 옵션 조합마다 성공하고 LAST_ANALYZED 가 채워지는지 본다.
/// 조정자 실측(Tibero 6)으로 AUTO_SAMPLE_SIZE·SKEWONLY 는 되고 AUTO_INVALIDATE 는 ODP-15146 — 생성기가 그 상수를 넣지 않는지도 본다.
/// 공유 계정이라 <c>ORT_TE_GS</c> 표 하나만(TableEditProbe 접두 규칙) 만들고 끝에 지운다.
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
        var t = TiberoItSessions.ObjectName("TE_GS");
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

    /// <summary>
    /// 결함 2026-11-22: 확인 창 길(전용 세션에서 앱 실행 서비스로 수집 → 닫기) 뒤, 이미 열려 통계를 한 번 읽은 작업 세션의 테이블 창 「통계」 구역이
    /// 곧바로 새 값을 읽는가. Tibero 세션은 트랜잭션 객체를 잡아 두므로(자동 커밋 아님) DBMS_STATS 가 스스로 커밋하지 않으면 닫을 때 버려진다 — 그것도 여기서 드러난다.
    /// 시험 표 = <c>ORT_AC_GS</c> 하나, 끝에 지운다.
    /// </summary>
    [TiberoWriteFact]
    public async Task Stats_gathered_on_the_dedicated_session_show_at_once_in_the_work_sessions_table_window()
    {
        await using var it = TiberoItSessions.Create();
        if (it is null || TiberoItSessions.SkipWrite())
        {
            return;
        }

        var work = await it.OpenAsync(SessionPurpose.Work);
        var owner = work.Server.SessionUser!.ToUpperInvariant();
        var p = new TableEditProbe(work, output, owner, it.Env.DbName);
        var t = TiberoItSessions.ObjectName("AC_GS");
        await p.SilentAsync($"DROP TABLE {t}");
        try
        {
            await p.MustAsync($"CREATE TABLE {t} (ID NUMBER PRIMARY KEY, CD VARCHAR2(10))");
            await p.MustAsync($"INSERT INTO {t} SELECT LEVEL, MOD(LEVEL, 7) FROM DUAL CONNECT BY LEVEL <= 300");
            await p.MustAsync("COMMIT");
            var meta = TableEditProbe.NewProvider();
            var target = new DbObjectRef(owner, t, "TABLE");
            Assert.Null(Analyzed(await meta.GetDetailPartAsync(work, target, DetailPart.Stats, default)));
            _ = await meta.GetTableAsync(work, target, default);

            var dedicated = await it.OpenAsync(SessionPurpose.Dedicated);
            var sql = GatherStatsDialect.For(Dbms.Tibero).Build(owner, t, GatherStatsOptions.Default);
            var exec = new TiberoExecutionService(new AllowAll(), ExecutionFakes.NoQuestions(), it.Events, null, dbmsOutput: false);
            var options = new ExecutionOptions { Source = HistorySource.SchemaBrowser, PreConfirmed = new HashSet<string>(StringComparer.Ordinal) { sql } };
            var result = await exec.ExecuteAsync(dedicated, new SqlStatement(sql, default, StatementKind.PlSql) { Verb = "BEGIN" },
                new Dictionary<string, BindValue>(), options, default);
            Assert.True(result.Status == ExecutionStatus.Succeeded, result.Error?.Code + " " + result.Error?.Message);
            output.WriteLine("dedicated pending after gather: " + dedicated.HasPendingTransaction);
            await dedicated.DisposeAsync(); // 확인 창은 다 쓰면 닫는다

            await meta.ForgetObjectDetailsAsync(work, [new DbObjectRef(owner, t, string.Empty)], default);
            var after = await meta.GetDetailPartAsync(work, target, DetailPart.Stats, default);
            Assert.NotNull(Analyzed(after));
        }
        finally
        {
            await p.SilentAsync($"DROP TABLE {t}");
        }

        static object? Analyzed(IReadOnlyList<DetailSection> sections) => sections.Single().Rows.Single(r => (string?)r[0] == "마지막 분석")[1];
    }
}
