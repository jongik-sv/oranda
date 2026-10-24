using System.Text.RegularExpressions;
using Oranda.App.Features.Tuning;
using Oranda.App.Features.Tuning.Report;
using Oranda.App.Shell;
using Oranda.App.Shell.Menus;
using Oranda.App.Tests.Shell;
using Oranda.Core.Connection;
using Oranda.Core.Shell;
using Oranda.Core.Tuning;

namespace Oranda.App.Tests.Features.Tuning;

/// <summary>
/// TSK-06-07 SQL 튜닝 HTML 보고서: 생성기(절 순서·이스케이프·바인드 빼기·팩 제외·외부 자원 없음), 수집기(절별 실패·취소·SELECT 전용 서비스만),
/// 대화 상자 ViewModel(기본값·막힘 이유·만들기·취소·쓰기 실패), sql_id 셈, 명령 배치.
/// </summary>
public partial class TuningReportTests
{
    [GeneratedRegex("data-section=\"([A-Za-z]+)\"")]
    private static partial Regex SectionAttr();

    private static string Section(string html, TuningReportSection s)
    {
        var start = html.IndexOf("data-section=\"" + s + "\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "절 없음: " + s);
        var end = html.IndexOf("</details>", start, StringComparison.Ordinal);
        return html[start..end];
    }

    // ---- 생성기 ----

    [Fact]
    public async Task All_eleven_sections_appear_in_the_mockup_order_with_a_matching_table_of_contents()
    {
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync());

        var order = SectionAttr().Matches(html).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(TuningReportHtml.Order.Select(s => s.ToString()), order);
        for (var i = 1; i <= 11; i++)
        {
            Assert.Contains($"<a href=\"#s{i}\">", html, StringComparison.Ordinal);
            Assert.Contains($"id=\"s{i}\"", html, StringComparison.Ordinal);
        }

        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.Contains("<title>SQL 튜닝 보고서 7h35uxf5uhmm1</title>", html, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-color-scheme:dark)", html, StringComparison.Ordinal);
        Assert.Contains("@media print", html, StringComparison.Ordinal);
        Assert.Contains("<svg viewBox=", html, StringComparison.Ordinal);
        Assert.Contains("class=\"n-hot\"", html, StringComparison.Ordinal); // 가장 비싼 단계(Id 4) 주황
        Assert.Contains("<span class=\"chip danger\">운영</span>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_file_is_self_contained_without_scripts_or_external_resources()
    {
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync());

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("@import", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("url(", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("<[^>]*\\son[a-z]+\\s*=", html); // 이벤트 속성 없음
    }

    [Fact]
    public async Task Sql_text_bind_values_and_object_names_are_html_escaped()
    {
        const string evil = "SELECT '</pre><script>alert(\"x\")</script>' AS a, 'Tom & Jerry' b FROM dual WHERE c = :B1";
        var monitor = new FakeReportMonitor { Text = evil };
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync(monitor: monitor, options: ReportFixtures.Options(binds: true)));

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;/pre&gt;&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("Tom &amp; Jerry", html, StringComparison.Ordinal);
        Assert.Contains("EQP-&lt;01&gt;&amp;&quot;&#39;", html, StringComparison.Ordinal); // 바인드 값 < > & " '
        Assert.Contains("&lt;SPLIT&amp;&quot;x&quot;&gt;", html, StringComparison.Ordinal); // 히스토그램 끝점 값
        Assert.Equal("&lt;a href=&quot;x&quot;&gt; &amp; &#39;", TuningReportHtml.Esc("<a href=\"x\"> & '"));
        Assert.Equal(string.Empty, TuningReportHtml.Esc(null));
    }

    [Fact]
    public async Task Bind_values_are_left_out_unless_chosen_and_the_types_stay()
    {
        var off = TuningReportHtml.Build(await ReportFixtures.CollectAsync());
        var on = TuningReportHtml.Build(await ReportFixtures.CollectAsync(options: ReportFixtures.Options(binds: true)));

        Assert.DoesNotContain("4471029", off, StringComparison.Ordinal);
        Assert.DoesNotContain("EQP-&lt;01", off, StringComparison.Ordinal);
        Assert.Contains(":B1 VARCHAR2(32)", off, StringComparison.Ordinal);
        Assert.Contains("값은 만들 때 「바인드 값 포함」을 끄고 만들어 빠졌습니다", off, StringComparison.Ordinal);
        Assert.Contains("바인드 값 <b>포함 안 함</b>", off, StringComparison.Ordinal);
        Assert.Contains("4471029", on, StringComparison.Ordinal);
        Assert.Contains("캡처 안 됨", on, StringComparison.Ordinal); // :B4 는 캡처 값 없음
        Assert.Contains("바인드 값 <b>포함</b>", on, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bind_values_are_dropped_already_in_the_collected_data()
    {
        var data = await ReportFixtures.CollectAsync();

        Assert.All(data.SqlText.Value!.Binds, b => Assert.Null(b.Value));
        Assert.Equal(4, data.SqlText.Value.Binds.Count);
    }

    [Fact]
    public async Task Without_a_diagnostics_declaration_awr_is_not_queried_and_the_reason_is_written()
    {
        var tuning = new FakeReportTuning();
        var data = await ReportFixtures.CollectAsync(tuning);
        var html = TuningReportHtml.Build(data);

        Assert.DoesNotContain(tuning.Calls, c => c.StartsWith(nameof(ITuningService.GetAwrPlanHistoryAsync), StringComparison.Ordinal));
        Assert.DoesNotContain(tuning.Calls, c => c.StartsWith(nameof(ITuningService.GetAshWaitsAsync), StringComparison.Ordinal));
        Assert.Contains(tuning.Calls, c => c == nameof(ITuningService.SampleWaitsAsync));
        var awr = Section(html, TuningReportSection.Awr);
        Assert.Contains("포함 안 함: Diagnostics Pack 보유가 선언되지 않았습니다.", awr, StringComparison.Ordinal);
        Assert.Contains("접속 관리자에서 라이선스를 고친 뒤", awr, StringComparison.Ordinal);
        Assert.DoesNotContain(" open>", awr[..awr.IndexOf('>', StringComparison.Ordinal)], StringComparison.Ordinal); // 닫힌 채
        Assert.Contains("<li class=\"off\"><a href=\"#s9\">AWR 계획 이력 (포함 안 함)</a></li>", html, StringComparison.Ordinal);
        Assert.Contains("gv$session 표본 3 초 · 팩 없음", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_diagnostics_declared_awr_and_ash_are_used()
    {
        var tuning = new FakeReportTuning();
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync(tuning, ReportFixtures.Session(pack: LicensePack.Diagnostics)));

        Assert.Contains(nameof(ITuningService.GetAwrPlanHistoryAsync), tuning.Calls);
        Assert.Contains(nameof(ITuningService.GetAshWaitsAsync), tuning.Calls);
        Assert.DoesNotContain(nameof(ITuningService.SampleWaitsAsync), tuning.Calls);
        var awr = Section(html, TuningReportSection.Awr);
        Assert.Contains("<tr class=\"chg\">", awr, StringComparison.Ordinal); // plan_hash 가 바뀐 구간
        Assert.Contains("가장 빠른 plan_hash 3392001144", awr, StringComparison.Ordinal); // AWR 계획 두 개(GetAwrPlanAsync)
        Assert.Contains("가장 느린 plan_hash 1838229974", awr, StringComparison.Ordinal);
        Assert.Contains(nameof(ITuningService.GetAwrPlanAsync) + ":3392001144", tuning.Calls);
        Assert.Contains("비용 비중", awr, StringComparison.Ordinal);
        Assert.Contains("ASH 지난 1 시간", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Server_pack_access_lower_than_the_declaration_wins()
    {
        var session = ReportFixtures.Session(pack: LicensePack.Diagnostics);
        var lowered = new FakeServerSession(session.Profile, session.Server with { ManagementPackAccess = "NONE" });
        var tuning = new FakeReportTuning();

        await ReportFixtures.CollectAsync(tuning, lowered);

        Assert.Equal(LicensePack.None, TuningReportCollector.HeldPack(lowered));
        Assert.DoesNotContain(nameof(ITuningService.GetAwrPlanHistoryAsync), tuning.Calls);
    }

    [Fact]
    public async Task Unchosen_sections_stay_in_the_contents_as_not_included_and_are_not_collected()
    {
        var tuning = new FakeReportTuning();
        var only = new HashSet<TuningReportSection> { TuningReportSection.SqlText, TuningReportSection.Environment };
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync(tuning, options: ReportFixtures.Options(sections: only)));

        Assert.Equal(11, SectionAttr().Count(html));
        Assert.Contains("만들 때 이 절을 고르지 않았습니다.", Section(html, TuningReportSection.Plans), StringComparison.Ordinal);
        Assert.DoesNotContain(tuning.Calls, c => c.StartsWith(nameof(ITuningService.GetChildPlanAsync), StringComparison.Ordinal));
        Assert.DoesNotContain(nameof(ITuningService.SampleWaitsAsync), tuning.Calls);
        Assert.Contains(nameof(ITuningService.GetTuningEnvironmentAsync), tuning.Calls);
        Assert.Contains("optimizer_features_enable", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_cards_show_the_hottest_step_stale_statistics_and_diverging_children()
    {
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync());
        var summary = Section(html, TuningReportSection.Summary);

        Assert.Contains("가장 비싼 단계", summary, StringComparison.Ordinal);
        Assert.Contains("Id 4 <code>TABLE ACCESS FULL MES.LOT_HIST</code>", summary, StringComparison.Ordinal);
        Assert.Contains("오래된 통계", summary, StringComparison.Ordinal);
        Assert.Contains("52 일 전", summary, StringComparison.Ordinal);
        Assert.Contains("자식 커서 갈림", summary, StringComparison.Ordinal);
        Assert.Contains("예상 행 수 어긋남", summary, StringComparison.Ordinal);
        Assert.Contains("1,842", summary, StringComparison.Ordinal); // 실행당 경과 ms
        Assert.True(Regex.Count(summary, "<div class=\"card ") <= TuningReportHtml.MaxCards);
        Assert.Contains("<tr class=\"hot\"><td class=\"n dim\">4</td>", Section(html, TuningReportSection.Plans), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plans_take_the_top_four_children_and_index_candidates_come_from_the_shared_rules()
    {
        var tuning = new FakeReportTuning();
        var data = await ReportFixtures.CollectAsync(tuning);
        var html = TuningReportHtml.Build(data);

        Assert.Equal([0, 1, 2, 3], data.Plans.Value!.Select(p => p.ChildNumber));
        Assert.Contains("<h3>child 1 · plan_hash 3392001144 · 실행 2,940</h3>", Section(html, TuningReportSection.Plans), StringComparison.Ordinal);
        Assert.NotEmpty(data.Candidates.Value!.Indexes);
        Assert.Contains("MES.LOT_HIST_IXC1", Section(html, TuningReportSection.Candidates), StringComparison.Ordinal);
        Assert.Contains("NOT IN", Section(html, TuningReportSection.Candidates), StringComparison.Ordinal);
        Assert.Contains("후보 DDL·재작성 SQL 은 제안이며 실행되지 않았습니다.", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task English_reports_use_english_text()
    {
        var html = TuningReportHtml.Build(await ReportFixtures.CollectAsync(options: ReportFixtures.Options(english: true)));

        Assert.Contains("<html lang=\"en\">", html, StringComparison.Ordinal);
        Assert.Contains("SQL tuning report", html, StringComparison.Ordinal);
        Assert.Contains("Not included: Diagnostics Pack ownership is not declared.", html, StringComparison.Ordinal);
        Assert.Contains("Most expensive step", html, StringComparison.Ordinal);
        foreach (var ko in new[] { "요약 진단", "포함 안 함", "수집 실패", "실행 계획", "가장 비싼 단계", "만든 때", "목차" })
        {
            Assert.DoesNotContain(ko, html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Suggested_file_name_follows_the_mockup_pattern()
    {
        Assert.Equal("tuning_7h35uxf5uhmm1_20261024_1412.html", TuningReportHtml.SuggestedFileName("7h35uxf5uhmm1", new DateTimeOffset(2026, 10, 24, 14, 12, 8, TimeSpan.FromHours(9))));
        Assert.Equal("tuning_sql_20261024_1412.html", TuningReportHtml.SuggestedFileName("../", new DateTimeOffset(2026, 10, 24, 14, 12, 0, TimeSpan.Zero)));
    }

    // ---- 수집기 ----

    [Fact]
    public async Task One_failing_query_marks_only_its_section_and_the_rest_is_kept()
    {
        var tuning = new FakeReportTuning();
        tuning.FailOn.Add(nameof(ITuningService.GetSqlStatsAsync));
        tuning.FailOn.Add(nameof(ITuningService.GetTableStatsAsync) + ":LOT_HIST");
        tuning.FailOn.Add(nameof(ITuningService.GetTuningEnvironmentAsync));
        var data = await ReportFixtures.CollectAsync(tuning);
        var html = TuningReportHtml.Build(data);

        Assert.Equal("ORA-00942: table or view does not exist", data.ExecStats.Failure);
        Assert.Contains("수집 실패: ORA-00942: table or view does not exist", Section(html, TuningReportSection.ExecStats), StringComparison.Ordinal);
        Assert.Contains("수집 실패: ORA-00942", Section(html, TuningReportSection.Environment), StringComparison.Ordinal);
        var objects = data.Objects.Value!;
        Assert.NotNull(objects.Single(o => o.Table == "LOT_HIST").Failure);
        Assert.Null(objects.Single(o => o.Table == "LOT_MST").Failure);
        Assert.True(data.Plans.HasValue);
        Assert.True(data.Waits.HasValue);
        Assert.Contains("MES.LOT_HIST_IXC1", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_children_and_missing_text_fall_back_with_reasons()
    {
        var tuning = new FakeReportTuning { NoChildren = true };
        var monitor = new FakeReportMonitor { Fail = true };
        var options = ReportFixtures.Options() with { EditorText = "select 1 from dual" };
        var data = await ReportFixtures.CollectAsync(tuning, options: options, monitor: monitor);
        var html = TuningReportHtml.Build(data);

        Assert.True(data.SqlText.Value!.FromEditor);
        Assert.Contains("편집기 문장을 담았습니다", html, StringComparison.Ordinal);
        Assert.Contains("자식 커서가 없어 실제 계획을 받을 수 없습니다", data.Plans.Failure, StringComparison.Ordinal);
        Assert.Contains("계획을 받지 못해 관련 표를 알 수 없습니다", data.Objects.Failure, StringComparison.Ordinal);
        Assert.Contains("공유 풀에 이 sql_id 의 자식 커서가 없습니다.", Section(html, TuningReportSection.ChildCursors), StringComparison.Ordinal);

        var noText = await ReportFixtures.CollectAsync(new FakeReportTuning(), monitor: new FakeReportMonitor { Fail = true });
        Assert.Contains("SQL 을 공유 풀에서 찾을 수 없습니다", noText.SqlText.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Child_list_failure_still_tries_child_zero_and_reason_failure_keeps_children()
    {
        var tuning = new FakeReportTuning();
        tuning.FailOn.Add(nameof(ITuningService.GetChildCursorsAsync));
        var data = await ReportFixtures.CollectAsync(tuning);
        Assert.NotNull(data.Cursors.Failure);
        Assert.Equal([0], data.Plans.Value!.Select(p => p.ChildNumber));

        var t2 = new FakeReportTuning();
        t2.FailOn.Add(nameof(ITuningService.GetSharedCursorReasonsAsync));
        var d2 = await ReportFixtures.CollectAsync(t2);
        Assert.Equal(4, d2.Cursors.Value!.Children.Count);
        Assert.NotNull(d2.Cursors.Value.ReasonsFailure);
        Assert.Contains("불일치 사유 수집 실패", TuningReportHtml.Build(d2), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancelling_stops_collection_with_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        var tuning = new FakeReportTuning { CancelOn = nameof(ITuningService.SampleWaitsAsync), Cts = cts };
        var collector = ReportFixtures.Collector(tuning);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(ReportFixtures.Session(), ReportFixtures.Options(), null, cts.Token));
        Assert.DoesNotContain(nameof(ITuningService.GetTuningEnvironmentAsync), tuning.Calls);
    }

    [Fact]
    public async Task Progress_counts_sections_and_lists_finished_ones()
    {
        var reports = new List<TuningReportProgress>();
        await ReportFixtures.Collector(new FakeReportTuning()).CollectAsync(ReportFixtures.Session(), ReportFixtures.Options(), new ListProgress(reports), CancellationToken.None);

        Assert.All(reports, r => Assert.Equal(11, r.Total));
        Assert.Equal(11, reports[^1].Done);
        Assert.Contains("SQL 전문", reports[^1].Finished);
        Assert.Contains(reports, r => r.Current.StartsWith("대기 분해 표본 모으는 중… 1 / 3 초", StringComparison.Ordinal));
        Assert.True(reports.Zip(reports.Skip(1)).All(p => p.Second.Done >= p.First.Done));
    }

    [Fact]
    public void Plan_targets_are_unique_children_ordered_by_executions_capped_at_four()
    {
        var rows = Enumerable.Range(0, 6).Select(i => new ChildCursorRow(i, i % 2, false, false, true, i * 10, 1, 1) { InstanceId = 1 })
            .Append(new ChildCursorRow(5, 1, false, false, true, 5, 1, 1) { InstanceId = 2 }).ToList();

        Assert.Equal([5, 4, 3, 2], TuningReportCollector.PlanTargets(rows, null).Select(r => r.ChildNumber));
        Assert.Equal(50, TuningReportCollector.PlanTargets(rows, 5).Single().Executions);
        Assert.Empty(TuningReportCollector.PlanTargets(rows, 9));
    }

    [Fact]
    public void Awr_compare_pair_is_the_fastest_and_slowest_plan_hash_per_execution()
    {
        var t = new DateTime(2026, 10, 1);
        AwrPlanRow Row(long snap, long hash, long execs, long micros) => new(snap, t, hash, execs, micros, 1);

        Assert.Empty(TuningReportCollector.AwrComparePair([Row(1, 7, 10, 100)]));
        Assert.Empty(TuningReportCollector.AwrComparePair([]));
        Assert.Equal([5L, 9L], TuningReportCollector.AwrComparePair([Row(1, 9, 1, 900), Row(2, 5, 10, 100), Row(3, 7, 2, 600), Row(4, 9, 0, 0)]));
    }

    [Fact]
    public async Task Waits_use_the_shared_breakdown_with_sessions_and_class_colours()
    {
        var data = await ReportFixtures.CollectAsync();
        var html = Section(TuningReportHtml.Build(data), TuningReportSection.Waits);

        Assert.Equal(WaitBreakdown.OnCpu, data.Waits.Value!.Events[0].Event);
        Assert.Contains("<th class=\"n\">세션</th>", html, StringComparison.Ordinal);
        Assert.Contains("background:var(--i-red)", html, StringComparison.Ordinal); // Application
        Assert.Contains("background:var(--i-green)", html, StringComparison.Ordinal); // CPU
    }

    [Fact]
    public void Tables_come_from_table_access_steps_and_default_to_the_session_owner()
    {
        var plan = ReportFixtures.Plan() with { Steps = [.. ReportFixtures.Plan().Steps, new Oranda.Core.Plans.PlanStep(8, 2, 3, "TABLE ACCESS") { Options = "FULL", ObjectName = "LOCAL_T" }] };

        var tables = TuningReportCollector.TablesOf([plan, plan], "SCOTT");

        Assert.Equal(["MES.LOT_HIST", "MES.LOT_MST", "MES.EVT_SKIP", "SCOTT.LOCAL_T"], tables.Select(t => t.Owner + "." + t.Table));
    }

    // ---- 대화 상자 ViewModel ----

    [Fact]
    public void Dialog_defaults_bind_values_off_and_awr_off_without_a_pack()
    {
        var vm = new TuningReportDialogViewModel(ReportFixtures.Session(), new FakeReportTuning(), new FakeReportMonitor(), ReportFixtures.SqlId);

        Assert.False(vm.IncludeBindValues);
        Assert.False(vm.IncludeAwr);
        Assert.False(vm.AwrAvailable);
        Assert.True(vm.IsProduction);
        Assert.Contains("Diagnostics Pack 선언 없음", vm.AwrNote, StringComparison.Ordinal);
        Assert.Contains("30 초", vm.WaitNote, StringComparison.Ordinal);
        Assert.True(vm.OpenAfter);
        Assert.EndsWith(".html", vm.FilePath, StringComparison.Ordinal);
        Assert.Contains("tuning_7h35uxf5uhmm1_", vm.FilePath, StringComparison.Ordinal);
        Assert.True(vm.CanCreate);
        var options = vm.BuildOptions();
        Assert.Contains(TuningReportSection.Awr, options.Sections); // 이유를 적으려고 늘 담는다(서버로는 안 보낸다)
        Assert.False(options.IncludeBindValues);
        Assert.Matches("예상 \\d+ 초 · 질의 약 \\d+개", vm.EstimateText);
    }

    [Fact]
    public void Dialog_with_diagnostics_enables_awr_and_reads_ash()
    {
        var vm = new TuningReportDialogViewModel(ReportFixtures.Session(ConnectionEnvironment.Test, LicensePack.Diagnostics), new FakeReportTuning(), null, ReportFixtures.SqlId);

        Assert.True(vm.AwrAvailable);
        Assert.True(vm.IncludeAwr);
        Assert.False(vm.IsProduction);
        Assert.Contains("ASH", vm.WaitNote, StringComparison.Ordinal);
        vm.IncludeAwr = false;
        Assert.DoesNotContain(TuningReportSection.Awr, vm.BuildOptions().Sections);
    }

    [Fact]
    public void Dialog_blocks_create_with_a_reason()
    {
        var none = new TuningReportDialogViewModel();
        Assert.False(none.CanCreate);
        Assert.True(none.ShowNoSession);
        Assert.Contains("접속이 없습니다", none.CreateBlockedReason, StringComparison.Ordinal);

        var vm = new TuningReportDialogViewModel(ReportFixtures.Session(), new FakeReportTuning(), null, "nope");
        Assert.Contains("sql_id 가 아닙니다", vm.CreateBlockedReason, StringComparison.Ordinal);
        Assert.Equal(vm.CreateBlockedReason, vm.EstimateText);
        Assert.True(vm.EstimateIsWarning);
        vm.SqlId = ReportFixtures.SqlId;
        Assert.True(vm.CanCreate);
        vm.FilePath = "relative.html";
        Assert.Contains("저장 위치", vm.CreateBlockedReason, StringComparison.Ordinal);
        vm.FilePath = Path.Combine(Path.GetTempPath(), "x.html");
        foreach (var p in typeof(TuningReportDialogViewModel).GetProperties().Where(p => p.Name.StartsWith("Include", StringComparison.Ordinal) && p.PropertyType == typeof(bool)))
        {
            p.SetValue(vm, false);
        }

        Assert.Contains("담을 절을 하나 이상", vm.CreateBlockedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dialog_loads_child_choices_and_warns_when_the_cursor_is_gone()
    {
        var vm = new TuningReportDialogViewModel(ReportFixtures.Session(), new FakeReportTuning(), null, ReportFixtures.SqlId);
        await vm.LoadChildrenAsync();

        Assert.Equal(5, vm.ChildChoices.Count);
        Assert.Equal("전체 (4개 · plan_hash 2)", vm.ChildChoices[0].Label);
        Assert.StartsWith("child 0 · plan_hash 1838229974 · 실행 14,882", vm.ChildChoices[1].Label, StringComparison.Ordinal);
        Assert.False(vm.HasChildNotice);
        vm.SelectedChild = vm.ChildChoices[2];
        Assert.Equal(1, vm.BuildOptions().ChildNumber);

        var gone = new TuningReportDialogViewModel(ReportFixtures.Session(), new FakeReportTuning { NoChildren = true }, null, ReportFixtures.SqlId);
        await gone.LoadChildrenAsync();
        Assert.True(gone.HasChildNotice);
        Assert.Contains("공유 풀에 이 sql_id 의 커서가 없습니다", gone.ChildNotice, StringComparison.Ordinal);

        var failing = new FakeReportTuning();
        failing.FailOn.Add(nameof(ITuningService.GetChildCursorsAsync));
        var bad = new TuningReportDialogViewModel(ReportFixtures.Session(), failing, null, ReportFixtures.SqlId);
        await bad.LoadChildrenAsync();
        Assert.Equal("자식 커서를 읽지 못했습니다: ORA-00942: table or view does not exist", bad.ChildNotice);
    }

    [Fact]
    public async Task Dialog_create_writes_one_html_file_and_completes()
    {
        var written = new Dictionary<string, string>();
        var vm = Dialog(written);
        var completed = 0;
        vm.Completed += (_, _) => completed++;

        var path = await vm.CreateAsync();

        Assert.Equal(vm.FilePath, path);
        Assert.Equal(1, completed);
        var html = Assert.Single(written).Value;
        Assert.StartsWith("<!doctype html>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("4471029", html, StringComparison.Ordinal);
        Assert.False(vm.IsRunning);
        Assert.Null(vm.ErrorText);
        Assert.Equal(path, vm.ResultPath);
    }

    [Fact]
    public async Task Dialog_cancel_and_write_failure_leave_no_file_and_say_why()
    {
        var written = new Dictionary<string, string>();
        var vm = Dialog(written);
        vm.WriteFile = (_, _, _) => throw new IOException("디스크가 꽉 찼습니다");
        Assert.Null(await vm.CreateAsync());
        Assert.Equal("파일을 쓰지 못했습니다: 디스크가 꽉 찼습니다", vm.ErrorText);
        Assert.True(vm.HasError);
        Assert.True(vm.CanCreate); // 다시 해 볼 수 있다

        var blocking = new FakeReportTuning();
        var cancel = Dialog(written, blocking);
        cancel.CreateCollector = (t, m) => new TuningReportCollector(t, m)
        {
            Delay = async (_, ct) =>
            {
                cancel.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            },
        };
        Assert.Null(await cancel.CreateAsync());
        Assert.Equal("만들기를 취소했습니다. 파일은 만들지 않았습니다.", cancel.ErrorText);
        Assert.Empty(written);
    }

    [Fact]
    public void Picked_path_sticks_when_the_sql_id_changes()
    {
        var vm = new TuningReportDialogViewModel(ReportFixtures.Session(), new FakeReportTuning(), null, ReportFixtures.SqlId);
        var auto = vm.FilePath;
        vm.SqlId = "abcdabcdabcd1";
        Assert.NotEqual(auto, vm.FilePath);
        Assert.Contains("abcdabcdabcd1", vm.FilePath, StringComparison.Ordinal);

        var picked = Path.Combine(Path.GetTempPath(), "mine.html");
        vm.SetPickedPath(picked);
        vm.SetPickedPath(null);
        vm.SqlId = ReportFixtures.SqlId;
        Assert.Equal(picked, vm.FilePath);
    }

    private static TuningReportDialogViewModel Dialog(Dictionary<string, string> written, FakeReportTuning? tuning = null) =>
        new(ReportFixtures.Session(), tuning ?? new FakeReportTuning(), new FakeReportMonitor(), ReportFixtures.SqlId)
        {
            FilePath = Path.Combine(Path.GetTempPath(), "tn-report-test.html"),
            WaitSampleSeconds = 2,
            WriteFile = (p, html, _) =>
            {
                written[p] = html;
                return Task.CompletedTask;
            },
            CreateCollector = (t, m) => new TuningReportCollector(t, m) { Delay = (_, _) => Task.CompletedTask },
        };

    // ---- sql_id 셈 ----

    [Fact]
    public void Sql_id_hash_is_thirteen_base32_characters_and_depends_on_every_character()
    {
        var a = SqlIdHash.Compute("select * from dual");

        Assert.Matches("^[0-9a-z]{13}$", a);
        Assert.DoesNotMatch("[eilo]", a); // Oracle base32 에 없는 글자
        Assert.Equal(a, SqlIdHash.Compute("select * from dual"));
        Assert.NotEqual(a, SqlIdHash.Compute("select *  from dual"));
        Assert.NotEqual(a, SqlIdHash.Compute("SELECT * FROM DUAL"));
    }

    // ---- 명령 ----

    [Fact]
    public void Report_command_sits_at_the_end_of_the_assistant_group_with_both_titles()
    {
        var commands = ShellContributions.Scan(typeof(ShellContributions).Assembly).Commands.Select(c => c.Definition).ToList();
        var report = commands.Single(c => c.Id == TuningReportNavigation.CommandId);

        Assert.Contains("menu:tuning/report@490", report.Placements);
        Assert.Contains(TuningReportNavigation.CommandId, TuningCommands.Ids);
        Assert.Equal("튜닝 보고서 만들기…", new ShellText(language: "ko").Title(report));
        Assert.Equal("Create Tuning Report…", new ShellText(language: "en").Title(report));
        var tuning = MenuModelBuilder.BuildMenuBar(commands, new ShellText(language: "ko"), _ => null, [])[MenuModelBuilder.TopMenus.ToList().IndexOf("tuning")];
        Assert.Contains(tuning.Children, n => n.CommandId == TuningReportNavigation.CommandId);
    }

    private sealed class ListProgress(List<TuningReportProgress> list) : IProgress<TuningReportProgress>
    {
        public void Report(TuningReportProgress value) => list.Add(value);
    }
}

/// <summary>서버 정보를 바꿔 쓰는 가짜 세션(팩 서버 값 시험).</summary>
internal sealed class FakeServerSession(ConnectionProfile profile, ServerInfo server) : Oranda.Core.Sessions.ISession
{
    public Guid Id { get; } = Guid.NewGuid();

    public ConnectionProfile Profile { get; } = profile;

    public ServerInfo Server { get; } = server;

    public Oranda.Core.Sessions.SessionPurpose Purpose => Oranda.Core.Sessions.SessionPurpose.Monitor;

    public string DbKey => "X";

    public Oranda.Core.Sessions.SessionState State => Oranda.Core.Sessions.SessionState.Idle;

    public bool HasPendingTransaction => false;

    public Task CommitAsync(CancellationToken ct) => Task.CompletedTask;

    public Task RollbackAsync(CancellationToken ct) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
