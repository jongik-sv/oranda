using Oranda.App.Features.Tuning;
using Oranda.App.Features.Tuning.Assistant;
using Oranda.App.Shell;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Execution;
using Oranda.Core.Plans;
using CorePlan = Oranda.Core.Plans.ExecutionPlan;
using Oranda.Core.Sessions;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;
using Oranda.Language.Tuning;
using Oranda.Testing;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>TSK-06-05 튜닝 도우미: 명령 배치, ViewModel(후보·상태·벤치마크 거부·취소·행 수 불일치·운영 확인·문안 넣기).</summary>
public class TuningAssistantTests
{

    private static readonly string[] MyCommands = ["oranda.tuning.assistant", "oranda.tuning.benchmark", "oranda.tuning.indexCandidates", "oranda.tuning.rewriteCandidates"];
    internal const string Sql = """
        SELECT lh.lot_id, lh.evt_time, lm.prod_id
          FROM mes.lot_hist lh JOIN mes.lot_mst lm ON lm.lot_id = lh.lot_id
         WHERE lh.eqp_id = 'E01' AND TRUNC(lh.evt_time) = DATE '2026-10-01'
           AND lh.evt_cd NOT IN (SELECT s.evt_cd FROM mes.evt_skip s WHERE s.site = 'P1')
        """;

