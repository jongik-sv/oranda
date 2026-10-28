using Oranda.Core.Tuning;
using R = Oranda.Core.Tuning.TuningRecommendationRules;

namespace Oranda.Core.Tests.Tuning;

/// <summary>TSK-14-17 튜닝 추천 규칙: 규칙마다 걸림/안 걸림, 점수 순서, 0 나누기·NULL, 대상 제외.</summary>
public class TuningRecommendationRulesTests
{
    private const long Sec = 1_000_000;

    // 아무 규칙에도 안 걸리는 평범한 SQL: 1000회, 실행당 1 ms, 실행당 100 블록, 행당 1 블록, CPU 위주 아님.
    private static SqlStatsSample Calm(string id = "calm000000001") => new(id)
    {
        ParsingSchema = "APP_OWNER",
        SqlText = "SELECT * FROM orders WHERE id = :1",
        Executions = 1000,
        ElapsedMicros = 1 * Sec,
        CpuMicros = Sec / 2,
        BufferGets = 100_000,
        RowsProcessed = 100_000,
        DiskReads = 10,
        ParseCalls = 10,
        VersionCount = 1,
    };

    private static IEnumerable<TuningRule> Rules(SqlStatsSample s, long total = 0, IReadOnlyList<PlanFindingLine>? plan = null) =>
        R.Evaluate(s, total, plan).Reasons.Select(r => r.Rule);

    [Fact]
    public void Calm_sql_gets_no_reason_and_is_not_recommended()
    {
        var rec = R.Evaluate(Calm(), totalElapsedMicros: 100 * Sec);

        Assert.Empty(rec.Reasons);
        Assert.Equal(0, rec.Score);
        Assert.Empty(R.Rank(new TuningCandidateSet([Calm()], 100 * Sec)));
    }

