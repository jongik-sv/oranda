using System.Diagnostics;
using System.Text;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.Core.Execution;
using Oranda.Core.MyBatis;
using Oranda.Core.Plans;
using Oranda.Core.Sessions;
using Oranda.Language;
using Oranda.Language.MyBatis;
using Oranda.Testing;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;

namespace Oranda.App.Tests.Features.MyBatisWorkspace;

// 검사 실행기: 조합 수·줄이기, 오류 판정·원문 줄, 시나리오, 실행 계획 경로, 취소, 256개 시간.
public partial class ComboCheckTests
{
    /// <summary>시안 Combos 아트보드의 updateEmpSal(줄 3 SET, 줄 5 SAL = #{sal},).</summary>
    internal const string MockupUpdateEmpSal =
        "<update id=\"updateEmpSal\" parameterType=\"map\">\n" +
        "  UPDATE EMP\n" +
        "     SET\n" +
        "  <if test=\"sal != null\">\n" +
        "       SAL = #{sal},\n" +
        "  </if>\n" +
        "  <if test=\"comm != null\">\n" +
        "       COMM = #{comm}\n" +
        "  </if>\n" +
        "   WHERE EMPNO = #{empNo}\n" +
        "</update>";

    private static readonly MyBatisEvaluator Evaluator = new();
    private static readonly LanguageService Language = new();

    private static MyBatisElement Statement(string text, MyBatisElementKind kind = MyBatisElementKind.Update, string id = "updateEmpSal") =>
        new() { Kind = kind, Name = kind.ToString().ToLowerInvariant(), Id = id, Text = text };

    private static ComboCheckRequest Request(string text, IReadOnlyList<ComboScenarioInput>? scenarios = null, ComboCheckMode mode = ComboCheckMode.Syntax, ISession? session = null, MyBatisElementKind kind = MyBatisElementKind.Update) =>
        new("s", Statement(text, kind), NoFragments.Instance, scenarios ?? [], mode, session);

    private static Task<ComboCheckReport> RunAsync(ComboCheckRequest request, IExecutionPlanService? plans = null, IProgress<ComboCheckResult>? progress = null, CancellationToken ct = default) =>
        new ComboCheckRunner(Evaluator, Language, plans).RunAsync(request, progress, ct);

    // if n 개짜리 select(각 if 가 AND 조건 하나).
    private static string SelectWithIfs(int n)
    {
        var sb = new StringBuilder("<select id=\"s\">\n  SELECT * FROM EMP\n  <where>\n");
        for (int i = 0; i < n; i++)
        {
            sb.Append("    <if test=\"p").Append(i).Append(" != null\">AND C").Append(i).Append(" = #{p").Append(i).Append("}</if>\n");
        }

        return sb.Append("  </where>\n</select>").ToString();
    }

    [Fact]
    public async Task UpdateEmpSal_has_four_combinations_and_the_two_mockup_errors()
    {
        var report = await RunAsync(Request(MockupUpdateEmpSal));

        Assert.Equal(["sal != null", "comm != null"], report.Conditions.Select(c => c.Label));
        Assert.Equal([4, 7], report.Conditions.Select(c => c.Line));
        Assert.Equal(4, report.Results.Count);
        Assert.Equal(2, report.ErrorCount);
        Assert.False(report.Reduced);

        var comma = Assert.Single(report.Results, r => r.States.SequenceEqual([true, false]));
        Assert.Equal(ComboOutcome.Error, comma.Outcome);
        Assert.Equal("WHERE 앞 쉼표", comma.Reason);
        Assert.Equal(5, comma.SourceLine);
        Assert.Contains("(줄 5 의 SAL = #{sal},)", comma.Detail, StringComparison.Ordinal);
        Assert.Equal(",", comma.Sql.Substring(comma.ErrorRange!.Value.Start, comma.ErrorRange.Value.Length));

        var empty = Assert.Single(report.Results, r => r.States.SequenceEqual([false, false]));
        Assert.Equal("SET 뒤가 비어 있음", empty.Reason);
        Assert.Equal(3, empty.SourceLine);

        Assert.All(report.Results.Where(r => r.States.Contains(true) && !r.States.SequenceEqual([true, false])), r => Assert.Equal(ComboOutcome.Pass, r.Outcome));
    }

