using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Oranda.App.Features.MyBatisWorkspace.Combos;
using Oranda.App.Features.MyBatisWorkspace.Hooks;
using Oranda.App.Features.MyBatisWorkspace.Statements;
using Oranda.App.Features.MyBatisWorkspace.Workspace;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Shell;
using Oranda.App.Tests.Features.MyBatisWorkspace.Flow;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.App.Tests.Shell.Headless;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.MyBatisWorkspace.Perf;

/// <summary>
/// TSK-04-01 I3: 사용자 매퍼(쿼리 153개, 180KB)로 PRD 6 성능 수치를 시험 PC 에서 잰다. 파일이 없으면 아무것도 하지 않고 끝난다.
/// 열기→목록 표시(상한 1 s), 쿼리 탭 열기·펼친 SQL 분할 표시(상한 없음, 수치만), 바인드 값 바꾼 뒤 다시 계산(디바운스 제외, 상한 50 ms),
/// 조합 검사(구문 분석만, 조합 256개 3 s 를 문장당 시간으로 환산). 쿼리 본문·이름은 출력하지 않는다(개수·시간만).
/// 합성 매퍼 수치(1MB 열기·폴더 훑기·합성 256개)는 TSK-04-02 시험이 잰다.
/// </summary>
public sealed class MyBatisUserMapperPerfTests(ITestOutputHelper output)
{
    private const int OpenLimitMs = 1000;
    private const int RecomputeLimitMs = 50;
    private const int ComboLimitMs = 3000;
    private const int ComboTargetCount = 256;