    internal static CorePlan Plan() => new(
        [
            new PlanStep(0, null, 0, "SELECT STATEMENT") { Cost = 4200, ActualTime = TimeSpan.FromMilliseconds(1842) },
            new PlanStep(1, 0, 1, "HASH JOIN") { Cost = 4200, AccessPredicates = "\"LM\".\"LOT_ID\"=\"LH\".\"LOT_ID\"", ActualTime = TimeSpan.FromMilliseconds(1840) },
            new PlanStep(2, 1, 2, "TABLE ACCESS") { Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_MST", Cost = 300, ActualTime = TimeSpan.FromMilliseconds(100) },
            new PlanStep(3, 1, 2, "TABLE ACCESS")
            {
                Options = "FULL", ObjectOwner = "MES", ObjectName = "LOT_HIST", Cost = 3800, ActualTime = TimeSpan.FromMilliseconds(1600),
                FilterPredicates = "(\"LH\".\"EQP_ID\"='E01' AND \"LH\".\"EVT_TIME\">=:B2 AND TO_NUMBER(\"LH\".\"LOT_NO\")=123)",
            },
        ],
        """
        Query Block Name / Object Alias (identified by operation id):
        -------------------------------------------------------------
           2 - SEL$1 / LM@SEL$1
           3 - SEL$1 / LH@SEL$1
        """)
    { PlanHashValue = 1838229974 };

    internal static SqlStatement Statement(string text = Sql) => new(text, new Oranda.Core.Text.TextRange(0, text.Length), StatementKind.Query) { Verb = "SELECT" };

    internal static FakeSession Session(ConnectionEnvironment env = ConnectionEnvironment.Test) =>
        new(new ConnectionProfile("MESQA", "MES", "h:1521/qa", Environment: env));

    internal static async Task<(TuningAssistantViewModel Vm, FakeTuning Tuning, List<string> Opened)> LoadedAsync(
        ISession? session = null, CorePlan? plan = null, string sql = Sql, bool noPlan = false, FakeTuning? tuning = null)
    {
        tuning ??= new FakeTuning();
        var opened = new List<string>();
        var vm = new TuningAssistantViewModel(tuning) { PostToUi = a => a(), OpenInNewEditor = opened.Add };
        await vm.LoadAsync(new TuningAssistantInput(Statement(sql), session ?? Session())
        {
            SourceTitle = "query3.sql",
            FirstLine = 5,
            Plan = noPlan ? null : plan ?? Plan(),
            PlanIsActual = true,
            PlanTime = new DateTime(2026, 10, 24, 14, 8, 0),
        });
        return (vm, tuning, opened);
    }

    // ---- 명령 배치 ----

    [Fact]
    public void Commands_sit_in_the_tuning_helper_group_and_the_editor_context_menu()
    {
        var commands = ShellTestHelpers.AppContributions.Commands.Select(c => c.Definition).ToDictionary(c => c.Id);
        Assert.Contains("menu:tuning/assistant@400", commands["oranda.tuning.assistant"].Placements);
        Assert.Contains("context:editor/tuningAssistant@1030", commands["oranda.tuning.assistant"].Placements);
        Assert.Contains("menu:tuning/benchmark@410", commands["oranda.tuning.benchmark"].Placements);
        Assert.Equal("tuningAssistantOpen", commands["oranda.tuning.benchmark"].Enablement);
        Assert.Equal("tuningAssistantOpen", commands["oranda.tuning.indexCandidates"].Enablement);
        Assert.Equal("tuningAssistantOpen", commands["oranda.tuning.rewriteCandidates"].Enablement);
        Assert.All(MyCommands,
            id => Assert.Contains(id, TuningCommands.Ids));
        var panel = ShellTestHelpers.RequirePanel(TuningCommands.AssistantPanelId);
        Assert.Equal(ToolPanelKind.Document, panel.Kind);
    }

    // ---- 후보 ----

    [Fact]
    public async Task Loading_a_statement_and_its_plan_fills_all_three_candidate_lists()
    {
        var (vm, tuning, _) = await LoadedAsync();
        Assert.Equal(["MES.LOT_HIST", "MES.LOT_MST"], tuning.IndexLookups.Order());
        Assert.Equal("LOT_HIST (EQP_ID, EVT_TIME)", vm.IndexItems[0].TableText);
        Assert.Equal("LOT_HIST_IX2 앞 열", vm.IndexItems[0].OverlapText);
        Assert.True(vm.IndexItems[0].IsOverlapWarning);
        Assert.Contains(vm.IndexItems, i => i.IsCovered && i.OverlapText == "이미 있음 (LOT_MST_PK)");
        Assert.Same(vm.IndexItems[0], vm.SelectedIndex);
        Assert.StartsWith("-- 후보 1 · 실행하면 DB 에 씁니다", vm.IndexDdl, StringComparison.Ordinal);
        Assert.Contains(vm.RewriteItems, r => r.Advice.Rule == RewriteRule.NotInToNotExists && !r.IsChecked); // NULL 의미 → 꺼 둠
        Assert.Contains(vm.RewriteItems, r => r.Advice.Rule == RewriteRule.FunctionOnColumn && r.IsChecked);
        Assert.Contains(vm.RewriteItems, r => r.Advice.Rule == RewriteRule.ImplicitConversion && !r.CanCheck);
        Assert.Contains(vm.HintItems, h => h.HintText == "/*+ INDEX(lh LOT_HIST_IX2) */" && h.IsChecked);
        Assert.Contains(vm.HintItems, h => h.IsDisabled && h.MeaningText == "병렬 — 설정에서 꺼짐");
        Assert.Equal("query3.sql · 줄 5–8 · 실제 계획 14:08", vm.SourceText);
        Assert.False(vm.HasNotice);
        Assert.True(vm.CanRunBenchmark);
        Assert.Equal(vm.IndexItems.Count, vm.IndexCount);
        Assert.StartsWith("인덱스 후보 3 · 재작성 후보", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_plan_only_rewrites_show_and_the_actual_plan_button_loads_one()
    {
        var plan = Plan();
        var (vm, _, _) = await LoadedAsync(noPlan: true);
        vm.FetchActualPlan = _ => Task.FromResult<CorePlan?>(plan);
        Assert.False(vm.HasPlan);
        Assert.True(vm.IndexEmpty);
        Assert.True(vm.HintEmpty);
        Assert.Equal("실제 계획이 있어야 후보를 고릅니다", vm.IndexEmptyText);
        Assert.True(vm.ShowPlanButtons);
        Assert.True(vm.CanFetchActualPlan);
        Assert.False(vm.CanFetchEstimatedPlan);
        Assert.Equal(1, vm.CandidateTab); // 볼 것이 있는 재작성 탭
        Assert.EndsWith("계획 없음", vm.SourceText, StringComparison.Ordinal);

        await vm.FetchActualPlanAsync();
        Assert.True(vm.HasPlan);
        Assert.False(vm.IndexEmpty);
        Assert.False(vm.ShowPlanButtons);
        Assert.Contains("실제 계획", vm.SourceText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Actual_plan_button_without_a_matching_cursor_explains_what_to_do()
    {
        var (vm, _, _) = await LoadedAsync(noPlan: true);
        vm.FetchActualPlan = _ => Task.FromResult<CorePlan?>(null);
        await vm.FetchActualPlanAsync();
        Assert.False(vm.HasPlan);
        Assert.StartsWith("받을 실제 계획이 없습니다", vm.StatusText, StringComparison.Ordinal);

        vm.FetchEstimatedPlan = _ => throw new InvalidOperationException("ORA-00942: table or view does not exist\nline 2");
        await vm.FetchEstimatedPlanAsync();
        Assert.True(vm.HasError);
        Assert.Equal("ORA-00942: table or view does not exist", vm.ErrorText);
        Assert.False(vm.IsAnalyzing);
    }

    [Fact]
    public async Task Without_a_session_a_notice_shows_and_the_benchmark_is_blocked()
    {
        var tuning = new FakeTuning();
        var vm = new TuningAssistantViewModel(tuning) { PostToUi = a => a() };
        await vm.LoadAsync(new TuningAssistantInput(Statement(), null) { Plan = null });
        Assert.True(vm.HasNotice);
        Assert.StartsWith("접속이 없습니다", vm.NoticeText, StringComparison.Ordinal);
        Assert.False(vm.CanRunBenchmark);
        Assert.Equal("접속이 없습니다", vm.BenchmarkTip);
        Assert.NotEmpty(vm.RewriteItems);
        await vm.RunBenchmarkAsync();
        Assert.Equal(0, tuning.BenchmarkCalls);
    }

    [Fact]
    public async Task No_statement_shows_the_empty_screen()
    {
        var vm = new TuningAssistantViewModel(new FakeTuning()) { PostToUi = a => a() };
        await vm.LoadAsync(new TuningAssistantInput(null, Session()));
        Assert.True(vm.ShowNoStatement);
        Assert.False(vm.ShowCandidates);
        Assert.Empty(vm.RewriteItems);
        Assert.Equal("문장이 없습니다", vm.BenchmarkBlockedReason);
    }

    [Fact]
    public async Task Index_lookup_not_supported_marks_overlap_unknown()
    {
        var (vm, _, _) = await LoadedAsync(tuning: new FakeTuning { IndexColumnsNotSupported = true });
        Assert.All(vm.IndexItems, i => Assert.True(i.IsUnknown));
        Assert.All(vm.IndexItems, i => Assert.Equal("확인 못함", i.OverlapText));
    }

    [Fact]
    public async Task Parallel_hints_turn_on_by_option_and_keep_other_checks()
    {
        var (vm, _, _) = await LoadedAsync();
        var nl = vm.HintItems.First(h => h.HintText.Contains("USE_NL", StringComparison.Ordinal));
        nl.IsChecked = true;
        vm.AllowParallelHints = true;
        var parallel = Assert.Single(vm.HintItems, h => h.HintText.Contains("PARALLEL", StringComparison.Ordinal));
        Assert.True(parallel.CanCheck);
        Assert.True(vm.HintItems.First(h => h.HintText == nl.HintText).IsChecked);
    }

    // ---- 문안 넣기(실행하지 않음) ----

    [Fact]
    public async Task Ddl_and_rewrites_go_to_a_new_editor_tab_and_are_never_executed()
    {
        var (vm, tuning, opened) = await LoadedAsync();
        Assert.True(vm.InsertIndexDdl());
        Assert.Contains("실행하면 DB 에 씁니다", opened[0], StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX MES.LOT_HIST_IXC1", opened[0], StringComparison.Ordinal);

        vm.SelectedIndex = vm.IndexItems.First(i => i.IsCovered);
        Assert.False(vm.CanInsertIndexDdl);
        Assert.False(vm.InsertIndexDdl());

        vm.SelectedRewrite = vm.RewriteItems.First(r => r.Advice.Rule == RewriteRule.NotInToNotExists);
        Assert.True(vm.HasRewriteCaution);
        Assert.True(vm.InsertRewrite());
        Assert.StartsWith("-- 튜닝 도우미 재작성 후보: NOT IN → NOT EXISTS", opened[1], StringComparison.Ordinal);
        Assert.Contains("NOT EXISTS (SELECT 1", opened[1], StringComparison.Ordinal);

        vm.SelectedRewrite = vm.RewriteItems.First(r => r.Advice.Rule == RewriteRule.ImplicitConversion);
        Assert.False(vm.CanInsertRewrite);
        Assert.False(vm.InsertRewrite());
        Assert.Equal(2, opened.Count);
        Assert.Equal(0, tuning.BenchmarkCalls); // 넣기는 DB 로 아무것도 보내지 않는다
    }

    [Fact]
    public async Task Diff_with_the_original_lists_removed_and_added_lines()
    {
        var (vm, _, _) = await LoadedAsync();
        vm.SelectedRewrite = vm.RewriteItems.First(r => r.Advice.Rule == RewriteRule.FunctionOnColumn);
        Assert.True(vm.ShowRewriteCode);
        vm.ShowRewriteDiff = true;
        Assert.True(vm.ShowRewriteDiffList);
        Assert.False(vm.ShowRewriteCode);
        Assert.Contains(vm.RewriteDiff, d => d.IsRemoved && d.Text.Contains("TRUNC(lh.evt_time)", StringComparison.Ordinal));
        Assert.Contains(vm.RewriteDiff, d => d.IsAdded && d.Text.Contains("lh.evt_time >= TRUNC(", StringComparison.Ordinal));
        vm.SelectedRewrite = vm.RewriteItems[0];
        Assert.False(vm.ShowRewriteDiff); // 다른 후보를 고르면 문안 보기로 돌아간다
    }

    // ---- 벤치마크 ----

    [Theory]
    [InlineData("UPDATE mes.lot_hist SET evt_cd = 'X' WHERE eqp_id = 'E01'", "SELECT 문만 벤치마크합니다(이 문장: UPDATE).")]
    [InlineData("SELECT * FROM mes.lot_hist WHERE eqp_id = :eqp", "변수 :eqp 가 있습니다. 값을 써 넣은 사본으로 벤치마크하세요.")]
    [InlineData("SELECT * FROM mes.lot_hist FOR UPDATE", "FOR UPDATE 는 행을 잠가서 벤치마크하지 않습니다.")]
    public async Task Non_select_statements_are_refused_before_anything_runs(string sql, string reason)
    {
        var (vm, tuning, _) = await LoadedAsync(sql: sql);
        Assert.False(vm.CanRunBenchmark);
        Assert.Equal(reason, vm.BenchmarkTip);
        await vm.RunBenchmarkAsync();
        Assert.Equal(0, tuning.BenchmarkCalls);
        Assert.Empty(vm.BenchmarkRows);
        Assert.Equal("벤치마크하지 않습니다: " + reason, vm.StatusText);
    }

    [Fact]
    public async Task Benchmark_runs_each_case_n_times_and_flags_row_mismatch_and_the_best()
    {
        var tuning = new FakeTuning();
        var (vm, _, _) = await LoadedAsync(tuning: tuning);
        vm.Iterations = 3;
        // 원문 1,842 ms·1,208 행 / TRUNC 제거 58 ms·1,203 행(다름) / INDEX 힌트 64 ms·1,208 행
        tuning.Result = c => c.Sql.Contains("INDEX(", StringComparison.Ordinal) ? (64, 1440, 12, 1208, 3392001144)
            : c.Sql.Contains(">= TRUNC(", StringComparison.Ordinal) ? (58, 1402, 10, 1203, 3392001144)
            : (1842, 42180, 31022, 1208, 1838229974);
        await vm.RunBenchmarkAsync();
        var rows = vm.BenchmarkRows;
        Assert.Equal(["원문", "TRUNC 제거", "INDEX(lh LOT_HIST_IX2)"], rows.Select(r => r.Label));
        Assert.Equal(rows.Count * 3, tuning.BenchmarkCalls);
        Assert.All(tuning.Iterations, n => Assert.Equal(1, n)); // 한 번씩 불러 최소·최대를 안다
        Assert.Equal("기준", rows[0].VerdictText);
        Assert.Equal("1,842 ms", rows[0].ElapsedText);
        Assert.Equal("42,180", rows[0].LogicalText);
        Assert.Equal("1838229974", rows[0].PlanHashText);
        Assert.True(rows[1].IsRowsMismatch);
        Assert.Equal("행 수 다름 (−5)", rows[1].VerdictText);
        Assert.False(rows[1].IsBest);
        Assert.True(rows[2].IsBest);
        Assert.Equal("가장 나음 · 28.8 배", rows[2].VerdictText);
        Assert.Equal(BenchmarkRowItem.BarMax, rows[0].BarWidth);
        Assert.True(rows[2].BarWidth < 10);
        Assert.Contains("가장 나음 = 변형 3", vm.StatusText, StringComparison.Ordinal);
        Assert.Contains("행 수가 다른 변형 2 은 판정에서 뺌", vm.StatusText, StringComparison.Ordinal);
        Assert.False(vm.IsBenchmarking);
        Assert.NotEmpty(vm.BenchmarkFinishedText);
    }

    [Fact]
    public async Task Min_and_max_come_from_individual_runs()
    {
        var tuning = new FakeTuning();
        var (vm, _, _) = await LoadedAsync(tuning: tuning);
        foreach (var r in vm.RewriteItems)
        {
            r.IsChecked = false;
        }

        foreach (var h in vm.HintItems)
        {
            h.IsChecked = false;
        }

        var times = new Queue<double>([100, 300, 200]);
        tuning.Result = _ => (times.Dequeue(), 10, 1, 5, null);
        await vm.RunBenchmarkAsync();
        var row = Assert.Single(vm.BenchmarkRows);
        Assert.Equal("200 ms", row.ElapsedText);
        Assert.Equal("100 / 300", row.MinMaxText);
        Assert.Equal(string.Empty, row.PlanHashText);
        Assert.Contains("원문보다 나은 변형이 없습니다", vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancel_stops_the_running_case_and_marks_the_rest_cancelled()
    {
        var tuning = new FakeTuning { BlockOnCall = 2 };
        var (vm, _, _) = await LoadedAsync(tuning: tuning);
        var run = vm.RunBenchmarkAsync();
        await tuning.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsBenchmarking);
        Assert.False(vm.CanRunBenchmark);
        Assert.StartsWith("변형 1/", vm.BenchmarkProgress, StringComparison.Ordinal);
        vm.CancelBenchmark();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsBenchmarking);
        Assert.Equal(BenchmarkRowState.Cancelled, vm.BenchmarkRows[0].State);
        Assert.All(vm.BenchmarkRows.Skip(1), r => Assert.Equal("취소됨", r.VerdictText));
        Assert.Contains("중간에 멈춤", vm.StatusText, StringComparison.Ordinal);
        Assert.Equal(string.Empty, vm.BenchmarkProgress);
        Assert.True(vm.CanRunBenchmark);
    }

    [Fact]
    public async Task A_failing_variant_is_marked_and_the_rest_still_run()
    {
        var tuning = new FakeTuning();
        var (vm, _, _) = await LoadedAsync(tuning: tuning);
        tuning.Fail = c => c.Sql.Contains(">= TRUNC(", StringComparison.Ordinal) ? new InvalidOperationException("ORA-01843: not a valid month\nmore") : null;
        await vm.RunBenchmarkAsync();
        var failed = Assert.Single(vm.BenchmarkRows, r => r.IsFailed);
        Assert.Equal("실패: ORA-01843: not a valid month", failed.VerdictText);
        Assert.All(vm.BenchmarkRows.Where(r => r != failed), r => Assert.Equal(BenchmarkRowState.Done, r.State));
    }

    [Fact]
    public async Task Not_supported_service_stops_the_benchmark_with_a_reason()
    {
        var tuning = new FakeTuning { BenchmarkNotSupported = true };
        var (vm, _, _) = await LoadedAsync(tuning: tuning);
        await vm.RunBenchmarkAsync();
        Assert.Equal("이 접속에서는 벤치마크를 지원하지 않습니다", vm.BenchmarkRows[0].VerdictText);
        Assert.Equal(1, tuning.BenchmarkCalls);
    }

    [Fact]
    public async Task Production_asks_first_and_declining_runs_nothing()
    {
        var tuning = new FakeTuning();
        var (vm, _, _) = await LoadedAsync(session: Session(ConnectionEnvironment.Production), tuning: tuning);
        string? asked = null;
        vm.Confirm = (q, _) =>
        {
            asked = q;
            return Task.FromResult(false);
        };
        await vm.RunBenchmarkAsync();
        Assert.StartsWith("운영 접속(MESQA)에서 SELECT 3개를 3회씩 실행합니다", asked, StringComparison.Ordinal);
        Assert.Equal(0, tuning.BenchmarkCalls);
        Assert.Equal("운영 접속 벤치마크를 취소했습니다.", vm.StatusText);

        vm.Confirm = (_, _) => Task.FromResult(true);
        await vm.RunBenchmarkAsync();
        Assert.Equal(9, tuning.BenchmarkCalls);

        var noDialog = (await LoadedAsync(session: Session(ConnectionEnvironment.Production), tuning: new FakeTuning())).Vm;
        await noDialog.RunBenchmarkAsync(); // 확인 창이 없으면 운영에서 돌리지 않는다
        Assert.Empty(noDialog.BenchmarkRows);
    }

    [Fact]
    public async Task Iterations_are_clamped_and_the_caption_follows()
    {
        var (vm, _, _) = await LoadedAsync();
        vm.Iterations = 0;
        Assert.Equal(1, vm.Iterations);
        vm.Iterations = 99;
        Assert.Equal(20, vm.Iterations);
        Assert.StartsWith("20 회 평균", vm.BenchmarkCaption, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Closing_the_document_cancels_and_clears_the_menu_key()
    {
        var keys = new ContextKeyService();
        var (vm, _, _) = await LoadedAsync();
        vm.Keys = keys;
        await vm.ReanalyzeAsync();
        Assert.True(keys.Get("tuningAssistantOpen"));
        vm.Closed();
        Assert.False(keys.Get("tuningAssistantOpen"));
    }

    /// <summary>가짜 튜닝 서비스: 인덱스 열 구성과 벤치마크만.</summary>
    internal sealed class FakeTuning : ITuningService
    {
        private int _calls;

        public List<string> IndexLookups { get; } = [];

        public List<int> Iterations { get; } = [];

        public int BenchmarkCalls => _calls;

        public bool IndexColumnsNotSupported { get; set; }

        public bool BenchmarkNotSupported { get; set; }

        /// <summary>이 번째 호출(1 부터)에서 취소될 때까지 멈춘다.</summary>
        public int BlockOnCall { get; set; }

        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<BenchmarkCase, (double Ms, long Logical, long Physical, long Rows, long? Hash)> Result { get; set; } = _ => (10, 100, 1, 7, 42);

        public Func<BenchmarkCase, Exception?> Fail { get; set; } = _ => null;

        public Task<IReadOnlyList<IndexColumnsRow>> GetIndexColumnsAsync(ISession s, string owner, string table, CancellationToken ct)
        {
            if (IndexColumnsNotSupported)
            {
                throw new NotSupportedException();
            }

            IndexLookups.Add(owner + "." + table);
            IReadOnlyList<IndexColumnsRow> rows = table switch
            {
                "LOT_HIST" => [new IndexColumnsRow("LOT_HIST_IX1", ["LOT_ID", "EVT_TIME"]), new IndexColumnsRow("LOT_HIST_IX2", ["EQP_ID"])],
                "LOT_MST" => [new IndexColumnsRow("LOT_MST_PK", ["LOT_ID"])],
                _ => [],
            };
            return Task.FromResult(rows);
        }

        public async Task<BenchmarkResult> RunBenchmarkAsync(ISession s, BenchmarkCase benchmark, int iterations, CancellationToken ct)
        {
            var n = Interlocked.Increment(ref _calls);
            Iterations.Add(iterations);
            if (BenchmarkNotSupported)
            {
                throw new NotSupportedException();
            }

            if (n == BlockOnCall)
            {
                Blocked.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (Fail(benchmark) is { } ex)
            {
                throw ex;
            }

            var (ms, logical, physical, rows, hash) = Result(benchmark);
            return new BenchmarkResult(benchmark, iterations, TimeSpan.FromMilliseconds(ms), logical, physical, rows, hash);
        }
    }
}