    [Fact]
    public async Task After_wrapping_with_set_all_four_combinations_pass_and_the_empty_set_is_a_warning()
    {
        var fix = Assert.Single(MyBatisAutoFix.Find(MockupUpdateEmpSal));
        var fixedText = MyBatisAutoFix.Apply(MockupUpdateEmpSal, fix);

        var report = await RunAsync(Request(fixedText));

        Assert.Equal(4, report.Results.Count);
        Assert.Equal(0, report.ErrorCount);
        Assert.Equal(4, report.PassCount);
        var empty = Assert.Single(report.Results, r => r.Outcome == ComboOutcome.Warning);
        Assert.True(empty.States.SequenceEqual([false, false]));
        Assert.Equal(ComboCheckRunner.EmptySetReason, empty.Reason);
    }

    [Theory]
    [InlineData(2, 4, false)]
    [InlineData(8, 256, false)]
    [InlineData(10, 12, true)]
    public async Task Combination_count_is_all_up_to_eight_conditions_then_reduced(int conditions, int expected, bool reduced)
    {
        var report = await RunAsync(Request(SelectWithIfs(conditions), kind: MyBatisElementKind.Select));

        Assert.Equal(expected, report.CombinationCount);
        Assert.Equal(expected, report.Results.Count);
        Assert.Equal(reduced, report.Reduced);
        Assert.Equal(0, report.ErrorCount);
    }

    // 2026-10-24 사용자 결정: foreach 는 켬·끔 조건이 아니라 '=' 의 #{x} 같은 바인드 자리. 모든 조합에서 항목 하나(IN (:id))로 펼친다.
    [Fact]
    public async Task Foreach_is_a_bind_slot_not_a_condition_and_real_syntax_errors_still_show()
    {
        const string text =
            "<select id=\"s\">\n  SELECT * FROM EMP WHERE DEPTNO IN\n" +
            "  <foreach collection=\"ids\" item=\"id\" open=\"(\" separator=\",\" close=\")\">#{id}</foreach>\n  AND SAL > 0\n</select>";

        var report = await RunAsync(Request(text, kind: MyBatisElementKind.Select));

        Assert.Empty(report.Conditions);
        var only = Assert.Single(report.Results);
        Assert.Equal(ComboOutcome.Pass, only.Outcome);
        Assert.Contains("(:id)", only.Sql.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);

        // 값 없는 foreach 가 빈자리로 숨기지 않는다: 다른 곳의 진짜 구문 오류는 오류로 센다.
        var broken = await RunAsync(Request(text.Replace("AND SAL > 0", "AND AND SAL > 0", StringComparison.Ordinal), kind: MyBatisElementKind.Select));
        Assert.Equal(ComboOutcome.Error, Assert.Single(broken.Results).Outcome);
    }

    [Fact]
    public void Saving_a_combination_as_a_scenario_gives_foreach_one_item_or_no_value_inside_an_off_condition()
    {
        const string text =
            "<select id=\"s\">\n  SELECT * FROM EMP WHERE 1 = 1\n" +
            "  <if test=\"ids != null\">AND DEPTNO IN <foreach collection=\"ids\" item=\"id\" open=\"(\" separator=\",\" close=\")\">#{id}</foreach></if>\n" +
            "  AND JOB IN <foreach collection=\"jobs\" item=\"j\" open=\"(\" separator=\",\" close=\")\">#{j}</foreach>\n</select>";
        var runner = new ComboCheckRunner(Evaluator, Language);
        var statement = Statement(text, MyBatisElementKind.Select);
        var conditions = runner.Plan(new ComboCheckRequest("s", statement, NoFragments.Instance, [])).Conditions;
        Assert.Single(conditions); // if 하나만(foreach 둘은 조건이 아니다)

        var off = runner.ScenarioValues(statement, NoFragments.Instance, conditions, [false], out bool offMatches);
        Assert.Equal(MyBatisScenarioValueKind.Null, off["ids"].Kind);
        Assert.Equal(["1"], off["jobs"].Items);
        Assert.True(offMatches);

        var on = runner.ScenarioValues(statement, NoFragments.Instance, conditions, [true], out bool onMatches);
        Assert.Equal(["1"], on["ids"].Items);
        Assert.True(onMatches);
    }