    private static readonly bool Present = File.Exists(MyBatisFlowRig.UserMapperPath);

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted[sorted.Count / 2];
    }

    [Fact]
    [Trait("Category", "Perf")]
    public Task Opening_the_users_mapper_shows_the_statement_list_within_one_second() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        // 첫 실행의 JIT·파서 준비는 재지 않는다.
        var warm = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
        await warm.OpenWorkspaceFileAsync(MyBatisFlowRig.UserMapperPath, default);

        var times = new List<double>();
        for (var run = 0; run < 5; run++)
        {
            var host = new SqlEditorHost { MyBatisHooks = new MyBatisHooks(openHandler: new MyBatisOpenHandler()) };
            var vm = new MyBatisStatementsViewModel { Host = host, Text = new ShellText() };
            var window = new Window { Width = 340, Height = 560, Content = new MyBatisStatementsView { DataContext = vm } };
            window.Show();
            MyBatisFlowRig.Pump();

            var sw = Stopwatch.StartNew();
            await host.OpenWorkspaceFileAsync(MyBatisFlowRig.UserMapperPath, default);
            MyBatisFlowRig.Pump();
            window.UpdateLayout();
            sw.Stop();
            times.Add(sw.Elapsed.TotalMilliseconds);

            var session = (MyBatisWorkspaceSession)host.ActiveWorkspace!.MyBatis!;
            Assert.Equal(session.Entries.Count, vm.Groups.Sum(g => g.Children.Count));
            Assert.Equal(153, session.Entries.Count);
            window.Close();
        }

        output.WriteLine($"[perf] 사용자 매퍼(요소 153개) 열기→목록 표시: {string.Join(", ", times.Select(t => t.ToString("F0", CultureInfo.InvariantCulture)))} ms, 중앙값 {Median(times):F0} ms (상한 {OpenLimitMs} ms)");
        Assert.True(Median(times) <= OpenLimitMs, $"중앙값 {Median(times):F0} ms > {OpenLimitMs} ms");
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Opening_query_tabs_with_the_expanded_sql_split_and_recomputing_after_a_value_change() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml");
        var (_, session) = await rig.OpenAsync(path);
        var withParams = session.VisibleEntries
            .Where(e => e.IsStatement && e.CanOpen && e.Text.Contains("#{", StringComparison.Ordinal))
            .OrderBy(e => e.Text.Length).ToList();
        Assert.True(withParams.Count >= 10);

        // 첫 실행 준비는 재지 않는다: 가장 짧은 문장 탭을 한 번 열었다 닫는다.
        var warmTab = session.OpenEntry(withParams[0])!;
        var (warmView, warmWindow) = rig.ShowEditor(warmTab, session);
        await HeadlessHost.WaitUntil(() => warmView.Expanded!.Sql.Length > 0);
        warmWindow.Close();

        // 길이가 다른 문장 5개(짧은 쪽→긴 쪽)에서 탭 열기→펼친 SQL 이 분할에 보일 때까지, 그리고 값 고침 뒤 다시 계산
        var picks = new[] { 1, withParams.Count / 4, withParams.Count / 2, withParams.Count * 3 / 4, withParams.Count - 1 };
        var worstRecompute = 0.0;
        foreach (var index in picks)
        {
            var entry = withParams[index];
            var sw = Stopwatch.StartNew();
            var tab = session.OpenEntry(entry)!;
            var (view, window) = rig.ShowEditor(tab, session);
            var vm = view.Expanded!;
            await HeadlessHost.WaitUntil(() => vm.Sql.Length > 0 || vm.Eval?.Error is not null);
            MyBatisFlowRig.Pump();
            window.UpdateLayout();
            var openMs = sw.Elapsed.TotalMilliseconds;

            var eval = rig.Hub.ForTab(tab)!;
            rig.Hub.Refresh();
            Assert.NotEmpty(tab.Binds.Rows);
            var row = tab.Binds.Rows.First(r => !r.Name.StartsWith('$'));
            var samples = new List<double>();
            for (var i = 0; i < 30; i++)
            {
                row.Text = "V" + i.ToString(CultureInfo.InvariantCulture); // 디바운스는 재지 않고, 값 반영→다시 계산→펼친 SQL 갱신만 잰다
                var t = Stopwatch.StartNew();
                eval.Flush();
                Assert.Equal(eval.Result!.Sql, vm.Sql);
                samples.Add(t.Elapsed.TotalMilliseconds);
            }

            var avg = samples.Average();
            worstRecompute = Math.Max(worstRecompute, avg);
            output.WriteLine($"[perf] 문장 {index + 1}/{withParams.Count}번째(길이 순, {entry.Text.Length}자): 탭 열기→펼친 SQL {openMs:F0} ms / 다시 계산 최소 {samples.Min():F2} 평균 {avg:F2} 최대 {samples.Max():F2} ms (상한 {RecomputeLimitMs} ms)");
            window.Close();
        }

        Assert.True(worstRecompute <= RecomputeLimitMs, $"다시 계산 평균 최악 {worstRecompute:F2} ms > {RecomputeLimitMs} ms");
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Syntax_check_of_typical_statements_projects_to_256_combinations_within_three_seconds() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml");
        var (_, session) = await rig.OpenAsync(path);
        var conditional = session.VisibleEntries
            .Where(e => e.IsStatement && e.CanOpen && e.Text.Contains("<if", StringComparison.Ordinal))
            .OrderBy(e => e.Text.Length).ToList();
        Assert.True(conditional.Count >= 20);
        var evaluator = rig.Hub.Evaluator!;
        var language = rig.Get<Oranda.Core.Language.ILanguageService>();

        // 첫 실행 준비(ATN·JIT)는 재지 않는다: 가장 짧은 문장을 한 번 분석해 둔다.
        await language.AnalyzeAsync(AllOnSql(evaluator, session, conditional[0]), default);

        // 길이 순 40~60% 구간의 보통 문장 8개 = 대표값(상한 판정), 가장 긴 문장 3개 = 관찰(상한 판정 없음)
        var typical = conditional.Skip(conditional.Count * 2 / 5).Take(8).ToList();
        var longest = conditional.TakeLast(3).ToList();
        var typicalWarm = await MeasureAsync(language, evaluator, session, typical, "보통");
        var longWarm = await MeasureAsync(language, evaluator, session, longest, "가장 긴");

        var projected = typicalWarm * ComboTargetCount;
        output.WriteLine($"[perf] 조합 검사(구문 분석만) 환산: 보통 문장 {typical.Count}개 조합당 평균 {typicalWarm:F2} ms → 조합 {ComboTargetCount}개 {projected:F0} ms (상한 {ComboLimitMs} ms)");
        output.WriteLine($"[perf] 조합 검사(구문 분석만) 환산: 가장 긴 문장 {longest.Count}개 조합당 평균 {longWarm:F2} ms → 조합 {ComboTargetCount}개 {longWarm * ComboTargetCount:F0} ms (관찰만)");
        Assert.True(projected <= ComboLimitMs, $"보통 문장 조합 {ComboTargetCount}개 환산 {projected:F0} ms > {ComboLimitMs} ms");
    });

    /// <summary>
    /// TSK-03-11: 매퍼를 열면 백그라운드에서 그 문장 모양으로 파서를 데운다. 데우기가 끝난 뒤 조합 검사(구문 분석만)의 첫 결과까지 시간을 잰다.
    /// 문장 = 위 시험과 같은 보통 문장 8개·가장 긴 문장 3개. 목표 = 첫 결과 1.5 s 이하(보통·긴 문장 모두). 데우기 시간·검사 전체 시간은 관찰만.
    /// </summary>
    [Fact]
    [Trait("Category", "Perf")]
    public Task First_combo_check_result_after_the_mapper_warm_up_is_within_one_and_a_half_seconds() => Ui.Run(async () =>
    {
        if (!Present)
        {
            output.WriteLine("사용자 매퍼가 없어 건너뜀");
            return;
        }

        using var rig = MyBatisFlowRig.Create();
        var path = rig.CopyIn(MyBatisFlowRig.UserMapperPath!, "UserMapper.xml");
        var opened = Stopwatch.StartNew();
        var (_, session) = await rig.OpenAsync(path);
        var warm = Oranda.Language.MyBatis.MapperParseWarmUp.Last;
        int parsed = await warm.WaitAsync(TimeSpan.FromMinutes(5));
        output.WriteLine($"[perf] 매퍼 열기→파서 데우기 끝: {opened.ElapsedMilliseconds} ms (SQL 문장 {parsed}개 분석, 낮은 우선순위 백그라운드)");

        var conditional = session.VisibleEntries
            .Where(e => e.IsStatement && e.CanOpen && e.Text.Contains("<if", StringComparison.Ordinal))
            .OrderBy(e => e.Text.Length).ToList();
        var typical = conditional.Skip(conditional.Count * 2 / 5).Take(8).ToList();
        var longest = conditional.TakeLast(3).ToList();
        var runner = new ComboCheckRunner(rig.Hub.Evaluator!, rig.Get<Oranda.Core.Language.ILanguageService>());

        double worst = 0;
        foreach (var (label, entries) in new[] { ("보통", typical), ("가장 긴", longest) })
        {
            var firsts = new List<double>();
            foreach (var entry in entries)
            {
                var request = new ComboCheckRequest(entry.Id!, session.ElementOf(entry.Id!)!, session.Fragments, []);
                var first = new FirstReport();
                var sw = Stopwatch.StartNew();
                var report = await runner.RunAsync(request, first, default);
                var all = sw.Elapsed.TotalMilliseconds;
                firsts.Add(first.At!.Value.TotalMilliseconds);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[perf] 조합 검사 {label} 문장(원문 {entry.Text.Length}자, 조합 {report.Results.Count}개): 첫 결과 {first.At!.Value.TotalMilliseconds:F0} ms / 전체 {all:F0} ms"));
            }

            output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[perf] 조합 검사 {label} 문장 {entries.Count}개 첫 결과 최대 {firsts.Max():F0} ms (목표 {FirstResultTargetMs} ms)"));
            worst = Math.Max(worst, firsts.Max());
        }

        Assert.True(worst <= FirstResultTargetMs, $"조합 검사 첫 결과 최대 {worst:F0} ms > {FirstResultTargetMs} ms");
    });

    private const int FirstResultTargetMs = 1500;

    /// <summary>첫 결과가 온 때(검사를 부른 때부터).</summary>
    private sealed class FirstReport : IProgress<ComboCheckResult>
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public TimeSpan? At { get; private set; }

        public void Report(ComboCheckResult value) => At ??= _clock.Elapsed;
    }

    // 조건을 모두 켠 펼친 SQL.
    private static string AllOnSql(Oranda.Language.MyBatis.IMyBatisEvaluator evaluator, MyBatisWorkspaceSession session, MyBatisStatementEntry entry) =>
        evaluator.Evaluate(session.ElementOf(entry.Id!)!, session.Fragments, new Dictionary<string, object?>(), null, new Oranda.Language.MyBatis.MyBatisEvalOptions(AllOn: true)).Sql;

    /// <summary>
    /// 문장마다 (1) 처음 분석하는 글의 시간(ANTLR 예측 캐시가 차가운 상태, 관찰) (2) 글자만 다른 변형 20개의 분석 시간(캐시가 데워진 뒤, 조합 하나의 시간)을 재서
    /// 조합당 평균(2)을 돌려준다. 변형은 공백 수만 달라 문장 캐시에 걸리지 않으면서 토큰은 같다.
    /// </summary>
    private async Task<double> MeasureAsync(Oranda.Core.Language.ILanguageService language, Oranda.Language.MyBatis.IMyBatisEvaluator evaluator, MyBatisWorkspaceSession session, List<MyBatisStatementEntry> entries, string label)
    {
        var all = new List<double>();
        foreach (var entry in entries)
        {
            var sql = AllOnSql(evaluator, session, entry);
            var sw = Stopwatch.StartNew();
            await language.AnalyzeAsync(sql, default);
            var cold = sw.Elapsed.TotalMilliseconds;
            var warm = new List<double>();
            for (var k = 2; k < 22; k++)
            {
                var variant = sql.Replace(" ", new string(' ', k), StringComparison.Ordinal);
                sw.Restart();
                await language.AnalyzeAsync(variant, default);
                warm.Add(sw.Elapsed.TotalMilliseconds);
            }

            all.AddRange(warm);
            output.WriteLine($"[perf] 구문 분석 {label} 문장 SQL {sql.Length}자: 처음(차가운 캐시) {cold:F0} ms / 데워진 뒤 조합당 평균 {warm.Average():F2} 최대 {warm.Max():F2} ms");
        }

        return all.Average();
    }
}
