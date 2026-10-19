using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using AvaloniaEdit;
using EditorSpike.L1;
using EditorSpike.MultiCaret;

namespace EditorSpike.Ui;

/// <summary>
/// e1: 키 입력 → 화면 반영 지연. 표본 1개 = [헤드리스 키 입력 주입 → 디스패처 작업 처리(입력·문서 변경·L1 렉싱)
/// → 렌더 타이머 강제 틱 → 레이아웃·Skia CPU 래스터화로 프레임 캡처] 의 벽시계 시간.
/// </summary>
public static class TypingLatency
{
    record Scenario(string Name, bool L1, int Carets, string Kind);

    public static void Run(int samples, int warmup)
    {
        HeadlessHost.Start();
        var text = PlSqlGen.Package();
        Console.WriteLine($"# typing latency  samples={samples} warmup={warmup}  doc lines={text.Count(c => c == '\n') + 1} chars={text.Length}");
        Console.WriteLine($"# AvaloniaEdit {typeof(TextEditor).Assembly.GetName().Version}  Avalonia {typeof(Avalonia.Application).Assembly.GetName().Version}  " +
                          $".NET {Environment.Version}  {System.Runtime.InteropServices.RuntimeInformation.OSDescription}  CPU={Environment.ProcessorCount}");
        Console.WriteLine("| 시나리오 | n | p50 ms | p95 ms | p99 ms | max ms | 입력처리 p95 ms | 렉싱 p95 ms | 재렉싱 줄 max |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|");

        var scenarios = new[]
        {
            new Scenario("빈 프레임(입력 없음, 기준선)", false, 1, "none"),
            new Scenario("문자 입력/Backspace, 강조 없음", false, 1, "char"),
            new Scenario("Enter/Backspace, 강조 없음", false, 1, "enter"),
            new Scenario("문자 입력/Backspace, L1 강조", true, 1, "char"),
            new Scenario("Enter/Backspace, L1 강조", true, 1, "enter"),
            new Scenario("'/*' 열고 닫기(상태 전파), L1 강조", true, 1, "comment"),
            new Scenario("문자 입력/Backspace, L1 + 다중 커서 50개", true, 50, "char"),
        };
        foreach (var sc in scenarios) RunScenario(sc, text, samples, warmup);
    }

    static void RunScenario(Scenario sc, string text, int samples, int warmup)
    {
        var (w, ed) = HeadlessHost.CreateEditorWindow(text);
        L1Colorizer? col = sc.L1 ? new L1Colorizer(ed) : null;
        MultiCaretController? mc = sc.Carets > 1 ? new MultiCaretController(ed) : null;
        // 5000번째 줄 근처로 이동해 화면에 보이게
        int line = 5000;
        var dl = ed.Document.GetLineByNumber(line);
        ed.CaretOffset = dl.Offset + Math.Min(12, dl.Length);
        ed.ScrollToLine(line);
        if (mc != null) for (int k = 1; k < sc.Carets; k++) mc.AddCaretVertical(1);
        HeadlessHost.Frame(w);

        var total = new List<double>(samples);
        var input = new List<double>(samples);
        var lex = new List<double>(samples);
        int maxRelex = 0;
        bool toggle = false;
        for (int s = 0; s < warmup + samples; s++)
        {
            toggle = !toggle;
            long t0 = Stopwatch.GetTimestamp();
            switch (sc.Kind)
            {
                case "none": break;
                case "char":
                    if (toggle) w.KeyTextInput("x"); else PressKey(w, Key.Back, PhysicalKey.Backspace);
                    break;
                case "enter":
                    if (toggle) PressKey(w, Key.Enter, PhysicalKey.Enter); else PressKey(w, Key.Back, PhysicalKey.Backspace);
                    break;
                case "comment":
                    // 짝수 표본: '/' 다음 '*' 를 넣는 키(=주석 시작으로 뒤 줄 상태가 바뀜), 홀수: Backspace 로 되돌림
                    if (toggle)
                    {
                        w.KeyTextInput("/"); HeadlessHost.Frame(w); // 첫 글자는 측정 밖
                        t0 = Stopwatch.GetTimestamp();
                        w.KeyTextInput("*");
                    }
                    else
                    {
                        PressKey(w, Key.Back, PhysicalKey.Backspace);
                    }
                    break;
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            long t1 = Stopwatch.GetTimestamp();
            HeadlessHost.Frame(w);
            long t2 = Stopwatch.GetTimestamp();
            if (sc.Kind == "comment" && !toggle)
            {   // 남은 '/' 도 지움(측정 밖)
                PressKey(w, Key.Back, PhysicalKey.Backspace); HeadlessHost.Frame(w);
            }
            if (s < warmup) continue;
            total.Add(Ms(t2 - t0));
            input.Add(Ms(t1 - t0));
            if (col != null) { lex.Add(Ms(col.LastRelexTicks)); maxRelex = Math.Max(maxRelex, col.LastRelexLines); }
        }
        w.Close();
        HeadlessHost.Pump();
        string L(List<double> v, double p) => v.Count == 0 ? "-" : Pct(v, p).ToString("0.00");
        Console.WriteLine($"| {sc.Name} | {total.Count} | {L(total, 50)} | {L(total, 95)} | {L(total, 99)} | {total.Max():0.00} | {L(input, 95)} | {L(lex, 95)} | {(col != null ? maxRelex.ToString() : "-")} |");
    }

    static void PressKey(Window w, Key key, PhysicalKey pk)
    {
        w.KeyPress(key, RawInputModifiers.None, pk, null);
        w.KeyRelease(key, RawInputModifiers.None, pk, null);
    }

    static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    public static double Pct(List<double> v, double p)
    {
        var a = v.OrderBy(x => x).ToArray();
        double r = p / 100.0 * (a.Length - 1);
        int lo = (int)Math.Floor(r), hi = (int)Math.Ceiling(r);
        return a[lo] + (a[hi] - a[lo]) * (r - lo);
    }
}