    [Theory]
    [InlineData(0.99, false, 0)]
    [InlineData(1.0, true, 30)]
    [InlineData(12.0, true, 45)]
    public void Slow_per_execution_from_one_second(double perExecSeconds, bool hit, int points)
    {
        var s = Calm() with { Executions = 10, ElapsedMicros = (long)(perExecSeconds * 10 * Sec), CpuMicros = (long)(perExecSeconds * 5 * Sec) };

        var reason = R.Evaluate(s, 0).Reasons.FirstOrDefault(r => r.Rule == TuningRule.SlowPerExecution);

        Assert.Equal(hit, reason is not null);
        if (hit)
        {
            Assert.Equal(points, reason!.Points);
            Assert.Contains("실행당 경과", reason.Evidence, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Time_share_needs_ten_percent_and_a_positive_total()
    {
        var s = Calm() with { ElapsedMicros = 10 * Sec, CpuMicros = 5 * Sec };

        Assert.Contains(TuningRule.TimeShare, Rules(s, total: 100 * Sec));
        Assert.DoesNotContain(TuningRule.TimeShare, Rules(s, total: 101 * Sec));
        Assert.DoesNotContain(TuningRule.TimeShare, Rules(s, total: 0)); // 분모 0: 건너뜀
        Assert.Equal(35, R.Evaluate(s, 30 * Sec).Reasons.Single(r => r.Rule == TuningRule.TimeShare).Points);
    }

    [Fact]
    public void Logical_reads_per_execution_and_per_row()
    {
        var heavy = Calm() with { BufferGets = 1000L * 20_000, RowsProcessed = 1000 };
        Assert.Contains(TuningRule.ReadsPerExecution, Rules(heavy));
        Assert.Contains(TuningRule.ReadsPerRow, Rules(heavy));

        // 행이 0 이면 행당 비율은 나눌 수 없어 건너뛴다(실행당 읽기는 그대로).
        var noRows = heavy with { RowsProcessed = 0 };
        Assert.Contains(TuningRule.ReadsPerExecution, Rules(noRows));
        Assert.DoesNotContain(TuningRule.ReadsPerRow, Rules(noRows));

        // 행당 비율이 커도 실행당 읽기가 작으면 빼다.
        var tiny = Calm() with { BufferGets = 1000L * 500, RowsProcessed = 1 };
        Assert.DoesNotContain(TuningRule.ReadsPerRow, Rules(tiny));
    }

    [Fact]
    public void Physical_reads_per_execution()
    {
        Assert.Contains(TuningRule.DiskReadsPerExecution, Rules(Calm() with { DiskReads = 1000L * 1000 }));
        Assert.DoesNotContain(TuningRule.DiskReadsPerExecution, Rules(Calm() with { DiskReads = 1000L * 999 }));
    }

    [Fact]
    public void Plan_rules_large_full_scan_index_full_scan_and_cartesian()
    {
        IReadOnlyList<PlanFindingLine> plan =
        [
            new(2, "TABLE ACCESS", "FULL", "APP_OWNER", "BIG_T") { TableBlocks = 20_000, TableRows = 500_000 },
            new(3, "TABLE ACCESS", "FULL", "APP_OWNER", "SMALL_T") { TableBlocks = 10, TableRows = 100 },
            new(4, "INDEX", "FULL SCAN", "APP_OWNER", "BIG_IX") { Cardinality = 50_000 },
            new(5, "MERGE JOIN", "CARTESIAN", null, null),
        ];

        var rec = R.Evaluate(Calm(), 0, plan);

        var full = rec.Reasons.Single(r => r.Rule == TuningRule.FullScanLargeTable);
        Assert.Contains("BIG_T", full.Evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("SMALL_T", full.Evidence, StringComparison.Ordinal);
        Assert.Contains(TuningRule.IndexFullScan, rec.Reasons.Select(r => r.Rule));
        Assert.Contains(TuningRule.CartesianJoin, rec.Reasons.Select(r => r.Rule));

        // 통계가 없는 표는 계획 예상 행으로 본다. 계획이 없으면(null) 계획 규칙을 건너뛴다.
        Assert.True(R.IsLargeFullScan(new(1, "TABLE ACCESS", "STORAGE FULL", "O", "T") { Cardinality = 200_000 }));
        Assert.False(R.IsLargeFullScan(new(1, "TABLE ACCESS", "FULL", "O", "T") { Cardinality = 50 }));
        Assert.False(R.IsLargeFullScan(new(1, "TABLE ACCESS", "BY INDEX ROWID", "O", "T") { TableBlocks = 99_999 }));
        Assert.Empty(R.Evaluate(Calm(), 0, null).Reasons);
        Assert.DoesNotContain(TuningRule.IndexFullScan, Rules(Calm(), plan: [new(4, "INDEX", "FULL SCAN", "O", "IX") { Cardinality = 10 }]));
    }

    [Fact]
    public void Child_cursors_literals_and_parse_per_execution()
    {
        Assert.Contains(TuningRule.ManyChildCursors, Rules(Calm() with { VersionCount = 20 }));
        Assert.DoesNotContain(TuningRule.ManyChildCursors, Rules(Calm() with { VersionCount = 19 }));
        Assert.Contains(TuningRule.LiteralSql, Rules(Calm() with { SimilarSqlCount = 10 }));
        Assert.DoesNotContain(TuningRule.LiteralSql, Rules(Calm() with { SimilarSqlCount = 9 }));
        Assert.Contains(TuningRule.ParsePerExecution, Rules(Calm() with { ParseCalls = 950 }));
        Assert.DoesNotContain(TuningRule.ParsePerExecution, Rules(Calm() with { ParseCalls = 950, Executions = 99 }));
    }

    [Fact]
    public void Wait_heavy_versus_cpu_heavy()
    {
        var waiting = Calm() with { Executions = 10, ElapsedMicros = 10 * Sec, CpuMicros = 1 * Sec, UserIoWaitMicros = 8 * Sec };
        var wait = R.Evaluate(waiting, 0).Reasons.Single(r => r.Rule == TuningRule.WaitHeavy);
        Assert.Contains("User I/O", wait.Evidence, StringComparison.Ordinal);

        var cpu = Calm() with { Executions = 10, ElapsedMicros = 20 * Sec, CpuMicros = 19 * Sec };
        Assert.Contains(TuningRule.CpuHeavy, Rules(cpu));
        Assert.DoesNotContain(TuningRule.WaitHeavy, Rules(cpu));

        // 경과 0 이면 비율을 계산하지 않는다(0 나누기 없음).
        Assert.Empty(R.Evaluate(Calm() with { ElapsedMicros = 0, CpuMicros = 0 }, 0).Reasons);
    }

    [Fact]
    public void Zero_executions_and_null_fields_do_not_throw()
    {
        var empty = new SqlStatsSample("empty00000001");
        var rec = R.Evaluate(empty, 0);
        Assert.Empty(rec.Reasons);
        Assert.Null(rec.ElapsedPerExecSeconds);
        Assert.Null(rec.GetsPerExec);
        Assert.False(R.IsTarget(empty, new TuningRecommendationOptions()));

        // 지금 처음 도는 SQL(실행 0, 경과 있음)은 실행 하나로 본다 — 세션 모니터(MinExecutions 0)만 대상.
        var running = empty with { ElapsedMicros = 30 * Sec, CpuMicros = 29 * Sec, ParsingSchema = "APP_OWNER" };
        Assert.Contains(TuningRule.SlowPerExecution, Rules(running));
        Assert.False(R.IsTarget(running, new TuningRecommendationOptions()));
        Assert.True(R.IsTarget(running, new TuningRecommendationOptions { MinExecutions = 0 }));
    }

    [Fact]
    public void Rank_orders_by_score_then_elapsed_and_caps_score_at_100()
    {
        var worst = Calm("worst00000001") with
        {
            Executions = 10, ElapsedMicros = 200 * Sec, CpuMicros = 10 * Sec, UserIoWaitMicros = 150 * Sec,
            BufferGets = 10L * 2_000_000, DiskReads = 10L * 50_000, RowsProcessed = 10, VersionCount = 150, SimilarSqlCount = 200,
        };
        var mid = Calm("mid0000000001") with { Executions = 10, ElapsedMicros = 20 * Sec, CpuMicros = 10 * Sec };
        var midLonger = mid with { SqlId = "mid0000000002", ElapsedMicros = 25 * Sec, CpuMicros = 12 * Sec };

        var ranked = R.Rank(new TuningCandidateSet([mid, Calm(), worst, midLonger], 10_000 * Sec));

        Assert.Equal(["worst00000001", "mid0000000002", "mid0000000001"], ranked.Select(r => r.Sql.SqlId));
        Assert.Equal(100, ranked[0].Score);
        Assert.True(ranked[0].Reasons.Zip(ranked[0].Reasons.Skip(1)).All(p => p.First.Points >= p.Second.Points));
        Assert.Contains("실행당 오래 걸림", ranked[0].Summary, StringComparison.Ordinal);
        Assert.Single(R.Rank(new TuningCandidateSet([mid, worst, midLonger], 0), new TuningRecommendationOptions { Top = 1 }));
    }

    [Fact]
    public void Rank_excludes_internal_schemas_recursive_sql_and_short_sql_unless_turned_off()
    {
        var slow = Calm() with { Executions = 1, ElapsedMicros = 5 * Sec, CpuMicros = 1 * Sec };
        var sys = slow with { SqlId = "sys0000000001", ParsingSchema = "SYS" };
        var apex = slow with { SqlId = "apex000000001", ParsingSchema = "APEX_230200" };
        var sampling = slow with { SqlId = "ds00000000001", SqlText = "SELECT /* DS_SVC */ /*+ dynamic_sampling(0) */ COUNT(*) FROM t" };
        var unknownSchema = slow with { SqlId = "null000000001", ParsingSchema = null };
        var shortSql = slow with { SqlId = "short00000001", ElapsedMicros = Sec / 20 };
        var set = new TuningCandidateSet([sys, apex, sampling, unknownSchema, shortSql], 0);

        Assert.Equal(["null000000001"], R.Rank(set).Select(r => r.Sql.SqlId));
        var all = R.Rank(set, new TuningRecommendationOptions { ExcludeInternalSchemas = false }).Select(r => r.Sql.SqlId).ToList();
        Assert.Equal(4, all.Count);
        Assert.DoesNotContain("short00000001", all);
        Assert.True(R.IsInternalSchema(" system "));
        Assert.False(R.IsInternalSchema(null));
        Assert.False(R.IsInternalSchema("APP_OWNER"));
    }

    [Fact]
    public void Literal_groups_attach_to_a_candidate_with_the_same_signature_or_join_as_their_own_line()
    {
        var row = Calm("lit0000000001") with { ForceMatchingSignature = "17415914164128958688" };
        var attached = new LiteralSqlGroup("17415914164128958688", 40, new SqlStatsSample("lit0000000000"));
        var alone = new LiteralSqlGroup("123", 150, new SqlStatsSample("alone00000001") { Executions = 150, ElapsedMicros = Sec / 100, ParsingSchema = "APP_OWNER" });
        var zero = new LiteralSqlGroup("0", 500, new SqlStatsSample("zero000000001") { Executions = 500, ElapsedMicros = Sec });
        var set = new TuningCandidateSet([row], 0) { LiteralGroups = [attached, alone, zero] };

        var ranked = R.Rank(set);

        Assert.Equal(["alone00000001", "lit0000000001"], ranked.Select(r => r.Sql.SqlId));
        Assert.True(ranked[0].Sql.IsLiteralGroup); // 짧은 SQL 이라도 리터럴 묶음은 남는다
        Assert.Equal(30, ranked[0].Score);
        Assert.Equal(40, ranked[1].Sql.SimilarSqlCount);
        Assert.False(ranked[1].Sql.IsLiteralGroup);
    }
}