    [Fact]
    public async Task Where_errors_are_named_and_unevaluable_tests_are_not_errors()
    {
        const string text =
            "<select id=\"s\">\n  SELECT * FROM EMP\n   WHERE\n" +
            "  <if test=\"a != null\">AND A = #{a}</if>\n" +
            "  <if test=\"b.size() > 0 ? true : false\">AND B = #{b}</if>\n</select>";

        var report = await RunAsync(Request(text, kind: MyBatisElementKind.Select));

        Assert.Equal(4, report.Results.Count);
        Assert.Contains(report.Results, r => r.Reason == "WHERE 바로 뒤 AND");
        Assert.Contains(report.Results, r => r.Reason == "WHERE 뒤가 비어 있음" && r.SourceLine == 3);
        Assert.DoesNotContain(report.Results, r => r.Reason == "계산 오류");
    }

    [Fact]
    public async Task Scenario_values_are_checked_after_the_combinations()
    {
        var values = new Dictionary<string, object?> { ["sal"] = 100m, ["comm"] = null, ["empNo"] = 7369m };

        var report = await RunAsync(Request(MockupUpdateEmpSal, [new ComboScenarioInput("급여만", values)]));

        Assert.Equal(5, report.Results.Count);
        var scenario = report.Results[^1];
        Assert.True(scenario.IsScenario);
        Assert.Equal("급여만", scenario.Title);
        Assert.Equal(5, scenario.Number);
        Assert.Equal([true, false], scenario.States);
        Assert.Equal("WHERE 앞 쉼표", scenario.Reason);
    }

    [Fact]
    public void Saved_scenario_values_become_evaluator_values()
    {
        var scenario = new MyBatisScenario
        {
            MapperPath = "/m.xml",
            StatementId = "s",
            Name = "n",
            Values = new Dictionary<string, MyBatisScenarioValue>(StringComparer.Ordinal)
            {
                ["a"] = MyBatisScenarioValue.Null,
                ["b"] = new(MyBatisScenarioValueKind.Number, "1.5"),
                ["c"] = new(MyBatisScenarioValueKind.Boolean, "true"),
                ["d"] = new(MyBatisScenarioValueKind.List, Items: ["10", "20"], ItemKind: MyBatisScenarioValueKind.Number),
                ["e"] = new(MyBatisScenarioValueKind.Text, "x"),
            },
        };

        var values = ComboCheckRunner.ToValues(scenario);

        Assert.Null(values["a"]);
        Assert.Equal(1.5m, values["b"]);
        Assert.Equal(true, values["c"]);
        Assert.Equal([10m, 20m], Assert.IsType<List<object?>>(values["d"]));
        Assert.Equal("x", values["e"]);
    }

