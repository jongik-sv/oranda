using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Oranda.App.Features.Diagnostics;
using Oranda.App.Features.SqlEditor;
using Oranda.App.Tests.Features.SqlEditor;
using Oranda.Core.Language;
using Oranda.Language;
using Xunit.Abstractions;

namespace Oranda.App.Tests.Features.Diagnostics;

/// <summary>
/// TSK-04-09: 디바운스가 마지막 키에서 <see cref="EditorDiagnostics.Debounce"/> 가 지난 뒤에 분석을 시작하는지,
/// 1만 줄 문서의 반영 시간(헤드리스, 값만 출력. 판정은 tests/Oranda.Perf.Ui diagnostics).
/// </summary>
public sealed class DiagnosticsLatencyTests(ITestOutputHelper output)
{
    private static readonly ILanguageService Language = new LanguageService();

    [Fact]
    public Task Analysis_starts_once_the_debounce_has_passed_since_the_last_key() => Ui.Run(async () =>
    {
        var (window, view, vm) = Show("SELECT 1 FROM dual");
        var probe = new ProbeLanguage(Language);
        var diagnostics = EditorDiagnostics.Attach(view, () => probe);
        await WaitUntil(() => diagnostics.AppliedAnalyses >= 1 && !diagnostics.IsPending);
        int calls = probe.Calls;

        // 디바운스보다 짧은 간격으로 다섯 번 입력한다.
        var gap = EditorDiagnostics.Debounce / 5;
        long lastKey = 0;
        var longestGap = TimeSpan.Zero;
        for (var i = 0; i < 5; i++)
        {
            if (lastKey != 0)
            {
                longestGap = TimeSpan.FromTicks(Math.Max(longestGap.Ticks, Stopwatch.GetElapsedTime(lastKey).Ticks));
            }

            lastKey = Stopwatch.GetTimestamp();
            vm.Document.Insert(vm.Document.TextLength, " ");
            await Pump(gap);
        }

        await WaitUntil(() => diagnostics.AppliedAnalyses >= 2 && !diagnostics.IsPending);

        var waited = Stopwatch.GetElapsedTime(lastKey, probe.LastCallAt);
        output.WriteLine($"마지막 키 → 분석 시작 {waited.TotalMilliseconds:F1} ms (디바운스 {EditorDiagnostics.Debounce.TotalMilliseconds:F0} ms), 키 사이 최대 {longestGap.TotalMilliseconds:F0} ms");
        Assert.True(waited >= EditorDiagnostics.Debounce, $"디바운스 전에 분석을 시작했다: {waited.TotalMilliseconds:F1} ms");
        if (longestGap < EditorDiagnostics.Debounce)
        {
            // 기계가 바빠 키 사이가 디바운스를 넘지 않았다면 입력 묶음 전체에 분석은 한 번이다.
            Assert.Equal(calls + 1, probe.Calls);
        }

        window.Close();
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Measure_10k_line_document_without_asserting() => Ui.Run(async () =>
    {
        // tests/Oranda.Perf.Ui DiagnosticsScenario 와 같은 문서·편집·횟수를 헤드리스 창에서 잰다(화면에 창을 띄우지 않는다).
        const int Warmup = 3, Samples = 15;
        const string Bad = "select (1 from dual;\n";
        var (window, view, vm) = Show(TenThousandLineDocument());
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        var cold = Stopwatch.StartNew();
        await WaitUntil(() => diagnostics.AppliedAnalyses >= 1 && !diagnostics.IsPending, 120_000);
        var coldMs = cold.Elapsed.TotalMilliseconds;

        var toggle = false;
        void Edit()
        {
            toggle = !toggle;
            int end = vm.Document.TextLength;
            if (toggle)
            {
                vm.Document.Insert(end, Bad);
            }
            else
            {
                vm.Document.Remove(end - Bad.Length, Bad.Length);
            }
        }

        var fromLastKey = new List<double>();
        for (var i = 0; i < Warmup + Samples; i++)
        {
            await Pump(TimeSpan.FromMilliseconds(400));
            int applied = diagnostics.AppliedAnalyses;
            long key = Stopwatch.GetTimestamp();
            Edit();
            while (diagnostics.AppliedAnalyses == applied)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(1);
                Assert.True(Stopwatch.GetElapsedTime(key).TotalSeconds < 60, "분석이 반영되지 않았다");
            }

            if (i >= Warmup)
            {
                fromLastKey.Add(Stopwatch.GetElapsedTime(key).TotalMilliseconds);
            }
        }

        var analyzeNow = new List<double>();
        for (var i = 0; i < Warmup + Samples; i++)
        {
            await Pump(TimeSpan.FromMilliseconds(400));
            Edit();
            long t0 = Stopwatch.GetTimestamp();
            await diagnostics.AnalyzeNowAsync();
            if (i >= Warmup)
            {
                analyzeNow.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
            }
        }

        output.WriteLine($"1만 줄 문법 검사(헤드리스): 디바운스 뒤 → 반영 {Summary(analyzeNow)}");
        output.WriteLine($"1만 줄 문법 검사(헤드리스): 마지막 키 → 반영(디바운스 {EditorDiagnostics.Debounce.TotalMilliseconds:F0} ms 포함) {Summary(fromLastKey)}");
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"1만 줄 문법 검사(헤드리스): 첫 전체 분석(디바운스 포함) {coldMs:F0} ms"));
        output.WriteLine("RAW analyzeNow " + string.Join(' ', analyzeNow.Select(v => v.ToString("F1", CultureInfo.InvariantCulture))));
        output.WriteLine("RAW lastKey " + string.Join(' ', fromLastKey.Select(v => v.ToString("F1", CultureInfo.InvariantCulture))));
        window.Close();
    });

    [Fact]
    [Trait("Category", "Perf")]
    public Task Typing_in_a_10k_line_document_never_blocks_the_ui_thread_for_long() => Ui.Run(async () =>
    {
        // 디바운스(100 ms)보다 짧은 간격으로 계속 입력하며 UI 스레드가 한 번에 묶이는 가장 긴 시간을 잰다(분석은 백그라운드여야 한다).
        const int Keys = 60;
        var (window, view, vm) = Show(TenThousandLineDocument());
        var diagnostics = EditorDiagnostics.Attach(view, () => Language);
        await WaitUntil(() => diagnostics.AppliedAnalyses >= 1 && !diagnostics.IsPending, 120_000);

        var longest = TimeSpan.Zero;
        for (var i = 0; i < Keys; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            vm.Document.Insert(vm.Document.TextLength, " ");
            Dispatcher.UIThread.RunJobs();
            var blocked = Stopwatch.GetElapsedTime(t0);
            if (blocked > longest)
            {
                longest = blocked;
            }

            await Pump(TimeSpan.FromMilliseconds(30));
        }

        await WaitUntil(() => !diagnostics.IsPending, 120_000);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"1만 줄 입력 {Keys}회(간격 30 ms): UI 스레드 한 번에 묶인 최장 시간 {longest.TotalMilliseconds:F1} ms"));
        Assert.True(longest.TotalMilliseconds < 100, $"타이핑 중 UI 스레드가 {longest.TotalMilliseconds:F1} ms 묶였다");
        window.Close();
    });

    private static (Window Window, SqlEditorView View, SqlEditorViewModel Vm) Show(string text)
    {
        var vm = new SqlEditorViewModel(Guid.NewGuid(), text);
        var view = new SqlEditorView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, view, vm);
    }

    private static async Task WaitUntil(Func<bool> done, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.ElapsedMilliseconds < timeoutMs)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>UI 작업을 돌리며 <paramref name="duration"/> 만큼 기다린다(타이머가 그동안 깬다).</summary>
    private static async Task Pump(TimeSpan duration)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < duration)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }

    /// <summary>중앙값·p95(nearest-rank, tests/Oranda.Perf.Common 의 Stats 와 같은 방식).</summary>
    private static string Summary(List<double> values)
    {
        var sorted = values.Order().ToArray();
        int n = sorted.Length;
        double median = n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
        double p95 = sorted[Math.Clamp((int)Math.Ceiling(n * 0.95), 1, n) - 1];
        return string.Create(CultureInfo.InvariantCulture, $"n={n} median={median:F1} p95={p95:F1} max={sorted[^1]:F1} ms");
    }

    /// <summary>tests/Oranda.Perf.Common 의 <c>SampleSql.EditorDocument(10000)</c> 과 같은 문서.</summary>
    private static string TenThousandLineDocument()
    {
        var sb = new StringBuilder(10_000 * 40);
        for (var i = 0; i < 10_000; i++)
        {
            sb.Append((i % 20) switch
            {
                0 => "-- section " + i,
                5 => "/* block comment " + i,
                6 => "   still comment */",
                10 => "BEGIN",
                11 => "  IF x" + i + " > 1 THEN y := fn(" + i + ", 'text " + i + "'); END IF;",
                12 => "END;",
                _ => "SELECT col" + i + ", :bind" + i + ", 'literal' FROM tab_" + i + " WHERE id = " + i + ";",
            }).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>분석 요청을 센다(마지막 요청 시각 포함).</summary>
    private sealed class ProbeLanguage(ILanguageService inner) : ILanguageService
    {
        private int _calls;
        private long _lastCallAt;

        public int Calls => Volatile.Read(ref _calls);

        public long LastCallAt => Interlocked.Read(ref _lastCallAt);

        public Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(string text, CancellationToken ct)
        {
            Interlocked.Exchange(ref _lastCallAt, Stopwatch.GetTimestamp());
            Interlocked.Increment(ref _calls);
            return inner.AnalyzeAsync(text, ct);
        }

        public IReadOnlyList<Oranda.Core.Execution.SqlStatement> Split(string text, SplitOptions o) => inner.Split(text, o);

        public Oranda.Core.Execution.SqlStatement? GetStatementAt(string text, int offset, SplitOptions o) => inner.GetStatementAt(text, offset, o);

        public CompletionContext GetCompletionContext(string text, int offset) => inner.GetCompletionContext(text, offset);
    }
}
