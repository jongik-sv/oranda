using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using AvaloniaEdit;
using EditorSpike.L1;
using EditorSpike.MultiCaret;

namespace EditorSpike.Ui;

/// <summary>
/// e1(실제 창): Win32 실제 창 + 기본 렌더러(GPU). 표본 1개 = [TextArea 에 TextInput/KeyDown 라우팅 이벤트 주입]
/// → [합성 배치 커밋 요청] → [그 배치가 렌더 스레드에서 실제로 그려짐(CompositionBatch.Rendered)] 까지.
/// 다음 렌더 틱(≈16.7 ms 주기)을 기다리는 시간이 포함된다. OS 입력(WM_CHAR) 경로와 모니터 스캔아웃은 빠진다.
/// 열: 반영 = 주입 → Rendered. 커밋 = 주입 → Processed(렌더 스레드가 배치를 받음, 틱 대기 포함).
/// 입력처리 = RaiseEvent 동기 처리(문서 변경·L1 렉싱 포함). 레이아웃 = 그 직후 window.UpdateLayout() 강제(동기).
/// 렌더 = Processed → Rendered(렌더 스레드에서 그리기·표시). 작업 합 = 표본마다 입력처리+레이아웃+렌더(틱 대기 제외).
/// </summary>
public static class WindowTyping
{
    public static void Run(int samples, int warmup)
    {
        AppBuilder.Configure<SpikeApp>().UsePlatformDetect().SetupWithoutStarting();
        var cts = new CancellationTokenSource();
        var text = PlSqlGen.Package();
        Console.WriteLine($"# real-window typing  samples={samples} warmup={warmup} lines={text.Count(c => c == '\n') + 1}");
        Console.WriteLine("| 시나리오 | n | 반영 p50 ms | 반영 p95 ms | 반영 p99 ms | 반영 max ms | 커밋 p50 ms | 커밋 p95 ms | 입력처리 p50/p95 | 레이아웃 p50/p95 | 렌더 p50/p95 | 작업 합 p50/p95/max |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|");
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                await Scenario("(JIT 예열, 버림)", text, true, 50, "char", 100, 50, print: false);
                await Scenario("문자 입력/Backspace, 강조 없음", text, false, 1, "char", samples, warmup);
                await Scenario("문자 입력/Backspace, L1 강조", text, true, 1, "char", samples, warmup);
                await Scenario("Enter/Backspace, L1 강조", text, true, 1, "enter", samples, warmup);
                await Scenario("문자 입력/Backspace, L1 + 다중 커서 50개", text, true, 50, "char", samples, warmup);
            }
            catch (Exception ex) { Console.WriteLine("FAILED: " + ex); }
            finally { cts.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(cts.Token);
    }

    static async Task Scenario(string name, string text, bool l1, int carets, string kind, int samples, int warmup, bool print = true)
    {
        var editor = new TextEditor
        {
            FontFamily = new Avalonia.Media.FontFamily("Consolas, Cascadia Mono, Courier New, monospace"),
            FontSize = 14, ShowLineNumbers = true, Text = text,
        };
        var window = new Window { Width = 1600, Height = 1000, Content = editor, Title = "TSK-01-05 typing", Topmost = true };
        window.Show();
        await Task.Delay(500);
        if (l1) _ = new L1Colorizer(editor);
        var mc = carets > 1 ? new MultiCaretController(editor) : null;
        var dl = editor.Document.GetLineByNumber(5000);
        editor.TextArea.Focus();
        editor.CaretOffset = dl.Offset + Math.Min(12, dl.Length);
        editor.ScrollToLine(5000);
        if (mc != null) for (int k = 1; k < carets; k++) mc.AddCaretVertical(1);
        await Task.Delay(300);
        var compositor = ElementComposition.GetElementVisual(window)!.Compositor;
        var area = editor.TextArea;
        var tot = new List<double>(); var work = new List<double>(); var inp = new List<double>(); var lay = new List<double>(); var rnd = new List<double>(); var sum = new List<double>();
        bool toggle = false;
        for (int s = 0; s < warmup + samples; s++)
        {
            await Task.Delay(25); // 사람 타자 간격 흉내(이전 프레임이 끝나게)
            toggle = !toggle;
            long t0 = Stopwatch.GetTimestamp();
            if (toggle && kind == "char") area.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "x" });
            else if (toggle && kind == "enter") Key(area, Avalonia.Input.Key.Enter);
            else Key(area, Avalonia.Input.Key.Back);
            long ti = Stopwatch.GetTimestamp();
            window.UpdateLayout();
            long tl = Stopwatch.GetTimestamp();
            var batch = compositor.RequestCompositionBatchCommitAsync();
            await batch.Processed;
            long t1 = Stopwatch.GetTimestamp();
            await batch.Rendered;
            long t2 = Stopwatch.GetTimestamp();
            if (s < warmup) continue;
            tot.Add(Stopwatch.GetElapsedTime(t0, t2).TotalMilliseconds);
            work.Add(Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds);
            inp.Add(Stopwatch.GetElapsedTime(t0, ti).TotalMilliseconds);
            lay.Add(Stopwatch.GetElapsedTime(ti, tl).TotalMilliseconds);
            rnd.Add(Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds);
            sum.Add(inp[^1] + lay[^1] + rnd[^1]);
        }
        window.Close();
        await Task.Delay(200);
        string P(List<double> v, double p) => TypingLatency.Pct(v, p).ToString("0.00");
        if (!print) return;
        Console.WriteLine($"| {name} | {tot.Count} | {P(tot, 50)} | {P(tot, 95)} | {P(tot, 99)} | {tot.Max():0.00} | {P(work, 50)} | {P(work, 95)} | {P(inp, 50)} / {P(inp, 95)} | {P(lay, 50)} / {P(lay, 95)} | {P(rnd, 50)} / {P(rnd, 95)} | {P(sum, 50)} / {P(sum, 95)} / {sum.Max():0.00} |");
    }

    static void Key(Interactive target, Key key)
    {
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = target });
        target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key, Source = target });
    }
}