    [Fact]
    public async Task Plan_mode_explains_only_syntax_passes_through_the_plan_service_and_never_executes()
    {
        var plans = new RecordingPlans();

        var report = await RunAsync(Request(MockupUpdateEmpSal, mode: ComboCheckMode.SyntaxAndPlan, session: new FakeSession()), plans);

        Assert.Equal(ComboCheckMode.SyntaxAndPlan, report.Mode);
        Assert.Equal(2, plans.Explained.Count);
        Assert.All(plans.Explained, s =>
        {
            Assert.Equal(StatementKind.Dml, s.Kind);
            Assert.StartsWith("UPDATE EMP", s.Text.TrimStart(), StringComparison.Ordinal);
            Assert.Contains("empNo", s.BindNames, StringComparer.OrdinalIgnoreCase);
        });
        Assert.All(report.Results.Where(r => r.Outcome == ComboOutcome.Pass), r => Assert.StartsWith("실행 계획 확인", r.Detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Plan_denial_is_a_warning_and_server_error_is_an_error()
    {
        var denied = await RunAsync(Request(SelectWithIfs(1), mode: ComboCheckMode.SyntaxAndPlan, session: new FakeSession(), kind: MyBatisElementKind.Select),
            new RecordingPlans { Throw = new UnauthorizedAccessException("읽기 전용 프로필") });
        Assert.All(denied.Results, r => Assert.Equal("통과(실행 계획 거부)", r.Reason));
        Assert.Equal(0, denied.ErrorCount);

        var failed = await RunAsync(Request(SelectWithIfs(1), mode: ComboCheckMode.SyntaxAndPlan, session: new FakeSession(), kind: MyBatisElementKind.Select),
            new RecordingPlans { Throw = new InvalidOperationException("ORA-00942: table or view does not exist") });
        Assert.All(failed.Results, r => Assert.Equal("실행 계획 오류 ORA-00942", r.Reason));
        Assert.Equal(2, failed.ErrorCount);

        // 읽기 전용 DB 는 문장 탓이 아니다. 예상하지 못한 예외도 검사를 멈추지 않고 조합 하나의 오류로 남는다.
        var readOnlyDb = await RunAsync(Request(SelectWithIfs(1), mode: ComboCheckMode.SyntaxAndPlan, session: new FakeSession(), kind: MyBatisElementKind.Select),
            new RecordingPlans { Throw = new InvalidOperationException("ORA-01456: may not perform insert/delete/update operation inside a READ ONLY transaction") });
        Assert.All(readOnlyDb.Results, r => Assert.Equal("통과(실행 계획 거부)", r.Reason));
        Assert.Equal(0, readOnlyDb.ErrorCount);

        var unexpected = await RunAsync(Request(SelectWithIfs(1), mode: ComboCheckMode.SyntaxAndPlan, session: new FakeSession(), kind: MyBatisElementKind.Select),
            new RecordingPlans { Throw = new TimeoutException("접속 시간 초과") });
        Assert.All(unexpected.Results, r => Assert.Equal("실행 계획 오류", r.Reason));
        Assert.Equal(2, unexpected.ErrorCount);
    }

    [Fact]
    public async Task Include_fragments_are_resolved_on_the_calling_thread_only()
    {
        const string text = "<select id=\"s\">\n  SELECT <include refid=\"cols\"/> FROM EMP\n  <where><if test=\"a != null\">AND A = #{a}</if></where>\n</select>";
        var fragments = new ThreadRecordingFragments();
        var request = new ComboCheckRequest("s", Statement(text, MyBatisElementKind.Select), fragments, []);

        int caller = Environment.CurrentManagedThreadId;
        var report = await RunAsync(request);

        Assert.Equal(0, report.ErrorCount);
        Assert.All(report.Results, r => Assert.Contains("EMPNO, ENAME", r.Sql, StringComparison.Ordinal));
        Assert.NotEmpty(fragments.Threads);
        Assert.All(fragments.Threads, t => Assert.Equal(caller, t)); // 세션 조각은 UI 스레드 상태다
    }

    [Fact]
    public async Task Substitution_without_a_value_is_a_warning_not_an_error()
    {
        const string text = "<select id=\"s\">\n  SELECT * FROM EMP\n  <where><if test=\"a != null\">AND A = #{a}</if></where>\n  ORDER BY ${sort}\n</select>";

        var report = await RunAsync(Request(text, kind: MyBatisElementKind.Select));

        Assert.Equal(0, report.ErrorCount);
        Assert.All(report.Results, r => Assert.Equal("통과(주의: 치환 값 없음)", r.Reason));
        Assert.All(report.Results, r => Assert.Equal(4, r.SourceLine));
    }

    [Fact]
    public async Task Substitution_without_a_value_does_not_hide_real_errors()
    {
        // ${schema} 를 채워도 남는 오류(WHERE 앞 쉼표, SET 뒤 비어 있음)는 그대로 오류다.
        var text = MockupUpdateEmpSal.Replace("UPDATE EMP", "UPDATE ${schema}.EMP", StringComparison.Ordinal);

        var report = await RunAsync(Request(text));

        Assert.Equal(2, report.ErrorCount);
        var comma = Assert.Single(report.Results, r => r.States.SequenceEqual([true, false]));
        Assert.Equal("WHERE 앞 쉼표", comma.Reason);
        Assert.Equal(5, comma.SourceLine);
    }

    [Fact]
    public async Task Plan_mode_without_a_session_falls_back_to_syntax_with_a_notice()
    {
        var plans = new RecordingPlans();

        var report = await RunAsync(Request(MockupUpdateEmpSal, mode: ComboCheckMode.SyntaxAndPlan), plans);

        Assert.Equal(ComboCheckMode.Syntax, report.Mode);
        Assert.NotNull(report.Notice);
        Assert.Empty(plans.Explained);
    }

    [Fact]
    public async Task Cancelling_keeps_the_results_so_far()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(r =>
        {
            if (r.Number == 3)
            {
                cts.Cancel();
            }
        });

        var report = await RunAsync(Request(SelectWithIfs(8), kind: MyBatisElementKind.Select), progress: progress, ct: cts.Token);

        Assert.True(report.Cancelled);
        Assert.Equal(3, report.Results.Count);
        Assert.Equal(256, report.CombinationCount);
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task Two_hundred_fifty_six_combinations_are_checked_within_three_seconds()
    {
        // L2 첫 파싱의 워밍업(약 2 s)은 앱이 유휴 시간에 미리 치른다. 재기 전에 한 번 분석한다.
        await Language.AnalyzeAsync("SELECT 1 FROM dual", default);
        var request = Request(SelectWithIfs(8), kind: MyBatisElementKind.Select);

        var watch = Stopwatch.StartNew();
        var report = await RunAsync(request);
        watch.Stop();

        Assert.Equal(256, report.Results.Count);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"256 조합 {watch.ElapsedMilliseconds} ms");
    }

    private sealed class NoFragments : IMyBatisFragmentResolver
    {
        public static NoFragments Instance { get; } = new();

        public MyBatisFragment? Resolve(string refid, string? currentNamespace) => null;
    }

    // Progress<T> 는 동기화 문맥으로 넘기므로 시험은 부른 자리에서 바로 받는다.
    private sealed class ThreadRecordingFragments : IMyBatisFragmentResolver
    {
        public List<int> Threads { get; } = [];

        public MyBatisFragment? Resolve(string refid, string? currentNamespace)
        {
            lock (Threads)
            {
                Threads.Add(Environment.CurrentManagedThreadId);
            }

            return refid == "cols" ? new MyBatisFragment("cols", null, "<sql id=\"cols\">EMPNO, ENAME</sql>") : null;
        }
    }

    private sealed class SyncProgress(Action<ComboCheckResult> onReport) : IProgress<ComboCheckResult>
    {
        public void Report(ComboCheckResult value) => onReport(value);
    }

    private sealed class RecordingPlans : IExecutionPlanService
    {
        public List<SqlStatement> Explained { get; } = [];

        public Exception? Throw { get; init; }

        public Task<CorePlan> ExplainAsync(ISession s, SqlStatement stmt, CancellationToken ct)
        {
            lock (Explained)
            {
                Explained.Add(stmt);
            }

            return Throw is { } ex
                ? Task.FromException<CorePlan>(ex)
                : Task.FromResult(new CorePlan([new PlanStep(0, null, 0, "UPDATE STATEMENT")], "plan"));
        }

        public Task<CorePlan> GetCursorPlanAsync(ISession s, CursorRef cursor, string format, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
