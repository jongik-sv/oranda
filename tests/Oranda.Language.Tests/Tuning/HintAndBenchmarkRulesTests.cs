using Oranda.Core.Plans;
using Oranda.Language.Tuning;

namespace Oranda.Language.Tests.Tuning;

/// <summary>TSK-06-05 TN-10·11: 힌트 대안과 벤치마크 규칙(거부·판정).</summary>
public class HintAndBenchmarkRulesTests
{
    // ---- 힌트 대안 ----

    [Fact]
    public void Hints_use_sql_alias_spelling_and_skip_the_current_join_combination()
    {
        var index = IndexAdvisor.Suggest(TuningFixtures.JoinPlan(), TuningFixtures.Existing);
        var hints = HintAdvisor.Suggest(TuningFixtures.JoinPlan(), TuningFixtures.Sql, index);
        var texts = hints.Select(h => h.Hint).ToList();
        Assert.Contains("INDEX(lh LOT_HIST_IX2)", texts);
        Assert.Contains("LEADING(lh lm) USE_NL(lm)", texts);
        Assert.Contains("LEADING(lh lm) USE_HASH(lm)", texts);
        Assert.Contains("LEADING(lm lh) USE_NL(lh)", texts);
        Assert.DoesNotContain("LEADING(lm lh) USE_HASH(lh)", texts); // 지금 계획(LOT_MST 먼저 해시 조인)
        var parallel = Assert.Single(hints, h => h.Hint.Contains("PARALLEL", StringComparison.Ordinal));
        Assert.Equal("FULL(lh) PARALLEL(lh 4)", parallel.Hint);
        Assert.False(parallel.Enabled);
        Assert.Equal("병렬 — 설정에서 꺼짐", parallel.DisabledReason);
        Assert.Equal("/*+ FULL(lh) PARALLEL(lh 4) */", parallel.Comment);
        Assert.Contains("LOT_MST 먼저, LOT_HIST 를 중첩 루프로", hints.Select(h => h.Meaning));
    }

    [Fact]
    public void Parallel_is_enabled_by_option_and_index_access_offers_full()
    {
        var plan = new ExecutionPlan(
            [
                new PlanStep(0, null, 0, "SELECT STATEMENT"),
                new PlanStep(1, 0, 1, "TABLE ACCESS") { Options = "BY INDEX ROWID", ObjectName = "EMP" },
                new PlanStep(2, 1, 2, "INDEX") { Options = "RANGE SCAN", ObjectName = "EMP_IX" },
            ], string.Empty);
        var hints = HintAdvisor.Suggest(plan, "SELECT * FROM emp e WHERE e.deptno = 10", options: new HintAdvisorOptions { AllowParallel = true, English = true });
        var full = Assert.Single(hints);
        Assert.Equal("FULL(e)", full.Hint);
        Assert.StartsWith("Read all of EMP", full.Meaning, StringComparison.Ordinal);

        var fullPlan = TuningFixtures.JoinPlan();
        var p = Assert.Single(HintAdvisor.Suggest(fullPlan, null, options: new HintAdvisorOptions { AllowParallel = true, ParallelDegree = 8 }), h => h.Hint.Contains("PARALLEL", StringComparison.Ordinal));
        Assert.True(p.Enabled);
        Assert.Null(p.DisabledReason);
        Assert.Equal("FULL(LH) PARALLEL(LH 8)", p.Hint); // SQL 이 없으면 계획 별칭 그대로
    }

    [Fact]
    public void Apply_inserts_after_the_main_select_or_merges_into_an_existing_hint()
    {
        Assert.Equal("SELECT /*+ FULL(e) */ * FROM emp e", HintAdvisor.Apply("SELECT * FROM emp e", "FULL(e)"));
        Assert.Equal("SELECT /*+ ORDERED FULL(e) */ * FROM emp e", HintAdvisor.Apply("SELECT /*+ ORDERED */ * FROM emp e", "FULL(e)"));
        Assert.Equal("WITH c AS (SELECT 1 x FROM dual) SELECT /*+ FULL(c) */ * FROM c",
            HintAdvisor.Apply("WITH c AS (SELECT 1 x FROM dual) SELECT * FROM c", "FULL(c)"));
        Assert.Null(HintAdvisor.Apply("UPDATE emp SET sal = 1", "FULL(emp)"));
    }

    [Fact]
    public void Plan_without_table_access_gives_no_hints()
    {
        Assert.Empty(HintAdvisor.Suggest(new ExecutionPlan([new PlanStep(0, null, 0, "SELECT STATEMENT")], string.Empty), "SELECT 1 FROM dual"));
    }

    // ---- 벤치마크 거부 ----

    [Theory]
    [InlineData("SELECT * FROM emp")]
    [InlineData("  select 1 from dual ;")]
    [InlineData("WITH a AS (SELECT 1 x FROM dual), b AS (SELECT 2 y FROM dual) SELECT * FROM a, b")]
    [InlineData("(SELECT 1 FROM dual) UNION ALL (SELECT 2 FROM dual)")]
    [InlineData("SELECT ':x' FROM dual -- :y in a comment")]
    public void Queries_are_accepted(string sql) => Assert.Null(BenchmarkRules.RejectReason(sql));

    [Theory]
    [InlineData("UPDATE emp SET sal = 1", "SELECT 문만 벤치마크합니다(이 문장: UPDATE).")]
    [InlineData("DELETE FROM emp", "SELECT 문만 벤치마크합니다(이 문장: DELETE).")]
    [InlineData("INSERT INTO t SELECT * FROM emp", "SELECT 문만 벤치마크합니다(이 문장: INSERT).")]
    [InlineData("MERGE INTO t USING s ON (t.k = s.k) WHEN MATCHED THEN UPDATE SET t.v = s.v", "SELECT 문만 벤치마크합니다(이 문장: MERGE).")]
    [InlineData("BEGIN NULL; END;", "문장 하나만 벤치마크합니다.")]
    [InlineData("CREATE INDEX ix ON t (a)", "SELECT 문만 벤치마크합니다(이 문장: CREATE).")]
    [InlineData("SELECT * FROM emp FOR UPDATE", "FOR UPDATE 는 행을 잠가서 벤치마크하지 않습니다.")]
    [InlineData("SELECT * FROM emp WHERE empno = :id", "변수 :id 가 있습니다. 값을 써 넣은 사본으로 벤치마크하세요.")]
    [InlineData("SELECT * FROM emp WHERE empno = &no", "변수 &no 가 있습니다. 값을 써 넣은 사본으로 벤치마크하세요.")]
    [InlineData("SELECT 1 FROM dual; SELECT 2 FROM dual", "문장 하나만 벤치마크합니다.")]
    [InlineData("WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1; END; SELECT f FROM dual", "문장 하나만 벤치마크합니다.")]
    [InlineData("WITH FUNCTION f RETURN NUMBER IS BEGIN RETURN 1 END SELECT f FROM dual", "WITH FUNCTION(인라인 PL/SQL)은 벤치마크하지 않습니다.")]
    [InlineData("", "문장이 비었습니다.")]
    [InlineData("  -- 주석만", "문장이 비었습니다.")]
    public void Non_queries_and_unsafe_queries_are_rejected_with_a_reason(string sql, string reason) =>
        Assert.Equal(reason, BenchmarkRules.RejectReason(sql));

    [Fact]
    public void Reject_reasons_have_english_and_trim_strips_terminators()
    {
        Assert.Equal("Only SELECT statements are benchmarked (this one is UPDATE).", BenchmarkRules.RejectReason("update t set a = 1", english: true));
        Assert.Equal("SELECT 1 FROM dual", BenchmarkRules.Trim("  SELECT 1 FROM dual ;\n/ "));
        Assert.Equal(string.Empty, BenchmarkRules.Trim(null));
    }

    // ---- 판정 ----

    private static BenchmarkMeasure M(double? ms, long reads, long? rows) => new(ms is null ? null : TimeSpan.FromMilliseconds(ms.Value), reads, rows);

    [Fact]
    public void Best_is_the_fastest_with_the_same_row_count_and_row_mismatch_is_excluded()
    {
        var v = BenchmarkRules.Judge([M(1842, 42180, 1208), M(912, 41960, 1208), M(64, 1440, 1208), M(58, 1402, 1203)]);
        Assert.Equal([BenchmarkVerdictKind.Baseline, BenchmarkVerdictKind.Faster, BenchmarkVerdictKind.Best, BenchmarkVerdictKind.RowMismatch], v.Select(x => x.Kind));
        Assert.Equal(1842d / 64d, v[2].Ratio!.Value, 3);
        Assert.Equal(-5, v[3].RowDelta);
    }

    [Fact]
    public void Ties_in_time_go_to_fewer_logical_reads_and_slower_variants_are_not_best()
    {
        var v = BenchmarkRules.Judge([M(100, 500, 1), M(50, 300, 1), M(50, 200, 1), M(200, 10, 1)]);
        Assert.Equal([BenchmarkVerdictKind.Baseline, BenchmarkVerdictKind.Faster, BenchmarkVerdictKind.Best, BenchmarkVerdictKind.Slower], v.Select(x => x.Kind));
        Assert.Equal(0.5, v[3].Ratio!.Value, 3);
    }

    [Fact]
    public void No_variant_beats_the_baseline_means_no_best_and_unmeasured_rows_stay_unmeasured()
    {
        var v = BenchmarkRules.Judge([M(10, 5, 3), M(20, 1, 3), M(null, 0, null)]);
        Assert.DoesNotContain(v, x => x.Kind == BenchmarkVerdictKind.Best);
        Assert.Equal(BenchmarkVerdictKind.NotMeasured, v[2].Kind);

        var noBase = BenchmarkRules.Judge([M(null, 0, null), M(5, 1, 1)]);
        Assert.Equal([BenchmarkVerdictKind.Baseline, BenchmarkVerdictKind.NotMeasured], noBase.Select(x => x.Kind));
        Assert.Empty(BenchmarkRules.Judge([]));
    }
}
